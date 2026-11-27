using System;
using System.Windows;
using System.Windows.Media;

namespace WirelessBattery;

internal static class BatteryRingVisual
{
    internal const int LowThreshold = 20;
    internal const string Green = "#50E65B";
    internal const string Red = "#FF484C";

    internal static string ReadingLabel(BatteryDevice device)
    {
        if (device.Percent is not (>= 0 and <= 100))
            return "未上报电量";

        string kind = device.IsCurrent && device.Percent <= LowThreshold ? "低电量" : "电量";
        string label = $"{kind} {device.Percent}%";
        string note = device.ReadingNote;
        return note.Length > 0 ? $"{label} · {note}" : label;
    }

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
