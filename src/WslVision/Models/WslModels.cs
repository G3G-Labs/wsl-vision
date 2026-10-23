namespace WslVision.Models;

public sealed record DistroInfo(string Name, bool IsDefault, bool IsRunning, int Version)
{
    /// <summary>Distros internas do Docker Desktop não podem ser escolhidas.</summary>
    public bool IsDockerDesktopInternal => Name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase);
}

public enum LinuxEntryType
{
    Directory,
    File,
    Other,
}

public sealed record LinuxFileEntry(
    string Name,
    string FullPath,
    LinuxEntryType Type,
    bool IsSymlink,
    long Size,
    DateTimeOffset Modified,
    string Owner);

public sealed record DirectoryListing(
    string Path,
    IReadOnlyList<LinuxFileEntry> Entries,
    bool Truncated,
    bool Writable);

public sealed record WslMetricsSample(
    long MemTotalBytes,
    long MemAvailableBytes,
    double CpuPercent,
    long DiskUsedBytes,
    long DiskSizeBytes)
{
    public long MemUsedBytes => Math.Max(0, MemTotalBytes - MemAvailableBytes);
}

public sealed record SetupStatus(
    string User,
    string Home,
    bool DockerInstalled,
    bool SystemdActive,
    bool DockerEnabled,
    bool DockerActive,
    bool UserInDockerGroup);
