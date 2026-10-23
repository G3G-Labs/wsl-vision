using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslVision.Models;
using WslVision.Services;
using WslVision.Services.Docker;

namespace WslVision.ViewModels;

/// <summary>
/// Os containers e os projetos do Docker, mantidos ao vivo enquanto alguma tela precisa deles (aba Containers,
/// Visão geral, painel da bandeja). É a única fonte da lista: uma conexão de eventos, uma atualização lenta e
/// o consumo vindo do <see cref="MetricsService"/>, compartilhados por todas as telas.
/// </summary>
public sealed partial class ContainerInventory : ObservableObject
{
    private readonly AppState _state;
    private readonly DockerManager _docker;
    private readonly MetricsService _metrics;
    private readonly SettingsService _settings;
    private readonly Notifier _notifier;
    private readonly LauncherService _launcher;
    private readonly ActivityLog _activity;
    private readonly HashSet<object> _users = [];
    private readonly HashSet<string> _collapsed = [];
    private readonly Dictionary<string, ContainerGroupViewModel> _groupsByKey = [];
    // A lista muda pelos eventos do Docker; este timer lento só atualiza o "Ligado há" e cobre falhas do stream.
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _eventDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private CancellationTokenSource? _eventsCts;
    private readonly RetryBackoff _eventsBackoff = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    private bool _refreshing;
    private bool _refreshPending;

