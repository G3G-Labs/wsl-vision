using System.Text.Json.Serialization;

namespace WslVision.Models;

/// <summary>O que o app liga ao abrir. Cada opção inclui as anteriores; a ordem importa.</summary>
public enum LaunchAction
{
    /// <summary>Só o app: a distro e o Docker ligam quando o usuário pedir.</summary>
    None,
    Distro,
    DistroAndDocker,

    /// <summary>Distro, Docker e os projetos marcados para iniciar com o ambiente.</summary>
    Environment,
}

public enum DockerConnectionMode
{
    /// <summary>Fala com /var/run/docker.sock via "docker system dial-stdio" dentro do WSL. Nenhuma porta aberta.</summary>
    Socket,

    /// <summary>API HTTP em 127.0.0.1:2375 (sem autenticação; qualquer programa local consegue usar).</summary>
    Tcp,
}

public sealed class AppSettings
{
    public string? Distro { get; set; }
    public string? LinuxUser { get; set; }
    public string? LinuxHome { get; set; }
    public DockerConnectionMode Connection { get; set; } = DockerConnectionMode.Socket;
    public bool SetupCompleted { get; set; }
    public bool CloseToTray { get; set; } = true;
    public List<string> Favorites { get; set; } = [];
    public List<string> RecentFolders { get; set; } = [];

    /// <summary>Formato antigo dos comandos rápidos. Só é lido para migrar para os workspaces do Terminal (grupo "Comandos rápidos").</summary>
    public List<QuickCommand> QuickCommands { get; set; } = [];

    /// <summary>Projetos do Docker Compose que o "Iniciar ambiente" sobe junto com a distro e o Docker.</summary>
    public List<string> StartupProjects { get; set; } = [];

    /// <summary>O que o app liga sozinho ao abrir (inclusive com o Windows). Padrão: nada.</summary>
    public LaunchAction OnLaunch { get; set; } = LaunchAction.None;

    /// <summary>Formato antigo (true = <see cref="LaunchAction.Environment"/>). Só é lido para migrar; nunca é gravado.</summary>
    [JsonPropertyName("StartEnvironmentOnLaunch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyStartEnvironmentOnLaunch { get; set; }

    /// <summary>Nos terminais do app: autocomplete sem maiúsculas e histórico gravado a cada comando (só variáveis de ambiente).</summary>
    public bool TerminalHelpers { get; set; } = true;

    /// <summary>Escala da interface (1 = 100%). 0 = seguir o "Tamanho do texto" do Windows.</summary>
    public double UiScale { get; set; }

    /// <summary>Alterações feitas pelo app no sistema, para poder desfazer.</summary>
    public List<ConfigChange> Changes { get; set; } = [];
}

public sealed class QuickCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Folder { get; set; } = "~";
}

public enum ConfigChangeKind
{
    UserAddedToDockerGroup,
    DockerServiceEnabled,
    SystemdEnabled,
    DockerTcpOverride,
    WslConfigEdited,

    /// <summary>O app desativou o início automático do Docker (o serviço vinha ativado, ex.: pelo instalador).</summary>
    DockerAutostartDisabled,
}

public sealed class ConfigChange
{
    public ConfigChangeKind Kind { get; set; }
    public string? Distro { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
}
