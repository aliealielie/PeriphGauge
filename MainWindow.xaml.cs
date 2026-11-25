using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DrawingPoint = System.Drawing.Point;
using Screen = System.Windows.Forms.Screen;

namespace WirelessBattery;

public partial class MainWindow : Window
{
    private DrawingPoint? trayAnchor;

    internal event EventHandler? RefreshRequested;
    internal bool AllowClose { get; set; }
    internal bool IsPreview { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Dispatcher.InvokeAsync(PositionNearTray, DispatcherPriority.Loaded);
        Deactivated += (_, _) =>
        {
            if (!IsPreview)
                Hide();
        };
        SetScanning();
    }

    internal void SetScanning()
    {
        RefreshButton.IsEnabled = false;
        if (DeviceScroll.Visibility == Visibility.Visible)
        {
            UpdatedText.Text = "正在刷新…";
            return;
        }

        Summary.Text = "正在扫描无线设备…";
        UpdatedText.Text = string.Empty;
        ShowPlaceholder("正在读取电量", "扫描可能需要几秒。");
    }

    internal void SetDevices(IReadOnlyList<BatteryDevice> devices, DateTime scannedAt)
    {
        RefreshButton.IsEnabled = !IsPreview;
        ModeText.Text = IsPreview ? "示例数据" : "自动刷新";
        UpdatedText.Text = IsPreview ? "界面预览" : $"更新于 {scannedAt:HH:mm}";
        if (devices.Count == 0)
        {
            Summary.Text = "未发现无线设备";
            ShowPlaceholder("暂无可显示的设备", "唤醒蓝牙设备，或检查 2.4G 接收器是否已插好，然后点击刷新。");
            return;
        }

        int known = devices.Count(device => device.Percent is >= 0 and <= 100);
        Summary.Text = $"{devices.Count} 台设备 · {known} 台有电量";
        DeviceItems.ItemsSource = devices
            .GroupBy(device => device.Connection)
            .OrderBy(group => group.Key == "蓝牙" ? 0 : 1)
            .Select(group => new DeviceGroup(
                group.Key,
                group.Key == "蓝牙" ? "#A4C9FA" : "#93E9C5",
                group.Key == "蓝牙"
                    ? "M 4,4 L 14,14 L 9,18 L 9,0 L 14,4 L 4,14"
                    : "M 4,8 L 14,8 A 2,2 0 0 1 16,10 L 16,16 L 2,16 L 2,10 A 2,2 0 0 1 4,8 Z M 6,8 L 6,2 L 12,2 L 12,8 M 8,4 L 10,4",
                group.Select(ToRow).OrderBy(row => row.Value ?? 101).ThenBy(row => row.Name).ToArray()))
            .ToArray();
        DeviceScroll.Visibility = Visibility.Visible;
        Placeholder.Visibility = Visibility.Collapsed;
    }

    internal void SetError()
    {
        RefreshButton.IsEnabled = true;
        Summary.Text = "读取电量失败";
        UpdatedText.Text = "等待刷新";
        ShowPlaceholder("暂时无法读取设备", "点击刷新重试。");
    }

