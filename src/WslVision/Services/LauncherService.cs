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
        var paths = await WriteScriptsAsync(distro, [TerminalScript.Build(linuxDir, command, tabId: null, helpers: false)], asRoot);
        return WslArgs(distro, asRoot, paths[0]);
    }

    /// <summary>
    /// Prepara várias abas embutidas de uma vez: um wsl.exe por usuário (o seu e, se houver, o root), não um por aba.
    /// Abrir dez abas não pode virar uma rajada de wsl.exe: cada um abre canais na VM, e isso já travou a distro.
    /// Devolve a linha de comando de cada aba, na ordem pedida.
    /// </summary>
    public async Task<IReadOnlyList<string>> PrepareEmbeddedTerminalsAsync(string distro, IReadOnlyList<TerminalRequest> requests, bool helpers)
    {
        var lines = new string[requests.Count];
        foreach (var byUser in requests.Select((r, i) => (Request: r, Index: i)).GroupBy(x => x.Request.AsRoot))
        {
            var items = byUser.ToList();
            var scripts = items.Select(x => TerminalScript.Build(x.Request.Folder, x.Request.Command, x.Request.TabId, helpers)).ToList();
            var paths = await WriteScriptsAsync(distro, scripts, byUser.Key, items.Select(x => x.Request.TabId).OfType<string>());
            for (var k = 0; k < items.Count; k++)
                lines[items[k].Index] = string.Join(' ', WslArgs(distro, byUser.Key, paths[k]).Select(QuoteArgument));
        }
        return lines;
    }

    /// <summary>Grava os scripts numa pasta temporária do usuário dentro da distro e devolve os caminhos, na mesma ordem.</summary>
    private async Task<IReadOnlyList<string>> WriteScriptsAsync(string distro, IReadOnlyList<string> scripts, bool asRoot, IEnumerable<string>? tabIds = null)
    {
        // Tudo vai pelo stdin: como argumentos, um lote grande estouraria a linha de comando do Windows (32 767 caracteres).
        var r = await wsl.RunScriptAsync(distro, TerminalScript.Writer(scripts, tabIds ?? []), asRoot: asRoot);
        var paths = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!r.Success || paths.Length != scripts.Count)
            throw new InvalidOperationException(WslService.ErrorText(r, "Não foi possível preparar o terminal."));
        return paths;
    }

    private static List<string> WslArgs(string distro, bool asRoot, string scriptPath)
    {
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
