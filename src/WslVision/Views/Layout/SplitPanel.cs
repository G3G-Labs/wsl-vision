using System.Windows;
using System.Windows.Controls;

namespace WslVision.Views.Layout;

/// <summary>
/// Dois blocos lado a lado (larguras <see cref="FirstWidth"/> e <see cref="SecondWidth"/>, como colunas de um Grid)
/// que passam a um embaixo do outro quando a largura fica abaixo de <see cref="StackBelow"/>.
/// Empilhados, as alturas seguem <see cref="StackedFirstHeight"/> e <see cref="StackedSecondHeight"/> (Auto ou proporção, se houver altura).
/// Um bloco recolhido (Collapsed) deixa o outro com todo o espaço. Dentro, SplitPanel.IsStacked diz como ficou, para os estilos.
/// </summary>
public sealed class SplitPanel : Panel
{
    public static readonly DependencyProperty FirstWidthProperty = DependencyProperty.Register(
        nameof(FirstWidth), typeof(GridLength), typeof(SplitPanel), new FrameworkPropertyMetadata(new GridLength(1, GridUnitType.Star), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SecondWidthProperty = DependencyProperty.Register(
        nameof(SecondWidth), typeof(GridLength), typeof(SplitPanel), new FrameworkPropertyMetadata(GridLength.Auto, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty StackedFirstHeightProperty = DependencyProperty.Register(
        nameof(StackedFirstHeight), typeof(GridLength), typeof(SplitPanel), new FrameworkPropertyMetadata(GridLength.Auto, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty StackedSecondHeightProperty = DependencyProperty.Register(
        nameof(StackedSecondHeight), typeof(GridLength), typeof(SplitPanel), new FrameworkPropertyMetadata(GridLength.Auto, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(SplitPanel), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty StackBelowProperty = DependencyProperty.Register(
        nameof(StackBelow), typeof(double), typeof(SplitPanel), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty StarMinWidthProperty = DependencyProperty.Register(
        nameof(StarMinWidth), typeof(double), typeof(SplitPanel), new FrameworkPropertyMetadata(160.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty IsStackedProperty = DependencyProperty.RegisterAttached(
        "IsStacked", typeof(bool), typeof(SplitPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsStacked(DependencyObject d) => (bool)d.GetValue(IsStackedProperty);

    public static void SetIsStacked(DependencyObject d, bool value) => d.SetValue(IsStackedProperty, value);

    public GridLength FirstWidth
    {
        get => (GridLength)GetValue(FirstWidthProperty);
        set => SetValue(FirstWidthProperty, value);
    }

    public GridLength SecondWidth
    {
        get => (GridLength)GetValue(SecondWidthProperty);
        set => SetValue(SecondWidthProperty, value);
    }

    public GridLength StackedFirstHeight
    {
        get => (GridLength)GetValue(StackedFirstHeightProperty);
        set => SetValue(StackedFirstHeightProperty, value);
    }

    public GridLength StackedSecondHeight
    {
        get => (GridLength)GetValue(StackedSecondHeightProperty);
        set => SetValue(StackedSecondHeightProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double StackBelow
    {
        get => (double)GetValue(StackBelowProperty);
        set => SetValue(StackBelowProperty, value);
    }

    /// <summary>Largura que um bloco proporcional (*) sempre mantém ao lado de um Auto, para o título não sumir.</summary>
    public double StarMinWidth
    {
        get => (double)GetValue(StarMinWidthProperty);
        set => SetValue(StarMinWidthProperty, value);
    }

    private UIElement? Child(int i) =>
        InternalChildren.Count > i && InternalChildren[i] is { Visibility: not Visibility.Collapsed } c ? c : null;

    protected override Size MeasureOverride(Size available)
    {
        var stacked = LayoutMath.ShouldStack(available.Width, StackBelow);
        // Antes de medir os filhos: os estilos deles dependem disso.
        if (GetIsStacked(this) != stacked)
            SetIsStacked(this, stacked);

        var (first, second) = (Child(0), Child(1));
        if (first is null || second is null)
        {
            var only = first ?? second;
            only?.Measure(available);
            return only?.DesiredSize ?? default;
        }

        if (stacked)
        {
            var (h1, h2) = StackedHeights(available.Height, first, second, available.Width);
            return new Size(Math.Max(first.DesiredSize.Width, second.DesiredSize.Width), h1 + Spacing + h2);
        }

        var (w1, w2) = Widths(available.Width, first, second, available.Height);
        first.Measure(new Size(w1, available.Height));
        second.Measure(new Size(w2, available.Height));
        var width = double.IsFinite(available.Width) ? available.Width : first.DesiredSize.Width + Spacing + second.DesiredSize.Width;
        return new Size(width, Math.Max(first.DesiredSize.Height, second.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (first, second) = (Child(0), Child(1));
        if (first is null || second is null)
        {
            (first ?? second)?.Arrange(new Rect(final));
            return final;
        }

        if (GetIsStacked(this))
        {
            var (h1, h2) = StackedHeights(final.Height, first, second, final.Width, arranging: true);
            first.Arrange(new Rect(0, 0, final.Width, h1));
            second.Arrange(new Rect(0, h1 + Spacing, final.Width, h2));
        }
        else
        {
            var (w1, w2) = Widths(final.Width, first, second, final.Height, arranging: true);
            first.Arrange(new Rect(0, 0, w1, final.Height));
            second.Arrange(new Rect(w1 + Spacing, 0, w2, final.Height));
        }
        return final;
    }

    /// <summary>Pixels primeiro, depois Auto (com o que sobra), e as proporções dividem o resto.</summary>
    private (double, double) Widths(double width, UIElement first, UIElement second, double height, bool arranging = false)
    {
        var t1 = FirstWidth;
        var t2 = SecondWidth;
        if (!double.IsFinite(width))
        {
            // Sem limite, proporção vira Auto.
            return (Fixed(t1, first, width, height, arranging), Fixed(t2, second, width, height, arranging));
        }
        var space = Math.Max(0, width - Spacing);
        double w1 = 0, w2 = 0;
        if (t1.IsAbsolute) w1 = Math.Min(t1.Value, space);
        if (t2.IsAbsolute) w2 = Math.Min(t2.Value, space - w1);
        // Um bloco Auto que quebra linha (um WrapPanel de botões) pediria a largura toda: o proporcional guarda um mínimo.
        var reserve1 = t1.IsStar ? Math.Min(StarMinWidth, space) : 0;
        var reserve2 = t2.IsStar ? Math.Min(StarMinWidth, space) : 0;
        if (t1.IsAuto) w1 = Math.Min(AutoWidth(first, space - w2 - reserve2, height, arranging), space - w2 - reserve2);
        if (t2.IsAuto) w2 = Math.Min(AutoWidth(second, space - w1 - reserve1, height, arranging), space - w1 - reserve1);
        w1 = Math.Max(0, w1);
        w2 = Math.Max(0, w2);
        var rest = Math.Max(0, space - (t1.IsStar ? 0 : w1) - (t2.IsStar ? 0 : w2));
        if (t1.IsStar && t2.IsStar)
            (w1, w2) = LayoutMath.ShareStars(rest, t1.Value, t2.Value);
        else if (t1.IsStar)
            w1 = rest;
        else if (t2.IsStar)
            w2 = rest;
        return (w1, w2);
    }

    private static double Fixed(GridLength t, UIElement child, double width, double height, bool arranging) =>
        t.IsAbsolute ? t.Value : AutoWidth(child, width, height, arranging);

    private static double AutoWidth(UIElement child, double width, double height, bool arranging)
    {
        if (!arranging)
            child.Measure(new Size(Math.Max(0, width), height));
        return child.DesiredSize.Width;
    }

    private (double, double) StackedHeights(double height, UIElement first, UIElement second, double width, bool arranging = false)
    {
        var t1 = StackedFirstHeight;
        var t2 = StackedSecondHeight;
        if (!arranging)
        {
            // Auto (ou sem altura definida): mede com altura livre. Proporção: mede com a parte que vai receber.
            if (!t1.IsStar || !double.IsFinite(height)) first.Measure(new Size(width, double.PositiveInfinity));
            if (!t2.IsStar || !double.IsFinite(height)) second.Measure(new Size(width, double.PositiveInfinity));
        }
        if (!double.IsFinite(height))
            return (first.DesiredSize.Height, second.DesiredSize.Height);

        var space = Math.Max(0, height - Spacing);
        var h1 = t1.IsAbsolute ? t1.Value : t1.IsAuto ? first.DesiredSize.Height : 0;
        var h2 = t2.IsAbsolute ? t2.Value : t2.IsAuto ? second.DesiredSize.Height : 0;
        var rest = Math.Max(0, space - h1 - h2);
        if (t1.IsStar && t2.IsStar)
            (h1, h2) = LayoutMath.ShareStars(rest, t1.Value, t2.Value);
        else if (t1.IsStar)
            h1 = rest;
        else if (t2.IsStar)
            h2 = rest;
        if (!arranging)
        {
            if (t1.IsStar) first.Measure(new Size(width, h1));
            if (t2.IsStar) second.Measure(new Size(width, h2));
        }
        return (h1, h2);
    }
}
