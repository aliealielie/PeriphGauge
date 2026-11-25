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

    internal static (bool Visible, Point? Position) Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        string? coordinates = key?.GetValue(Location) as string;
        var parts = coordinates?.Split(',');
        Point? position = parts is { Length: 2 }
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
            ? new Point(x, y)
            : null;
        return (key?.GetValue(Visible) is int and 1, position);
    }

    internal static void SetVisible(bool visible)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(Visible, visible ? 1 : 0, RegistryValueKind.DWord);
    }

    internal static void SetPosition(Point point)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(Location, FormattableString.Invariant($"{point.X},{point.Y}"), RegistryValueKind.String);
    }
}
