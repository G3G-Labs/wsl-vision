namespace WslVision.Views.Layout;

/// <summary>Faixa de largura de uma área da tela. As páginas escondem colunas ou empilham painéis conforme ela.</summary>
public enum LayoutSize
{
    Wide,
    Medium,
    Compact,
}

/// <summary>As contas do layout responsivo, sem WPF (para os testes).</summary>
public static class LayoutMath
{
    // Larguras da área da página, em unidades do WPF já sem a escala da interface (a mesma régua do canvas de design).
    public const double PageMediumBelow = 1100;
    public const double PageCompactBelow = 840;

    /// <summary>Largura mínima da janela a 100%: menu lateral (72) + página compacta. Cabe em meia tela de 1366 px.</summary>
    public const double WindowMinWidth = 640;

    public static LayoutSize SizeFor(double width, double mediumBelow = PageMediumBelow, double compactBelow = PageCompactBelow)
    {
        // Sem limite (ex.: dentro de um StackPanel horizontal) conta como largo.
        if (!double.IsFinite(width) || width >= mediumBelow)
            return LayoutSize.Wide;
        return width >= compactBelow ? LayoutSize.Medium : LayoutSize.Compact;
    }

    /// <summary>Quantas colunas de pelo menos <paramref name="minColumnWidth"/> cabem, de 1 a <paramref name="maxColumns"/>.</summary>
    public static int Columns(double width, double minColumnWidth, int maxColumns, double spacing)
    {
        maxColumns = Math.Max(1, maxColumns);
        if (!double.IsFinite(width) || minColumnWidth <= 0)
            return maxColumns;
        var n = (int)Math.Floor((width + spacing) / (minColumnWidth + spacing));
        return Math.Clamp(n, 1, maxColumns);
    }

    /// <summary>Arredonda para baixo até um dos números permitidos (ex.: 1, 2 e 4, para quatro cartões nunca ficarem 3 + 1).</summary>
    public static int SnapColumns(int columns, IReadOnlyList<int> allowed)
    {
        var best = 1;
        foreach (var a in allowed)
            if (a <= columns && a > best)
                best = a;
        return best;
    }

    public static double ColumnWidth(double width, int columns, double spacing) =>
        Math.Max(0, (width - spacing * (Math.Max(1, columns) - 1)) / Math.Max(1, columns));

    public static bool ShouldStack(double width, double stackBelow) => double.IsFinite(width) && width < stackBelow;

    /// <summary>Divide <paramref name="space"/> entre duas trilhas proporcionais (pesos <paramref name="a"/> e <paramref name="b"/>).</summary>
    public static (double First, double Second) ShareStars(double space, double a, double b)
    {
        space = Math.Max(0, space);
        var total = a + b;
        if (total <= 0)
            return (space / 2, space / 2);
        var first = space * a / total;
        return (first, space - first);
    }
}
