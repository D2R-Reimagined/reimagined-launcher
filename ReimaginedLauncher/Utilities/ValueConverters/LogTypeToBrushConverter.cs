using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ReimaginedLauncher.Utilities.ValueConverters;

public class LogTypeToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string type)
        {
            return type.ToLower() switch
            {
                "success" => new SolidColorBrush(Color.Parse("#4CAF50")),
                "warning" => new SolidColorBrush(Color.Parse("#FFA000")),
                "error" => new SolidColorBrush(Color.Parse("#F44336")),
                "log" => new SolidColorBrush(Color.Parse("#888")),
                _ => new SolidColorBrush(Color.Parse("#2196F3"))
            };
        }
        return new SolidColorBrush(Color.Parse("#2196F3"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
