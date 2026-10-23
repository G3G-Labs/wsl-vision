using System.Globalization;
using WslVision.Models;

namespace WslVision.Services;

public sealed class PermissionRequiredException(string message) : Exception(message);

/// <summary>
/// Operações de arquivo feitas por dentro do Linux. Isso evita os erros de permissão do
/// \\wsl.localhost no Explorer e é bem mais rápido em pastas grandes (ex.: node_modules).
/// Quando o usuário não tem permissão, o chamador pode repetir a operação como root, depois de confirmar.
/// </summary>
public sealed class FileService(WslService wsl)
{
    public const int PageSize = 500;

    private const string ListScript = """
        [ -d "$A1" ] || exit 2
        { [ -r "$A1" ] && [ -x "$A1" ]; } || exit 13
        if [ -w "$A1" ]; then echo W; else echo R; fi
        cd -- "$A1" || exit 13
        # Ordena antes de cortar (pastas primeiro, depois o nome), para a primeira página ser a mesma que o app mostra.
        find . -mindepth 1 -maxdepth 1 \( -xtype d -printf '0\t' -o -printf '1\t' \) -printf '%y\t%Y\t%s\t%T@\t%u\t%f\0' 2>/dev/null \
          | sort -z -t "$(printf '\t')" -k1,1 -k7f | head -z -n "$A2"
        """;

