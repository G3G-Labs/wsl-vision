using WslVision.Services;
using Xunit;

namespace WslVision.Tests;

/// <summary>O settings.json guarda favoritos, comandos e o histórico do "Desfazer": não pode ser perdido por acidente.</summary>
public sealed class SettingsFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wslvision-tests-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_folder, "settings.json");

    public SettingsFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private SettingsService NewService() => new(_folder, TimeSpan.FromMilliseconds(1));

    // Outro programa (antivírus, sincronização) com o arquivo aberto sem compartilhar.
    private FileStream Lock() => new(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private const string Saved = """{"Distro": "Debian", "Favorites": ["/home/gabriel/obra"], "SetupCompleted": true}""";

    [Fact]
    public void SemArquivo_UsaOPadrao()
    {
        var s = NewService();
        s.Load();

        Assert.Null(s.Current.Distro);
        Assert.Null(s.LoadProblem);
    }

    [Fact]
    public void Legivel_Carrega()
    {
        File.WriteAllText(FilePath, Saved);
        var s = NewService();
        s.Load();

        Assert.Equal("Debian", s.Current.Distro);
        Assert.Null(s.LoadProblem);
    }

    [Fact]
    public void Corrompido_GuardaUmaCopiaERecomeca()
    {
        File.WriteAllText(FilePath, "{ isto não é json");
        var s = NewService();
        s.Load();

        Assert.Null(s.Current.Distro);
        Assert.NotNull(s.LoadProblem);
        Assert.Equal("{ isto não é json", File.ReadAllText(FilePath + ".corrompido"));
    }

    [Fact]
    public void Bloqueado_NaAbertura_NaoTrataComoCorrompido()
    {
        File.WriteAllText(FilePath, Saved);
        var s = NewService();
        using (Lock())
            s.Load();

        Assert.Null(s.Current.Distro);
        Assert.NotNull(s.LoadProblem);
        Assert.False(File.Exists(FilePath + ".corrompido"));
        Assert.Equal(Saved, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Bloqueado_NaAbertura_ProximaAlteracaoValeSobreOArquivo()
    {
        File.WriteAllText(FilePath, Saved);
        var s = NewService();
        using (Lock())
            s.Load();

        s.Update(x => x.CloseToTray = false);

        Assert.Equal("Debian", s.Current.Distro);
        Assert.Null(s.LoadProblem);
        var reloaded = NewService();
        reloaded.Load();
        Assert.Equal("Debian", reloaded.Current.Distro);
        Assert.Equal(["/home/gabriel/obra"], reloaded.Current.Favorites);
        Assert.False(reloaded.Current.CloseToTray);
    }

    [Fact]
    public void AindaBloqueado_NaGravacao_FalhaSemApagar()
    {
        File.WriteAllText(FilePath, Saved);
        var s = NewService();
        using (Lock())
        {
            s.Load();
            Assert.ThrowsAny<IOException>(() => s.Update(x => x.CloseToTray = false));
        }

        Assert.Equal(Saved, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Gravacao_TrocaOArquivoInteiro()
    {
        var s = NewService();
        s.Load();
        s.Update(x => x.Distro = "Ubuntu");

        Assert.False(File.Exists(FilePath + ".tmp"));
        var reloaded = NewService();
        reloaded.Load();
        Assert.Equal("Ubuntu", reloaded.Current.Distro);
    }
}
