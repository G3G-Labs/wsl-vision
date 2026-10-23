using System.Windows;

namespace WslVision.Views.Layout;

/// <summary>
/// Mede a largura de uma área (Track="True") e passa a faixa dela (<see cref="LayoutSize"/>) para tudo o que está dentro.
/// Os estilos reagem com um Trigger em Responsive.Size. Uma área de dentro pode medir de novo com outros limites
/// (ex.: a lista de containers, que perde largura quando os logs abrem).
/// </summary>
public static class Responsive
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached(
        "Size", typeof(LayoutSize), typeof(Responsive),
        new FrameworkPropertyMetadata(LayoutSize.Wide, FrameworkPropertyMetadataOptions.Inherits));

    public static LayoutSize GetSize(DependencyObject d) => (LayoutSize)d.GetValue(SizeProperty);

    public static void SetSize(DependencyObject d, LayoutSize value) => d.SetValue(SizeProperty, value);

    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(bool), typeof(Responsive), new PropertyMetadata(false, OnTrackChanged));

    public static bool GetTrack(DependencyObject d) => (bool)d.GetValue(TrackProperty);

    public static void SetTrack(DependencyObject d, bool value) => d.SetValue(TrackProperty, value);

    public static readonly DependencyProperty MediumBelowProperty = DependencyProperty.RegisterAttached(
        "MediumBelow", typeof(double), typeof(Responsive), new PropertyMetadata(LayoutMath.PageMediumBelow, OnLimitChanged));

    public static double GetMediumBelow(DependencyObject d) => (double)d.GetValue(MediumBelowProperty);

    public static void SetMediumBelow(DependencyObject d, double value) => d.SetValue(MediumBelowProperty, value);

    public static readonly DependencyProperty CompactBelowProperty = DependencyProperty.RegisterAttached(
        "CompactBelow", typeof(double), typeof(Responsive), new PropertyMetadata(LayoutMath.PageCompactBelow, OnLimitChanged));

    public static double GetCompactBelow(DependencyObject d) => (double)d.GetValue(CompactBelowProperty);

    public static void SetCompactBelow(DependencyObject d, double value) => d.SetValue(CompactBelowProperty, value);

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
            return;
        fe.SizeChanged -= OnSizeChanged;
        if (e.NewValue is true)
        {
            fe.SizeChanged += OnSizeChanged;
            Update(fe);
        }
    }

    private static void OnLimitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement fe && GetTrack(fe))
            Update(fe);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            Update((FrameworkElement)sender);
    }

    private static void Update(FrameworkElement fe)
    {
        // Antes do primeiro layout a largura é 0: fica no padrão (largo) até medir de verdade.
        if (fe.ActualWidth <= 0)
            return;
        var size = LayoutMath.SizeFor(fe.ActualWidth, GetMediumBelow(fe), GetCompactBelow(fe));
        if (GetSize(fe) != size || fe.ReadLocalValue(SizeProperty) == DependencyProperty.UnsetValue)
            SetSize(fe, size);
    }
}
