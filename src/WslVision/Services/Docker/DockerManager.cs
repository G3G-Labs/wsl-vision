using WslVision.Models;

namespace WslVision.Services.Docker;

/// <summary>Mantém o cliente da API conforme a distro e o tipo de conexão escolhidos, e liga/desliga o serviço.</summary>
public sealed class DockerManager(WslService wsl, SettingsService settings) : IDisposable
{
    private readonly Lock _gate = new();
    private DockerClient? _client;
    private string? _key;

    public DockerClient? Client
    {
        get
        {
            var s = settings.Current;
            if (s.Distro is null)
                return null;
            var key = $"{s.Distro}|{s.Connection}";
            lock (_gate)
            {
                if (_client is null || _key != key)
                {
                    _client?.Dispose();
                    _client = Create(wsl, s.Distro, s.Connection);
                    _key = key;
                }
                return _client;
            }
        }
    }

    public static DockerClient Create(WslService wsl, string distro, DockerConnectionMode mode) =>
        mode == DockerConnectionMode.Tcp ? DockerClient.ViaTcp() : DockerClient.ViaWslSocket(wsl, distro);

    /// <summary>Fecha conexões abertas (ex.: antes de desligar o WSL).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
            _key = null;
        }
    }

    // Com systemd, liga e para também o docker.socket; senão a próxima chamada à API religaria o serviço.
    public Task StartServiceAsync(string distro) => RunServiceScript(distro, "start", "docker.socket docker.service", "ligar");

    public async Task StopServiceAsync(string distro)
    {
        Reset();
        await RunServiceScript(distro, "stop", "docker.socket docker.service", "desligar");
    }

    public async Task RestartServiceAsync(string distro)
    {
        Reset();
        await RunServiceScript(distro, "restart", "docker.service", "reiniciar");
    }

    /// <summary>systemctl quando a distro usa systemd; senão, o "service" do SysV.</summary>
    private async Task RunServiceScript(string distro, string action, string systemdUnits, string verb)
    {
        var script = $"""
            if [ "$(ps -p 1 -o comm= 2>/dev/null)" = systemd ]; then
              systemctl {action} {systemdUnits}
            else
              service docker {action}
            fi
            """;
        var r = await wsl.RunScriptAsync(distro, script, asRoot: true, timeout: TimeSpan.FromMinutes(2));
        if (!r.Success)
            throw new InvalidOperationException(WslService.ErrorText(r, $"Não foi possível {verb} o Docker."));
    }

    public void Dispose() => Reset();
}
