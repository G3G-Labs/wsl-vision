using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using WslVision.Models;
using WslVision.Services.Docker;

namespace WslVision.Services;

/// <summary>
/// Estado compartilhado: a distro está ligada? O Docker responde?
/// Só consulta enquanto a janela está visível, e nunca liga a distro por conta própria.
/// </summary>
public sealed partial class AppState : ObservableObject
{
    private readonly WslService _wsl;
    private readonly DockerManager _docker;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private Task? _inflight;
    private string? _lastDistro;
    private DateTimeOffset _lastOffPing = DateTimeOffset.MinValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsDockerUp), nameof(IsDistroOff), nameof(IsDockerOff))]
    private bool _isDistroRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsDockerUp), nameof(IsDockerOff))]
    private bool _isDockerResponding;

    [ObservableProperty]
    private bool _hasCheckedOnce;

    [ObservableProperty]
    private string? _dockerVersion;

    /// <summary>"Iniciar ambiente" em andamento (texto do passo atual, ou null).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartingEnvironment))]
    private string? _environmentStep;

    public bool IsStartingEnvironment => EnvironmentStep is not null;

    public AppState(WslService wsl, DockerManager docker, SettingsService settings)
    {
        _wsl = wsl;
        _docker = docker;
        _settings = settings;
        _timer.Tick += async (_, _) => await RefreshAsync(periodic: true);
        _lastDistro = settings.Current.Distro;
        settings.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(Distro));
            if (_lastDistro != Distro)
            {
                // Trocou de distro: o que se sabia da anterior não vale mais.
                _lastDistro = Distro;
                DockerVersion = null;
                OnPropertyChanged(nameof(StatusText));
                _ = RefreshAsync();
            }
        };
    }

    public string? Distro => _settings.Current.Distro;

    public bool IsDockerUp => IsDistroRunning && IsDockerResponding;

    public bool IsDistroOff => HasCheckedOnce && !IsDistroRunning;

    public bool IsDockerOff => HasCheckedOnce && IsDistroRunning && !IsDockerResponding;

    public string StatusText => Distro is null
        ? "Nenhuma distro escolhida"
        : !IsDistroRunning ? $"{Distro} · desligado"
        : IsDockerResponding ? $"{Distro} · rodando" : $"{Distro} · Docker desligado";

    public void StartPolling()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public void StopPolling() => _timer.Stop();

    /// <summary>
    /// Atualiza o estado. Se já houver uma consulta em andamento, espera ela e faz outra:
    /// quem chama (ex.: logo depois de "wsl --shutdown") sempre recebe um resultado novo, nunca um antigo.
    /// </summary>
    public Task RefreshAsync() => RefreshAsync(periodic: false);

    private async Task RefreshAsync(bool periodic)
    {
        while (_inflight is { } running)
        {
            await running;
            if (_inflight == running)
                _inflight = null;
        }
        var task = RefreshCoreAsync(periodic);
        _inflight = task;
        try
        {
            await task;
        }
        finally
        {
            if (_inflight == task)
                _inflight = null;
        }
    }

    private async Task RefreshCoreAsync(bool periodic)
    {
        if (Distro is not { } distro)
            return;
        var running = await _wsl.IsRunningAsync(distro);
        var docker = false;
        var now = DateTimeOffset.UtcNow;
        // Sem pingar, o Docker continua desligado: só se pula o ping quando ele já estava desligado.
        if (running && _docker.Client is { } client
            && DockerPolling.ShouldPing(periodic, IsDistroRunning, IsDockerResponding, _lastOffPing, now))
        {
            docker = await client.PingAsync();
            _lastOffPing = docker ? DateTimeOffset.MinValue : now;
            if (docker && DockerVersion is null)
            {
                try { DockerVersion = (await client.GetVersionAsync()).Version; } catch { /* opcional */ }
            }
        }
        // IsDistroOff e IsDockerOff são avisados pelos próprios setters (NotifyPropertyChangedFor e OnHasCheckedOnceChanged).
        IsDistroRunning = running;
        IsDockerResponding = docker;
        HasCheckedOnce = true;
    }

    // Toda virada de estado vai para o log: é o que explica "o app achou que estava ligado".
    partial void OnIsDistroRunningChanged(bool value) =>
        AppLog.Info("estado", $"Distro {Distro ?? "?"} {(value ? "ligada" : "desligada")}");

    partial void OnIsDockerRespondingChanged(bool value) =>
        AppLog.Info("estado", value ? "Docker respondendo" : "Docker sem resposta");

    partial void OnHasCheckedOnceChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDistroOff));
        OnPropertyChanged(nameof(IsDockerOff));
    }

    public async Task StartDistroAsync()
    {
        if (Distro is not { } distro)
            return;
        await _wsl.StartAsync(distro);
        await RefreshAsync();
    }

    public async Task SetDockerAsync(bool on)
    {
        if (Distro is not { } distro)
            return;
        if (on)
        {
            if (!IsDistroRunning)
                await _wsl.StartAsync(distro);
            await _docker.StartServiceAsync(distro);
        }
        else
        {
            await _docker.StopServiceAsync(distro);
        }
        await RefreshAsync();
    }

    /// <summary>O "Iniciar ambiente" completo terminou (distro, Docker e projetos): os workspaces marcados sobem agora.</summary>
    public event EventHandler? EnvironmentReady;

    /// <summary>
    /// Um clique para trabalhar: liga a distro, o serviço do Docker e os containers dos projetos
    /// marcados para iniciar junto. Com <paramref name="upTo"/>, para antes (ex.: só a distro, ao abrir o app).
    /// Cada passo só acontece se ainda não estiver pronto.
    /// </summary>
    public async Task<string> StartEnvironmentAsync(LaunchAction upTo = LaunchAction.Environment)
    {
        if (Distro is not { } distro)
            return "Escolha uma distro em Ajustes.";
        if (upTo == LaunchAction.None)
            return "Nada para ligar.";
        if (IsStartingEnvironment)
            return "O ambiente já está sendo iniciado.";
        AppLog.Info("estado", $"Iniciando o ambiente (até: {upTo})");
        try
        {
            if (!await _wsl.IsRunningAsync(distro))
            {
                EnvironmentStep = $"Ligando a distro {distro}…";
                await _wsl.StartAsync(distro);
            }
            if (upTo == LaunchAction.Distro)
            {
                await RefreshAsync();
                return $"Distro {distro} ligada.";
            }

            var client = _docker.Client ?? throw new InvalidOperationException("Docker indisponível.");
            if (!await client.PingAsync())
            {
                EnvironmentStep = "Ligando o Docker…";
                // Já volta com o Docker respondendo (a espera acontece dentro da distro).
                await _docker.StartServiceAsync(distro);
                client = _docker.Client!;
            }

            if (upTo == LaunchAction.DistroAndDocker)
            {
                await RefreshAsync();
                return "Distro e Docker ligados.";
            }

            var projects = _settings.Current.StartupProjects;
            var started = 0;
            if (projects.Count > 0)
            {
                EnvironmentStep = "Iniciando os projetos…";
                var stopped = (await client.ListContainersAsync())
                    .Where(c => c.State != "running"
                                && c.Labels?.GetValueOrDefault(ComposeOrder.ProjectLabel) is { } p
                                && projects.Contains(p))
                    .ToList();
                await ComposeRunner.StartAsync(client, stopped.Select(ComposeRunner.Member).ToList());
                started = stopped.Count;
            }

            await RefreshAsync();
            EnvironmentReady?.Invoke(this, EventArgs.Empty);
            return projects.Count == 0
                ? "Ambiente pronto: distro e Docker ligados. Marque projetos em Containers para subirem juntos."
                : started == 0
                    ? "Ambiente pronto: tudo já estava rodando."
                    : $"Ambiente pronto: {started} {(started == 1 ? "container iniciado" : "containers iniciados")}.";
        }
        finally
        {
            EnvironmentStep = null;
        }
    }

    public async Task ShutdownWslAsync()
    {
        AppLog.Info("estado", "Desligando o WSL (wsl --shutdown)");
        _docker.Reset();
        await _wsl.ShutdownAsync();
        DockerVersion = null;
        await RefreshAsync();
    }
}
