using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WslVision.Services;

public sealed record WslLimits(int? MemoryGb, int? Processors, bool AutoMemoryReclaim);

/// <summary>
/// Lê e grava o %UserProfile%\.wslconfig preservando tudo o que o app não conhece.
/// Antes de gravar, guarda uma cópia em .wslconfig.wslvision.bak (ou marca que o arquivo não existia).
/// </summary>
public sealed partial class WslConfigService
{
    public static string ConfigPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");

    public static string BackupPath => ConfigPath + ".wslvision.bak";

    private static string NoFileMarker => ConfigPath + ".wslvision.none";

    public WslLimits Read() => Parse(File.Exists(ConfigPath) ? File.ReadAllLines(ConfigPath) : []);

    /// <summary>Lê os limites de um .wslconfig já carregado em linhas.</summary>
    public static WslLimits Parse(IReadOnlyList<string> lines)
    {
        var mem = Get(lines, "wsl2", "memory");
        var cpu = Get(lines, "wsl2", "processors");
        var reclaim = Get(lines, "experimental", "autoMemoryReclaim");
        return new WslLimits(
            mem is null ? null : ParseGb(mem),
            int.TryParse(cpu, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null,
            reclaim is not null && !reclaim.Equals("disabled", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Limites recomendados para o PC: um terço dos processadores lógicos (12 threads → 4) e um quarto da RAM,
    /// entre 4 e 8 GB (16 GB → 4, 32 GB → 8), nunca mais que metade. Para desenvolvimento com Docker, sobra folga
    /// para o Windows sem deixar a distro apertada. Devolver memória ociosa fica ligado.
    /// </summary>
    public static WslLimits Recommend(int totalRamGb, int logicalProcessors)
    {
        var processors = Math.Clamp((int)Math.Round(logicalProcessors / 3.0), Math.Min(2, logicalProcessors), logicalProcessors);
        var memory = Math.Min(Math.Clamp((int)Math.Round(totalRamGb / 4.0), 4, 8), Math.Max(1, totalRamGb / 2));
        return new WslLimits(memory, processors, AutoMemoryReclaim: true);
    }

    public bool HasBackup => File.Exists(BackupPath) || File.Exists(NoFileMarker);

    /// <summary>
    /// Grava os limites. Com <paramref name="previous"/> (o que foi lido), só as chaves que mudaram são reescritas:
    /// valores que o app não representa exatamente (memory=512MB, autoMemoryReclaim=dropCache) ficam como estavam.
    /// </summary>
    public void Write(WslLimits limits, WslLimits? previous = null)
    {
        var existed = File.Exists(ConfigPath);
        if (!HasBackup)
        {
            if (existed)
                File.Copy(ConfigPath, BackupPath);
            else
                File.WriteAllText(NoFileMarker, "");
        }

        var lines = Apply(existed ? File.ReadAllLines(ConfigPath) : [], limits, previous);
        File.WriteAllText(ConfigPath, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// Aplica os limites nas linhas do arquivo, mantendo comentários e chaves desconhecidas.
    /// Com <paramref name="previous"/>, só mexe nas chaves cujo valor mudou.
    /// </summary>
    public static List<string> Apply(IEnumerable<string> original, WslLimits limits, WslLimits? previous = null)
    {
        var lines = original.ToList();
        if (previous is null || limits.MemoryGb != previous.MemoryGb)
            Set(lines, "wsl2", "memory", limits.MemoryGb is { } gb ? $"{gb}GB" : null);
        if (previous is null || limits.Processors != previous.Processors)
            Set(lines, "wsl2", "processors", limits.Processors?.ToString(CultureInfo.InvariantCulture));
        if (previous is null || limits.AutoMemoryReclaim != previous.AutoMemoryReclaim)
            Set(lines, "experimental", "autoMemoryReclaim", limits.AutoMemoryReclaim ? "gradual" : "disabled");
        return lines;
    }

    /// <summary>Volta o .wslconfig para como estava antes da primeira alteração feita pelo app.</summary>
    public bool RestoreBackup()
    {
        if (File.Exists(BackupPath))
        {
            File.Copy(BackupPath, ConfigPath, overwrite: true);
            File.Delete(BackupPath);
            return true;
        }
        if (File.Exists(NoFileMarker))
        {
            File.Delete(ConfigPath);
            File.Delete(NoFileMarker);
            return true;
        }
        return false;
    }

    private static string? Get(IReadOnlyList<string> lines, string section, string key)
    {
        var current = "";
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var s = Section().Match(line);
            if (s.Success)
            {
                current = s.Groups[1].Value.Trim();
                continue;
            }
            if (!current.Equals(section, StringComparison.OrdinalIgnoreCase) || line.StartsWith('#') || line.StartsWith(';'))
                continue;
            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..].Trim();
        }
        return null;
    }

    private static void Set(List<string> lines, string section, string key, string? value)
    {
        var current = "";
        var sectionEnd = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            var s = Section().Match(line);
            if (s.Success)
            {
                current = s.Groups[1].Value.Trim();
                if (current.Equals(section, StringComparison.OrdinalIgnoreCase))
                    sectionEnd = i + 1;
                continue;
            }
            if (!current.Equals(section, StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.Length > 0)
                sectionEnd = i + 1;
            var eq = line.IndexOf('=');
            if (eq > 0 && !line.StartsWith('#') && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                if (value is null)
                    lines.RemoveAt(i);
                else
                    lines[i] = $"{key}={value}";
                return;
            }
        }
        if (value is null)
            return;
        if (sectionEnd < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
        }
        else
        {
            lines.Insert(sectionEnd, $"{key}={value}");
        }
    }

    private static int? ParseGb(string v)
    {
        var m = Size().Match(v.Trim());
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return null;
        var unit = m.Groups[2].Value.ToUpperInvariant();
        // Sem unidade, o WSL lê bytes: é assim que as Configurações do WSL gravam (memory=4294967296 = 4 GB).
        var gb = unit switch
        {
            "G" or "GB" => n,
            "" or "B" => n / 1024 / 1024 / 1024,
            _ when unit.StartsWith('K') => n / 1024 / 1024,
            _ when unit.StartsWith('M') => n / 1024,
            _ => n * 1024, // T, TB
        };
        return (int)Math.Clamp(Math.Round(gb, MidpointRounding.AwayFromZero), 1, int.MaxValue);
    }

    [GeneratedRegex(@"^\[(.+)\]$")]
    private static partial Regex Section();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*([KMGT]?B?)$", RegexOptions.IgnoreCase)]
    private static partial Regex Size();
}
