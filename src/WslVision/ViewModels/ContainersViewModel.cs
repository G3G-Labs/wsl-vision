using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslVision.Models;
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

/// <summary>
/// Aba Containers: busca e filtro sobre a lista do <see cref="ContainerInventory"/>, os logs do container escolhido
/// e as ações de cada container. As ações de projeto inteiro e a lista em si ficam no inventário.
/// </summary>
public sealed partial class ContainersViewModel : PageViewModel
{
    private const int MaxLogLines = 5000;

    private readonly AppState _state;
    private readonly DockerManager _docker;
    private readonly DialogService _dialogs;
    private readonly Notifier _notifier;
    private readonly TerminalViewModel _terminal;
    private readonly ActivityLog _activity;
    private readonly DispatcherTimer _logFlushTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly ConcurrentQueue<LogLineViewModel> _pendingLogs = new();
    private CancellationTokenSource? _logCts;
    private readonly RetryBackoff _logBackoff = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

    public ContainersViewModel(AppState state, ContainerInventory inventory, EnvironmentViewModel env, DockerManager docker,
        DialogService dialogs, Notifier notifier, TerminalViewModel terminal, ActivityLog activity)
    {
        _state = state;
        Inventory = inventory;
        Env = env;
        _docker = docker;
        _dialogs = dialogs;
        _notifier = notifier;
        _terminal = terminal;
        _activity = activity;

        LogsView = CollectionViewSource.GetDefaultView(LogLines);
        LogsView.Filter = o => string.IsNullOrWhiteSpace(LogSearch)
            || (o is LogLineViewModel l && l.Text.Contains(LogSearch.Trim(), StringComparison.OrdinalIgnoreCase));

        _logFlushTimer.Tick += (_, _) => FlushLogs();
        inventory.Changed += (_, _) => OnInventoryChanged();
        inventory.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ContainerInventory.HasLoaded))
                NotifyListState();
        };
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.IsDockerUp) or nameof(AppState.HasCheckedOnce) or nameof(AppState.DockerVersion))
                NotifyListState();
        };
    }

    public override string Title => "Containers";

    public override SymbolRegular Icon => SymbolRegular.Box24;

    public AppState State => _state;

    public ContainerInventory Inventory { get; }

    public EnvironmentViewModel Env { get; }

    /// <summary>Projetos com algum container que passa na busca e no filtro.</summary>
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

    private ObservableCollection<ContainerItemViewModel> Containers => Inventory.Containers;

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

    public bool ShowList => _state.IsDockerUp && Inventory.HasLoaded && Groups.Count > 0;

    public bool IsLoading => _state.IsDockerUp && !Inventory.HasLoaded;

    public bool ShowEmpty => _state.IsDockerUp && Inventory.HasLoaded && Groups.Count == 0;

    public bool IsTotallyEmpty => Containers.Count == 0;

    public string EmptyTitle => IsTotallyEmpty ? "Nenhum container por aqui" : "Nenhum container encontrado";

    public string EmptyText => IsTotallyEmpty
        ? "Suba um projeto com docker compose e ele aparece nesta lista."
        : "Nenhum container combina com a busca e o filtro atuais.";

    protected override void OnActivated()
    {
        Inventory.Acquire(this);
        if (Selected is not null && _logCts is null)
            StartLogs(Selected);
    }

    protected override void OnDeactivated()
    {
        StopLogs();
        Inventory.Release(this);
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
        StopLogs();
        _logBackoff.Reset();
        LogLines.Clear();
        _pendingLogs.Clear();
        LogSearch = "";
        IsLogPaused = false;
        if (newValue is not null && IsActive)
            StartLogs(newValue);
    }

    private void OnInventoryChanged()
    {
        if (Selected is { } sel && !Containers.Contains(sel))
            Selected = null;
        ApplyFilter();
        // O container dos logs voltou a rodar (Iniciar, Reiniciar, compose up): retoma os logs ao vivo.
        if (IsActive && Selected is { IsRunning: true } running && _logCts is null)
        {
            LogLines.Clear();
            _pendingLogs.Clear();
            StartLogs(running);
        }
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
        var visibleGroups = new List<ContainerGroupViewModel>();
        var q = Search.Trim();
        foreach (var group in Inventory.Groups)
        {
            var visible = group.All.Where(Matches).ToList();
            // Buscar pelo nome do projeto mostra o projeto inteiro.
            if (visible.Count == 0 && q.Length > 0 && group.Title.Contains(q, StringComparison.OrdinalIgnoreCase) && Filter == ContainerFilter.All)
                visible = [.. group.All];
            group.SetVisible(visible);
            if (visible.Count > 0)
                visibleGroups.Add(group);
        }
        Groups.SyncTo(visibleGroups);
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
    }

    public override IEnumerable<PaletteItem> PaletteItems()
    {
        foreach (var g in Inventory.Groups.Where(g => g.IsCompose))
        {
            if (g.HasWorkingDir)
            {
                yield return PaletteItem.For(PaletteSection.Projects, $"{g.Title} · Abrir no VS Code", g.WorkingDirLabel, SymbolRegular.Code24,
                    Inventory.OpenGroupInVsCodeCommand, g);
                yield return PaletteItem.For(PaletteSection.Projects, $"{g.Title} · Terminal na pasta", g.StatusLabel, SymbolRegular.WindowConsole20,
                    OpenGroupTerminalCommand, g);
            }
            yield return g.AnyRunning
                ? PaletteItem.For(PaletteSection.Projects, $"{g.Title} · Reiniciar", g.StatusLabel, SymbolRegular.ArrowClockwise24, Inventory.RestartGroupCommand, g)
                : PaletteItem.For(PaletteSection.Projects, $"{g.Title} · Iniciar projeto", g.StatusLabel, SymbolRegular.Play24, Inventory.StartGroupCommand, g);
        }

        foreach (var c in Containers)
        {
            var where = c.ComposeProject is { } p ? $"{p} · {c.Image}" : c.Image;
            var target = c;
            yield return new PaletteItem(PaletteSection.Containers, $"{c.DisplayName} · Ver logs", where, SymbolRegular.DocumentText24, () =>
            {
                RequestShow();
                Selected = target;
                return Task.CompletedTask;
            });
            if (c.PortUrl is not null)
                yield return PaletteItem.For(PaletteSection.Containers, $"{c.DisplayName} · Abrir {c.PortText}", where, SymbolRegular.Open24, OpenPortCommand, c);
        }
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
    private void ToggleGroup(ContainerGroupViewModel g) => g.IsExpanded = !g.IsExpanded;

    [RelayCommand]
    private async Task OpenGroupTerminalAsync(ContainerGroupViewModel g)
    {
        if (g.WorkingDir is not { } dir)
            return;
        try
        {
            await (g.Project is { } project ? _terminal.OpenProjectAsync(project, dir) : _terminal.OpenSessionAsync(dir, title: g.Title));
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
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
            _activity.Add(_state.IsDockerUp ? "Docker ligado" : "Docker desligado", _state.IsDockerUp ? Tone.Ok : Tone.Off);
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
        await Inventory.RefreshAsync();
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
        var started = Stopwatch.GetTimestamp();
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        _ = LiveStream.RunAsync(
            ct => client.StreamLogsAsync(c.Id, tail: 500, ct),
            a => dispatcher.InvokeAsync(a).Task,
            line =>
            {
                var isErr = line.IsStdErr
                    || line.Text.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Text.Contains("exception", StringComparison.OrdinalIgnoreCase);
                _pendingLogs.Enqueue(new LogLineViewModel(line.Time?.ToString("HH:mm:ss") ?? "", line.Text, isErr));
                // Pausado não pode virar vazamento: guarda no máximo as últimas MaxLogLines linhas.
                while (_pendingLogs.Count > MaxLogLines && _pendingLogs.TryDequeue(out _))
                {
                }
                return Task.CompletedTask;
            },
            error => OnLogsEnded(cts, c, error, started),
            cts.Token);
    }

    /// <summary>
    /// O fluxo de logs acabou. Sozinho e sem erro, o container parou: a lista avisa quando ele voltar a rodar.
    /// Com erro e o container ainda rodando (ex.: o túnel do dial-stdio morreu), religa do zero, com espera crescente.
    /// </summary>
    private async void OnLogsEnded(CancellationTokenSource cts, ContainerItemViewModel c, Exception? error, long started)
    {
        if (_logCts != cts)
            return;
        FlushLogs();
        _logFlushTimer.Stop();
        _logCts = null;
        cts.Dispose();
        OnPropertyChanged(nameof(LiveLabel));
        if (error is null)
            return;
        if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromMinutes(1))
            _logBackoff.Reset();
        await Task.Delay(_logBackoff.Next());
        if (IsActive && Selected == c && c.IsRunning && _logCts is null)
        {
            LogLines.Clear();
            _pendingLogs.Clear();
            StartLogs(c);
        }
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
