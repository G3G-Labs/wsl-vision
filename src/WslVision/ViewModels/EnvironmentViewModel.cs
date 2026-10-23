using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WslVision.Models;
using WslVision.Services;
using WslVision.Services.Docker;
using Wpf.Ui.Controls;

namespace WslVision.ViewModels;

/// <summary>Um passo da sequência distro → Docker → projetos: o valor curto e o tom do ponto.</summary>
public sealed record EnvStep(string Value, Tone Tone);

/// <summary>
/// O ambiente de trabalho (distro, Docker e projetos marcados) num só lugar: o estado em frases, os passos
/// e as ações Iniciar, Parar, Reiniciar o Docker e Desligar o WSL. A Visão geral, a aba Containers, o painel e
/// o menu da bandeja e a paleta de comandos mostram e executam tudo por aqui.
/// </summary>
public sealed partial class EnvironmentViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly ContainerInventory _inventory;
    private readonly DockerManager _docker;
    private readonly MetricsService _metrics;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly Notifier _notifier;
    private readonly ActivityLog _activity;

    public EnvironmentViewModel(AppState state, ContainerInventory inventory, DockerManager docker, MetricsService metrics,
        SettingsService settings, DialogService dialogs, Notifier notifier, ActivityLog activity)
    {
        _state = state;
        _inventory = inventory;
        _docker = docker;
        _metrics = metrics;
        _settings = settings;
        _dialogs = dialogs;
        _notifier = notifier;
        _activity = activity;
        // Tudo aqui é derivado do estado e da lista: qualquer mudança neles atualiza todas as frases de uma vez.
        state.PropertyChanged += (_, _) => OnPropertyChanged(string.Empty);
        inventory.Changed += (_, _) => OnPropertyChanged(string.Empty);
        settings.Changed += (_, _) => OnPropertyChanged(nameof(StartHint));
    }

    public AppState State => _state;

    public bool IsStarting => _state.IsStartingEnvironment;

    public bool IsOn => !IsStarting && _state.IsDockerUp;

    public bool IsOff => !IsStarting && !_state.IsDockerUp;

    /// <summary>Há o que ligar: o Docker está parado ou um projeto marcado para subir junto está parado.</summary>
    public bool NeedsStart => !_state.IsDockerUp || _inventory.HasStoppedStartupProject;

    public Tone Tone => IsStarting ? Tone.Work : _state.IsDockerUp ? Tone.Ok : _state.IsDistroRunning ? Tone.Work : Tone.Off;

    public string Title => IsStarting ? "Iniciando…"
        : _state.IsDockerUp ? (_inventory.HasStoppedStartupProject ? "Docker ligado, projeto parado" : "Tudo rodando")
        : _state.IsDistroRunning ? "Docker parado"
        : "Desligado";

    /// <summary>O estado numa frase: o passo em andamento, o que está rodando ou por que está parado.</summary>
    public string Detail
    {
        get
        {
            if (IsStarting)
                return _state.EnvironmentStep ?? "";
            if (!_state.IsDistroRunning)
                return "Nada ocupa memória no Windows agora";
            if (!_state.IsDockerUp)
                return "O serviço do Docker não está respondendo";
            var running = _inventory.RunningCount;
            var projects = _inventory.ProjectCount;
            return $"{running} {(running == 1 ? "container" : "containers")} em {projects} {(projects == 1 ? "projeto" : "projetos")}";
        }
    }

    public string DistroName => _state.Distro ?? "distro";

    public EnvStep Distro => _state.IsDistroRunning
        ? new(DistroName, Tone.Ok)
        : new("desligada", IsStarting ? Tone.Work : Tone.Off);

    public EnvStep Docker => _state.IsDockerUp
        ? new(_state.DockerVersion ?? "ligado", Tone.Ok)
        : new("parado", IsStarting && _state.IsDistroRunning ? Tone.Work : Tone.Off);

    public EnvStep Projects => _state.IsDockerUp
        ? new(_inventory.ProjectsSummary, _inventory.AnyProjectRunning ? Tone.Ok : IsStarting ? Tone.Work : Tone.Off)
        : new("—", IsStarting ? Tone.Work : Tone.Off);

    public string StartHint
    {
        get
        {
            var marked = _settings.Current.StartupProjects.Count;
            return marked == 0 ? "Distro e Docker · marque projetos em Containers para subirem juntos"
                : $"Distro, Docker e {marked} {(marked == 1 ? "projeto marcado" : "projetos marcados")}";
        }
    }

    /// <summary>Liga a distro, o Docker e os projetos marcados. Devolve a frase do que foi feito.</summary>
    public async Task<string> StartAsync()
    {
        var message = await _state.StartEnvironmentAsync();
        _activity.Add("Ambiente iniciado");
        await _inventory.RefreshAsync();
        return message;
    }

    /// <summary>Parar o ambiente = parar o Docker (que para os containers com segurança). A distro continua ligada.</summary>
    public async Task StopAsync()
    {
        await _state.SetDockerAsync(false);
        _activity.Add("Ambiente parado: Docker desligado", Tone.Off);
    }

    /// <summary>Pergunta e desliga o WSL inteiro. Devolve false se a pessoa desistiu.</summary>
    public async Task<bool> ShutdownWslAsync()
    {
        var back = MetricsService.GetVmmemBytes() is { } vm ? $" Isso devolve cerca de {Formatting.Gb(vm)} ao Windows." : "";
        if (!await _dialogs.ConfirmAsync("Desligar o WSL?",
                $"Todas as distros param, junto com os containers e terminais abertos nelas.{back}",
                "Desligar"))
            return false;
        // Nada pode consultar a distro durante o desligamento (isso a religaria).
        _metrics.Stop();
        await _state.ShutdownWslAsync();
        _activity.Add("WSL desligado", Tone.Off);
        return true;
    }

    [RelayCommand]
    private Task Start() => Try(async () => _notifier.Show(await StartAsync()));

    [RelayCommand]
    private Task Stop() => Try(async () =>
    {
        await StopAsync();
        _notifier.Show("Docker desligado. A distro continua ligada; use Desligar WSL para liberar a memória.");
    });

    [RelayCommand]
    private Task ShutdownWsl() => Try(async () =>
    {
        if (await ShutdownWslAsync())
            _notifier.Show("WSL desligado. A memória voltou para o Windows.");
    });

    [RelayCommand]
    private Task RestartDocker() => Try(async () =>
    {
        if (_settings.Current.Distro is not { } distro)
            return;
        await _docker.RestartServiceAsync(distro);
        await _state.RefreshAsync();
        _notifier.Show("Docker reiniciado");
    });

    /// <summary>Itens do ambiente na paleta de comandos: a ação principal conforme o estado, e as do WSL e do Docker.</summary>
    public IEnumerable<PaletteItem> PaletteItems()
    {
        yield return _state.IsDockerUp
            ? PaletteItem.For(PaletteSection.Actions, "Parar ambiente", "Para o Docker; a distro continua ligada", SymbolRegular.Stop24, StopCommand)
            : PaletteItem.For(PaletteSection.Actions, "Iniciar ambiente", "Distro, Docker e projetos marcados", SymbolRegular.Play24, StartCommand) with { Accent = true };
        if (!_state.IsDistroRunning)
            yield break;
        yield return PaletteItem.For(PaletteSection.Actions, "Desligar o WSL", "Devolve a memória ao Windows", SymbolRegular.Power24, ShutdownWslCommand);
        if (_state.IsDockerUp)
            yield return PaletteItem.For(PaletteSection.Actions, "Reiniciar o Docker", _state.DockerVersion is { } v ? $"Docker {v}" : null,
                SymbolRegular.ArrowClockwise24, RestartDockerCommand);
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
