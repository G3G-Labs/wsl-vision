using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using WslVision.Services.Docker;

namespace WslVision.Services;

/// <summary>
/// Limpeza e otimização: caches do Linux, lixo do Docker, memória da VM e o disco virtual (.vhdx).
/// Tudo o que mede ou limpa dentro da distro roda como root, num script só, sem passar caminhos pelo shell do Windows.
/// </summary>
public sealed class MaintenanceService(WslService wsl)
{
    /// <summary>Chaves das limpezas feitas dentro da distro (as do Docker que usam a API ficam em <see cref="PruneDockerAsync"/>).</summary>
    public static readonly string[] LinuxKeys = ["apt", "journal", "usercache", "npm", "tmp", "dockerlogs"];

    // $A1 = home do usuário. Uma linha "chave bytes" por item, mais o cache de páginas e o uso do disco.
    private const string MeasureScript = """
        sum() { awk '{s+=$1} END {printf "%.0f\n", s+0}'; }
        size() { du -sb -- "$@" 2>/dev/null | sum; }
        echo "apt $(size /var/cache/apt/archives)"
        echo "journal $(size /var/log/journal)"
        echo "usercache $(size "$A1/.cache")"
        echo "npm $(size "$A1/.npm/_cacache")"
        echo "tmp $(find /tmp -xdev -type f -mtime +7 -printf '%s\n' 2>/dev/null | sum)"
        echo "dockerlogs $(find /var/lib/docker/containers -name '*-json.log' -printf '%s\n' 2>/dev/null | sum)"
        echo "pagecache $(awk '/^Cached:/ {printf "%.0f\n", $2 * 1024}' /proc/meminfo)"
        echo "diskused $(df -P -B1 / | awk 'NR == 2 {print $3}')"
        """;

    // $A1 = home do usuário; $A2 = chaves separadas por espaço.
    private const string CleanScript = """
        case "$A1" in ""|/) echo "Pasta pessoal inválida." >&2; exit 3;; esac
        for k in $A2; do
          case $k in
            apt) command -v apt-get >/dev/null 2>&1 && apt-get clean ;;
            journal) command -v journalctl >/dev/null 2>&1 && journalctl --vacuum-size=50M >/dev/null 2>&1 ;;
            usercache) [ -d "$A1/.cache" ] && find "$A1/.cache" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} + ;;
            npm) rm -rf -- "$A1/.npm/_cacache" ;;
            tmp) find /tmp -xdev -type f -mtime +7 -delete 2>/dev/null ;;
            dockerlogs) find /var/lib/docker/containers -name '*-json.log' -exec truncate -s 0 -- {} + 2>/dev/null ;;
          esac
        done
        exit 0
        """;

    // Devolve o cache de arquivos do Linux; com autoMemoryReclaim, o Windows recebe a memória de volta.
    private const string DropCachesScript = """
        before=$(awk '/^MemAvailable:/ {print $2}' /proc/meminfo)
        sync
        echo 3 > /proc/sys/vm/drop_caches
        echo 1 > /proc/sys/vm/compact_memory 2>/dev/null
        after=$(awk '/^MemAvailable:/ {print $2}' /proc/meminfo)
        echo "$before $after"
        """;

    /// <summary>Tamanho de cada item limpável (bytes), mais "pagecache" e "diskused".</summary>
    public async Task<Dictionary<string, long>> MeasureAsync(string distro, string home)
    {
        var r = await wsl.RunScriptAsync(distro, MeasureScript, [home], asRoot: true, timeout: TimeSpan.FromMinutes(3));
        if (!r.Success)
            throw new InvalidOperationException(WslService.ErrorText(r, "Não foi possível analisar a distro."));
        return ParseSizes(r.StdOut);
    }

    public static Dictionary<string, long> ParseSizes(string output)
    {
        var sizes = new Dictionary<string, long>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (p.Length == 2 && long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                sizes[p[0]] = n;
        }
        return sizes;
    }

