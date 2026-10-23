using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslVision.Models;
using WslVision.Services;
using WslVision.Services.Docker;

namespace WslVision.ViewModels;

public sealed partial class DistroOption : ObservableObject
{
    public required string Name { get; init; }
    public required string Info { get; init; }
    public required bool IsSelectable { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class SetupStep : ObservableObject
{
    public required int Index { get; init; }
    public required string Title { get; init; }

    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isReachable;

    public int Number => Index + 1;
}

public sealed partial class SetupViewModel : ObservableObject
{
    private readonly WslService _wsl;
    private readonly SetupService _setup;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly DockerManager _docker;
    private bool _groupJustAdded;

    public SetupViewModel(WslService wsl, SetupService setup, SettingsService settings, DialogService dialogs, DockerManager docker)
    {
        _wsl = wsl;
        _setup = setup;
        _settings = settings;
        _dialogs = dialogs;
        _docker = docker;
        string[] titles = ["Distro", "Docker Engine", "Permissões", "Conexão", "Pronto"];
        string[] subs = ["Escolha a principal", "Verificação", "Grupo docker", "Socket ou TCP", "Resumo"];
        for (var i = 0; i < titles.Length; i++)
            Steps.Add(new SetupStep { Index = i, Title = titles[i], Subtitle = subs[i] });
        _useTcp = settings.Current.Connection == DockerConnectionMode.Tcp;
        UpdateSteps();
    }

    public ObservableCollection<SetupStep> Steps { get; } = [];

    public ObservableCollection<DistroOption> Distros { get; } = [];

    public event EventHandler? Completed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepLabel), nameof(IsStep0), nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsStep4), nameof(CanGoBack), nameof(IsLastStep), nameof(IsNotLastStep))]
    private int _step;

    private int _maxStep;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyText;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _dockerDesktopDetected;
    [ObservableProperty] private bool _noDistros;
    [ObservableProperty] private SetupStatus? _status;
    // Desligado por padrão: o mais simples é o Docker só ligar quando o usuário pedir.
    [ObservableProperty] private bool _autoStartDocker;
    [ObservableProperty] private bool _startWithWindows = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseSocket), nameof(ConnectionName))]
    private bool _useTcp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestFailed))]
    private bool _tested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestFailed))]
    private bool _testOk;

    public bool TestFailed => Tested && !TestOk;
    [ObservableProperty] private string _testMessage = "";
    [ObservableProperty] private string _testContainers = "";

    public bool UseSocket
    {
        get => !UseTcp;
        set => UseTcp = !value;
    }

    public string StepLabel => $"Etapa {Step + 1} de {Steps.Count}";
    public bool IsStep0 => Step == 0;
    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool IsStep4 => Step == 4;
    public bool CanGoBack => Step > 0;
    public bool IsLastStep => Step == Steps.Count - 1;
    public bool IsNotLastStep => !IsLastStep;

    public string? SelectedDistro => Distros.FirstOrDefault(d => d.IsSelected)?.Name;

    public string ConnectionName => UseTcp ? "API TCP em 127.0.0.1:2375" : "Socket local (sem porta aberta)";

    public string User => Status?.User ?? "seu usuário";

    public string GroupCommand => $"usermod -aG docker {User}";

    public string GroupSummary => Status?.UserInDockerGroup == true ? "Ativado" : "Não configurado";

    partial void OnStepChanged(int value)
    {
        _maxStep = Math.Max(_maxStep, value);
        UpdateSteps();
        Error = null;
    }

    partial void OnStatusChanged(SetupStatus? value)
    {
        OnPropertyChanged(nameof(User));
        OnPropertyChanged(nameof(GroupCommand));
        OnPropertyChanged(nameof(GroupSummary));
    }

    private void UpdateSteps()
    {
        foreach (var s in Steps)
        {
            s.IsCurrent = s.Index == Step;
            s.IsDone = s.Index < _maxStep && s.Index != Step || s.Index < Step;
            s.IsReachable = s.Index <= _maxStep;
        }
        if (SelectedDistro is { } d)
            Steps[0].Subtitle = d;
        OnPropertyChanged(nameof(SelectedDistro));
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        await Busy("Procurando distros…", async () =>
        {
            var list = await _wsl.ListDistrosAsync();
            DockerDesktopDetected = list.Any(d => d.IsDockerDesktopInternal);
            Distros.Clear();
            foreach (var d in list)
            {
                var info = d.IsDockerDesktopInternal
                    ? "Distro interna do Docker Desktop · não pode ser escolhida"
                    : $"WSL {d.Version} · {(d.IsRunning ? "rodando" : "desligada")}{(d.IsDefault ? " · distro padrão" : "")}";
                Distros.Add(new DistroOption { Name = d.Name, Info = info, IsSelectable = !d.IsDockerDesktopInternal && d.Version == 2 });
            }
            var pick = Distros.FirstOrDefault(d => d.IsSelectable && d.Name == _settings.Current.Distro)
                       ?? Distros.FirstOrDefault(d => d.IsSelectable && d.Name.StartsWith("Debian", StringComparison.OrdinalIgnoreCase))
                       ?? Distros.FirstOrDefault(d => d.IsSelectable);
            if (pick is not null)
                pick.IsSelected = true;
            NoDistros = !Distros.Any(d => d.IsSelectable);
            UpdateSteps();
        });
    }

    [RelayCommand]
    private void SelectDistro(DistroOption d)
    {
        if (!d.IsSelectable)
            return;
        foreach (var o in Distros)
            o.IsSelected = o == d;
        Status = null;
        UpdateSteps();
    }

    [RelayCommand]
    private void GoToStep(SetupStep s)
    {
        if (s.Index <= _maxStep)
            Step = s.Index;
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 0)
            Step--;
    }

    [RelayCommand]
    private async Task Next()
    {
        switch (Step)
        {
            case 0:
                if (SelectedDistro is null)
                {
                    Error = "Escolha uma distro para continuar.";
                    return;
                }
                Step = 1;
                await Detect();
                break;

            case 1:
                if (Status is not { DockerInstalled: true } st)
                {
                    Error = "Instale o Docker Engine na distro antes de continuar.";
                    return;
                }
                var ok = await Busy("Configurando o serviço do Docker…", async () =>
                {
                    // Aplica a escolha nos dois sentidos: o instalador do Docker costuma deixar o início automático ligado.
                    if (st.SystemdActive && AutoStartDocker != st.DockerEnabled)
                        await _setup.SetDockerAutostartAsync(SelectedDistro!, AutoStartDocker);
                    // O assistente ainda testa a conexão, então o Docker precisa estar ligado agora.
                    if (!st.DockerActive)
                        await _docker.StartServiceAsync(SelectedDistro!);
                });
                if (ok)
                    Step = 2;
                break;

            case 2:
                if (_groupJustAdded)
                {
                    // O grupo novo só vale em sessões novas: reinicia a distro uma vez.
                    var restarted = await Busy("Reiniciando a distro para aplicar o grupo docker…", async () =>
                    {
                        await _wsl.TerminateAsync(SelectedDistro!);
                        await _wsl.StartAsync(SelectedDistro!);
                        var st2 = await _setup.DetectAsync(SelectedDistro!);
                        if (!st2.DockerActive)
                            await _docker.StartServiceAsync(SelectedDistro!);
                        Status = st2;
                    });
                    if (!restarted)
                        return;
                    _groupJustAdded = false;
                }
                Step = 3;
                break;

            case 3:
                if (!TestOk)
                {
                    var go = await _dialogs.ConfirmAsync("Continuar sem testar?",
                        "A conexão com o Docker ainda não foi testada com sucesso. Você pode testar depois, em Ajustes.",
                        "Continuar");
                    if (!go)
                        return;
                }
                Step = 4;
                break;
        }
    }

    [RelayCommand]
    private async Task Detect()
    {
        if (SelectedDistro is not { } distro)
            return;
        await Busy($"Verificando a distro {distro}…", async () =>
        {
            Status = await _setup.DetectAsync(distro);
        });
    }

    [RelayCommand]
    private async Task InstallDocker()
    {
        if (SelectedDistro is not { } distro)
            return;
        await Busy("Abrindo o instalador…", () => _setup.OpenDockerInstallerAsync(distro, Status?.Home ?? "~"));
    }

    [RelayCommand]
    private async Task EnableSystemd()
    {
        if (SelectedDistro is not { } distro)
            return;
        if (!await _dialogs.ConfirmAsync("Ativar o systemd?",
                "O app altera o /etc/wsl.conf como root (com backup) e reinicia a distro. Com o systemd, o Docker sobe sozinho junto com a distro.",
                "Ativar e reiniciar"))
            return;
        await Busy("Ativando o systemd e reiniciando a distro…", async () =>
        {
            await _setup.EnableSystemdAsync(distro);
            await _wsl.TerminateAsync(distro);
            Status = await _setup.DetectAsync(distro);
        });
    }

    [RelayCommand]
    private async Task AddToGroup()
    {
        if (SelectedDistro is not { } distro || Status is null)
            return;
        var ok = await Busy("Adicionando ao grupo docker…", async () =>
        {
            await _setup.AddUserToDockerGroupAsync(distro, Status.User);
            Status = Status with { UserInDockerGroup = true };
        });
        if (ok)
            _groupJustAdded = true;
    }

    partial void OnUseTcpChanged(bool value)
    {
        Tested = false;
        TestOk = false;
    }

    [RelayCommand]
    private async Task Test()
    {
        if (SelectedDistro is not { } distro)
            return;
        var mode = UseTcp ? DockerConnectionMode.Tcp : DockerConnectionMode.Socket;
        if (UseTcp && !await EnsureTcpAsync(distro))
            return;
        await Busy("Testando a conexão…", async () =>
        {
            var (ok, message, count) = await SetupService.TestConnectionAsync(_wsl, distro, mode);
            Tested = true;
            TestOk = ok;
            TestMessage = message;
            TestContainers = ok ? $"{count} {(count == 1 ? "container encontrado" : "containers encontrados")}" : "";
        });
    }

    [RelayCommand]
    private async Task Finish()
    {
        if (SelectedDistro is not { } distro)
            return;
        // A API TCP é aplicada aqui, e não só no "Testar": quem escolhe TCP e pula o teste também precisa dela.
        if (UseTcp && !await EnsureTcpAsync(distro))
            return;
        var ok = await Busy("Salvando…", async () =>
        {
            // Testou o TCP e depois voltou para o socket: a porta sem senha não pode ficar aberta.
            if (!UseTcp && IsTcpConfigured(distro))
                await _setup.SetTcpListenerAsync(distro, false);
            var (user, home) = Status is { } st ? (st.User, st.Home) : await _wsl.GetDefaultUserAsync(distro);
            _docker.Reset();
            _settings.Update(s =>
            {
                s.Distro = distro;
                s.LinuxUser = user;
                s.LinuxHome = home;
                s.Connection = UseTcp ? DockerConnectionMode.Tcp : DockerConnectionMode.Socket;
                s.SetupCompleted = true;
            });
            StartupService.SetEnabled(StartWithWindows);
        });
        if (ok)
            Completed?.Invoke(this, EventArgs.Empty);
    }

    private bool IsTcpConfigured(string distro) =>
        _settings.Current.Changes.Any(c => c.Kind == ConfigChangeKind.DockerTcpOverride && c.Distro == distro);

    /// <summary>Liga a API TCP na distro (com confirmação), se ainda não estiver ligada.</summary>
    private async Task<bool> EnsureTcpAsync(string distro)
    {
        if (IsTcpConfigured(distro))
            return true;
        if (!await _dialogs.ConfirmAsync("Ativar a API TCP?",
                "O Docker passa a aceitar comandos em 127.0.0.1:2375, sem senha. Qualquer programa deste PC poderá controlá-lo. O socket local é mais seguro.",
                "Ativar mesmo assim", danger: true))
            return false;
        return await Busy("Configurando a API TCP…", () => _setup.SetTcpListenerAsync(distro, true));
    }

    private async Task<bool> Busy(string text, Func<Task> action)
    {
        IsBusy = true;
        BusyText = text;
        Error = null;
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }
}
