using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslVision.Models;
using WslVision.Services;
using WslVision.Services.Docker;
using Wpf.Ui.Controls;

namespace WslVision.ViewModels;

public sealed record ContainerUsageRow(string Name, string Cpu, string Memory, double Percent);

public sealed partial class MonitorViewModel : PageViewModel
{
    private const int HistoryLength = 30; // 30 amostras × 2 s = último minuto

    private readonly AppState _state;
    private readonly MetricsService _metrics;
    private readonly WslConfigService _wslConfig;
    private readonly DockerManager _docker;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly Notifier _notifier;
    private readonly DispatcherTimer _containersTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<string, string> _containerNames = [];
    private readonly Queue<double> _cpuHistory = new();
    private WslLimits _savedLimits = new(null, null, false);
    // O que os controles mostraram ao carregar (já com o padrão e os limites do PC aplicados).
    private WslLimits _shownLimits = new(null, null, false);
    private bool _loadingLimits;
    private int _sampleCount;

    public MonitorViewModel(AppState state, MetricsService metrics, WslConfigService wslConfig, DockerManager docker,
        SettingsService settings, DialogService dialogs, Notifier notifier)
    {
        _state = state;
        _metrics = metrics;
        _wslConfig = wslConfig;
        _docker = docker;
        _settings = settings;
        _dialogs = dialogs;
        _notifier = notifier;
        TotalRamGb = Math.Max(1, (int)Math.Round(MetricsService.GetTotalPhysicalMemory() / 1024d / 1024 / 1024));
        CpuCount = Environment.ProcessorCount;
        _containersTimer.Tick += async (_, _) => await RefreshContainerNamesAsync();
        TopContainers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoContainers));
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.IsDistroRunning) && IsActive)
                UpdateStreaming();
        };
    }

    public override string Title => "Monitor";

    public override SymbolRegular Icon => SymbolRegular.Pulse24;

    public AppState State => _state;

    public int TotalRamGb { get; }

    public int CpuCount { get; }

    public ObservableCollection<ContainerUsageRow> TopContainers { get; } = [];

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

    // Limites do .wslconfig
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryLimitText), nameof(IsDirty), nameof(IsRecommended))]
    private double _memoryLimitGb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProcessorsText), nameof(IsDirty), nameof(IsRecommended))]
    private double _processors;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(IsRecommended))]
    private bool _autoMemoryReclaim;

    [ObservableProperty]
    private bool _savedMessageVisible;

    public string MemoryLimitText => $"{MemoryLimitGb:0} GB";

    public string ProcessorsText => $"{Processors:0} de {CpuCount}";

    public string RamHint => $"Este PC tem {TotalRamGb} GB de RAM. Sem limite, o WSL pode usar até metade.";

    public bool IsDirty =>
        (int)MemoryLimitGb != _shownLimits.MemoryGb
        || (int)Processors != _shownLimits.Processors
        || AutoMemoryReclaim != _shownLimits.AutoMemoryReclaim;

    public bool CanUndoConfig => _wslConfig.HasBackup;

    private int DefaultMemoryGb => Math.Max(1, TotalRamGb / 2);

    /// <summary>Limites sugeridos para este PC (veja <see cref="WslConfigService.Recommend"/>).</summary>
    public WslLimits Recommended => WslConfigService.Recommend(TotalRamGb, CpuCount);

    public string RecommendationTitle => $"Recomendado para este PC: {Recommended.MemoryGb} GB e {Recommended.Processors} processadores";

    public string RecommendationReason =>
        $"Um terço dos {CpuCount} processadores lógicos e cerca de um quarto dos {TotalRamGb} GB de RAM (de 4 a 8 GB). "
        + "Sobra folga para o Windows, e o Docker de desenvolvimento roda bem. Devolver memória ociosa fica ligado.";

    public bool IsRecommended =>
        (int)MemoryLimitGb == Recommended.MemoryGb && (int)Processors == Recommended.Processors && AutoMemoryReclaim;

    [RelayCommand]
    private void ApplyRecommended()
    {
        var r = Recommended;
        MemoryLimitGb = r.MemoryGb ?? MemoryLimitGb;
        Processors = r.Processors ?? Processors;
        AutoMemoryReclaim = r.AutoMemoryReclaim;
        _notifier.Show(IsDirty ? "Valores recomendados aplicados. Clique em Salvar e reiniciar o WSL para valer." : "Os limites já estão no recomendado.");
    }

    public string DockerSummary => _state.IsDockerUp
        ? $"Ligado{(_state.DockerVersion is { } v ? " · versão " + v : "")}"
        : _state.IsDistroRunning ? "Desligado" : "Distro desligada";

    protected override void OnActivated()
    {
        LoadLimits();
        _state.PropertyChanged += OnStateChanged;
        _metrics.Sampled += OnSampled;
        UpdateStreaming();
    }

    protected override void OnDeactivated()
    {
        _state.PropertyChanged -= OnStateChanged;
        _metrics.Sampled -= OnSampled;
        _metrics.Unsubscribe(this);
        _containersTimer.Stop();
    }

    private void OnStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.IsDockerUp) or nameof(AppState.StatusText))
        {
            OnPropertyChanged(nameof(DockerSummary));
            if (_state.IsDockerUp)
                _ = RefreshContainerNamesAsync();
            else
            {
                _containerNames.Clear();
                TopContainers.Clear();
            }
        }
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
            _containersTimer.Start();
            _ = RefreshContainerNamesAsync();
            if (_metrics.Last is { } last)
                OnSample(last);
        }
        else
        {
            _metrics.Unsubscribe(this);
            _containersTimer.Stop();
            HasSample = false;
            TopContainers.Clear();
            VmmemText = "—";
            VmmemPercent = 0;
        }
    }

    private void OnSampled(object? sender, LiveSample s) => OnSample(s);

    private void OnSample(LiveSample sample)
    {
        if (!IsActive)
            return;
        var s = sample.Wsl;
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

        // Consumo por container vem da mesma leitura (cgroups); a API do Docker só dá os nomes.
        var rows = sample.Containers
            .Where(kv => _containerNames.ContainsKey(kv.Key))
            .OrderByDescending(kv => kv.Value.MemoryBytes)
            .Take(8)
            .ToList();
        var max = rows.Count > 0 ? Math.Max(1, rows.Max(r => r.Value.MemoryBytes)) : 1;
        // Atualiza linha a linha: só as que mudaram são redesenhadas, em vez de recriar a lista a cada 2 s.
        for (var r = 0; r < rows.Count; r++)
        {
            var (id, u) = rows[r];
            var row = new ContainerUsageRow(_containerNames[id], Formatting.Percent(u.CpuPercent), Formatting.Bytes(u.MemoryBytes), 100.0 * u.MemoryBytes / max);
            if (r >= TopContainers.Count)
                TopContainers.Add(row);
            else if (TopContainers[r] != row)
                TopContainers[r] = row;
        }
        while (TopContainers.Count > rows.Count)
            TopContainers.RemoveAt(TopContainers.Count - 1);
    }

    /// <summary>Nomes dos containers (a cada 15 s): uma chamada leve numa conexão já aberta.</summary>
    private async Task RefreshContainerNamesAsync()
    {
        if (!IsActive || !_state.IsDockerUp || _docker.Client is not { } client)
            return;
        try
        {
            var list = await client.ListContainersAsync();
            _containerNames.Clear();
            foreach (var c in list.Where(c => c.State == "running"))
                _containerNames[c.Id] = c.Labels?.GetValueOrDefault("com.docker.compose.service") is { Length: > 0 } svc
                    && c.Labels.GetValueOrDefault("com.docker.compose.project") is { Length: > 0 } proj
                        ? $"{svc} ({proj})"
                        : c.Name;
        }
        catch
        {
            // Docker parou entre uma leitura e outra; o AppState atualiza o estado.
        }
    }

    private void LoadLimits()
    {
        _loadingLimits = true;
        try
        {
            _savedLimits = _wslConfig.Read();
            MemoryLimitGb = Math.Clamp(_savedLimits.MemoryGb ?? DefaultMemoryGb, 1, TotalRamGb);
            Processors = Math.Clamp(_savedLimits.Processors ?? CpuCount, 1, CpuCount);
            AutoMemoryReclaim = _savedLimits.AutoMemoryReclaim;
            _shownLimits = new WslLimits((int)MemoryLimitGb, (int)Processors, AutoMemoryReclaim);
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
        finally
        {
            _loadingLimits = false;
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(CanUndoConfig));
        }
    }

    partial void OnMemoryLimitGbChanged(double value)
    {
        if (!_loadingLimits)
            SavedMessageVisible = false;
    }

    [RelayCommand]
    private async Task SaveLimits()
    {
        var ok = await _dialogs.ConfirmAsync("Aplicar os novos limites?",
            "Para aplicar, o WSL é desligado e ligado de novo. Containers e terminais abertos na distro param por alguns segundos.",
            "Salvar e reiniciar o WSL");
        if (!ok)
            return;
        IsBusy = true;
        try
        {
            // Controle que o usuário não mexeu mantém o valor original do arquivo (inclusive "sem limite").
            var limits = new WslLimits(
                (int)MemoryLimitGb == _shownLimits.MemoryGb ? _savedLimits.MemoryGb : (int)MemoryLimitGb,
                (int)Processors == _shownLimits.Processors ? _savedLimits.Processors : (int)Processors,
                AutoMemoryReclaim);
            _wslConfig.Write(limits, _savedLimits);
            _settings.RecordChange(ConfigChangeKind.WslConfigEdited, null);
            // Nada pode consultar a distro durante o desligamento (isso a religaria).
            _containersTimer.Stop();
            _metrics.Stop();
            var wasRunning = _state.IsDistroRunning;
            await _state.ShutdownWslAsync();
            if (wasRunning)
                await _state.StartDistroAsync();
            LoadLimits();
            SavedMessageVisible = true;
            UpdateStreaming();
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UndoLimits()
    {
        if (!await _dialogs.ConfirmAsync("Desfazer alterações no .wslconfig?",
                "O arquivo volta a ser como era antes da primeira alteração feita pelo WSL Vision. Vale na próxima vez que o WSL ligar.",
                "Desfazer"))
            return;
        try
        {
            if (_wslConfig.RestoreBackup())
            {
                _settings.Update(s => s.Changes.RemoveAll(c => c.Kind == ConfigChangeKind.WslConfigEdited));
                _notifier.Show(".wslconfig restaurado. Desligue o WSL para aplicar.");
            }
            LoadLimits();
            SavedMessageVisible = false;
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
    }

    [RelayCommand]
    private async Task ShutdownWsl()
    {
        if (!await _dialogs.ConfirmAsync("Desligar o WSL?",
                $"Todas as distros param, junto com os containers e terminais abertos nelas. Isso devolve cerca de {VmmemText} ao Windows.",
                "Desligar"))
            return;
        IsBusy = true;
        try
        {
            // Nada pode consultar a distro durante o desligamento (isso a religaria).
            _containersTimer.Stop();
            _metrics.Stop();
            await _state.ShutdownWslAsync();
            _notifier.Show("WSL desligado. A memória voltou para o Windows.");
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
        finally
        {
            IsBusy = false;
            UpdateStreaming();
        }
    }

    [RelayCommand]
    private async Task StartDistro()
    {
        IsBusy = true;
        try
        {
            await _state.StartDistroAsync();
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleDocker()
    {
        IsBusy = true;
        try
        {
            await _state.SetDockerAsync(!_state.IsDockerUp);
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(DockerSummary));
        }
    }

    [RelayCommand]
    private async Task RestartDocker()
    {
        if (_settings.Current.Distro is not { } distro)
            return;
        IsBusy = true;
        try
        {
            await _docker.RestartServiceAsync(distro);
            await _state.RefreshAsync();
            _notifier.Show("Docker reiniciado");
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
