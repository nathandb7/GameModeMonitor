using System;
using System.Windows;
using System.Windows.Media;

namespace GameModeMonitor.App;

public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - 4);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(29, 40, 61)), 5), center, radius, radius);
        var fraction = Math.Clamp(Value / 100, 0, 0.9999);
        if (fraction <= 0) return;
        var angle = fraction * Math.PI * 2;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            context.ArcTo(new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)), new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(62, 193, 247)), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
    }
}
