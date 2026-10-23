using System.Text.Json.Serialization;

namespace WslVision.Models;

// Só os campos da Docker Engine API que o app usa.

public sealed class ContainerSummary
{
    [JsonPropertyName("Id")] public string Id { get; set; } = "";
    [JsonPropertyName("Names")] public List<string> Names { get; set; } = [];
    [JsonPropertyName("Image")] public string Image { get; set; } = "";
    [JsonPropertyName("State")] public string State { get; set; } = "";
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
    [JsonPropertyName("Created")] public long Created { get; set; }
    [JsonPropertyName("Ports")] public List<ContainerPort> Ports { get; set; } = [];
    [JsonPropertyName("Labels")] public Dictionary<string, string>? Labels { get; set; }

    public string Name => Names.Count > 0 ? Names[0].TrimStart('/') : Id[..Math.Min(12, Id.Length)];
}

public sealed class ContainerPort
{
    [JsonPropertyName("IP")] public string? Ip { get; set; }
    [JsonPropertyName("PrivatePort")] public int PrivatePort { get; set; }
    [JsonPropertyName("PublicPort")] public int? PublicPort { get; set; }
    [JsonPropertyName("Type")] public string Type { get; set; } = "tcp";
}

public sealed class DockerVersion
{
    [JsonPropertyName("Version")] public string Version { get; set; } = "";
    [JsonPropertyName("ApiVersion")] public string ApiVersion { get; set; } = "";
}

public sealed class ContainerInspect
{
    [JsonPropertyName("Config")] public ContainerConfig? Config { get; set; }
}

public sealed class ContainerConfig
{
    [JsonPropertyName("Tty")] public bool Tty { get; set; }
}

/// <summary>Resposta de GET /system/df: o que o Docker ocupa e quanto daria para liberar.</summary>
public sealed class DockerDiskUsage
{
    [JsonPropertyName("LayersSize")] public long LayersSize { get; set; }
    [JsonPropertyName("Images")] public List<DfImage>? Images { get; set; }
    [JsonPropertyName("Containers")] public List<DfContainer>? Containers { get; set; }
    [JsonPropertyName("Volumes")] public List<DfVolume>? Volumes { get; set; }
    [JsonPropertyName("BuildCache")] public List<DfBuildCache>? BuildCache { get; set; }

    /// <summary>Mesma conta do "docker system df": camadas menos o que as imagens em uso ocupam sozinhas.</summary>
    public long ReclaimableImages =>
        Math.Max(0, LayersSize - (Images ?? [])
            .Where(i => i.Containers > 0 && i.Size >= 0 && i.SharedSize >= 0)
            .Sum(i => i.Size - i.SharedSize));

    public long ReclaimableContainers => (Containers ?? []).Where(c => c.State != "running").Sum(c => Math.Max(0, c.SizeRw));

    public long ReclaimableVolumes => (Volumes ?? []).Where(v => v.UsageData is { RefCount: 0 }).Sum(v => Math.Max(0, v.UsageData!.Size));

    public long ReclaimableBuildCache => (BuildCache ?? []).Where(b => !b.InUse && !b.Shared).Sum(b => Math.Max(0, b.Size));
}

public sealed class DfImage
{
    [JsonPropertyName("Size")] public long Size { get; set; }
    [JsonPropertyName("SharedSize")] public long SharedSize { get; set; }
    [JsonPropertyName("Containers")] public long Containers { get; set; }
}

public sealed class DfContainer
{
    [JsonPropertyName("SizeRw")] public long SizeRw { get; set; }
    [JsonPropertyName("State")] public string State { get; set; } = "";
}

public sealed class DfVolume
{
    [JsonPropertyName("UsageData")] public DfVolumeUsage? UsageData { get; set; }
}

public sealed class DfVolumeUsage
{
    [JsonPropertyName("Size")] public long Size { get; set; }
    [JsonPropertyName("RefCount")] public long RefCount { get; set; }
}

public sealed class DfBuildCache
{
    [JsonPropertyName("Size")] public long Size { get; set; }
    [JsonPropertyName("InUse")] public bool InUse { get; set; }
    [JsonPropertyName("Shared")] public bool Shared { get; set; }
}

public sealed record ContainerUsage(double CpuPercent, long MemoryBytes);

public sealed record DockerLogLine(DateTimeOffset? Time, string Text, bool IsStdErr);

/// <summary>Um evento de container vindo do fluxo /events do Docker.</summary>
public sealed record ContainerEvent(string Action, string Name, string? Service, string? Project, string? ExitCode, DateTimeOffset Time);
