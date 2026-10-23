using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WslVision.Models;
using WslVision.Services.Docker;

namespace WslVision.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is true;
        return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible ^ Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value is not null && (value is not string s || s.Length > 0);
        return has ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Liga um RadioButton a um valor de enum: IsChecked="{Binding Filter, ConverterParameter=Running}".</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string p && string.Equals(value.ToString(), p, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string p ? Enum.Parse(targetType, p) : Binding.DoNothing;
}


public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>true quando há valor (texto não vazio); útil para abrir um InfoBar de erro.</summary>
public sealed class NotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && (value is not string s || s.Length > 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Qual pincel do tom: o ponto/texto, o fundo suave dos selos ou o anel translúcido em volta do ponto.</summary>
public enum TonePart
{
    Dot,
    Soft,
    Halo,
}

/// <summary>
/// Cor de um estado a partir do <see cref="Tone"/> (ou do status de um container, que vira tom no <see cref="DockerText.ToneOf"/>).
/// Os pincéis são os tokens Wv.Green, Wv.GreenSoft, Wv.GreenHalo etc.
/// </summary>
public sealed class ToneBrushConverter : IValueConverter
{
    public TonePart Part { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tone = value switch
        {
            Tone t => t,
            ContainerStatusKind k => DockerText.ToneOf(k),
            _ => Tone.Off,
        };
        var color = tone switch
        {
            Tone.Ok => "Green",
            Tone.Work => "Amber",
            Tone.Error => "Red",
            _ => "Gray",
        };
        var suffix = Part == TonePart.Dot ? "" : Part.ToString();
        return Application.Current.TryFindResource($"Wv.{color}{suffix}") as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true quando a coleção (Count) está vazia.</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is int n && n == 0) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true quando todos os valores são iguais (ex.: o local da lista é a pasta aberta).</summary>
public sealed class AllEqualConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length > 1 && values.Skip(1).All(v => Equals(v, values[0]));

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Alinhamento de um bloco de ações: à direita ao lado do conteúdo; à esquerda quando o SplitPanel empilha (true).</summary>
public sealed class EndAlignConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? HorizontalAlignment.Left : HorizontalAlignment.Right;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
