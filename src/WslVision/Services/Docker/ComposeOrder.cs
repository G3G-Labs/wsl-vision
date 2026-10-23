namespace WslVision.Services.Docker;

/// <summary>Um container visto pelo Compose: projeto, serviço e o label com.docker.compose.depends_on.</summary>
public sealed record ComposeMember(string Id, string? Project, string? Service, string? DependsOn);

public sealed record ComposeDependency(string Service, string Condition);

/// <summary>Ordem de subida do Compose (depends_on), calculada a partir dos labels dos containers.</summary>
public static class ComposeOrder
{
    public const string ProjectLabel = "com.docker.compose.project";
    public const string ServiceLabel = "com.docker.compose.service";
    public const string DependsOnLabel = "com.docker.compose.depends_on";

    /// <summary>"redis:service_started:false,db:service_healthy:true" → [(redis, service_started), (db, service_healthy)].</summary>
    public static IReadOnlyList<ComposeDependency> ParseDependsOn(string? label) =>
        string.IsNullOrWhiteSpace(label)
            ? []
            : label.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(d => d.Split(':'))
                .Where(p => p[0].Length > 0)
                .Select(p => new ComposeDependency(p[0], p.Length > 1 && p[1].Length > 0 ? p[1] : "service_started"))
                .ToList();

    /// <summary>
    /// Separa em ondas: cada onda só depende das anteriores ou de serviços fora da lista (que já estão de pé).
    /// Um ciclo, que o Compose não aceita, vai inteiro para a última onda em vez de travar.
    /// </summary>
    public static List<List<ComposeMember>> Waves(IReadOnlyList<ComposeMember> members)
    {
        var present = members.Where(m => m.Service is not null).Select(m => (m.Project, m.Service!)).ToHashSet();
        var placed = new HashSet<(string?, string)>();
        var pending = members.ToList();
        var waves = new List<List<ComposeMember>>();
        while (pending.Count > 0)
        {
            var wave = pending
                .Where(m => ParseDependsOn(m.DependsOn).All(d => !present.Contains((m.Project, d.Service)) || placed.Contains((m.Project, d.Service))))
                .ToList();
            if (wave.Count == 0)
                wave = pending.ToList();
            waves.Add(wave);
            foreach (var m in wave)
            {
                pending.Remove(m);
                if (m.Service is not null)
                    placed.Add((m.Project, m.Service));
            }
        }
        return waves;
    }
}
