using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslVision.Services;

namespace WslVision.ViewModels;

/// <summary>
/// Painel da bandeja (clique esquerdo no ícone): estado do ambiente, a ação principal, consumo e projetos.
/// Só lê dados enquanto está aberto, e nunca liga o WSL sozinho.
/// </summary>
public sealed partial class TrayFlyoutViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly MetricsService _metrics;
    private readonly LauncherService _launcher;
    private readonly SettingsService _settings;
    private readonly Notifier _notifier;
    private bool _open;

    public TrayFlyoutViewModel(AppState state, EnvironmentViewModel env, ContainerInventory inventory, MetricsService metrics,
        LauncherService launcher, SettingsService settings, Notifier notifier)
    {
        _state = state;
        Env = env;
        Inventory = inventory;
        _metrics = metrics;
        _launcher = launcher;
        _settings = settings;
        _notifier = notifier;
        state.PropertyChanged += (_, e) =>
        {
            if (_open && e.PropertyName == nameof(AppState.IsDistroRunning))
                UpdateLive();
        };
    }

    public EnvironmentViewModel Env { get; }

    public ContainerInventory Inventory { get; }

    /// <summary>Pedidos para o App (abrir a janela, sair) e para a bandeja (fechar o painel).</summary>
    public event EventHandler? ShowWindowRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? CloseRequested;

    [ObservableProperty] private string _memText = "—";
    [ObservableProperty] private double _memPercent;
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private string _vmmemText = "—";
    [ObservableProperty] private double _vmmemPercent;

    /// <summary>O painel abriu: confere o estado e, se a distro já estiver ligada, acompanha ao vivo.</summary>
    public async Task OnOpenedAsync()
    {
        _open = true;
        _metrics.Sampled -= OnSampled;
        _metrics.Sampled += OnSampled;
        Inventory.Acquire(this);
        await _state.RefreshAsync();
        // Fechou durante a consulta: religar as métricas agora as deixaria rodando sem ninguém para desligar.
        if (_open)
            UpdateLive();
    }

    public void OnClosed()
    {
        _open = false;
        _metrics.Sampled -= OnSampled;
        _metrics.Unsubscribe(this);
        Inventory.Release(this);
    }

    private void UpdateLive()
    {
        if (_state.IsDistroRunning && _state.Distro is { } distro)
        {
            _metrics.Subscribe(this, distro);
            _metrics.EnsureRunning(distro);
            if (_metrics.Last is { } last)
                OnSampled(this, last);
        }
        else
        {
            _metrics.Unsubscribe(this);
            MemText = CpuText = "—";
            MemPercent = CpuPercent = 0;
            VmmemText = "0 GB";
            VmmemPercent = 0;
        }
    }

    private void OnSampled(object? sender, LiveSample s)
    {
        var w = s.Wsl;
        MemText = Formatting.Gb(w.MemUsedBytes);
        MemPercent = w.MemTotalBytes > 0 ? 100.0 * w.MemUsedBytes / w.MemTotalBytes : 0;
        CpuText = Formatting.Percent(w.CpuPercent);
        CpuPercent = w.CpuPercent;
        if (MetricsService.GetVmmemBytes() is { } vm)
        {
            VmmemText = Formatting.Gb(vm);
            var total = MetricsService.GetTotalPhysicalMemory();
            VmmemPercent = total > 0 ? 100.0 * vm / total : 0;
        }
    }

    /// <summary>O diálogo de confirmação não pode ficar atrás do painel: fecha o painel antes de perguntar.</summary>
    [RelayCommand]
    private Task ShutdownWsl()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return Env.ShutdownWslCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void OpenPanel()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        ShowWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Exit()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    // Terminais e VS Code abrem como janelas próprias (a janela do app pode estar escondida).

    [RelayCommand]
    private Task OpenTerminal()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return Try(() => _launcher.OpenTerminalAsync(_state.Distro!, _settings.Current.LinuxHome ?? "/"));
    }

    [RelayCommand]
    private void OpenProjectInVsCode(ContainerGroupViewModel g)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        Inventory.OpenGroupInVsCodeCommand.Execute(g);
    }

    [RelayCommand]
    private Task OpenProjectTerminal(ContainerGroupViewModel g)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return g.WorkingDir is { } dir ? Try(() => _launcher.OpenTerminalAsync(_state.Distro!, dir, title: g.Title)) : Task.CompletedTask;
    }

    private async Task Try(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
    }
}
