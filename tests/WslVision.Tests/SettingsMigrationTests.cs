using WslVision.Models;
using WslVision.Services;
using Xunit;

namespace WslVision.Tests;

public class SettingsMigrationTests
{
    private static AppSettings Load(string json)
    {
        var s = SettingsService.Deserialize(json)!;
        SettingsService.Migrate(s);
        return s;
    }

    [Fact]
    public void Novo_SoOApp() => Assert.Equal(LaunchAction.None, Load("{}").OnLaunch);

    [Fact]
    public void Antigo_IniciarAmbienteLigado_ViraAmbienteCompleto() =>
        Assert.Equal(LaunchAction.Environment, Load("""{"StartEnvironmentOnLaunch": true}""").OnLaunch);

    [Fact]
    public void Antigo_Desligado_ViraSoOApp() =>
        Assert.Equal(LaunchAction.None, Load("""{"StartEnvironmentOnLaunch": false}""").OnLaunch);

    [Fact]
    public void Gravado_NaoTemMaisOCampoAntigo()
    {
        var json = SettingsService.Serialize(Load("""{"StartEnvironmentOnLaunch": true}"""));
        Assert.DoesNotContain("StartEnvironmentOnLaunch", json);
        Assert.Contains("\"OnLaunch\": \"Environment\"", json);
    }

    [Fact]
    public void OpcaoNova_ELidaComoTexto() =>
        Assert.Equal(LaunchAction.DistroAndDocker, Load("""{"OnLaunch": "DistroAndDocker"}""").OnLaunch);
}