    public async Task CleanLinuxAsync(string distro, string home, IEnumerable<string> keys)
    {
        var list = keys.Where(LinuxKeys.Contains).ToList();
        if (list.Count == 0)
            return;
        var r = await wsl.RunScriptAsync(distro, CleanScript, [home, string.Join(' ', list)], asRoot: true, timeout: TimeSpan.FromMinutes(10));
        if (!r.Success)
            throw new InvalidOperationException(WslService.ErrorText(r, "Não foi possível limpar."));
    }

    /// <summary>Limpa pela API do Docker. Chaves: images, buildcache, containers, volumes. Devolve o espaço liberado.</summary>
    public static async Task<long> PruneDockerAsync(DockerClient client, IEnumerable<string> keys)
    {
        long freed = 0;
        foreach (var key in keys)
        {
            freed += key switch
            {
                // dangling=false: todas as imagens sem container, como "docker image prune -a".
                "images" => await client.PruneAsync("images", DockerClient.Filters("""{"dangling":["false"]}""")),
                "buildcache" => await client.PruneAsync("build", "all=true"),
                "containers" => await client.PruneAsync("containers"),
                "volumes" => await PruneVolumesAsync(client),
                _ => 0,
            };
        }
        return freed;
    }

    private static async Task<long> PruneVolumesAsync(DockerClient client)
    {
        try
        {
            // Desde a API 1.42, sem "all" só os volumes anônimos são removidos.
            return await client.PruneAsync("volumes", DockerClient.Filters("""{"all":["true"]}"""));
        }
        catch (DockerApiException ex) when (ex.Status == System.Net.HttpStatusCode.BadRequest)
        {
            return await client.PruneAsync("volumes");
        }
    }

    /// <summary>Descarta o cache de páginas do Linux. Devolve quantos bytes ficaram livres.</summary>
    public async Task<long> FreeMemoryAsync(string distro)
    {
        var r = await wsl.RunScriptAsync(distro, DropCachesScript, asRoot: true, timeout: TimeSpan.FromMinutes(2));
        var p = r.StdOut.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!r.Success || p.Length < 2
            || !long.TryParse(p[0], CultureInfo.InvariantCulture, out var before)
            || !long.TryParse(p[1], CultureInfo.InvariantCulture, out var after))
            throw new InvalidOperationException(WslService.ErrorText(r, "Não foi possível liberar a memória."));
        return Math.Max(0, after - before) * 1024;
    }

    /// <summary>
    /// Compacta o .vhdx da distro: fstrim dentro do Linux, desliga o WSL e roda o diskpart como administrador
    /// (o Windows pede a permissão). Devolve o tamanho do arquivo antes e depois.
    /// </summary>
    public async Task<(long Before, long After)> CompactDiskAsync(string distro, bool isRunning, Func<Task> shutdownWsl)
    {
        var vhd = WslService.GetVhdPath(distro)
                  ?? throw new InvalidOperationException($"Não encontrei o disco virtual da distro {distro}.");
        var before = new FileInfo(vhd).Length;

        // Marca os blocos livres como descartáveis; sem isso, compactar quase não devolve nada.
        if (isRunning)
            await wsl.RunScriptAsync(distro, "fstrim / 2>/dev/null || true", asRoot: true, timeout: TimeSpan.FromMinutes(5));
        await shutdownWsl();

        var script = Path.Combine(Path.GetTempPath(), $"wslvision-compact-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(script, $"select vdisk file=\"{vhd}\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\nexit\r\n");
        try
        {
            using var p = Process.Start(new ProcessStartInfo("diskpart.exe", $"/s \"{script}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException("Não foi possível abrir o diskpart.");
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(60));
            await p.WaitForExitAsync(cts.Token);
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"O diskpart não conseguiu compactar o disco (código {p.ExitCode}). Confira se nenhum programa está usando o WSL.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("A permissão de administrador foi negada. O disco não foi compactado.");
        }
        finally
        {
            try { File.Delete(script); } catch { /* temporário */ }
        }
        return (before, new FileInfo(vhd).Length);
    }
}