    public async Task<DirectoryListing> ListAsync(string distro, string path, int limit, bool asRoot = false, CancellationToken ct = default)
    {
        var r = await wsl.RunScriptAsync(distro, ListScript, [path, (limit + 1).ToString(CultureInfo.InvariantCulture)], asRoot, TimeSpan.FromSeconds(60), ct);
        if (r.ExitCode == WslService.NotFoundExitCode)
            throw new DirectoryNotFoundException($"A pasta {path} não existe.");
        if (r.ExitCode == WslService.PermissionDeniedExitCode)
            throw new PermissionRequiredException($"Você não tem permissão para abrir {path}.");
        if (!r.Success)
            throw new IOException(WslService.ErrorText(r, $"Não foi possível listar {path}."));

        var nl = r.StdOut.IndexOf('\n');
        var writable = nl > 0 && r.StdOut[..nl].Trim() == "W";
        var body = nl >= 0 ? r.StdOut[(nl + 1)..] : "";

        var entries = new List<LinuxFileEntry>();
        foreach (var rec in body.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // O primeiro campo é só a chave de ordenação do script.
            var f = rec.Split('\t', 7).Skip(1).ToArray();
            if (f.Length < 6)
                continue;
            var isLink = f[0] == "l";
            var type = (isLink ? f[1] : f[0]) switch
            {
                "d" => LinuxEntryType.Directory,
                "f" => LinuxEntryType.File,
                _ => LinuxEntryType.Other,
            };
            long.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var epoch);
            entries.Add(new LinuxFileEntry(
                f[5],
                LinuxPaths.Combine(path, f[5]),
                type,
                isLink,
                size,
                DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000)).ToLocalTime(),
                f[4]));
        }

        var truncated = entries.Count > limit;
        if (truncated)
            entries.RemoveRange(limit, entries.Count - limit);

        entries.Sort((a, b) =>
        {
            var byType = (a.Type == LinuxEntryType.Directory ? 0 : 1).CompareTo(b.Type == LinuxEntryType.Directory ? 0 : 1);
            return byType != 0 ? byType : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        return new DirectoryListing(path, entries, truncated, writable);
    }

    public Task CreateFolderAsync(string distro, string parent, string name, bool asRoot) =>
        Run(distro, """
            [ -e "$A1" ] && { echo "Já existe um item com esse nome." >&2; exit 17; }
            mkdir -- "$A1"
            """, [LinuxPaths.Combine(parent, ValidName(name))], asRoot, "criar a pasta");

    public Task RenameAsync(string distro, string path, string newName, string? home, bool asRoot) =>
        LinuxPaths.IsProtectedSystemPath(path, home)
            ? throw new InvalidOperationException("Essa pasta é essencial para o sistema e não pode ser renomeada pelo app.")
            : Run(distro, """
            [ -e "$A2" ] && { echo "Já existe um item com esse nome." >&2; exit 17; }
            mv -- "$A1" "$A2"
            """, [path, LinuxPaths.Combine(LinuxPaths.Parent(path), ValidName(newName))], asRoot, "renomear");

    public Task DeleteAsync(string distro, string path, string? home, bool asRoot)
    {
        if (LinuxPaths.IsProtectedSystemPath(path, home))
            throw new InvalidOperationException("Essa pasta é essencial para o sistema e não pode ser excluída pelo app.");
        return Run(distro, "rm -rf -- \"$A1\"", [path], asRoot, "excluir", TimeSpan.FromMinutes(10));
    }

    /// <summary>Copia (cp -a) ou move um item para dentro de outra pasta da distro.</summary>
    public Task CopyOrMoveAsync(string distro, string source, string targetDir, bool move, string? home, bool asRoot)
    {
        if (move && LinuxPaths.IsProtectedSystemPath(source, home))
            throw new InvalidOperationException("Essa pasta é essencial para o sistema e não pode ser movida pelo app.");
        var target = LinuxPaths.Combine(targetDir, LinuxPaths.Name(source));
        if (target == LinuxPaths.Normalize(source))
        {
            // Copiar na mesma pasta cria "nome (cópia)"; mover para onde já está não faz sentido.
            if (move)
                throw new InvalidOperationException("O item já está nesta pasta.");
            target = LinuxPaths.Combine(targetDir, CopyName(LinuxPaths.Name(source)));
        }
        if (target.StartsWith(LinuxPaths.Normalize(source) + "/", StringComparison.Ordinal))
            throw new InvalidOperationException("Não dá para colocar uma pasta dentro dela mesma.");

        var script = (move ? "mv" : "cp -a") + " -- \"$A1\" \"$A2\"";
        return Run(distro, """
            [ -e "$A2" ] && { echo "Já existe um item com esse nome no destino." >&2; exit 17; }
            """ + "\n" + script, [source, target], asRoot, move ? "mover" : "copiar", TimeSpan.FromMinutes(30));
    }

    /// <summary>Traz arquivos do Windows (arrastados para a janela) para uma pasta da distro.</summary>
    public async Task ImportFromWindowsAsync(string distro, string windowsPath, string targetDir, bool asRoot)
    {
        var source = LinuxPaths.FromWindowsDrive(windowsPath)
            ?? LinuxPaths.FromUnc(distro, windowsPath)
            ?? throw new InvalidOperationException($"Não é possível copiar {windowsPath} (só unidades locais, como C:).");
        await Run(distro, """
            [ -e "$A2" ] && { echo "Já existe um item com esse nome no destino." >&2; exit 17; }
            cp -a -- "$A1" "$A2"
            """, [source, LinuxPaths.Combine(targetDir, Path.GetFileName(windowsPath.TrimEnd('\\')))], asRoot, "copiar", TimeSpan.FromMinutes(30));
    }

    private async Task Run(string distro, string script, IReadOnlyList<string> args, bool asRoot, string verb, TimeSpan? timeout = null)
    {
        var r = await wsl.RunScriptAsync(distro, script, args, asRoot, timeout ?? TimeSpan.FromMinutes(2));
        if (r.Success)
            return;
        if (!asRoot && (r.StdErr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || r.StdErr.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase)))
            throw new PermissionRequiredException($"Você não tem permissão para {verb} aqui.");
        throw new IOException(WslService.ErrorText(r, $"Não foi possível {verb}."));
    }

    private static string ValidName(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
            throw new ArgumentException("Nome inválido. Não use \"/\" nem deixe em branco.");
        return name;
    }

    private static string CopyName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? $"{name[..dot]} (cópia){name[dot..]}" : $"{name} (cópia)";
    }
}
