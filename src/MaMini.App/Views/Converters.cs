using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MaMini.App.Views;

/// <summary>Collapsed when the value is null, an empty string or zero.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or "" or 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps 0..100 to 0..ConverterParameter (a width in DIPs).</summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = value is int i ? i : value is double d ? d : 0;
        var full = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 100;
        return Math.Clamp(percent, 0, 100) / 100.0 * full;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
