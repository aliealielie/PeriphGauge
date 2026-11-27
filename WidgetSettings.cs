using System;
using System.Drawing;
using System.Globalization;
using Microsoft.Win32;

namespace WirelessBattery;

internal enum DockEdge { None, Left, Right }

internal static class WidgetSettings
{
    private const string KeyPath = @"Software\PeriphGauge";
    private const string Visible = "WidgetVisible";
    private const string Location = "WidgetLocation";
    private const string Topmost = "WidgetTopmost";
    private const string Expanded = "WidgetExpanded";

    internal static (bool Visible, Point? Position, bool Topmost, bool Expanded, DockEdge Edge) Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        var (position, edge) = ParsePlacement(key?.GetValue(Location) as string);
        return (key?.GetValue(Visible) is 1, position,
            key?.GetValue(Topmost) is not 0, key?.GetValue(Expanded) is 1, edge);
    }

    private static (Point? Position, DockEdge Edge) ParsePlacement(string? coordinates)
    {
        var parts = coordinates?.Split(',', 3);
        if (parts is not { Length: >= 2 }
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
        {
            return (null, DockEdge.None);
        }

        var edge = (parts.Length == 3 ? parts[2] : null) switch
        {
            nameof(DockEdge.Left) => DockEdge.Left,
            nameof(DockEdge.Right) => DockEdge.Right,
            _ => DockEdge.None
        };
        return (new Point(x, y), edge);
    }

    internal static void SetVisible(bool visible) => SetFlag(Visible, visible);
    internal static void SetTopmost(bool topmost) => SetFlag(Topmost, topmost);
    internal static void SetExpanded(bool expanded) => SetFlag(Expanded, expanded);

    private static void SetFlag(string name, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(name, enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    internal static void SetPosition(Point point, DockEdge edge)
    {
        string coordinates = FormattableString.Invariant($"{point.X},{point.Y}");
        if (edge is DockEdge.Left or DockEdge.Right)
            coordinates += $",{edge}";

        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(Location, coordinates, RegistryValueKind.String);
    }
}