    internal void OpenNear(DrawingPoint? anchor = null)
    {
        trayAnchor = anchor;
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            UpdateLayout();
            PositionNearTray();
            Opacity = 1;
        }
        else
        {
            PositionNearTray();
        }
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DismissPanel();
        }
        base.OnPreviewKeyDown(e);
    }

    private void RefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void HideClick(object sender, RoutedEventArgs e) => DismissPanel();

    private void DismissPanel()
    {
        if (IsPreview)
            Close();
        else
            Hide();
    }

    private void ShowPlaceholder(string title, string detail)
    {
        DeviceItems.ItemsSource = null;
        DeviceScroll.Visibility = Visibility.Collapsed;
        PlaceholderTitle.Text = title;
        PlaceholderText.Text = detail;
        Placeholder.Visibility = Visibility.Visible;
    }

    private void PositionNearTray()
    {
        if (!IsVisible)
            return;

        Screen? screen = trayAnchor is DrawingPoint anchor ? Screen.FromPoint(anchor) : Screen.PrimaryScreen;
        if (screen is null)
            return;

        IntPtr window = new WindowInteropHelper(this).Handle;
        var area = screen.WorkingArea;
        const uint noSizeOrActivation = 0x0001 | 0x0004 | 0x0010;
        // A move across monitors can change the window's physical size after a DPI change.
        for (int pass = 0; pass < 2; pass++)
        {
            if (!GetWindowRect(window, out var bounds))
                return;

            int width = bounds.Right - bounds.Left;
            int height = bounds.Bottom - bounds.Top;
            int right = trayAnchor is DrawingPoint point ? point.X + 32 : area.Right;
            int x = Math.Clamp(right - width, area.Left, Math.Max(area.Left, area.Right - width));
            int y = trayAnchor is DrawingPoint cursor && cursor.Y <= area.Top
                ? area.Top
                : Math.Max(area.Top, area.Bottom - height);
            SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, noSizeOrActivation);
        }
    }

    private static DeviceRow ToRow(BatteryDevice device)
    {
        var (kind, icon) = device.Kind switch
        {
            "keyboard" => ("键盘", "M 3,5 L 21,5 Q 23,5 23,7 L 23,17 Q 23,19 21,19 L 3,19 Q 1,19 1,17 L 1,7 Q 1,5 3,5 Z M 5,9 L 6,9 M 10,9 L 11,9 M 15,9 L 16,9 M 19,9 L 20,9 M 5,13 L 6,13 M 10,13 L 11,13 M 15,13 L 16,13 M 19,13 L 20,13 M 8,16 L 16,16"),
            "mouse" => ("鼠标", "M 12,2 A 7,7 0 0 1 19,9 L 19,15 A 7,7 0 0 1 5,15 L 5,9 A 7,7 0 0 1 12,2 Z M 12,6 L 12,10"),
            "headphones" => ("耳机", "M 4,15 L 4,11 A 8,8 0 0 1 20,11 L 20,15 M 4,13 L 6,13 Q 8,13 8,15 L 8,19 L 6,19 Q 3,19 3,16 L 3,15 Q 3,13 4,13 Z M 20,13 L 18,13 Q 16,13 16,15 L 16,19 L 18,19 Q 21,19 21,16 L 21,15 Q 21,13 20,13 Z"),
            "controller" => ("手柄", "M 6,7 L 18,7 Q 20,7 21,10 L 23,16 Q 24,21 20,20 L 15,16 L 9,16 L 4,20 Q 0,21 1,16 L 3,10 Q 4,7 6,7 Z M 6,12 L 10,12 M 8,10 L 8,14 M 16,11 L 16,11.3 M 19,13 L 19,13.3"),
            _ => ("无线设备", "M 12,3 L 21,8 L 21,16 L 12,21 L 3,16 L 3,8 Z M 3,8 L 12,13 L 21,8 M 12,13 L 12,21")
        };

        int? value = device.Percent is >= 0 and <= 100 ? device.Percent : null;
        string detail = value.HasValue ? kind : $"{kind} · 未上报电量";
        string tone = value switch { null => "#A9B7BD", <= 20 => "#FFAE99", _ => "#93E9C5" };
        return new DeviceRow(device.Name, detail, icon, value, tone);
    }

    private sealed record DeviceGroup(string Name, string Tone, string Icon, DeviceRow[] Rows);

    private sealed record DeviceRow(string Name, string Detail, string Icon, int? Value, string Tone)
    {
        public bool HasBattery => Value.HasValue;
        public string Number => Value?.ToString() ?? "—";
        public string Unit => HasBattery ? "%" : string.Empty;
        public string BatteryLabel => HasBattery ? $"电量 {Number}%" : "未上报电量";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out WindowRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
