using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslVision.Models;
using WslVision.Services;
using WslVision.Services.Docker;
using Wpf.Ui.Controls;

namespace WslVision.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly SettingsService _settings;
    private readonly WslService _wsl;
    private readonly SetupService _setup;
    private readonly WslConfigService _wslConfig;
    private readonly DockerManager _docker;
    private readonly AppState _state;
    private readonly DialogService _dialogs;
    private readonly Notifier _notifier;
    private bool _loading;

    public SettingsViewModel(SettingsService settings, WslService wsl, SetupService setup, WslConfigService wslConfig,
        DockerManager docker, AppState state, DialogService dialogs, Notifier notifier)
    {
        _settings = settings;
        _wsl = wsl;
        _setup = setup;
        _wslConfig = wslConfig;
        _docker = docker;
        _state = state;
        _dialogs = dialogs;
        _notifier = notifier;
        // A distro ligou ou desligou com Ajustes aberto: a opção do Docker passa a poder (ou não) ser lida.
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.IsDistroRunning) && IsActive)
                _ = LoadDockerAutostartAsync();
        };
    }

    public override string Title => "Ajustes";

    public override SymbolRegular Icon => SymbolRegular.Settings24;

    public ObservableCollection<string> Distros { get; } = [];

    public ObservableCollection<string> ChangesLog { get; } = [];

    [ObservableProperty] private string? _selectedDistro;
    [ObservableProperty] private bool _useTcp;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _terminalHelpers;
    [ObservableProperty] private LaunchAction _onLaunch;

    /// <summary>Docker liga junto com a distro (systemd). Só dá para mudar com a distro ligada e usando systemd.</summary>
    [ObservableProperty] private bool _dockerAutostart;
    [ObservableProperty] private bool _canChangeDockerAutostart;
    [ObservableProperty] private string _dockerAutostartNote = "";
    [ObservableProperty] private ScaleOption? _selectedScale;

    /// <summary>Tamanho da interface. "Automático" segue Configurações › Acessibilidade › Tamanho do texto.</summary>
    public IReadOnlyList<ScaleOption> ScaleOptions { get; } =
    [
        new(0, "Automático (igual ao Windows)"),
        new(1.0, "100%"),
        new(1.25, "125%"),
        new(1.5, "150%"),
        new(1.75, "175%"),
        new(2.0, "200%"),
    ];

    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

    public bool HasChanges => ChangesLog.Count > 0;

    /// <summary>Pedido para reabrir o assistente (tratado pelo App).</summary>
    public event EventHandler? RunSetupRequested;

    protected override async void OnActivated()
    {
        _loading = true;
        try
        {
            StartWithWindows = StartupService.IsEnabled;
            CloseToTray = _settings.Current.CloseToTray;
            TerminalHelpers = _settings.Current.TerminalHelpers;
            OnLaunch = _settings.Current.OnLaunch;
            SelectedScale = ScaleOptions.FirstOrDefault(o => Math.Abs(o.Value - _settings.Current.UiScale) < 0.01) ?? ScaleOptions[0];
            UseTcp = _settings.Current.Connection == DockerConnectionMode.Tcp;
            LoadChanges();
            Distros.Clear();
            foreach (var d in await _wsl.ListDistrosAsync())
                if (!d.IsDockerDesktopInternal)
                    Distros.Add(d.Name);
            SelectedDistro = _settings.Current.Distro;
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
        finally
        {
            _loading = false;
        }
        await LoadDockerAutostartAsync();
    }

    /// <summary>Lê no systemd se o Docker liga junto com a distro. Só com a distro ligada: o app nunca a liga sozinho.</summary>
    private async Task LoadDockerAutostartAsync()
    {
        CanChangeDockerAutostart = false;
        if (_settings.Current.Distro is not { } distro || !_state.IsDistroRunning)
        {
            DockerAutostartNote = "Ligue a distro para ver e mudar esta opção.";
            return;
        }
        try
        {
            var autostart = await _setup.GetDockerAutostartAsync(distro);
            if (autostart is not { } on)
            {
                DockerAutostartNote = "Esta distro não usa systemd: o Docker só liga quando você pede.";
                return;
            }
            _loading = true;
            DockerAutostart = on;
            _loading = false;
            CanChangeDockerAutostart = true;
            DockerAutostartNote = DockerAutostartText(on);
        }
        catch (Exception ex)
        {
            DockerAutostartNote = ex.Message;
        }
    }

    private static string DockerAutostartText(bool on) => on
        ? "Hoje o Docker liga sempre que a distro liga (ao abrir um terminal, por exemplo), e containers com restart: always sobem junto."
        : "Hoje o Docker só liga quando você pede: “Ligar o Docker”, “Iniciar ambiente” ou a opção de abertura acima.";

    partial void OnDockerAutostartChanged(bool value)
    {
        if (!_loading)
            _ = SwitchDockerAutostartAsync(value);
    }

    private async Task SwitchDockerAutostartAsync(bool on)
    {
        if (_settings.Current.Distro is not { } distro)
            return;
        IsBusy = true;
        try
        {
            await _setup.SetDockerAutostartAsync(distro, on);
            DockerAutostartNote = DockerAutostartText(on);
            LoadChanges();
            _notifier.Show(on ? "O Docker vai ligar junto com a distro" : "O Docker não liga mais sozinho. Vale a partir da próxima vez que a distro ligar.");
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
            _loading = true;
            DockerAutostart = !on;
            _loading = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadChanges()
    {
        ChangesLog.Clear();
        foreach (var c in _settings.Current.Changes)
        {
            ChangesLog.Add(c.Kind switch
            {
                ConfigChangeKind.UserAddedToDockerGroup => $"Usuário {c.Detail} adicionado ao grupo docker ({c.Distro})",
                ConfigChangeKind.DockerServiceEnabled => $"Docker iniciando junto com a distro ({c.Distro})",
                ConfigChangeKind.SystemdEnabled => $"systemd ativado no /etc/wsl.conf ({c.Distro})",
                ConfigChangeKind.DockerTcpOverride => $"API do Docker em 127.0.0.1:2375 ({c.Distro})",
                ConfigChangeKind.WslConfigEdited => "Limites de memória e CPU no .wslconfig",
                ConfigChangeKind.DockerAutostartDisabled => $"Docker não liga mais junto com a distro ({c.Distro})",
                _ => c.Kind.ToString(),
            });
        }
        OnPropertyChanged(nameof(HasChanges));
    }

    partial void OnSelectedDistroChanged(string? value)
    {
        if (_loading || value is null || value == _settings.Current.Distro)
            return;
        _ = SwitchDistroAsync(value);
    }

    private async Task SwitchDistroAsync(string distro)
    {
        try
        {
            if (!await _dialogs.ConfirmAsync($"Usar a distro {distro}?",
                    $"O app passa a controlar o Docker e os arquivos da distro {distro}. Ela será ligada para identificar o seu usuário.",
                    "Trocar distro"))
            {
                _loading = true;
                SelectedDistro = _settings.Current.Distro;
                _loading = false;
                return;
            }
            var (user, home) = await _wsl.GetDefaultUserAsync(distro);
            _docker.Reset();
            _settings.Update(s =>
            {
                s.Distro = distro;
                s.LinuxUser = user;
                s.LinuxHome = home;
            });
            await _state.RefreshAsync();
            _notifier.Show($"Agora controlando a distro {distro}");
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
    }

    partial void OnUseTcpChanged(bool value)
    {
        if (_loading)
            return;
        _ = SwitchConnectionAsync(value);
    }

    private async Task SwitchConnectionAsync(bool tcp)
    {
        if (_settings.Current.Distro is not { } distro)
            return;
        try
        {
            if (tcp && !await _dialogs.ConfirmAsync("Ativar a API TCP?",
                    "O Docker passa a aceitar comandos em 127.0.0.1:2375, sem senha. Qualquer programa deste PC poderá controlar o Docker — e, por ele, a distro inteira. Prefira o socket local.",
                    "Ativar mesmo assim", danger: true))
            {
                _loading = true;
                UseTcp = false;
                _loading = false;
                return;
            }
            IsBusy = true;
            await _setup.SetTcpListenerAsync(distro, tcp);
            _docker.Reset();
            _settings.Update(s => s.Connection = tcp ? DockerConnectionMode.Tcp : DockerConnectionMode.Socket);
            await _state.RefreshAsync();
            LoadChanges();
            _notifier.Show(tcp ? "Conexão por TCP ativada" : "Conexão pelo socket local ativada");
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
            _loading = true;
            UseTcp = _settings.Current.Connection == DockerConnectionMode.Tcp;
            _loading = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
            return;
        try
        {
            StartupService.SetEnabled(value);
        }
        catch (Exception ex)
        {
            _notifier.Error(ex);
        }
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (!_loading)
            _settings.Update(s => s.CloseToTray = value);
    }

    partial void OnTerminalHelpersChanged(bool value)
    {
        if (!_loading)
            _settings.Update(s => s.TerminalHelpers = value);
    }

    partial void OnOnLaunchChanged(LaunchAction value)
    {
        if (!_loading)
            _settings.Update(s => s.OnLaunch = value);
    }

    partial void OnSelectedScaleChanged(ScaleOption? value)
    {
        if (!_loading && value is not null)
            _settings.Update(s => s.UiScale = value.Value);
    }

    [RelayCommand]
    private void RunSetup() => RunSetupRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task UndoAll()
    {
        if (!await _dialogs.ConfirmAsync("Desfazer a configuração?",
                "Tudo o que o WSL Vision alterou na distro e no Windows volta a ser como era: grupo docker, início automático do Docker, systemd, API TCP e .wslconfig. Algumas mudanças só valem depois de reiniciar a distro.",
                "Desfazer tudo", danger: true))
            return;
        IsBusy = true;
        try
        {
            var done = await _setup.UndoAllAsync(_wslConfig);
            _docker.Reset();
            _settings.Update(s => s.Connection = DockerConnectionMode.Socket);
            LoadChanges();
            _loading = true;
            UseTcp = false;
            _loading = false;
            await _dialogs.AlertAsync("Configuração desfeita", done.Count == 0 ? "Não havia nada para desfazer." : string.Join("\n", done));
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

public sealed record ScaleOption(double Value, string Label)
{
    public override string ToString() => Label;
}
