using System.Diagnostics;
using System.Text;
using System.Windows;

namespace WslVision.Services;

/// <summary>Ponte com o Windows: VS Code, Windows Terminal, Explorer, área de transferência.</summary>
public sealed class LauncherService(WslService wsl)
{
    /// <summary>Equivale a "code ." dentro da pasta: abre o VS Code já conectado ao WSL.</summary>
    public void OpenInVsCode(string distro, string linuxPath)
    {
        // Pelo protocolo vscode:// (registrado pelo instalador do VS Code), e não pelo code.cmd: um .cmd passa
        // pelo cmd.exe, que interpretaria & | % de nomes de pasta como comandos. Na URI, tudo vai codificado.
        using (var handler = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("vscode"))
        {
            if (handler is null)
                throw new InvalidOperationException("VS Code não encontrado. Instale o VS Code e a extensão WSL.");
        }
        var segments = LinuxPaths.Normalize(linuxPath).Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
        var uri = $"vscode://vscode-remote/wsl+{Uri.EscapeDataString(distro)}/{string.Join('/', segments)}";
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
    }

    public void OpenInExplorer(string distro, string linuxPath)
    {
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(LinuxPaths.ToUnc(distro, linuxPath));
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Abre um arquivo no programa padrão do Windows.</summary>
    public void OpenWithWindows(string distro, string linuxPath) =>
        Process.Start(new ProcessStartInfo(LinuxPaths.ToUnc(distro, linuxPath)) { UseShellExecute = true })?.Dispose();

    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    /// <summary>
    /// Abre o Windows Terminal na distro e na pasta pedidas, opcionalmente rodando um comando.
    /// A pasta e o comando vão para um script temporário dentro da distro, então nenhum texto do usuário
    /// passa pelas regras de aspas do wt.exe e do wsl.exe.
    /// </summary>
    public async Task OpenTerminalAsync(string distro, string linuxDir, bool asRoot = false, string? command = null, string? title = null)
    {
        var wslArgs = await PrepareSessionAsync(distro, linuxDir, asRoot, command);

        var wt = FindWindowsTerminal();
        ProcessStartInfo psi;
        if (wt is not null)
        {
            psi = new ProcessStartInfo(wt) { UseShellExecute = false };
            psi.ArgumentList.Add("--window");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("new-tab");
            psi.ArgumentList.Add("--title");
            // O wt.exe usa ";" para separar os próprios comandos: escapa para não abrir abas extras.
            psi.ArgumentList.Add((title ?? (asRoot ? $"{distro} (root)" : distro)).Replace(";", @"\;"));
            psi.ArgumentList.Add("--suppressApplicationTitle");
            foreach (var a in wslArgs)
                psi.ArgumentList.Add(a);
        }
        else
        {
            // Sem Windows Terminal: abre o console clássico.
            psi = new ProcessStartInfo("wsl.exe") { UseShellExecute = true };
            foreach (var a in wslArgs.Skip(1))
                psi.ArgumentList.Add(a);
        }
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Linha de comando do wsl.exe para um terminal embutido no app (mesmo script temporário do Windows Terminal).</summary>
    public async Task<string> PrepareEmbeddedTerminalAsync(string distro, string linuxDir, bool asRoot = false, string? command = null)
    {
        var args = await PrepareSessionAsync(distro, linuxDir, asRoot, command);
        return string.Join(' ', args.Select(QuoteArgument));
    }

    /// <summary>Aspas no padrão do Windows (CommandLineToArgvW), só quando o argumento precisa.</summary>
    public static string QuoteArgument(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"'))
            return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // Barras antes de aspas precisam ser dobradas; as próprias aspas são escapadas.
            sb.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Grava na distro o script que entra na pasta, roda o comando (se houver) e abre o shell do usuário.
    /// Devolve os argumentos do wsl.exe que executam esse script (o primeiro é o próprio "wsl.exe").
    /// </summary>
    private async Task<List<string>> PrepareSessionAsync(string distro, string linuxDir, bool asRoot, string? command)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

        var sb = new StringBuilder();
        sb.Append("rm -f -- \"$0\"\n");
        sb.Append($"cd -- \"$(printf '%s' '{B64(linuxDir)}' | base64 -d)\" 2>/dev/null || cd\n");
        if (!string.IsNullOrWhiteSpace(command))
        {
            sb.Append($"__wv_cmd=\"$(printf '%s' '{B64(command)}' | base64 -d)\"\n");
            sb.Append("printf '\\033[1;34m$ %s\\033[0m\\n' \"$__wv_cmd\"\n");
            sb.Append("eval \"$__wv_cmd\"\n");
            sb.Append("unset __wv_cmd\n");
        }
        sb.Append("exec \"${SHELL:-/bin/bash}\" -l\n");

        // Uma pasta por usuário: se o root criasse uma pasta compartilhada (0700), o usuário comum não conseguiria mais usá-la.
        var r = await wsl.RunScriptAsync(distro, """
            umask 077
            d="/tmp/wslvision-$(id -u)"
            mkdir -p "$d"
            f="$(mktemp "$d/term.XXXXXX")" || exit 1
            printf '%s' "$A1" > "$f"
            printf '%s' "$f"
            """, [sb.ToString()], asRoot);
        if (!r.Success || r.StdOut.Trim().Length == 0)
            throw new InvalidOperationException(WslService.ErrorText(r, "Não foi possível preparar o terminal."));
        var scriptPath = r.StdOut.Trim();

        var wslArgs = new List<string> { "wsl.exe", "--distribution", distro };
        if (asRoot)
            wslArgs.AddRange(["--user", "root"]);
        wslArgs.AddRange(["--exec", "bash", scriptPath]);
        return wslArgs;
    }

    public static void CopyText(string text)
    {
        // A área de transferência às vezes está ocupada por outro programa.
        for (var i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(50);
            }
        }
        throw new InvalidOperationException("A área de transferência está ocupada. Tente de novo.");
    }

    private static string? FindWindowsTerminal()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
        return File.Exists(alias) ? alias : FindInPath("wt.exe");
    }

    private static string? FindInPath(string file) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir =>
            {
                try { return Path.Combine(dir, file); }
                catch (ArgumentException) { return null; }
            })
            .FirstOrDefault(p => p is not null && File.Exists(p));
}
