using Avalonia;

namespace ReimaginedLauncher.Utilities.ValueConverters;

using System;
using Avalonia.Data.Converters;
using System.Globalization;

public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b ? !b : AvaloniaProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b ? !b : AvaloniaProperty.UnsetValue;
    }
}
