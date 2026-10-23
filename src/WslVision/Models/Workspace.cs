using System.Text.Json.Serialization;

namespace WslVision.Models;

/// <summary>
/// Os workspaces do Terminal, em %AppData%\WslVision\workspaces.json. Guarda a organização (grupos, abas, pastas e
/// comandos), nunca a saída dos terminais. Fica separado do settings.json porque muda muito mais.
/// </summary>
public sealed class WorkspaceFile
{
    public int Version { get; set; } = 1;

    /// <summary>Aba selecionada (id), para voltar nela ao abrir o app.</summary>
    public string? Selected { get; set; }

    /// <summary>Barra lateral recolhida: só os pontos de estado.</summary>
    public bool SidebarCollapsed { get; set; }

    public List<WorkspaceGroup> Groups { get; set; } = [];
}

/// <summary>Um grupo de abas, normalmente um projeto.</summary>
public sealed class WorkspaceGroup
{
    public string Id { get; set; } = WorkspaceIds.New();
    public string Name { get; set; } = "";

    /// <summary>Cor do grupo em #RRGGBB. Marca o grupo e, por padrão, as abas dele.</summary>
    public string Color { get; set; } = WorkspaceColors.All[0];

    public bool Collapsed { get; set; }

    /// <summary>
    /// Pasta do workspace (opcional): ponto de partida das abas novas e origem das pastas relativas ("./api").
    /// É só um agrupamento: os projetos são as abas, cada uma na sua pasta.
    /// </summary>
    public string Folder { get; set; } = "~";

    /// <summary>Formato antigo: o projeto Compose era do workspace. Só é lido para migrar para as abas.</summary>
    [JsonPropertyName("composeProject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyComposeProject { get; set; }

    /// <summary>O "Iniciar ambiente" (e o app, se estiver configurado para isso) também inicia este grupo.</summary>
    public bool StartWithEnvironment { get; set; }

    public List<WorkspaceTab> Tabs { get; set; } = [];
}

/// <summary>Uma aba de terminal salva: onde abre, como quem e o que roda.</summary>
public sealed class WorkspaceTab
{
    public string Id { get; set; } = WorkspaceIds.New();
    public string Name { get; set; } = "";

    /// <summary>Pasta inicial. Relativa ("." ou "./api") parte da pasta do grupo.</summary>
    public string Folder { get; set; } = ".";

    /// <summary>Comando que roda ao iniciar a aba (vazio: só o shell).</summary>
    public string Startup { get; set; } = "";

    /// <summary>Inicia sozinha ao abrir o app e no "Iniciar grupo".</summary>
    public bool Autostart { get; set; }

    /// <summary>Abre como root (pede confirmação ao iniciar).</summary>
    public bool Root { get; set; }

    /// <summary>
    /// Projeto do Docker Compose da aba, escolhido à mão. Sem escolha (null), vale o projeto cuja pasta contém a da aba.
    /// "Iniciar grupo" sobe os containers dos projetos das abas antes delas.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ComposeProject { get; set; }
}

public static class WorkspaceIds
{
    /// <summary>Id curto, só com letras e números (entra no nome do arquivo de estado da aba, dentro da distro).</summary>
    public static string New() => Guid.NewGuid().ToString("N")[..10];
}

public static class WorkspaceColors
{
    /// <summary>Cores dos grupos: distintas entre si e longe do vermelho (erro) e do âmbar (root).</summary>
    public static readonly string[] All = ["#4CC2FF", "#B48CFF", "#5FD38A", "#F78FB3", "#61D6D6", "#8FA3BF"];

    public static string Next(int index) => All[index % All.Length];
}
