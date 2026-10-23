using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WslVision.Services.Docker;

namespace WslVision.ViewModels;

/// <summary>Containers de um mesmo projeto do Docker Compose (ou os avulsos, sem projeto).</summary>
public sealed partial class ContainerGroupViewModel(string key, string? project, string? workingDir) : ObservableObject
{
    public const string StandaloneKey = "\0avulsos";

    public string Key { get; } = key;

    /// <summary>Nome do projeto Compose; null para o grupo de containers avulsos.</summary>
    public string? Project { get; } = project;

    public bool IsCompose => Project is not null;

    public string Title => Project ?? "Containers avulsos";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkingDir))]
    private string? _workingDir = workingDir;

    /// <summary>Pasta do projeto com "~" no lugar da home.</summary>
    [ObservableProperty]
    private string? _workingDirLabel;

    public bool HasWorkingDir => IsCompose && !string.IsNullOrEmpty(WorkingDir);

    /// <summary>O projeto sobe junto no "Iniciar ambiente".</summary>
    [ObservableProperty]
    private bool _isStartup;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Containers visíveis (já filtrados pela busca e pelo filtro de status).</summary>
    public ObservableCollection<ContainerItemViewModel> Items { get; } = [];

    /// <summary>Todos os containers do projeto, para o resumo e as ações em lote.</summary>
    public List<ContainerItemViewModel> All { get; } = [];

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _memText = "—";
    [ObservableProperty] private ContainerStatusKind _status;
    [ObservableProperty] private string _statusLabel = "";
    [ObservableProperty] private bool _anyRunning;
    [ObservableProperty] private bool _anyStopped;

    /// <summary>Sincroniza a lista visível sem recriar as linhas (evita piscar e perder o hover).</summary>
    public void SetVisible(IReadOnlyList<ContainerItemViewModel> visible) => Items.SyncTo(visible);

    public void Recompute()
    {
        var running = All.Count(c => c.Status == ContainerStatusKind.Running);
        var errors = All.Count(c => c.Status == ContainerStatusKind.Error);
        AnyRunning = All.Any(c => c.IsRunning);
        AnyStopped = All.Any(c => !c.IsRunning);

        Summary = All.Count == 1 ? "1 container" : $"{All.Count} containers";
        (Status, StatusLabel) =
            errors > 0 ? (ContainerStatusKind.Error, errors == 1 ? "1 com erro" : $"{errors} com erro")
            : running == All.Count ? (ContainerStatusKind.Running, "Todos rodando")
            : running == 0 ? (ContainerStatusKind.Stopped, "Parado")
            : (ContainerStatusKind.Partial, $"{running} de {All.Count} rodando");

        var live = All.Where(c => c.IsRunning && c.MemoryBytes > 0).ToList();
        CpuText = live.Count == 0 ? "—" : Formatting.Percent(live.Sum(c => c.CpuPercent));
        MemText = live.Count == 0 ? "—" : Formatting.Bytes(live.Sum(c => c.MemoryBytes));
    }
}
