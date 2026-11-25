using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;

namespace WirelessBattery;

internal sealed class BatteryTray : IDisposable
{
    private const int MaxTooltipLength = 127;
    private readonly Forms.NotifyIcon notifyIcon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Icon baseIcon;
    private Icon? batteryIcon;
    private int? displayedPercent;

    internal event EventHandler<Point?>? OpenRequested;
    internal event EventHandler? RefreshRequested;
    internal event EventHandler? ExitRequested;

    internal BatteryTray()
    {
        using var iconStream = Application.GetResourceStream(
            new Uri("pack://application:,,,/App.ico")).Stream;
        baseIcon = new Icon(iconStream);
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开电量面板", null, (_, _) => OpenRequested?.Invoke(this, null));
        menu.Items.Add("立即刷新", null, (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        notifyIcon = new Forms.NotifyIcon
        {
            Icon = baseIcon,
            Text = "外设电量\n正在读取电量…",
            ContextMenuStrip = menu,
            Visible = true
        };
        notifyIcon.MouseClick += (_, click) =>
        {
            if (click.Button == Forms.MouseButtons.Left)
                OpenRequested?.Invoke(this, Forms.Cursor.Position);
        };
    }

    internal void Update(IReadOnlyList<BatteryDevice> devices)
    {
        var measured = devices.Where(device => device.Percent is >= 0 and <= 100)
            .OrderBy(device => device.Percent).ToArray();
        SetPercent(measured.FirstOrDefault()?.Percent);

        string tooltip = measured.Length > 0 ? "外设电量 · 最低 " + displayedPercent + "%" : Program.Name;
        if (devices.Count == 0)
            tooltip += "\n暂无已连接的无线设备";

        foreach (var device in measured.Concat(devices.Where(device => device.Percent is null)))
        {
            string connection = device.Connection == "2.4G 接收器" ? "2.4G" : device.Connection;
            string name = device.Name.Length > 35 ? device.Name[..34] + "…" : device.Name;
            string suffix = device.Percent is int percent ? percent + "%" : "电量未知";
            string line = $"\n{name} · {connection} · {suffix}";
            if (tooltip.Length + line.Length > MaxTooltipLength)
                break;
            tooltip += line;
        }
        notifyIcon.Text = tooltip;
    }

    internal void ShowError()
    {
        SetPercent(null);
        notifyIcon.Text = "外设电量\n暂时无法读取电量 · 单击可打开面板";
    }

    private void SetPercent(int? percent)
    {
        if (displayedPercent == percent)
            return;

        var oldIcon = batteryIcon;
        batteryIcon = percent is int level ? DrawBatteryIcon(level) : null;
        notifyIcon.Icon = batteryIcon ?? baseIcon;
        displayedPercent = percent;
        oldIcon?.Dispose();
    }

    private static Icon DrawBatteryIcon(int level)
    {
        // Render oversize so Windows can scale the same icon for different tray DPIs.
        using var bitmap = new Bitmap(48, 48);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            Color accent = level <= 20 ? Color.FromArgb(255, 179, 149) : Color.FromArgb(155, 247, 207);
            using var outline = new Pen(Color.FromArgb(45, 70, 69), 1.4f);
            using var background = new SolidBrush(Color.FromArgb(255, 16, 28, 35));
            using var foreground = new SolidBrush(accent);
            using var badge = new GraphicsPath();
            badge.AddArc(1, 1, 19, 19, 180, 90);
            badge.AddArc(28, 1, 19, 19, 270, 90);
            badge.AddArc(28, 28, 19, 19, 0, 90);
            badge.AddArc(1, 28, 19, 19, 90, 90);
            badge.CloseFigure();
            graphics.FillPath(background, badge);
            graphics.DrawPath(outline, badge);
            using var font = new Font("Segoe UI", level == 100 ? 23 : 31, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(level.ToString(), font, foreground, new RectangleF(0, 1, 47, 38), format);
            using var track = new SolidBrush(Color.FromArgb(49, 135, 151, 143));
            graphics.FillRectangle(track, 11, 39, 26, 3);
            graphics.FillRectangle(foreground, 11, 39, 26 * level / 100f, 3);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        menu.Dispose();
        batteryIcon?.Dispose();
        baseIcon.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
