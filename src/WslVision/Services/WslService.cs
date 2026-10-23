using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using WslVision.Models;

namespace WslVision.Services;

/// <summary>
/// Único ponto do app que fala com o wsl.exe. Toda chamada que entra na distro passa por aqui,
/// para que mudanças em versões futuras do WSL fiquem isoladas numa camada só.
/// </summary>
public sealed class WslService
{
    private const string WslExe = "wsl.exe";

    // Saída em UTF-8 em vez de UTF-16 nos comandos de gerenciamento do wsl.exe.
    private static readonly Dictionary<string, string> Env = new() { ["WSL_UTF8"] = "1" };

    /// <summary>Código de saída usado pelos scripts do app para "permissão negada".</summary>
    public const int PermissionDeniedExitCode = 13;

    public const int NotFoundExitCode = 2;

    // O estado e cada conexão nova com o Docker perguntam quais distros estão ligadas, muitas vezes no mesmo instante:
    // a resposta vale por 2 s, e cada wsl.exe a menos é um canal a menos aberto na VM.
    private readonly CachedQuery<IReadOnlySet<string>> _running;

    public WslService() => _running = new(QueryRunningAsync, TimeSpan.FromSeconds(2));

    /// <summary>Lista as distros pelo registro (não liga o WSL e não depende do idioma do Windows).</summary>
    public async Task<IReadOnlyList<DistroInfo>> ListDistrosAsync()
    {
        var names = new List<string>();
        string? defaultName = null;
        var versions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        using (var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
        {
            if (lxss is not null)
            {
                var defaultId = lxss.GetValue("DefaultDistribution") as string;
                foreach (var id in lxss.GetSubKeyNames())
                {
                    using var k = lxss.OpenSubKey(id);
                    if (k?.GetValue("DistributionName") is not string name)
                        continue;
                    names.Add(name);
                    versions[name] = k.GetValue("Version") is int v ? v : 2;
                    if (string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase))
                        defaultName = name;
                }
            }
        }

        var running = await ListRunningAsync();
        return names
            .Select(n => new DistroInfo(n, n == defaultName, running.Contains(n), versions.GetValueOrDefault(n, 2)))
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Arquivo .vhdx da distro (pelo registro), ou null se não for encontrado.</summary>
    public static string? GetVhdPath(string distro)
    {
        using var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
        if (lxss is null)
            return null;
        foreach (var id in lxss.GetSubKeyNames())
        {
            using var k = lxss.OpenSubKey(id);
            if (k?.GetValue("DistributionName") is not string name || !name.Equals(distro, StringComparison.OrdinalIgnoreCase))
                continue;
            if (k.GetValue("BasePath") is not string basePath)
                return null;
            if (basePath.StartsWith(@"\\?\", StringComparison.Ordinal))
                basePath = basePath[4..];
            var file = k.GetValue("VhdFileName") as string ?? "ext4.vhdx";
            var path = Path.Combine(basePath, file);
            return File.Exists(path) ? path : null;
        }
        return null;
    }

    /// <summary>Distros ligadas agora (resposta de até 2 s atrás). Não liga nenhuma distro.</summary>
    public Task<IReadOnlySet<string>> ListRunningAsync() => _running.GetAsync();

    private static async Task<IReadOnlySet<string>> QueryRunningAsync()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var r = await ProcessRunner.RunAsync(WslExe, ["--list", "--running", "--quiet"], env: Env, timeout: TimeSpan.FromSeconds(15));
            if (!r.Success)
                return set; // "Não há distribuições em execução." sai com código de erro.
            foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = line.Trim('\0', '﻿', ' ');
                if (name.Length > 0 && !name.Contains(' '))
                    set.Add(name);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException)
        {
            // wsl.exe ausente ou travado: tratamos como desligado.
        }
        return set;
    }

    public async Task<bool> IsRunningAsync(string distro) => (await ListRunningAsync()).Contains(distro);

    public async Task ShutdownAsync()
    {
        try
        {
            await ProcessRunner.RunAsync(WslExe, ["--shutdown"], env: Env, timeout: TimeSpan.FromMinutes(1));
        }
        finally
        {
            _running.Invalidate();
        }
    }

    public async Task TerminateAsync(string distro)
    {
        try
        {
            await ProcessRunner.RunAsync(WslExe, ["--terminate", distro], env: Env, timeout: TimeSpan.FromMinutes(1));
        }
        finally
        {
            _running.Invalidate();
        }
    }

    /// <summary>Liga a distro (só quando o usuário pede).</summary>
    public async Task StartAsync(string distro)
    {
        var r = await RunScriptAsync(distro, "true", timeout: TimeSpan.FromMinutes(2));
        if (!r.Success)
            throw new InvalidOperationException(ErrorText(r, $"Não foi possível ligar a distro {distro}."));
    }

    /// <summary>
    /// Executa um script de shell na distro. O script vai pelo stdin e os argumentos chegam em base64,
    /// então nenhum caminho ou texto do usuário passa por regras de aspas do Windows ou do shell.
    /// Dentro do script, os argumentos ficam disponíveis como $A1, $A2, ...
    /// Todo "wsl.exe -d" liga a distro, então a lista de distros ligadas é consultada de novo depois.
    /// </summary>
    public async Task<ProcessResult> RunScriptAsync(
        string distro,
        string script,
        IReadOnlyList<string>? args = null,
        bool asRoot = false,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        args ??= [];
        var argv = new List<string> { "--distribution", distro };
        if (asRoot)
            argv.AddRange(["--user", "root"]);
        // --cd ~ evita depender da pasta atual do Windows (que nem sempre tem tradução no Linux).
        argv.AddRange(["--cd", "~", "--exec", "sh", "-s", "--"]);

        // Mensagens de erro sempre em inglês: o app reconhece "Permission denied" no stderr, e o idioma da distro não pode mudar isso.
        var prelude = new StringBuilder("unset LC_ALL LANGUAGE; export LC_MESSAGES=C\n");
        for (var i = 0; i < args.Count; i++)
        {
            // O prefixo "x" garante que argumentos vazios não sejam descartados no caminho.
            argv.Add("x" + Convert.ToBase64String(Encoding.UTF8.GetBytes(args[i])));
            prelude.Append($"A{i + 1}=\"$(printf '%s' \"${{{i + 1}#x}}\" | base64 -d)\"\n");
        }

        try
        {
            return await ProcessRunner.RunAsync(WslExe, argv, stdin: prelude + script + "\n", timeout: timeout, ct: ct);
        }
        finally
        {
            _running.Invalidate();
        }
    }

    /// <summary>Inicia um processo de longa duração na distro, com stdin e stdout redirecionados.</summary>
    public Process StartStreaming(string distro, IReadOnlyList<string> command, bool asRoot = false)
    {
        var psi = new ProcessStartInfo(WslExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
        };
        psi.ArgumentList.Add("--distribution");
        psi.ArgumentList.Add(distro);
        if (asRoot)
        {
            psi.ArgumentList.Add("--user");
            psi.ArgumentList.Add("root");
        }
        psi.ArgumentList.Add("--cd");
        psi.ArgumentList.Add("~");
        psi.ArgumentList.Add("--exec");
        foreach (var c in command)
            psi.ArgumentList.Add(c);

        var p = new Process { StartInfo = psi };
        p.Start();
        _running.Invalidate();
        return p;
    }

    public async Task<(string User, string Home)> GetDefaultUserAsync(string distro)
    {
        var r = await RunScriptAsync(distro, "id -un; printf '%s\\n' \"$HOME\"");
        var lines = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!r.Success || lines.Length < 2)
            throw new InvalidOperationException(ErrorText(r, "Não foi possível identificar o usuário padrão da distro."));
        return (lines[0], lines[1]);
    }

    public static string ErrorText(ProcessResult r, string fallback)
    {
        var msg = string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr;
        msg = msg.Trim();
        return msg.Length == 0 ? fallback : $"{fallback}\n{(msg.Length > 400 ? msg[..400] + "…" : msg)}";
    }
}
