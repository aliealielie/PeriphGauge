using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DrawingPoint = System.Drawing.Point;
using Screen = System.Windows.Forms.Screen;

namespace WirelessBattery;

public partial class MainWindow : Window
{
    private DrawingPoint? trayAnchor;
    private bool syncingOptions;

    internal event EventHandler? RefreshRequested;
    internal event EventHandler? WidgetToggleRequested;
    internal event EventHandler? StartupToggleRequested;
    internal event EventHandler? TopmostToggleRequested;
    internal event EventHandler? DesktopShortcutRequested;
    internal bool AllowClose { get; set; }
    internal bool IsPreview { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Dispatcher.InvokeAsync(PositionNearTray, DispatcherPriority.Loaded);
        Deactivated += (_, _) =>
        {
            if (!IsPreview && !OptionsMenu.IsOpen)
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
        OptionsButton.IsEnabled = !IsPreview;
        ModeText.Text = IsPreview ? "示例数据" : "自动刷新";
        UpdatedText.Text = IsPreview ? "界面预览" : $"更新于 {scannedAt:HH:mm}";
        if (devices.Count == 0)
        {
            Summary.Text = "未发现无线设备";
            ShowPlaceholder("暂无可显示的设备", "唤醒蓝牙设备，或检查 2.4 GHz 接收器是否已插好，然后点击刷新。");
            return;
        }

        int current = devices.Count(device => device.IsCurrent);
        Summary.Text = $"{devices.Count} 台设备 · 本次读到 {current} 台电量";
        DeviceItems.ItemsSource = devices
            .GroupBy(device => device.Connection)
            .OrderBy(group => group.Key == "蓝牙" ? 0 : 1)
            .Select(group => new DeviceGroup(
                group.Key == "2.4G 接收器" ? "2.4 GHz 接收器" : group.Key,
                (Geometry)FindResource(group.Key == "蓝牙" ? "GlyphBluetooth" : "GlyphReceiver"),
                group.Select(ToRow).OrderBy(row => row.Value ?? 101).ThenBy(row => row.Name).ToArray()))
            .ToArray();
        DeviceScroll.Visibility = Visibility.Visible;
        Placeholder.Visibility = Visibility.Collapsed;
    }

    internal void SetOptions(bool widgetVisible, bool startsWithWindows, bool widgetTopmost)
    {
        syncingOptions = true;
        try
        {
            WidgetOption.IsChecked = widgetVisible;
            StartupOption.IsChecked = startsWithWindows;
            TopmostOption.IsChecked = widgetTopmost;
        }
        finally
        {
            syncingOptions = false;
        }
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
            if (OptionsMenu.IsOpen)
                OptionsMenu.IsOpen = false;
            else
                DismissPanel();
        }
        base.OnPreviewKeyDown(e);
    }

    private void RefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void OptionsClick(object sender, RoutedEventArgs e)
    {
        OptionsMenu.PlacementTarget = OptionsButton;
        OptionsMenu.IsOpen = true;
    }

    private void OptionsClosed(object sender, RoutedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsPreview && !IsActive && !OptionsMenu.IsOpen)
                Hide();
        }, DispatcherPriority.Background);
    }

    private void WidgetChanged(object sender, RoutedEventArgs e)
    {
        if (!syncingOptions && !IsPreview)
            WidgetToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (!syncingOptions && !IsPreview)
            StartupToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void TopmostChanged(object sender, RoutedEventArgs e)
    {
        if (!syncingOptions && !IsPreview)
            TopmostToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void DesktopShortcutClick(object sender, RoutedEventArgs e) => DesktopShortcutRequested?.Invoke(this, EventArgs.Empty);

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
        string kindLabel = device.Kind switch
        {
            "keyboard" => "键盘",
            "mouse" => "鼠标",
            "headphones" => "耳机",
            "controller" => "手柄",
            _ => "无线设备"
        };

        int? value = device.Percent is >= 0 and <= 100 ? device.Percent : null;
        string readingNote = device.ReadingNote;
        string detail = readingNote.Length > 0 ? $"{kindLabel} · {readingNote}" : value switch
        {
            null => $"{kindLabel} · 未上报电量",
            <= BatteryRingVisual.LowThreshold => $"{kindLabel} · 低电量",
            _ => kindLabel
        };
        var (ringArc, ringTone, ringTrack) = BatteryRingVisual.Create(value);
        return new DeviceRow(device.Name, device.Kind, detail, value, BatteryRingVisual.ReadingLabel(device), device.LastKnownAt.HasValue,
            ringArc, ringTone, ringTrack);
    }

    private sealed record DeviceGroup(string Name, Geometry Icon, DeviceRow[] Rows);

    private sealed record DeviceRow(
        string Name, string Kind, string Detail, int? Value, string BatteryLabel, bool IsHistorical,
        Geometry RingArc, string RingTone, string RingTrack)
    {
        public string Battery => Value is int percent ? $"{percent}%" : "—";
        public string BatteryTone => IsHistorical ? "#B6B6B6" : Value.HasValue ? "#FFFFFF" : "#969696";
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
