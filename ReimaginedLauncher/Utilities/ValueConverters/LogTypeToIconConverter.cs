using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons;

namespace ReimaginedLauncher.Utilities.ValueConverters;

public class LogTypeToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string type)
        {
            return type.ToLower() switch
            {
                "success" => MaterialIconKind.CheckCircle,
                "warning" => MaterialIconKind.Alert,
                "error" => MaterialIconKind.Error,
                "log" => MaterialIconKind.History,
                _ => MaterialIconKind.Information
            };
        }
        return MaterialIconKind.Information;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
