using System;
using System.Drawing;
using System.Globalization;
using Microsoft.Win32;

namespace WirelessBattery;

internal static class WidgetSettings
{
    private const string KeyPath = @"Software\PeriphGauge";
    private const string Visible = "WidgetVisible";
    private const string Location = "WidgetLocation";
    private const string Topmost = "WidgetTopmost";
    private const string Expanded = "WidgetExpanded";

    internal static (bool Visible, Point? Position, bool Topmost, bool Expanded) Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        string? coordinates = key?.GetValue(Location) as string;
        var parts = coordinates?.Split(',');
        Point? position = parts is { Length: 2 }
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
            ? new Point(x, y)
            : null;
        return (key?.GetValue(Visible) is 1, position,
            key?.GetValue(Topmost) is not 0, key?.GetValue(Expanded) is 1);
    }

    internal static void SetVisible(bool visible) => SetFlag(Visible, visible);
    internal static void SetTopmost(bool topmost) => SetFlag(Topmost, topmost);
    internal static void SetExpanded(bool expanded) => SetFlag(Expanded, expanded);

    private static void SetFlag(string name, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(name, enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    internal static void SetPosition(Point point)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(Location, FormattableString.Invariant($"{point.X},{point.Y}"), RegistryValueKind.String);
    }
}