    public ContainerInventory(AppState state, DockerManager docker, MetricsService metrics, SettingsService settings, Notifier notifier,
        LauncherService launcher, ActivityLog activity)
    {
        _launcher = launcher;
        _state = state;
        _docker = docker;
        _metrics = metrics;
        _settings = settings;
        _notifier = notifier;
        _activity = activity;
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _eventDebounce.Tick += async (_, _) =>
        {
            _eventDebounce.Stop();
            await RefreshAsync();
        };
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AppState.IsDockerUp))
                return;
            if (!state.IsDockerUp)
                Clear();
            if (IsLive)
                UpdateSources();
        };
    }

    public ObservableCollection<ContainerItemViewModel> Containers { get; } = [];

    /// <summary>Todos os projetos do Compose em ordem alfabética; os containers avulsos ficam por último.</summary>
    public ObservableCollection<ContainerGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    private bool _hasLoaded;

    /// <summary>A lista mudou (containers entraram, saíram ou mudaram de estado).</summary>
    public event EventHandler? Changed;

    /// <summary>Chegou uma amostra de consumo (CPU e memória por container), a cada ~2 s.</summary>
    public event EventHandler? UsageChanged;

    public bool IsLive => _users.Count > 0;

    public int RunningCount => Containers.Count(c => c.IsRunning);

    public int ProjectCount => Groups.Count(g => g.IsCompose);

    public bool AnyProjectRunning => Groups.Any(g => g.AnyRunning);

    /// <summary>Um projeto marcado para subir com o ambiente está parado: o "Iniciar ambiente" ainda tem o que fazer.</summary>
    public bool HasStoppedStartupProject => Groups.Any(g => g.IsStartup && g.AnyStopped);

    /// <summary>"1 de 2": projetos com algo rodando, de todos os projetos do Compose.</summary>
    public string ProjectsSummary
    {
        get
        {
            var compose = Groups.Where(g => g.IsCompose).ToList();
            return compose.Count == 0 ? "nenhum" : $"{compose.Count(g => g.AnyRunning)} de {compose.Count}";
        }
    }

    /// <summary>Uma tela passou a mostrar os containers. A lista fica ao vivo enquanto houver alguma.</summary>
    public void Acquire(object user)
    {
        if (_users.Add(user) && _users.Count == 1)
        {
            _refreshTimer.Start();
            _metrics.Sampled += OnSampled;
            UpdateSources();
        }
    }

    public void Release(object user)
    {
        if (!_users.Remove(user) || _users.Count > 0)
            return;
        _refreshTimer.Stop();
        _eventDebounce.Stop();
        _metrics.Sampled -= OnSampled;
        _metrics.Unsubscribe(this);
        StopEvents();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!IsLive || !_state.IsDockerUp || _docker.Client is not { } client)
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
                Containers.Remove(gone);
            HasLoaded = true;
            if (_metrics.Last is { } last)
                ApplyUsage(last);
            RebuildGroups();
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

    /// <summary>O Docker desligou: a lista antiga não vale mais.</summary>
    private void Clear()
    {
        Containers.Clear();
        HasLoaded = false;
        RebuildGroups();
    }

    private void RebuildGroups()
    {
        var byKey = Containers
            .GroupBy(c => c.ComposeProject ?? ContainerGroupViewModel.StandaloneKey)
            .OrderBy(g => g.Key == ContainerGroupViewModel.StandaloneKey ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groups = new List<ContainerGroupViewModel>();
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
            groups.Add(group);
        }
        foreach (var key in _groupsByKey.Keys.Where(k => byKey.All(g => g.Key != k)).ToList())
            _groupsByKey.Remove(key);

        Groups.SyncTo(groups);
        OnPropertyChanged(string.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Liga ou desliga as fontes ao vivo (eventos + métricas) conforme quem usa e o Docker.</summary>
    private void UpdateSources()
    {
        if (IsLive && _state.IsDockerUp && _settings.Current.Distro is { } distro)
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
        var started = Stopwatch.GetTimestamp();
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        _ = LiveStream.RunAsync(
            client.StreamContainerEventsAsync,
            a => dispatcher.InvokeAsync(a).Task,
            ev => dispatcher.InvokeAsync(() =>
            {
                _activity.Add(ev);
                // Junta rajadas (ex.: compose up de 6 serviços) numa atualização só.
                _eventDebounce.Stop();
                _eventDebounce.Start();
            }).Task,
            _ => OnEventsEnded(cts, started),
            cts.Token);
    }

    /// <summary>
    /// O fluxo de eventos acabou. Se foi o <see cref="StopEvents"/>, nada a fazer. Se caiu sozinho, confere o estado:
    /// com o Docker desligado as fontes param; com ele ainda de pé (ex.: o túnel do dial-stdio morreu, a conexão
    /// mudou de socket para TCP), nada mais religaria os eventos, então religa aqui, com espera crescente.
    /// </summary>
    private async void OnEventsEnded(CancellationTokenSource cts, long started)
    {
        if (_eventsCts != cts)
            return;
        _eventsCts = null;
        cts.Dispose();
        // Um fluxo que durou bastante caiu por acaso: religa logo. Um que cai assim que abre espera cada vez mais.
        if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromMinutes(1))
            _eventsBackoff.Reset();
        try
        {
            await _state.RefreshAsync();
            await Task.Delay(_eventsBackoff.Next());
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or DockerApiException or TimeoutException)
        {
            // Consultar o estado falhou: a próxima consulta periódica resolve.
        }
        if (IsLive && _state.IsDockerUp && _eventsCts is null)
        {
            StartEvents();
            // Pega o que mudou enquanto os eventos estavam fora.
            await RefreshAsync();
        }
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

    private void OnSampled(object? sender, LiveSample s)
    {
        ApplyUsage(s);
        foreach (var g in Groups)
            g.Recompute();
        UsageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyUsage(LiveSample s)
    {
        var maxMem = Math.Max(1, s.Containers.Values.Select(u => u.MemoryBytes).DefaultIfEmpty(1).Max());
        foreach (var c in Containers)
            c.SetUsage(s.Containers.GetValueOrDefault(c.Id), maxMem);
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
        OnPropertyChanged(nameof(HasStoppedStartupProject));
        Changed?.Invoke(this, EventArgs.Empty);
        _notifier.Show(on ? $"{project} vai subir junto no Iniciar ambiente" : $"{project} não sobe mais junto no Iniciar ambiente");
    }

    [RelayCommand]
    private Task StartGroupAsync(ContainerGroupViewModel g) =>
        ActOnGroup(g, g.All.Where(c => !c.IsRunning), ComposeRunner.StartAsync, $"{g.Title}: containers iniciados");

    [RelayCommand]
    private Task StopGroupAsync(ContainerGroupViewModel g) =>
        ActOnGroup(g, g.All.Where(c => c.IsRunning), ComposeRunner.StopAsync, $"{g.Title}: containers parados");

    [RelayCommand]
    private Task RestartGroupAsync(ContainerGroupViewModel g) =>
        ActOnGroup(g, g.All.Where(c => c.IsRunning), ComposeRunner.RestartAsync, $"{g.Title}: containers reiniciados");

    /// <summary>Para o projeto se algo dele roda; senão, inicia.</summary>
    [RelayCommand]
    private Task ToggleGroupRunningAsync(ContainerGroupViewModel g) => g.AnyRunning ? StopGroupAsync(g) : StartGroupAsync(g);

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
}
