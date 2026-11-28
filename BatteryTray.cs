using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace WirelessBattery;

internal sealed class BatteryTray : IDisposable
{
    private const int MaxTooltipLength = 127;
    private readonly Forms.NotifyIcon notifyIcon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Forms.ToolStripMenuItem widgetItem;
    private readonly Forms.ToolStripMenuItem topmostItem;
    private readonly Forms.ToolStripMenuItem startupItem;
    private readonly Forms.ToolStripMenuItem lowBatteryAlertsItem;
    private readonly Icon baseIcon;
    private Icon? batteryIcon;
    private int? displayedPercent;

    internal event EventHandler<Point?>? OpenRequested;
    internal event EventHandler? RefreshRequested;
    internal event EventHandler? WidgetToggleRequested;
    internal event EventHandler? TopmostToggleRequested;
    internal event EventHandler? StartupToggleRequested;
    internal event EventHandler? LowBatteryAlertsToggleRequested;
    internal event EventHandler? DesktopShortcutRequested;
    internal event EventHandler? ExitRequested;

    internal BatteryTray()
    {
        baseIcon = DrawBatteryIcon(null);
        var darkRenderer = new TrayMenuRenderer();
        menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        menu.Opening += (_, _) =>
        {
            if (Forms.SystemInformation.HighContrast)
            {
                menu.RenderMode = Forms.ToolStripRenderMode.System;
                menu.ForeColor = SystemColors.MenuText;
            }
            else
            {
                menu.Renderer = darkRenderer;
                menu.ForeColor = Color.White;
            }
        };
        menu.Items.Add("打开电量面板", null, (_, _) => OpenRequested?.Invoke(this, null));
        menu.Items.Add("立即刷新", null, (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        widgetItem = new Forms.ToolStripMenuItem("显示桌面悬浮窗");
        widgetItem.Click += (_, _) => WidgetToggleRequested?.Invoke(this, EventArgs.Empty);
        topmostItem = new Forms.ToolStripMenuItem("悬浮窗置顶");
        topmostItem.Click += (_, _) => TopmostToggleRequested?.Invoke(this, EventArgs.Empty);
        startupItem = new Forms.ToolStripMenuItem("开机自启动");
        startupItem.Click += (_, _) => StartupToggleRequested?.Invoke(this, EventArgs.Empty);
        lowBatteryAlertsItem = new Forms.ToolStripMenuItem("低电量提醒");
        lowBatteryAlertsItem.Click += (_, _) => LowBatteryAlertsToggleRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(widgetItem);
        menu.Items.Add(topmostItem);
        menu.Items.Add(startupItem);
        menu.Items.Add(lowBatteryAlertsItem);
        menu.Items.Add("创建桌面快捷方式", null, (_, _) => DesktopShortcutRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        notifyIcon = new Forms.NotifyIcon
        {
            Icon = baseIcon,
            Text = "外设电量\n正在读取电量…",
            ContextMenuStrip = menu,
            BalloonTipTitle = "外设电量 · 低电量提醒",
            BalloonTipIcon = Forms.ToolTipIcon.Warning,
            Visible = true
        };
        notifyIcon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke(this, null);
        notifyIcon.MouseClick += (_, click) =>
        {
            if (click.Button == Forms.MouseButtons.Left)
                OpenRequested?.Invoke(this, Forms.Cursor.Position);
        };
    }

    internal void SetOptions(bool widgetVisible, bool startsWithWindows, bool widgetTopmost, bool lowBatteryAlerts)
    {
        widgetItem.Checked = widgetVisible;
        topmostItem.Checked = widgetTopmost;
        startupItem.Checked = startsWithWindows;
        lowBatteryAlertsItem.Checked = lowBatteryAlerts;
    }

    internal void ShowLowBatteryAlert(IReadOnlyList<BatteryDevice> devices)
    {
        if (devices.Count == 0)
            return;

        const int visibleDevices = 3;
        string text = string.Join("\n", devices.Take(visibleDevices)
            .Select(device => $"{ShortDeviceName(device.Name)} 剩余 {device.Percent}%"));
        if (devices.Count > visibleDevices)
            text += $"\n另有 {devices.Count - visibleDevices} 台设备电量较低";
        notifyIcon.BalloonTipText = text;
        notifyIcon.ShowBalloonTip(5000);
    }

    internal void Update(IReadOnlyList<BatteryDevice> devices)
    {
        var ordered = devices
            .OrderBy(device => device.IsCurrent ? 0 : device.LastKnownAt.HasValue ? 1 : 2)
            .ThenBy(device => ValidPercent(device) ?? 101)
            .ToArray();
        int? lowest = ordered.FirstOrDefault() is { IsCurrent: true } first ? ValidPercent(first) : null;
        SetPercent(lowest);

        string tooltip = lowest is int level ? $"外设电量 · 最低 {level}%" : Program.Name;
        if (devices.Count == 0)
            tooltip += "\n暂无已连接的无线设备";

        for (int i = 0; i < ordered.Length; i++)
        {
            var device = ordered[i];
            string connection = device.Connection == "2.4G 接收器" ? "2.4G" : device.Connection;
            string name = ShortDeviceName(device.Name);
            string suffix = ValidPercent(device) is int percent ? $"{percent}%" : "未上报电量";
            string note = device.ReadingNote;
            if (note.Length > 0)
                suffix += $" · {note}";
            string line = $"\n{name} · {connection} · {suffix}";
            int skipped = ordered.Length - i;
            string remaining = $"\n另有 {skipped} 台设备";
            int reserve = skipped > 1 ? remaining.Length : 0;
            if (tooltip.Length + line.Length + reserve > MaxTooltipLength)
            {
                tooltip += remaining;
                break;
            }
            tooltip += line;
        }
        notifyIcon.Text = tooltip;
    }

    private static string ShortDeviceName(string name)
    {
        name = name.Replace('\r', ' ').Replace('\n', ' ');
        return name.Length > 35 ? name[..(char.IsHighSurrogate(name[33]) ? 33 : 34)] + "…" : name;
    }

    private static int? ValidPercent(BatteryDevice device) => device.Percent is >= 0 and <= 100 ? device.Percent : null;

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

    private static Icon DrawBatteryIcon(int? level)
    {
        // Render oversize so Windows can scale the same icon for different tray DPIs.
        using var bitmap = new Bitmap(48, 48);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);

            using var outline = new Pen(Color.FromArgb(86, 86, 86), 1.5f);
            using var background = new SolidBrush(Color.FromArgb(13, 13, 13));
            using var foreground = new SolidBrush(level.HasValue ? Color.White : Color.FromArgb(192, 192, 192));
            using var badge = new GraphicsPath();
            badge.AddArc(1, 1, 19, 19, 180, 90);
            badge.AddArc(28, 1, 19, 19, 270, 90);
            badge.AddArc(28, 28, 19, 19, 0, 90);
            badge.AddArc(1, 28, 19, 19, 90, 90);
            badge.CloseFigure();
            graphics.FillPath(background, badge);
            graphics.DrawPath(outline, badge);
            using var font = new Font("Segoe UI", level switch { 100 => 22, >= 10 => 29, _ => 32 },
                FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };
            graphics.DrawString(level?.ToString() ?? "—", font, foreground, new RectangleF(1, 0, 46, 38), format);
            if (level is int percent)
            {
                Color accent = ColorTranslator.FromHtml(percent <= BatteryRingVisual.LowThreshold
                    ? BatteryRingVisual.Red : BatteryRingVisual.Green);
                using var status = new Pen(accent, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                graphics.DrawLine(status, 16, 40, 32, 40);
            }
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

    private sealed class TrayMenuRenderer() : Forms.ToolStripProfessionalRenderer(new TrayMenuColors())
    {
        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            Rectangle box = e.ImageRectangle;
            float scale = box.Width / 16f;
            using var check = new Pen(Color.White, 1.6f * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var previousSmoothing = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(check,
            [
                new PointF(box.Left + 3 * scale, box.Top + 8 * scale),
                new PointF(box.Left + 7 * scale, box.Top + 12 * scale),
                new PointF(box.Left + 13 * scale, box.Top + 5 * scale)
            ]);
            e.Graphics.SmoothingMode = previousSmoothing;
        }
    }

    private sealed class TrayMenuColors : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(24, 24, 24);
        public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
        public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
        public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
        public override Color MenuBorder => Color.FromArgb(75, 75, 75);
        public override Color MenuItemBorder => Color.FromArgb(100, 100, 100);
        public override Color MenuItemSelected => Color.FromArgb(51, 51, 51);
        public override Color SeparatorDark => MenuBorder;
        public override Color SeparatorLight => MenuBorder;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
