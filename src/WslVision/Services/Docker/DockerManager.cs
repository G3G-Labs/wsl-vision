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

    private const int ReadySeconds = 30;

    // Espera o Docker responder dentro da distro, num processo só. Pingar pela API em laço abriria dois wsl.exe por
    // tentativa (a conferência da distro e o dial-stdio), e sem systemd o "service docker start" volta antes de o
    // dockerd aceitar conexões. Roda como root para medir só o Docker, e não a permissão do usuário.
    private const string WaitReadyScript = """
        i=0
        while ! docker version >/dev/null 2>&1; do
          i=$((i + 1))
          [ "$i" -ge "$A1" ] && exit 1
          sleep 1
        done
        """;

    /// <summary>Liga o Docker e só volta quando ele responde.</summary>
    /// <remarks>Com systemd, liga e para também o docker.socket; senão a próxima chamada à API religaria o serviço.</remarks>
    public async Task StartServiceAsync(string distro)
    {
        await RunServiceScript(distro, "start", "docker.socket docker.service", "ligar");
        await WaitUntilReadyAsync(distro);
    }

    public async Task StopServiceAsync(string distro)
    {
        Reset();
        await RunServiceScript(distro, "stop", "docker.socket docker.service", "desligar");
    }

    /// <summary>Reinicia o Docker e só volta quando ele responde.</summary>
    public async Task RestartServiceAsync(string distro)
    {
        Reset();
        await RunServiceScript(distro, "restart", "docker.service", "reiniciar");
        await WaitUntilReadyAsync(distro);
    }

    private async Task WaitUntilReadyAsync(string distro)
    {
        var r = await wsl.RunScriptAsync(distro, WaitReadyScript, [ReadySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            asRoot: true, timeout: TimeSpan.FromSeconds(ReadySeconds + 15));
        if (!r.Success)
            throw new TimeoutException($"O serviço do Docker foi ligado, mas não respondeu em {ReadySeconds} segundos.");
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
