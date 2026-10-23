using WslVision.Models;

namespace WslVision.Services.Docker;

/// <summary>
/// Liga, para e reinicia containers respeitando o depends_on do Compose, como o "docker compose start/stop".
/// A API do Docker não conhece essa ordem, e o restart padrão do Compose ("no") não salva quem subiu antes do banco.
/// </summary>
public static class ComposeRunner
{
    private static readonly TimeSpan ConditionTimeout = TimeSpan.FromSeconds(90);

    public static ComposeMember Member(ContainerSummary c) => new(
        c.Id,
        c.Labels?.GetValueOrDefault(ComposeOrder.ProjectLabel),
        c.Labels?.GetValueOrDefault(ComposeOrder.ServiceLabel),
        c.Labels?.GetValueOrDefault(ComposeOrder.DependsOnLabel));

    /// <summary>Liga em ondas. Antes de cada onda, espera as dependências com condition service_healthy ou service_completed_successfully.</summary>
    public static async Task StartAsync(DockerClient client, IReadOnlyList<ComposeMember> targets, CancellationToken ct = default)
    {
        foreach (var wave in ComposeOrder.Waves(targets))
        {
            await WaitForConditionsAsync(client, wave, ct);
            await Task.WhenAll(wave.Select(m => client.StartAsync(m.Id, ct)));
        }
    }

    /// <summary>Para na ordem inversa: quem depende de um serviço sai antes dele.</summary>
    public static async Task StopAsync(DockerClient client, IReadOnlyList<ComposeMember> targets, CancellationToken ct = default)
    {
        var waves = ComposeOrder.Waves(targets);
        waves.Reverse();
        foreach (var wave in waves)
            await Task.WhenAll(wave.Select(m => client.StopAsync(m.Id, ct)));
    }

    /// <summary>Reinicia na ordem de subida, esperando as mesmas condições do StartAsync.</summary>
    public static async Task RestartAsync(DockerClient client, IReadOnlyList<ComposeMember> targets, CancellationToken ct = default)
    {
        foreach (var wave in ComposeOrder.Waves(targets))
        {
            await WaitForConditionsAsync(client, wave, ct);
            await Task.WhenAll(wave.Select(m => client.RestartAsync(m.Id, ct)));
        }
    }

    private static async Task WaitForConditionsAsync(DockerClient client, IReadOnlyList<ComposeMember> wave, CancellationToken ct)
    {
        var needed = wave
            .SelectMany(m => ComposeOrder.ParseDependsOn(m.DependsOn)
                .Where(d => d.Condition is "service_healthy" or "service_completed_successfully")
                .Select(d => (m.Project, d.Service, d.Condition)))
            .ToHashSet();
        if (needed.Count == 0)
            return;

        var deadline = DateTime.UtcNow + ConditionTimeout;
        while (true)
        {
            var all = await client.ListContainersAsync(ct);
            var waiting = false;
            foreach (var (project, service, condition) in needed)
            {
                foreach (var c in all.Where(c => c.Labels?.GetValueOrDefault(ComposeOrder.ProjectLabel) == project
                                                 && c.Labels?.GetValueOrDefault(ComposeOrder.ServiceLabel) == service))
                {
                    switch (IsSatisfied(c, condition))
                    {
                        case false:
                            throw new DockerApiException(condition == "service_healthy"
                                ? $"{c.Name} não ficou saudável, e os serviços que dependem dele não foram iniciados."
                                : $"{c.Name} terminou com erro, e os serviços que dependem dele não foram iniciados.");
                        case null:
                            waiting = true;
                            break;
                    }
                }
            }
            if (!waiting)
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"As dependências do projeto não ficaram prontas em {ConditionTimeout.TotalSeconds:0} segundos.");
            await Task.Delay(1000, ct);
        }
    }

    /// <summary>true = pronto; false = não vai ficar pronto; null = ainda esperando.</summary>
    private static bool? IsSatisfied(ContainerSummary c, string condition) => condition switch
    {
        "service_healthy" => c.Status.Contains("(healthy)", StringComparison.Ordinal) ? true
            : c.Status.Contains("(unhealthy)", StringComparison.Ordinal) || c.State is "exited" or "dead" ? false
            : null,
        "service_completed_successfully" => c.State == "exited" ? c.Status.StartsWith("Exited (0)", StringComparison.Ordinal)
            : c.State == "dead" ? false
            : null,
        _ => true,
    };
}
