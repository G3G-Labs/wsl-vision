using WslVision.Services;
using Xunit;

namespace WslVision.Tests;

public class WslConfigTests
{
    [Fact]
    public void Parse_ArquivoVazio_SemLimites()
    {
        var l = WslConfigService.Parse([]);
        Assert.Null(l.MemoryGb);
        Assert.Null(l.Processors);
        Assert.False(l.AutoMemoryReclaim);
    }

    [Theory]
    [InlineData("8GB", 8)]
    [InlineData("8gb", 8)]
    [InlineData("4096MB", 4)]
    [InlineData("12", 12)]
    [InlineData("6.5GB", 7)]
    [InlineData("1TB", 1024)]
    [InlineData("8388608KB", 8)]
    [InlineData("4096KB", 1)]
    public void Parse_Memoria(string value, int expected) =>
        Assert.Equal(expected, WslConfigService.Parse(["[wsl2]", $"memory={value}"]).MemoryGb);

    [Fact]
    public void Parse_IgnoraOutrasSecoesEComentarios()
    {
        var l = WslConfigService.Parse(
        [
            "[boot]", "memory=99GB",
            "[wsl2]", "# memory=1GB", " processors = 4 ", "memory = 6GB",
            "[experimental]", "autoMemoryReclaim=gradual",
        ]);
        Assert.Equal(6, l.MemoryGb);
        Assert.Equal(4, l.Processors);
        Assert.True(l.AutoMemoryReclaim);
    }

    [Fact]
    public void Apply_ArquivoNovo_CriaSecoes()
    {
        var lines = WslConfigService.Apply([], new WslLimits(8, 4, true));
        Assert.Equal(["[wsl2]", "memory=8GB", "processors=4", "", "[experimental]", "autoMemoryReclaim=gradual"], lines);
        Assert.Equal(new WslLimits(8, 4, true), WslConfigService.Parse(lines));
    }

    [Fact]
    public void Apply_PreservaChavesEComentariosDesconhecidos()
    {
        string[] original =
        [
            "# meu arquivo",
            "[wsl2]",
            "memory=4GB",
            "networkingMode=mirrored",
            "",
            "[experimental]",
            "sparseVhd=true",
        ];
        var lines = WslConfigService.Apply(original, new WslLimits(10, 6, false));
        Assert.Contains("# meu arquivo", lines);
        Assert.Contains("networkingMode=mirrored", lines);
        Assert.Contains("sparseVhd=true", lines);
        Assert.Contains("memory=10GB", lines);
        Assert.DoesNotContain("memory=4GB", lines);
        Assert.Equal(new WslLimits(10, 6, false), WslConfigService.Parse(lines));
        // As chaves novas ficam dentro da seção certa, antes da próxima seção.
        Assert.True(lines.IndexOf("processors=6") < lines.IndexOf("[experimental]"));
    }

    [Fact]
    public void Apply_ComAnterior_SoReescreveOQueMudou()
    {
        string[] original = ["[wsl2]", "memory=512MB", "processors=2", "", "[experimental]", "autoMemoryReclaim=dropCache"];
        var lido = WslConfigService.Parse(original);
        var lines = WslConfigService.Apply(original, lido with { Processors = 4 }, lido);
        // 512MB e dropCache não têm representação exata no app: ficam como o usuário escreveu.
        Assert.Equal(["[wsl2]", "memory=512MB", "processors=4", "", "[experimental]", "autoMemoryReclaim=dropCache"], lines);
    }

    [Fact]
    public void Apply_ComAnterior_SemMudancas_NaoMexe()
    {
        string[] original = ["[wsl2]", "memory=1536MB"];
        var lido = WslConfigService.Parse(original);
        Assert.Equal(original, WslConfigService.Apply(original, lido, lido));
    }

    [Theory]
    [InlineData(32, 12, 8, 4)]
    [InlineData(16, 8, 4, 3)]
    [InlineData(64, 24, 8, 8)]
    [InlineData(24, 16, 6, 5)]
    [InlineData(8, 4, 4, 2)]
    [InlineData(4, 2, 2, 2)]
    [InlineData(2, 1, 1, 1)]
    public void Recommend(int ramGb, int threads, int memoryGb, int processors) =>
        Assert.Equal(new WslLimits(memoryGb, processors, true), WslConfigService.Recommend(ramGb, threads));

    [Fact]
    public void Apply_SemLimite_RemoveAChave()
    {
        var lines = WslConfigService.Apply(["[wsl2]", "memory=4GB", "processors=2"], new WslLimits(null, null, false));
        Assert.DoesNotContain(lines, l => l.StartsWith("memory="));
        Assert.DoesNotContain(lines, l => l.StartsWith("processors="));
    }
}
