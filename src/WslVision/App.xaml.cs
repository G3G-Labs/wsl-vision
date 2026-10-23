using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using WslVision.Models;
using WslVision.Services;
using WslVision.Services.Docker;
using WslVision.ViewModels;
using WslVision.Views;

namespace WslVision;

public partial class App : Application
{
    private SingleInstance? _single;
    private TrayService? _tray;
    private MainWindow? _main;
    private DockerManager? _docker;
    private MetricsService? _metrics;

    public SettingsService Settings { get; } = new();

    public Notifier Notifier { get; private set; } = null!;

    private WslService _wsl = null!;
    private LauncherService _launcher = null!;
    private DialogService _dialogs = null!;
    private SetupService _setup = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new SingleInstance(() => Dispatcher.BeginInvoke(ShowMain));
        if (!_single.IsFirst)
        {
            // Já existe uma instância: só pede para ela aparecer.
            _single.SignalFirstInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        // Visual do redesign: tema escuro e o azul do produto como cor de destaque, independentes do Windows.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.None, updateAccent: false);
        ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(0x4C, 0xC2, 0xFF),
            System.Windows.Media.Color.FromRgb(0x6C, 0xC7, 0xFF),
            System.Windows.Media.Color.FromRgb(0x4C, 0xC2, 0xFF),
            System.Windows.Media.Color.FromRgb(0x2F, 0xA8, 0xEC));
        Settings.Load();

        // Composição: tudo é criado uma vez e vive enquanto o app estiver aberto.
        Notifier = new Notifier();
        if (Settings.LoadProblem is { } problem)
            Notifier.Show(problem, isError: true);
        _wsl = new WslService();
        _docker = new DockerManager(_wsl, Settings);
        _launcher = new LauncherService(_wsl);
        _dialogs = new DialogService();
        _setup = new SetupService(_wsl, Settings, _launcher);
        _metrics = new MetricsService(_wsl);
        var wslConfig = new WslConfigService();
        var state = new AppState(_wsl, _docker, Settings);
        var files = new FileService(_wsl);

        var settingsVm = new SettingsViewModel(Settings, _wsl, _setup, wslConfig, _docker, state, _dialogs, Notifier);
        settingsVm.RunSetupRequested += (_, _) => RunSetup(fromSettings: true);
        var terminal = new TerminalViewModel(Settings, _launcher, _dialogs, Notifier);
        var activity = new ActivityLog();
        // Uma lista de containers e um modelo do ambiente, compartilhados por todas as telas.
        var inventory = new ContainerInventory(state, _docker, _metrics, Settings, Notifier, _launcher, activity);
        var env = new EnvironmentViewModel(state, inventory, _docker, _metrics, Settings, _dialogs, Notifier, activity);
        var containers = new ContainersViewModel(state, inventory, env, _docker, _dialogs, Notifier, terminal, activity);
        var mainVm = new MainWindowViewModel(
            state,
            Notifier,
            containers,
            new FilesViewModel(state, Settings, files, _launcher, _dialogs, Notifier, terminal),
            new MonitorViewModel(state, _metrics, wslConfig, Settings, _dialogs, Notifier, inventory, env, activity),
            terminal,
            new MaintenanceViewModel(state, new MaintenanceService(_wsl), _docker, Settings, _metrics, _dialogs, Notifier),
            settingsVm);

        _main = new MainWindow(mainVm, Settings);
        MainWindow = _main;

        var flyout = new TrayFlyoutViewModel(state, env, inventory, _metrics, _launcher, Settings, Notifier);
        _tray = new TrayService(state, Settings, _launcher, Notifier, env, flyout);
        _tray.ShowWindowRequested += (_, _) => ShowMain();
        _tray.ExitRequested += (_, _) => ExitApp();
        var minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        var ready = Settings.Current.SetupCompleted && Settings.Current.Distro is not null;
        // Aberto só na bandeja: já começa em modo eficiência (prioridade baixa, EcoQoS no Windows 11).
        _tray.Initialize(efficiencyMode: ready && minimized);

        if (!ready)
            RunSetup(fromSettings: false);
        else if (!minimized)
            ShowMain();

        // Padrão: só o app abre. Em Ajustes dá para ligar junto a distro, o Docker ou o ambiente completo.
        if (ready && Settings.Current.OnLaunch != LaunchAction.None)
            _ = RunLaunchActionAsync(state, Settings.Current.OnLaunch);
    }

    private async Task RunLaunchActionAsync(AppState state, LaunchAction action)
    {
        try
        {
            var message = await state.StartEnvironmentAsync(action);
            Notifier.Show(message);
            _tray?.Notify(message);
        }
        catch (Exception ex)
        {
            Notifier.Error(ex);
            _tray?.Notify($"Não foi possível iniciar o ambiente: {ex.Message}");
        }
    }

    private void RunSetup(bool fromSettings)
    {
        var vm = new SetupViewModel(_wsl, _setup, Settings, _dialogs, _docker!);
        var window = new SetupWindow(vm);
        vm.Completed += (_, _) =>
        {
            window.Finished = true;
            window.Close();
            ShowMain();
            _ = _main!.ViewModel.State.RefreshAsync();
        };
        window.Closed += (_, _) =>
        {
            // Fechar o assistente na primeira execução, sem terminar, encerra o app.
            if (!window.Finished && !fromSettings && !Settings.Current.SetupCompleted)
                ExitApp();
        };
        if (fromSettings && _main is { IsVisible: true })
            window.Owner = _main;
        window.Show();
        window.Activate();
    }

    private void ShowMain() => _main?.BringToFront();

    private void ExitApp()
    {
        if (_main is not null)
        {
            _main.AllowClose = true;
            _main.Close();
        }
        Shutdown();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Um erro inesperado vira aviso na janela em vez de derrubar o app.
        Notifier.Error(e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _metrics?.Stop();
        _tray?.Dispose();
        _docker?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
