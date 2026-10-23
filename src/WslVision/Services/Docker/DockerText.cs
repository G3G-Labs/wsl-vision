using System.Text.RegularExpressions;
using WslVision.Models;

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

    public static Tone ToneOf(ContainerStatusKind k) => k switch
    {
        ContainerStatusKind.Running => Tone.Ok,
        ContainerStatusKind.Error => Tone.Error,
        ContainerStatusKind.Paused or ContainerStatusKind.Restarting or ContainerStatusKind.Partial => Tone.Work,
        _ => Tone.Off,
    };

    /// <summary>
    /// Um evento do Docker numa frase para a Atividade ("db iniciado · obraclick"). Null para os que só fazem barulho
    /// (kill, update, saída normal com 0, 137 ou 143).
    /// </summary>
    public static (string Text, Tone Tone)? Describe(ContainerEvent e)
    {
        var who = string.IsNullOrEmpty(e.Service) ? e.Name : e.Service;
        (string Text, Tone Tone)? d = e.Action switch
        {
            "start" => ($"{who} iniciado", Tone.Ok),
            "restart" => ($"{who} reiniciado", Tone.Ok),
            "stop" => ($"{who} parado", Tone.Off),
            "die" when e.ExitCode is not (null or "0" or "143" or "137") => ($"{who} saiu com erro (código {e.ExitCode})", Tone.Error),
            "oom" => ($"{who} ficou sem memória", Tone.Error),
            "destroy" => ($"{who} removido", Tone.Off),
            "create" => ($"{who} criado", Tone.Off),
            "pause" => ($"{who} pausado", Tone.Work),
            "unpause" => ($"{who} retomado", Tone.Ok),
            var a when a.EndsWith(": healthy", StringComparison.Ordinal) => ($"{who} ficou saudável", Tone.Ok),
            var a when a.EndsWith(": unhealthy", StringComparison.Ordinal) => ($"{who} com problema no healthcheck", Tone.Work),
            _ => null,
        };
        return d is { } x && e.Project is { } p && !string.IsNullOrEmpty(e.Service) ? ($"{x.Text} · {p}", x.Tone) : d;
    }

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
            case "About a minute": return "1 minuto";
            case "About an hour": return "1 hora";
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
