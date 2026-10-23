using WslVision.Models;
using WslVision.Services;
using Xunit;

namespace WslVision.Tests;

/// <summary>O workspaces.json guarda a organização do Terminal: reiniciar o PC (ou desligar no meio da gravação) não pode perdê-la.</summary>
public sealed class WorkspaceStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wslvision-ws-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_folder, "workspaces.json");

    public WorkspaceStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private WorkspaceStore NewStore() => new(_folder, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(1));

    private FileStream Lock() => new(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private static WorkspaceGroup Sample() => new()
    {
        Name = "ObraClick",
        Folder = "~/Projetos/ObraClick/obraclick-homologacao",
        StartWithEnvironment = true,
        Tabs =
        [
            new WorkspaceTab { Name = "api", Folder = "./api", Startup = "npm run dev", Autostart = true, ComposeProject = "obraclick-homologacao" },
            new WorkspaceTab { Name = "manutenção", Folder = "~", Root = true },
        ],
    };

    [Fact]
    public void SemArquivo_ComecaVazioENovo()
    {
        var s = NewStore();
        s.Load();

        Assert.True(s.IsNew);
        Assert.Empty(s.Current.Groups);
    }

    [Fact]
    public void GravaELeDeVolta()
    {
        var s = NewStore();
        s.Load();
        s.Current.Groups.Add(Sample());
        s.Current.Selected = s.Current.Groups[0].Tabs[0].Id;
        s.SaveNow();

        var again = NewStore();
        again.Load();

        Assert.False(again.IsNew);
        var g = Assert.Single(again.Current.Groups);
        Assert.Equal("obraclick-homologacao", g.Tabs[0].ComposeProject);
        Assert.Null(g.Tabs[1].ComposeProject);
        Assert.True(g.StartWithEnvironment);
        Assert.Equal(["api", "manutenção"], g.Tabs.Select(t => t.Name));
        Assert.True(g.Tabs[1].Root);
        Assert.Equal(g.Tabs[0].Id, again.Current.Selected);
    }

    [Fact]
    public void GravacaoAgendada_VaiParaODiscoNoFlush()
    {
        var s = new WorkspaceStore(_folder, TimeSpan.FromMinutes(10));
        s.Load();
        s.Current.Groups.Add(Sample());
        s.ScheduleSave();
        Assert.False(File.Exists(FilePath));

        s.Flush();

        Assert.Contains("ObraClick", File.ReadAllText(FilePath));
    }

    [Fact]
    public void GravacaoAgendada_UsaOEstadoDoMomentoDoPedido()
    {
        var s = new WorkspaceStore(_folder, TimeSpan.FromMinutes(10));
        s.Load();
        s.Current.Groups.Add(Sample());
        s.ScheduleSave();
        s.Current.Groups[0].Name = "mudou depois";

        s.Flush();

        Assert.Contains("ObraClick", File.ReadAllText(FilePath));
    }

    [Fact]
    public void GravacaoAgendada_AconteceSozinha()
    {
        var s = NewStore();
        s.Load();
        s.Current.Groups.Add(Sample());
        s.ScheduleSave();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(FilePath) && DateTime.UtcNow < deadline)
            Thread.Sleep(10);

        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void ArquivoCorrompido_VoltaParaACopiaBoaEGuardaOOriginal()
    {
        var s = NewStore();
        s.Load();
        s.Current.Groups.Add(Sample());
        s.SaveNow();
        s.Current.Groups[0].Name = "segunda versão";
        s.SaveNow(); // a primeira vira .bak
        File.WriteAllText(FilePath, "{ isto não é json");

        var again = NewStore();
        again.Load();

        Assert.NotNull(again.LoadProblem);
        Assert.Equal("ObraClick", Assert.Single(again.Current.Groups).Name);
        Assert.True(File.Exists(FilePath + ".ilegivel"));
    }

    [Fact]
    public void GravacaoAgendadaQueFalha_NaoLancaEContinuaPendente()
    {
        var s = NewStore();
        s.Load();
        s.Current.Groups.Add(Sample());
        // Uma pasta no lugar do arquivo temporário: a escrita falha, como com o arquivo em uso.
        Directory.CreateDirectory(FilePath + ".tmp");
        Exception? failure = null;
        using var failed = new ManualResetEventSlim();
        s.SaveFailed += (_, ex) =>
        {
            failure = ex;
            failed.Set();
        };

        s.ScheduleSave();

        Assert.True(failed.Wait(TimeSpan.FromSeconds(5)));
        Assert.NotNull(failure);
        Directory.Delete(FilePath + ".tmp");
        s.Flush();
        Assert.Contains("ObraClick", File.ReadAllText(FilePath));
    }

    [Fact]
    public void ArquivoEmUsoAoAbrir_NaoEhSobrescritoSemCopia()
    {
        var s = NewStore();
        s.Load();
        s.Current.Groups.Add(Sample());
        s.SaveNow();
        s.Current.Groups[0].Name = "mais nova";
        s.SaveNow(); // .bak = ObraClick, principal = mais nova

        var again = NewStore();
        using (Lock())
            again.Load();

        Assert.NotNull(again.LoadProblem);
        Assert.Equal("ObraClick", Assert.Single(again.Current.Groups).Name);
        again.SaveNow();
        // A versão mais nova, que estava em uso, foi guardada antes de o app gravar por cima.
        Assert.Contains("mais nova", File.ReadAllText(FilePath + ".ilegivel"));
    }

    [Fact]
    public void ComandosRapidos_ViramAbasQueNaoIniciamSozinhas()
    {
        var g = WorkspaceStore.FromQuickCommands(
        [
            new QuickCommand { Name = "Subir ambiente", Command = "docker compose up -d", Folder = "~/obra" },
            new QuickCommand { Name = "Atualizar", Command = "sudo apt update", Folder = "" },
        ]);

        Assert.Equal("Comandos rápidos", g.Name);
        Assert.Equal(2, g.Tabs.Count);
        Assert.Equal("docker compose up -d", g.Tabs[0].Startup);
        Assert.Equal("~/obra", g.Tabs[0].Folder);
        Assert.Equal("~", g.Tabs[1].Folder);
        Assert.All(g.Tabs, t => Assert.False(t.Autostart));
    }

    [Fact]
    public void FormatoAntigo_ProjetoDoWorkspaceVaiParaAAbaNaPastaDele()
    {
        var json = """
            {"groups": [{"name": "Rentalz", "folder": "~/Projetos/Rentalz", "composeProject": "rentalz-backend",
              "tabs": [{"name": "Front-end", "folder": "./rentalz-frontend"}, {"name": "raiz", "folder": "."}]}]}
            """;
        var file = WorkspaceStore.Deserialize(json)!;

        WorkspaceStore.Migrate(file);

        var g = file.Groups[0];
        Assert.Null(g.LegacyComposeProject);
        Assert.Null(g.Tabs[0].ComposeProject);
        Assert.Equal("rentalz-backend", g.Tabs[1].ComposeProject);
        Assert.Null(WorkspaceStore.Deserialize(WorkspaceStore.Serialize(file))!.Groups[0].LegacyComposeProject);
    }

    [Fact]
    public void FormatoAntigo_SemAbaNaRaiz_VaiParaAPrimeira()
    {
        var file = new WorkspaceFile { Groups = [new WorkspaceGroup { LegacyComposeProject = "obra", Tabs = [new WorkspaceTab { Folder = "./api" }, new WorkspaceTab { Folder = "./web" }] }] };

        WorkspaceStore.Migrate(file);

        Assert.Equal("obra", file.Groups[0].Tabs[0].ComposeProject);
        Assert.Null(file.Groups[0].Tabs[1].ComposeProject);
    }

    [Theory]
    [InlineData("/home/g/Projetos/Rentalz/rentalz-backend", "rentalz-backend")]
    [InlineData("/home/g/Projetos/Rentalz/rentalz-backend/src", "rentalz-backend")]
    [InlineData("/home/g/Projetos/Rentalz/rentalz-frontend", "rentalz-frontend")]
    [InlineData("/home/g/Projetos/Rentalz", null)] // a pasta de agrupamento não é um projeto
    [InlineData("/home/g/Projetos/Rentalz/rentalz-backend-old", null)]
    [InlineData("/home/g/Projetos/mono/apps/web", "mono-web")] // o mais fundo vence
    [InlineData("/home/g/Projetos/mono/apps/api", "mono")]
    public void ProjetoPelaPasta(string folder, string? expected)
    {
        (string, string)[] projects =
        [
            ("rentalz-backend", "/home/g/Projetos/Rentalz/rentalz-backend"),
            ("rentalz-frontend", "/home/g/Projetos/Rentalz/rentalz-frontend/"),
            ("mono", "/home/g/Projetos/mono"),
            ("mono-web", "/home/g/Projetos/mono/apps/web"),
        ];
        Assert.Equal(expected, WorkspaceStore.ProjectFor(folder, projects));
    }

    [Theory]
    [InlineData("~/obra", ".", "~/obra")]
    [InlineData("~/obra", "", "~/obra")]
    [InlineData("~/obra", "./api", "~/obra/api")]
    [InlineData("~/obra/", "api", "~/obra/api")]
    [InlineData("~/obra", "/etc", "/etc")]
    [InlineData("~/obra", "~", "~")]
    [InlineData("~/obra", "~/outro", "~/outro")]
    public void PastaDaAba(string group, string tab, string expected) => Assert.Equal(expected, WorkspaceStore.ResolveFolder(group, tab));

    [Fact]
    public void Ids_SoLetrasENumeros() => Assert.All(Enumerable.Range(0, 50), _ => Assert.Matches("^[a-z0-9]{10}$", WorkspaceIds.New()));
}
