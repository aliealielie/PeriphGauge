using System;
using System.Windows;
using System.Windows.Media;

namespace WirelessBattery;

internal static class BatteryRingVisual
{
    internal const int LowThreshold = 20;
    internal const string Green = "#50E65B";
    internal const string Red = "#FF484C";

    internal static (Geometry Arc, string Tone, string Track) Create(int? value) =>
        (BatteryArc(value), value switch { null => "#777777", <= LowThreshold => Red, _ => Green },
            value == 0 ? "#7F2C33" : "#353535");

    private static Geometry BatteryArc(int? value)
    {
        const double center = 14;
        const double radius = 11;
        if (value is not int percent || percent == 0)
            return Geometry.Empty;

        if (percent == 100)
        {
            var circle = new EllipseGeometry(new Point(center, center), radius, radius);
            circle.Freeze();
            return circle;
        }

        double angle = percent / 100d * Math.Tau;
        var end = new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));
        var arc = new StreamGeometry();
        using (var path = arc.Open())
        {
            path.BeginFigure(new Point(center, center - radius), isFilled: false, isClosed: false);
            path.ArcTo(end, new Size(radius, radius), rotationAngle: 0,
                isLargeArc: percent > 50, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        arc.Freeze();
        return arc;
    }
}
