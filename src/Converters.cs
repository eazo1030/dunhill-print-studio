using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dunhill.PrintStudio;

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : DependencyProperty.UnsetValue;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : DependencyProperty.UnsetValue;
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True → Collapsed, null/false → Visible. Used to hide panels when no selection.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Compares the bound string (e.g. an element "Type") against a parameter (e.g. "text").</summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var want = parameter as string;
        var have = value as string;
        var eq = string.Equals(have, want, StringComparison.OrdinalIgnoreCase);
        return targetType == typeof(Visibility)
            ? (eq ? Visibility.Visible : Visibility.Collapsed)
            : (object)eq;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Scales printer dots (typical 20-300 range) to on-screen font pixels for the preview canvas.</summary>
public sealed class DotToFontSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int dots) return Math.Max(8, dots * 0.5);
        if (value is string s && int.TryParse(s, out var n)) return Math.Max(8, n * 0.5);
        return 12.0;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True → Visible, false → Collapsed. WPF has no built-in bool→Visibility converter.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? (b ? Visibility.Visible : Visibility.Collapsed) : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>Barcode element rendered width: barcode height × 1.6 ratio (Code 128 typical aspect).</summary>
public sealed class BarcodeWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int h) return Math.Max(20, h * 1.6);
        return 60.0;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
