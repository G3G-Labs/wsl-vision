using WslVision.Views.Layout;
using Xunit;

namespace WslVision.Tests;

public class LayoutMathTests
{
    [Theory]
    [InlineData(1208, LayoutSize.Wide)] // janela padrão de 1280 menos o menu lateral
    [InlineData(1100, LayoutSize.Wide)]
    [InlineData(1099, LayoutSize.Medium)]
    [InlineData(840, LayoutSize.Medium)]
    [InlineData(839, LayoutSize.Compact)]
    [InlineData(568, LayoutSize.Compact)] // largura mínima da janela (640) menos o menu
    public void FaixaDaPagina(double width, LayoutSize expected) => Assert.Equal(expected, LayoutMath.SizeFor(width));

    [Fact]
    public void SemLimite_ContaComoLargo() => Assert.Equal(LayoutSize.Wide, LayoutMath.SizeFor(double.PositiveInfinity));

    [Fact]
    public void LimitesProprios_ParaUmaLista() =>
        Assert.Equal(LayoutSize.Medium, LayoutMath.SizeFor(700, mediumBelow: 940, compactBelow: 640));

    [Theory]
    [InlineData(1044, 4)]
    [InlineData(836, 4)] // 4 × 200 + 3 × 12
    [InlineData(835, 3)]
    [InlineData(412, 2)]
    [InlineData(150, 1)] // menos que uma coluna mínima ainda é uma coluna
    public void Colunas(double width, int expected) => Assert.Equal(expected, LayoutMath.Columns(width, 200, 4, 12));

    [Theory]
    [InlineData(3, 2)]
    [InlineData(4, 4)]
    [InlineData(1, 1)]
    public void Colunas_NuncaTresMaisUm(int columns, int expected) => Assert.Equal(expected, LayoutMath.SnapColumns(columns, [1, 2, 4]));

    [Fact]
    public void LarguraDaColuna_DescontaOsEspacos() => Assert.Equal(200, LayoutMath.ColumnWidth(836, 4, 12));

    [Fact]
    public void Empilha_SoAbaixoDoLimite()
    {
        Assert.True(LayoutMath.ShouldStack(979, 980));
        Assert.False(LayoutMath.ShouldStack(980, 980));
        Assert.False(LayoutMath.ShouldStack(double.PositiveInfinity, 980));
    }

    [Fact]
    public void Proporcoes_TresPorDois()
    {
        var (a, b) = LayoutMath.ShareStars(500, 3, 2);
        Assert.Equal(300, a);
        Assert.Equal(200, b);
    }
}
