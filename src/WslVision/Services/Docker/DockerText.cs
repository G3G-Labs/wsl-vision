using System.Text.RegularExpressions;

namespace WslVision.Services.Docker;

public enum ContainerStatusKind
{
    Running,
    Stopped,
    Error,
    Paused,
    Restarting,

    /// <summary>Só para grupos: parte dos containers do projeto está rodando.</summary>
    Partial,
}

/// <summary>Traduz o estado e o texto "Status" da API do Docker para o usuário.</summary>
public static partial class DockerText
{
    public static ContainerStatusKind Classify(string state, string status)
    {
        switch (state)
        {
            case "running":
                return ContainerStatusKind.Running;
            case "paused":
                return ContainerStatusKind.Paused;
            case "restarting":
                return ContainerStatusKind.Restarting;
            case "dead":
                return ContainerStatusKind.Error;
            case "exited":
                var m = ExitCode().Match(status);
                return m.Success && m.Groups[1].Value != "0" ? ContainerStatusKind.Error : ContainerStatusKind.Stopped;
            default:
                return ContainerStatusKind.Stopped;
        }
    }

    public static string Label(ContainerStatusKind k) => k switch
    {
        ContainerStatusKind.Running => "Rodando",
        ContainerStatusKind.Error => "Com erro",
        ContainerStatusKind.Paused => "Pausado",
        ContainerStatusKind.Restarting => "Reiniciando",
        ContainerStatusKind.Partial => "Parcial",
        _ => "Parado",
    };

    /// <summary>"Up 2 hours (healthy)" → "2 horas"; "Exited (1) 12 minutes ago" → "Saiu há 12 minutos (código 1)".</summary>
    public static string Uptime(string state, string status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return "—";

        if (status.StartsWith("Up ", StringComparison.Ordinal))
        {
            var rest = status[3..];
            var health = "";
            var paren = rest.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0)
            {
                health = rest[(paren + 2)..].TrimEnd(')') switch
                {
                    "healthy" => " · saudável",
                    "unhealthy" => " · com problema",
                    "Paused" => " · pausado",
                    var h when h.StartsWith("health: starting") => " · verificando",
                    _ => "",
                };
                rest = rest[..paren];
            }
            return Duration(rest) + health;
        }

        var exited = ExitedAgo().Match(status);
        if (exited.Success)
        {
            var code = exited.Groups[1].Value;
            var ago = Duration(exited.Groups[2].Value);
            return code == "0" ? $"Parado há {ago}" : $"Saiu há {ago} (código {code})";
        }

        if (status.StartsWith("Created", StringComparison.Ordinal))
            return "Criado, nunca iniciado";
        if (status.StartsWith("Restarting", StringComparison.Ordinal))
            return "Reiniciando…";
        return state == "dead" ? "Travado" : status;
    }

    private static string Duration(string en)
    {
        en = en.Trim();
        switch (en)
        {
            case "Less than a second": return "menos de 1 segundo";
            case "About a minute": return "cerca de 1 minuto";
            case "About an hour": return "cerca de 1 hora";
        }
        var m = Amount().Match(en);
        if (!m.Success)
            return en;
        var n = m.Groups[1].Value;
        var one = n == "1" || n == "an" || n == "a";
        var num = one ? "1" : n;
        var unit = m.Groups[2].Value.TrimEnd('s') switch
        {
            "second" => one ? "segundo" : "segundos",
            "minute" => one ? "minuto" : "minutos",
            "hour" => one ? "hora" : "horas",
            "day" => one ? "dia" : "dias",
            "week" => one ? "semana" : "semanas",
            "month" => one ? "mês" : "meses",
            "year" => one ? "ano" : "anos",
            var u => u,
        };
        return $"{num} {unit}";
    }

    [GeneratedRegex(@"^Exited \((-?\d+)\)")]
    private static partial Regex ExitCode();

    [GeneratedRegex(@"^Exited \((-?\d+)\) (.+) ago$")]
    private static partial Regex ExitedAgo();

    [GeneratedRegex(@"^(\d+|an|a) (\w+)$")]
    private static partial Regex Amount();
}
