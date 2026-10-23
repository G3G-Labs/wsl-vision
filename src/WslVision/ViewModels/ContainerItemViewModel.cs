using CommunityToolkit.Mvvm.ComponentModel;
using WslVision.Models;
using WslVision.Services.Docker;

namespace WslVision.ViewModels;

public sealed partial class ContainerItemViewModel : ObservableObject
{
    public ContainerItemViewModel(ContainerSummary s) => Update(s);

    public string Id { get; private set; } = "";

    /// <summary>Serviço e dependências no Compose, para ligar e desligar na ordem certa.</summary>
    public ComposeMember Member { get; private set; } = new("", null, null, null);

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _image = "";
    [ObservableProperty] private string _uptime = "";
    [ObservableProperty] private string _portText = "—";
    [ObservableProperty] private string? _portUrl;
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _memText = "—";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _composeProject;
    [ObservableProperty] private string? _composeWorkingDir;
    [ObservableProperty] private bool _isSelected;

    /// <summary>Nome curto: o serviço do Compose ("app", "db") ou o nome do container.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFullName))]
    private string _displayName = "";

    public bool ShowFullName => DisplayName != Name;

    /// <summary>O que o leitor de tela fala ao focar a linha.</summary>
    public string AccessibleName => $"{DisplayName}, {StatusLabel}, {Uptime}, imagem {Image}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel), nameof(IsRunning), nameof(IsNotRunning))]
    private ContainerStatusKind _status;

    public double CpuPercent { get; private set; }

    public long MemoryBytes { get; private set; }

    public string StatusLabel => DockerText.Label(Status);

    public bool IsRunning => Status is ContainerStatusKind.Running or ContainerStatusKind.Restarting or ContainerStatusKind.Paused;

    public bool IsNotRunning => !IsRunning;

    public void Update(ContainerSummary s)
    {
        Id = s.Id;
        Member = ComposeRunner.Member(s);
        Name = s.Name;
        Image = s.Image;
        Status = DockerText.Classify(s.State, s.Status);
        Uptime = DockerText.Uptime(s.State, s.Status);
        ComposeProject = Member.Project;
        ComposeWorkingDir = s.Labels?.GetValueOrDefault("com.docker.compose.project.working_dir");
        var service = Member.Service;
        var number = s.Labels?.GetValueOrDefault("com.docker.compose.container-number");
        DisplayName = string.IsNullOrEmpty(service) ? s.Name : number is null or "1" ? service : $"{service} #{number}";
        OnPropertyChanged(nameof(AccessibleName));

        var published = s.Ports
            .Where(p => p.PublicPort is > 0)
            .GroupBy(p => (p.PublicPort, p.PrivatePort))
            .Select(g => g.First())
            .OrderBy(p => p.PublicPort)
            .ToList();
        if (published.Count == 0 || !IsRunning)
        {
            PortText = "—";
            PortUrl = null;
        }
        else
        {
            var first = published[0];
            PortText = $"localhost:{first.PublicPort}" + (published.Count > 1 ? $" +{published.Count - 1}" : "");
            PortUrl = first.Type == "tcp" ? $"http://localhost:{first.PublicPort}" : null;
        }

        if (!IsRunning)
        {
            CpuText = "—";
            MemText = "—";
            CpuPercent = 0;
            MemoryBytes = 0;
        }
    }

    public void SetUsage(ContainerUsage? u)
    {
        if (u is null || !IsRunning)
            return;
        CpuPercent = u.CpuPercent;
        MemoryBytes = u.MemoryBytes;
        CpuText = Formatting.Percent(u.CpuPercent);
        MemText = Formatting.Bytes(u.MemoryBytes);
    }
}

public sealed record LogLineViewModel(string Time, string Text, bool IsError);
