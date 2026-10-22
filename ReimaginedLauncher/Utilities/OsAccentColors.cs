using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace ReimaginedLauncher.Utilities;

internal static class OsAccentColors
{
    internal static void Initialize(Application application)
    {
        var platform = application.PlatformSettings;
        Apply(application, platform?.GetColorValues().AccentColor1 ?? Color.FromRgb(0, 120, 212));
        if (platform is not null)
            platform.ColorValuesChanged += (_, colors) =>
                Dispatcher.UIThread.Post(() => Apply(application, colors.AccentColor1));
    }

    private static void Apply(Application application, Color accent)
    {
        var opaque = Color.FromRgb(accent.R, accent.G, accent.B);
        application.Resources["LaunchAccentBrush"] = new SolidColorBrush(opaque);
        application.Resources["LaunchAccentForeground"] = new SolidColorBrush(GetForeground(opaque));
    }

    internal static Color GetForeground(Color accent)
    {
        static double Linear(byte component)
        {
            var value = component / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Linear(accent.R) + 0.7152 * Linear(accent.G) + 0.0722 * Linear(accent.B);
        return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05) ? Colors.Black : Colors.White;
    }
}
