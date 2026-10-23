using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WslVision.Services;
using Wpf.Ui.Controls;

namespace WslVision.ViewModels;

public sealed partial class MonitorViewModel : PageViewModel
{
    private const int HistoryLength = 30; // 30 amostras × 2 s = último minuto

    private readonly AppState _state;
    private readonly MetricsService _metrics;
    private readonly SettingsService _settings;
    private readonly Queue<double> _cpuHistory = new();
    private int _sampleCount;

    public MonitorViewModel(AppState state, MetricsService metrics, SettingsService settings, ContainerInventory inventory, EnvironmentViewModel env)
    {
        Inventory = inventory;
        Env = env;
        env.PropertyChanged += (_, _) => OnPropertyChanged(nameof(EnvSubtitle));
        inventory.Changed += (_, _) => UpdateTop();
        inventory.UsageChanged += (_, _) => UpdateTop();
        _state = state;
        _metrics = metrics;
        _settings = settings;
        TotalRamGb = Math.Max(1, (int)Math.Round(MetricsService.GetTotalPhysicalMemory() / 1024d / 1024 / 1024));
        TopContainers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoContainers));
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.IsDistroRunning) && IsActive)
                UpdateStreaming();
        };
    }

    public override string Title => "Visão geral";

    public override SymbolRegular Icon => SymbolRegular.Board24;

    // ---- Ambiente (estado e ações vêm do EnvironmentViewModel) e projetos ----

    public EnvironmentViewModel Env { get; }

    public ContainerInventory Inventory { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnvSubtitle))]
    private double _uptimeSeconds;

    /// <summary>A frase do ambiente, com há quanto tempo a distro está ligada na frente.</summary>
    public string EnvSubtitle => !Env.IsStarting && _state.IsDistroRunning && UptimeSeconds > 0
        ? $"Ligado há {Formatting.Duration(UptimeSeconds)} · {Env.Detail}"
        : Env.Detail;

    public override IEnumerable<PaletteItem> PaletteItems() => Env.PaletteItems();

    public AppState State => _state;

    public int TotalRamGb { get; }

    public ObservableCollection<ContainerItemViewModel> TopContainers { get; } = [];

    public bool HasNoContainers => TopContainers.Count == 0;

    [ObservableProperty] private string _memUsedText = "—";
    [ObservableProperty] private string _memTotalText = "";
    [ObservableProperty] private double _memPercent;
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private PointCollection _cpuPoints = [];
    [ObservableProperty] private string _diskUsedText = "—";
    [ObservableProperty] private string _diskSizeText = "";
    [ObservableProperty] private double _diskPercent;
    [ObservableProperty] private string _vmmemText = "—";
    [ObservableProperty] private double _vmmemPercent;
    [ObservableProperty] private bool _hasSample;

    protected override void OnActivated()
    {
        Inventory.Acquire(this);
        _metrics.Sampled += OnSampled;
        UpdateStreaming();
        UpdateTop();
    }

    protected override void OnDeactivated()
    {
        Inventory.Release(this);
        _metrics.Sampled -= OnSampled;
        _metrics.Unsubscribe(this);
    }

    private void UpdateStreaming()
    {
        if (_state.IsDistroRunning && _settings.Current.Distro is { } distro)
        {
            if (!_metrics.IsRunning)
                _cpuHistory.Clear();
            _sampleCount = 0;
            _metrics.Subscribe(this, distro);
            _metrics.EnsureRunning(distro);
            if (_metrics.Last is { } last)
                OnSample(last);
        }
        else
        {
            _metrics.Unsubscribe(this);
            HasSample = false;
            VmmemText = "—";
            VmmemPercent = 0;
        }
    }

    /// <summary>Quem mais usa memória: os containers do inventário, que já trazem o consumo da última amostra.</summary>
    private void UpdateTop()
    {
        if (!IsActive)
            return;
        TopContainers.SyncTo(Inventory.Containers
            .Where(c => c.IsRunning && c.MemoryBytes > 0)
            .OrderByDescending(c => c.MemoryBytes)
            .Take(8)
            .ToList());
    }

    private void OnSampled(object? sender, LiveSample s) => OnSample(s);

    private void OnSample(LiveSample sample)
    {
        if (!IsActive)
            return;
        var s = sample.Wsl;
        UptimeSeconds = s.UptimeSeconds;
        HasSample = true;
        MemUsedText = Formatting.Gb(s.MemUsedBytes);
        MemTotalText = $"de {Formatting.Gb(s.MemTotalBytes)}";
        MemPercent = s.MemTotalBytes > 0 ? 100.0 * s.MemUsedBytes / s.MemTotalBytes : 0;

        CpuText = Formatting.Percent(s.CpuPercent);
        _cpuHistory.Enqueue(s.CpuPercent);
        while (_cpuHistory.Count > HistoryLength)
            _cpuHistory.Dequeue();
        var pts = new PointCollection();
        var i = 0;
        var offset = HistoryLength - _cpuHistory.Count;
        foreach (var v in _cpuHistory)
            pts.Add(new Point((offset + i++) * 4.0, 40 - Math.Clamp(v, 0, 100) * 0.4));
        pts.Freeze();
        CpuPoints = pts;

        if (s.DiskSizeBytes > 0)
        {
            DiskUsedText = Formatting.Gb(s.DiskUsedBytes);
            DiskSizeText = $"Disco virtual da distro · {Formatting.Gb(s.DiskSizeBytes)} no total";
            DiskPercent = 100.0 * s.DiskUsedBytes / s.DiskSizeBytes;
        }

        // Achar o vmmem percorre todos os processos do Windows: a cada 10 s basta (a primeira amostra já mostra).
        if (_sampleCount++ % 5 == 0 && MetricsService.GetVmmemBytes() is { } vm)
        {
            VmmemText = Formatting.Gb(vm);
            VmmemPercent = 100.0 * vm / (TotalRamGb * 1024d * 1024 * 1024);
        }
    }
}
