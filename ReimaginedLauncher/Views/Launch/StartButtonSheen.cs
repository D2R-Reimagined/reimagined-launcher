using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ReimaginedLauncher.Views.Launch;

public sealed class StartButtonSheen : Control
{
    public static readonly StyledProperty<double> SweepProgressProperty =
        AvaloniaProperty.Register<StartButtonSheen, double>(nameof(SweepProgress), 1);

    public double SweepProgress
    {
        get => GetValue(SweepProgressProperty);
        set => SetValue(SweepProgressProperty, value);
    }

    private readonly LinearGradientBrush _light = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Colors.Transparent, 0),
            new GradientStop(Color.FromArgb(58, 255, 239, 184), 0.5),
            new GradientStop(Colors.Transparent, 1)
        ]
    };

    static StartButtonSheen() => AffectsRender<StartButtonSheen>(SweepProgressProperty);

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 2 || Bounds.Height <= 2 || SweepProgress <= 0 || SweepProgress >= 1) return;
        var width = Math.Clamp(Bounds.Width * 0.3, 60, 120);
        var x = -width * 2 + SweepProgress * (Bounds.Width + width * 4);
        var band = new StreamGeometry();
        using (var geometry = band.Open())
        {
            geometry.BeginFigure(new Point(x, 0), true);
            geometry.LineTo(new Point(x + width, 0));
            geometry.LineTo(new Point(x + width - 24, Bounds.Height));
            geometry.LineTo(new Point(x - 24, Bounds.Height));
            geometry.EndFigure(true);
        }
        using (context.PushClip(new RoundedRect(new Rect(Bounds.Size).Deflate(1), 5)))
            context.DrawGeometry(_light, null, band);
    }
}
