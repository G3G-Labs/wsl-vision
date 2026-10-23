using System.Windows;
using System.Windows.Controls;

namespace WslVision.Views.Layout;

/// <summary>
/// Grade de colunas iguais: põe quantas couberem com pelo menos <see cref="MinColumnWidth"/>, até <see cref="MaxColumns"/>.
/// Os itens de uma linha ficam com a altura do mais alto. Itens recolhidos (Collapsed) não ocupam lugar.
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(AdaptiveGrid), new FrameworkPropertyMetadata(200.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(AdaptiveGrid), new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(AdaptiveGrid), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(AdaptiveGrid), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    public static readonly DependencyProperty ColumnStepsProperty = DependencyProperty.Register(
        nameof(ColumnSteps), typeof(string), typeof(AdaptiveGrid), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Números de colunas permitidos, ex.: "1,2,4". Vazio: qualquer um até <see cref="MaxColumns"/>.</summary>
    public string ColumnSteps
    {
        get => (string)GetValue(ColumnStepsProperty);
        set => SetValue(ColumnStepsProperty, value);
    }

    private int ColumnsFor(double width)
    {
        var n = LayoutMath.Columns(width, MinColumnWidth, MaxColumns, ColumnSpacing);
        if (string.IsNullOrWhiteSpace(ColumnSteps))
            return n;
        var steps = ColumnSteps.Split(',').Select(s => int.TryParse(s, out var v) ? v : 0).ToList();
        return LayoutMath.SnapColumns(n, steps);
    }

    private List<UIElement> Visible() => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Visible();

        var columns = ColumnsFor(availableSize.Width);
        var columnWidth = double.IsFinite(availableSize.Width)
            ? LayoutMath.ColumnWidth(availableSize.Width, columns, ColumnSpacing)
            : MinColumnWidth;

        var rowHeights = new List<double>();
        for (var i = 0; i < items.Count; i++)
        {
            items[i].Measure(new Size(columnWidth, double.PositiveInfinity));
            if (i % columns == 0)
                rowHeights.Add(0);
            rowHeights[^1] = Math.Max(rowHeights[^1], items[i].DesiredSize.Height);
        }

        var height = rowHeights.Sum() + RowSpacing * Math.Max(0, rowHeights.Count - 1);
        var usedColumns = Math.Min(columns, Math.Max(1, items.Count));
        var width = double.IsFinite(availableSize.Width)
            ? availableSize.Width
            : usedColumns * columnWidth + ColumnSpacing * (usedColumns - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var items = Visible();
        var columns = ColumnsFor(finalSize.Width);
        var columnWidth = LayoutMath.ColumnWidth(finalSize.Width, columns, ColumnSpacing);
        var y = 0.0;
        for (var start = 0; start < items.Count; start += columns)
        {
            var row = items.Skip(start).Take(columns).ToList();
            var rowHeight = row.Max(c => c.DesiredSize.Height);
            for (var i = 0; i < row.Count; i++)
                row[i].Arrange(new Rect(i * (columnWidth + ColumnSpacing), y, columnWidth, rowHeight));
            y += rowHeight + RowSpacing;
        }
        return finalSize;
    }
}
