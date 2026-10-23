using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslVision.Services;
using WslVision.Services.Docker;
using Wpf.Ui.Controls;

namespace WslVision.ViewModels;

public enum ContainerFilter
{
    All,
    Running,
    Stopped,
    Error,
}

public sealed partial class ContainersViewModel : PageViewModel
{
    private const int MaxLogLines = 5000;

    private readonly AppState _state;
    private readonly DockerManager _docker;
    private readonly DialogService _dialogs;
    private readonly Notifier _notifier;
    private readonly LauncherService _launcher;
    private readonly SettingsService _settings;
    private readonly HashSet<string> _collapsed = [];
    private readonly Dictionary<string, ContainerGroupViewModel> _groupsByKey = [];
    // A lista muda pelos eventos do Docker; este timer lento só atualiza o "Ligado há" e cobre falhas do stream.
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _eventDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly MetricsService _metrics;
    private readonly TerminalViewModel _terminal;
    private CancellationTokenSource? _eventsCts;
    private bool _refreshPending;
    private readonly DispatcherTimer _logFlushTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly ConcurrentQueue<LogLineViewModel> _pendingLogs = new();
    private CancellationTokenSource? _logCts;
    private bool _refreshing;

    public ContainersViewModel(AppState state, DockerManager docker, DialogService dialogs, Notifier notifier, LauncherService launcher, SettingsService settings, MetricsService metrics,
        TerminalViewModel terminal)
    {
        _metrics = metrics;
        _terminal = terminal;
        _state = state;
        _docker = docker;
        _dialogs = dialogs;
        _notifier = notifier;
        _launcher = launcher;
        _settings = settings;

        LogsView = CollectionViewSource.GetDefaultView(LogLines);
        LogsView.Filter = o => string.IsNullOrWhiteSpace(LogSearch)
            || (o is LogLineViewModel l && l.Text.Contains(LogSearch.Trim(), StringComparison.OrdinalIgnoreCase));

        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _eventDebounce.Tick += async (_, _) =>
        {
            _eventDebounce.Stop();
            await RefreshAsync();
        };
        _logFlushTimer.Tick += (_, _) => FlushLogs();
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.IsDockerUp) or nameof(AppState.HasCheckedOnce))
            {
                OnPropertyChanged(nameof(ShowList));
                OnPropertyChanged(nameof(ShowEmpty));
                OnPropertyChanged(nameof(IsLoading));
                if (IsActive)
                    UpdateLiveSources();
                OnPropertyChanged(nameof(ShowStartEnvironment));
            }
        };
    }

    public override string Title => "Containers";

    public override SymbolRegular Icon => SymbolRegular.Box24;

    public AppState State => _state;

    public ObservableCollection<ContainerItemViewModel> Containers { get; } = [];

    /// <summary>Containers agrupados por projeto do Docker Compose; os avulsos ficam por último.</summary>
    public ObservableCollection<ContainerGroupViewModel> Groups { get; } = [];

    public BoundedObservableCollection<LogLineViewModel> LogLines { get; } = new(MaxLogLines);

    public ICollectionView LogsView { get; }

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private ContainerFilter _filter = ContainerFilter.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(LiveLabel), nameof(IsLive))]
    private ContainerItemViewModel? _selected;

    [ObservableProperty]
    private string _logSearch = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveLabel), nameof(IsLive), nameof(PauseLabel))]
    private bool _isLogPaused;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    private bool _hasLoaded;

    public bool HasSelection => Selected is not null;

    public bool IsLive => Selected is { IsRunning: true } && !IsLogPaused;

    public string LiveLabel => Selected is not { IsRunning: true } ? "Container parado" : IsLogPaused ? "Pausado" : "Ao vivo";

    public string PauseLabel => IsLogPaused ? "Retomar" : "Pausar";

    public int CountAll => Containers.Count;

    public int CountRunning => Containers.Count(c => c.Status == ContainerStatusKind.Running);

    public int CountStopped => Containers.Count(c => c.Status == ContainerStatusKind.Stopped);

    public int CountError => Containers.Count(c => c.Status == ContainerStatusKind.Error);

    public string Subtitle => _state.IsDockerUp
        ? $"Docker Engine{(_state.DockerVersion is { } v ? " " + v : "")} na distro {_state.Distro} · {CountRunning} de {CountAll} rodando"
        : $"Docker Engine na distro {_state.Distro}";

    public bool ShowList => _state.IsDockerUp && HasLoaded && Groups.Count > 0;

    public bool IsLoading => _state.IsDockerUp && !HasLoaded;

    public bool ShowEmpty => _state.IsDockerUp && HasLoaded && Groups.Count == 0;

    /// <summary>Mostra "Iniciar ambiente" quando há algo para ligar: Docker parado ou projeto marcado com container parado.</summary>
    public bool ShowStartEnvironment => !_state.IsDockerUp || Groups.Any(g => g.IsStartup && g.AnyStopped);

    public bool IsTotallyEmpty => Containers.Count == 0;

    public string EmptyTitle => IsTotallyEmpty ? "Nenhum container por aqui" : "Nenhum container encontrado";

    public string EmptyText => IsTotallyEmpty
        ? "Suba um projeto com docker compose e ele aparece nesta lista."
        : "Nenhum container combina com a busca e o filtro atuais.";

    protected override void OnActivated()
    {
        _refreshTimer.Start();
        _metrics.Sampled += OnSampled;
        UpdateLiveSources();
        if (Selected is not null && _logCts is null)
            StartLogs(Selected);
    }

    protected override void OnDeactivated()
    {
        _refreshTimer.Stop();
        _eventDebounce.Stop();
        _metrics.Sampled -= OnSampled;
        _metrics.Unsubscribe(this);
        StopEvents();
        StopLogs();
    }

    partial void OnSearchChanged(string value) => ApplyFilter();

    partial void OnFilterChanged(ContainerFilter value) => ApplyFilter();

    partial void OnLogSearchChanged(string value) => LogsView.Refresh();

    partial void OnSelectedChanged(ContainerItemViewModel? oldValue, ContainerItemViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;
        if (newValue is not null)
            newValue.IsSelected = true;
        var value = newValue;
        StopLogs();
        LogLines.Clear();
        _pendingLogs.Clear();
        LogSearch = "";
        IsLogPaused = false;
        if (value is not null && IsActive)
            StartLogs(value);
    }

    private bool Matches(ContainerItemViewModel c)
    {
        var okFilter = Filter switch
        {
            ContainerFilter.Running => c.IsRunning,
            ContainerFilter.Stopped => c.Status == ContainerStatusKind.Stopped,
            ContainerFilter.Error => c.Status == ContainerStatusKind.Error,
            _ => true,
        };
        var q = Search.Trim();
        return okFilter && (q.Length == 0
            || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.Image.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (c.ComposeProject?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private void ApplyFilter()
    {
        // Monta os grupos a partir de todos os containers (para o resumo) e mostra só os que passam no filtro.
        var byKey = Containers
            .GroupBy(c => c.ComposeProject ?? ContainerGroupViewModel.StandaloneKey)
            .OrderBy(g => g.Key == ContainerGroupViewModel.StandaloneKey ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ordered = new List<ContainerGroupViewModel>();
        foreach (var g in byKey)
        {
            if (!_groupsByKey.TryGetValue(g.Key, out var group))
            {
                var first = g.First();
                group = new ContainerGroupViewModel(g.Key, first.ComposeProject, first.ComposeWorkingDir)
                {
                    IsExpanded = !_collapsed.Contains(g.Key),
                };
                group.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ContainerGroupViewModel.IsExpanded))
                    {
                        if (group.IsExpanded) _collapsed.Remove(group.Key);
                        else _collapsed.Add(group.Key);
                    }
                };
                _groupsByKey[g.Key] = group;
            }
            group.WorkingDir = g.Select(c => c.ComposeWorkingDir).FirstOrDefault(d => !string.IsNullOrEmpty(d));
            group.IsStartup = group.Project is { } proj && _settings.Current.StartupProjects.Contains(proj);
            group.WorkingDirLabel = group.WorkingDir is { } wd ? LinuxPaths.Pretty(wd, _settings.Current.LinuxHome) : null;
            group.All.Clear();
            group.All.AddRange(g.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase));
            group.Recompute();

            var visible = group.All.Where(Matches).ToList();
            // Buscar pelo nome do projeto mostra o projeto inteiro.
            var q = Search.Trim();
            if (visible.Count == 0 && q.Length > 0 && group.Title.Contains(q, StringComparison.OrdinalIgnoreCase) && Filter == ContainerFilter.All)
                visible = [.. group.All];
            group.SetVisible(visible);
            if (visible.Count > 0)
                ordered.Add(group);
        }

        foreach (var key in _groupsByKey.Keys.Where(k => byKey.All(g => g.Key != k)).ToList())
            _groupsByKey.Remove(key);

        for (var i = Groups.Count - 1; i >= 0; i--)
            if (!ordered.Contains(Groups[i]))
                Groups.RemoveAt(i);
        for (var i = 0; i < ordered.Count; i++)
        {
            var cur = Groups.IndexOf(ordered[i]);
            if (cur < 0)
                Groups.Insert(i, ordered[i]);
            else if (cur != i)
                Groups.Move(cur, i);
        }
        NotifyListState();
    }

    private void NotifyListState()
    {
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(IsTotallyEmpty));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CountAll));
        OnPropertyChanged(nameof(CountRunning));
        OnPropertyChanged(nameof(CountStopped));
        OnPropertyChanged(nameof(CountError));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ShowStartEnvironment));
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!IsActive || !_state.IsDockerUp || _docker.Client is not { } client)
            return;
        if (_refreshing)
        {
            _refreshPending = true;
            return;
        }
        _refreshing = true;
        try
        {
            var list = await client.ListContainersAsync();
            var byId = Containers.ToDictionary(c => c.Id);
            var seen = new HashSet<string>();
            foreach (var s in list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                seen.Add(s.Id);
                if (byId.TryGetValue(s.Id, out var existing))
                    existing.Update(s);
                else
                    Containers.Add(new ContainerItemViewModel(s));
            }
            foreach (var gone in Containers.Where(c => !seen.Contains(c.Id)).ToList())
            {
                Containers.Remove(gone);
                if (Selected == gone)
                    Selected = null;
            }
            HasLoaded = true;
            ApplyFilter();
            if (_metrics.Last is { } last)
                ApplyUsage(last);
            // O container dos logs voltou a rodar (Iniciar, Reiniciar, compose up): retoma os logs ao vivo.
            if (Selected is { IsRunning: true } sel && _logCts is null)
            {
                LogLines.Clear();
                _pendingLogs.Clear();
                StartLogs(sel);
            }
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or OperationCanceledException)
        {
            await _state.RefreshAsync();
        }
        finally
        {
            _refreshing = false;
        }
        if (_refreshPending)
        {
            _refreshPending = false;
            await RefreshAsync();
        }
    }

    /// <summary>Liga ou desliga as fontes ao vivo (eventos + métricas) conforme a aba e o Docker.</summary>
    private void UpdateLiveSources()
    {
        if (IsActive && _state.IsDockerUp && _settings.Current.Distro is { } distro)
        {
            _metrics.Subscribe(this, distro);
            _metrics.EnsureRunning(distro);
            StartEvents();
            _ = RefreshAsync();
        }
        else
        {
            _metrics.Unsubscribe(this);
            StopEvents();
        }
    }

    private void StartEvents()
    {
        if (_eventsCts is not null || _docker.Client is not { } client)
            return;
        var cts = new CancellationTokenSource();
        _eventsCts = cts;
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in client.StreamContainerEventsAsync(cts.Token))
                    await dispatcher.InvokeAsync(() =>
                    {
                        // Junta rajadas (ex.: compose up de 6 serviços) numa atualização só.
                        _eventDebounce.Stop();
                        _eventDebounce.Start();
                    });
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException or DockerApiException)
            {
                // Aba fechada ou Docker desligado.
            }
            await dispatcher.InvokeAsync(async () =>
            {
                if (_eventsCts != cts)
                    return;
                // O stream caiu sem a aba pedir: confere o estado; se o Docker voltar, os eventos religam.
                _eventsCts = null;
                cts.Dispose();
                await _state.RefreshAsync();
            });
        });
    }

    private void StopEvents()
    {
        var cts = _eventsCts;
        _eventsCts = null;
        if (cts is null)
            return;
        cts.Cancel();
        cts.Dispose();
    }

    private void OnSampled(object? sender, LiveSample s) => ApplyUsage(s);

    private void ApplyUsage(LiveSample s)
    {
        foreach (var c in Containers)
            c.SetUsage(s.Containers.GetValueOrDefault(c.Id));
        foreach (var g in Groups)
            g.Recompute();
    }

    [RelayCommand]
    private Task StartAsync(ContainerItemViewModel c) => Act(c, cl => cl.StartAsync(c.Id), $"{c.Name} iniciado");

    [RelayCommand]
    private Task StopAsync(ContainerItemViewModel c) => Act(c, cl => cl.StopAsync(c.Id), $"{c.Name} parado");

    [RelayCommand]
    private Task RestartAsync(ContainerItemViewModel c) => Act(c, cl => cl.RestartAsync(c.Id), $"{c.Name} reiniciado");

    [RelayCommand]
    private async Task DeleteAsync(ContainerItemViewModel c)
    {
        var ok = await _dialogs.ConfirmAsync(
            $"Excluir o container {c.Name}?",
            "O container será parado e removido. Imagens e volumes continuam no Docker; dados gravados fora de volumes são perdidos.",
            "Excluir", danger: true);
        if (!ok)
            return;
        if (Selected == c)
            Selected = null;
        await Act(c, cl => cl.RemoveAsync(c.Id, force: true), $"{c.Name} excluído");
    }

    [RelayCommand]
    private void ShowLogs(ContainerItemViewModel c) => Selected = Selected == c ? null : c;

    [RelayCommand]
    private async Task StartEnvironmentAsync()
    {
        try
        {
            _notifier.Show(await _state.StartEnvironmentAsync());
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
        OnPropertyChanged(nameof(ShowStartEnvironment));
    }

    // ---- Ações no projeto inteiro (Compose) ----

    [RelayCommand]
    private void ToggleStartup(ContainerGroupViewModel g)
    {
        if (g.Project is not { } project)
            return;
        var on = !_settings.Current.StartupProjects.Contains(project);
        _settings.Update(s =>
        {
            s.StartupProjects.Remove(project);
            if (on)
                s.StartupProjects.Add(project);
        });
        g.IsStartup = on;
        OnPropertyChanged(nameof(ShowStartEnvironment));
        _notifier.Show(on ? $"{project} vai subir junto no Iniciar ambiente" : $"{project} não sobe mais junto no Iniciar ambiente");
    }

    [RelayCommand]
    private void ToggleGroup(ContainerGroupViewModel g) => g.IsExpanded = !g.IsExpanded;

    [RelayCommand]
    private Task StartGroupAsync(ContainerGroupViewModel g) =>
        ActOnGroup(g, g.All.Where(c => !c.IsRunning), ComposeRunner.StartAsync, $"{g.Title}: containers iniciados");

    [RelayCommand]
    private Task StopGroupAsync(ContainerGroupViewModel g) =>
        ActOnGroup(g, g.All.Where(c => c.IsRunning), ComposeRunner.StopAsync, $"{g.Title}: containers parados");

    [RelayCommand]
    private Task RestartGroupAsync(ContainerGroupViewModel g) =>
        ActOnGroup(g, g.All.Where(c => c.IsRunning), ComposeRunner.RestartAsync, $"{g.Title}: containers reiniciados");

    [RelayCommand]
    private void OpenGroupInVsCode(ContainerGroupViewModel g)
    {
        if (_settings.Current.Distro is not { } distro || g.WorkingDir is not { } dir)
            return;
        try
        {
            _launcher.OpenInVsCode(distro, dir);
            _notifier.Show($"Abrindo {g.Title} no VS Code");
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
    }

    [RelayCommand]
    private async Task OpenGroupTerminalAsync(ContainerGroupViewModel g)
    {
        if (_settings.Current.Distro is not { } distro || g.WorkingDir is not { } dir)
            return;
        try
        {
            await _terminal.OpenSessionAsync(dir, title: g.Title);
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
    }

    private async Task ActOnGroup(ContainerGroupViewModel g, IEnumerable<ContainerItemViewModel> targets,
        Func<DockerClient, IReadOnlyList<ComposeMember>, CancellationToken, Task> action, string done)
    {
        if (_docker.Client is not { } client)
            return;
        var list = targets.ToList();
        if (list.Count == 0)
            return;
        g.IsBusy = true;
        foreach (var c in list)
            c.IsBusy = true;
        try
        {
            // Na ordem do depends_on; containers da mesma onda vão em paralelo.
            await action(client, list.Select(c => c.Member).ToList(), CancellationToken.None);
            _notifier.Show(done);
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
        finally
        {
            foreach (var c in list)
                c.IsBusy = false;
            g.IsBusy = false;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private void CloseLogs() => Selected = null;

    [RelayCommand]
    private void OpenPort(ContainerItemViewModel c)
    {
        if (c.PortUrl is { } url)
            LauncherService.OpenUrl(url);
    }

    [RelayCommand]
    private void ToggleLogPause()
    {
        IsLogPaused = !IsLogPaused;
        if (!IsLogPaused)
            FlushLogs();
    }

    [RelayCommand]
    private void CopyLogs()
    {
        var sb = new StringBuilder();
        foreach (var l in LogsView.Cast<LogLineViewModel>())
            sb.Append(l.Time).Append("  ").AppendLine(l.Text);
        try
        {
            LauncherService.CopyText(sb.ToString());
            _notifier.Show("Logs copiados");
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        Search = "";
        Filter = ContainerFilter.All;
    }

    [RelayCommand]
    private async Task ToggleDockerAsync()
    {
        try
        {
            IsBusy = true;
            await _state.SetDockerAsync(!_state.IsDockerUp);
            _notifier.Show(_state.IsDockerUp ? "Docker ligado" : "Docker desligado");
            if (!_state.IsDockerUp)
            {
                Containers.Clear();
                Selected = null;
                HasLoaded = false;
            }
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
    private async Task StartDistroAsync()
    {
        try
        {
            IsBusy = true;
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

    private async Task Act(ContainerItemViewModel c, Func<DockerClient, Task> action, string done)
    {
        if (_docker.Client is not { } client)
            return;
        c.IsBusy = true;
        try
        {
            await action(client);
            _notifier.Show(done);
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
        finally
        {
            c.IsBusy = false;
        }
        await RefreshAsync();
        if (Selected == c && c.IsRunning && _logCts is null)
            StartLogs(c);
    }

    private void StartLogs(ContainerItemViewModel c)
    {
        if (_docker.Client is not { } client)
            return;
        StopLogs();
        var cts = new CancellationTokenSource();
        _logCts = cts;
        _logFlushTimer.Start();
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in client.StreamLogsAsync(c.Id, tail: 500, cts.Token))
                {
                    // Trocou de container: linhas que ainda estavam no buffer não podem aparecer no novo.
                    if (cts.IsCancellationRequested)
                        break;
                    var isErr = line.IsStdErr
                        || line.Text.Contains("error", StringComparison.OrdinalIgnoreCase)
                        || line.Text.Contains("exception", StringComparison.OrdinalIgnoreCase);
                    _pendingLogs.Enqueue(new LogLineViewModel(line.Time?.ToString("HH:mm:ss") ?? "", line.Text, isErr));
                    // Pausado não pode virar vazamento: guarda no máximo as últimas MaxLogLines linhas.
                    while (_pendingLogs.Count > MaxLogLines && _pendingLogs.TryDequeue(out _))
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException or DockerApiException)
            {
                // Container parou, Docker desligou ou o painel de logs foi fechado.
            }
            // Stream acabou sozinho (ex.: container parou): libera para religar quando ele voltar.
            await dispatcher.InvokeAsync(() =>
            {
                if (_logCts != cts)
                    return;
                FlushLogs();
                _logFlushTimer.Stop();
                _logCts = null;
                cts.Dispose();
                OnPropertyChanged(nameof(LiveLabel));
            });
        });
    }

    private void StopLogs()
    {
        _logCts?.Cancel();
        _logCts?.Dispose();
        _logCts = null;
        _logFlushTimer.Stop();
        FlushLogs();
    }

    private void FlushLogs()
    {
        if (IsLogPaused || _pendingLogs.IsEmpty)
            return;
        var batch = new List<LogLineViewModel>();
        while (_pendingLogs.TryDequeue(out var l))
            batch.Add(l);
        LogLines.AddRange(batch);
        LogsAppended?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A view rola para o fim quando chegam linhas novas.</summary>
    public event EventHandler? LogsAppended;
}
