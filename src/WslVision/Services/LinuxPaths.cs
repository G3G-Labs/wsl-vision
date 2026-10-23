namespace WslVision.Services;

/// <summary>Conversões entre caminhos do Linux e do Windows, sem chamar o WSL.</summary>
public static class LinuxPaths
{
    public const string UncRoot = @"\\wsl.localhost\";

    public static string ToUnc(string distro, string linuxPath)
    {
        var p = Normalize(linuxPath);
        return UncRoot + distro + (p == "/" ? @"\" : p.Replace('/', '\\'));
    }

    /// <summary>Converte \\wsl.localhost\Distro\... ou \\wsl$\Distro\... para /...; devolve null se for de outra distro.</summary>
    public static string? FromUnc(string distro, string windowsPath)
    {
        foreach (var root in new[] { UncRoot, @"\\wsl$\" })
        {
            var prefix = root + distro;
            if (windowsPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (windowsPath.Length == prefix.Length || windowsPath[prefix.Length] == '\\'))
            {
                var rest = windowsPath[prefix.Length..].Replace('\\', '/');
                return Normalize(rest.Length == 0 ? "/" : rest);
            }
        }
        return null;
    }

    /// <summary>C:\Users\x → /mnt/c/Users/x (montagem padrão do WSL).</summary>
    public static string? FromWindowsDrive(string windowsPath)
    {
        if (windowsPath.Length >= 2 && char.IsAsciiLetter(windowsPath[0]) && windowsPath[1] == ':')
        {
            var rest = windowsPath.Length > 2 ? windowsPath[2..].Replace('\\', '/') : "/";
            return Normalize($"/mnt/{char.ToLowerInvariant(windowsPath[0])}{(rest.StartsWith('/') ? rest : "/" + rest)}");
        }
        return null;
    }

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";
        var parts = new List<string>();
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".")
                continue;
            if (seg == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(seg);
        }
        return "/" + string.Join('/', parts);
    }

    public static string Combine(string dir, string name) => Normalize(dir.TrimEnd('/') + "/" + name);

    public static string Parent(string path)
    {
        var p = Normalize(path);
        if (p == "/")
            return "/";
        var i = p.LastIndexOf('/');
        return i <= 0 ? "/" : p[..i];
    }

    public static string Name(string path)
    {
        var p = Normalize(path);
        return p == "/" ? "/" : p[(p.LastIndexOf('/') + 1)..];
    }

    /// <summary>Expande "~" usando a home conhecida do usuário.</summary>
    public static string Expand(string path, string? home)
    {
        path = path.Trim();
        if (home is null)
            return Normalize(path);
        if (path == "~")
            return Normalize(home);
        if (path.StartsWith("~/", StringComparison.Ordinal))
            return Combine(home, path[2..]);
        return Normalize(path);
    }

    /// <summary>Mostra a home como "~" para caminhos curtos e legíveis.</summary>
    public static string Pretty(string path, string? home)
    {
        var p = Normalize(path);
        if (home is null)
            return p;
        var h = Normalize(home);
        if (p == h)
            return "~";
        return p.StartsWith(h + "/", StringComparison.Ordinal) ? "~" + p[h.Length..] : p;
    }

    /// <summary>Caminhos que nunca podem ser apagados ou movidos pelo app.</summary>
    public static bool IsProtectedSystemPath(string path, string? home)
    {
        var p = Normalize(path);
        if (CriticalPaths.Contains(p) || (home is not null && p == Normalize(home)))
            return true;
        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Segundo nível das pastas do sistema: /usr/bin, /var/lib, /home/outro-usuario…
        if (parts.Length == 2 && SystemParents.Contains(parts[0]))
            return true;
        // /mnt/c é o disco inteiro do Windows (e /mnt/wsl, do próprio WSL). Dentro dele: pastas do Windows e perfis.
        if (parts.Length >= 2 && parts[0] == "mnt")
            return parts.Length == 2
                || (parts.Length == 3 && WindowsSystemFolders.Contains(parts[2]))
                || (parts.Length == 4 && parts[2].Equals("Users", StringComparison.OrdinalIgnoreCase));
        return false;
    }

    private static readonly HashSet<string> CriticalPaths =
    [
        "/", "/bin", "/boot", "/dev", "/etc", "/home", "/lib", "/lib64", "/mnt", "/opt", "/proc",
        "/root", "/run", "/sbin", "/srv", "/sys", "/tmp", "/usr", "/var",
    ];

    private static readonly HashSet<string> SystemParents =
        ["bin", "boot", "dev", "home", "lib", "lib64", "proc", "run", "sbin", "sys", "usr", "var"];

    // O DrvFs não diferencia maiúsculas: /mnt/c/windows é a mesma pasta que C:\Windows.
    private static readonly HashSet<string> WindowsSystemFolders = new(StringComparer.OrdinalIgnoreCase)
        { "Users", "Windows", "Program Files", "Program Files (x86)", "ProgramData" };
}
