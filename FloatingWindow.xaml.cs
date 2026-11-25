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
using DrawingRectangle = System.Drawing.Rectangle;
using Screen = System.Windows.Forms.Screen;

namespace WirelessBattery;

public partial class FloatingWindow : Window
{
    private DrawingPoint? preferredLocation;
    private bool dragging;

    internal event EventHandler? HideRequested;
    internal event EventHandler<DrawingPoint>? PositionChangedByDrag;
    internal bool AllowClose { get; set; }

    public FloatingWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Dispatcher.InvokeAsync(() => PositionOnScreen(), DispatcherPriority.Loaded);
    }

    internal void SetDevices(IReadOnlyList<BatteryDevice> devices)
    {
        DeviceItems.ItemsSource = devices.Select(ToRow).ToArray();
        DeviceScroll.Visibility = devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Status.Visibility = devices.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        Status.Text = "暂无无线设备";
    }

    internal void SetError()
    {
        DeviceItems.ItemsSource = null;
        DeviceScroll.Visibility = Visibility.Collapsed;
        Status.Text = "暂时无法读取电量";
        Status.Visibility = Visibility.Visible;
    }

    internal void OpenAt(DrawingPoint? location)
    {
        preferredLocation = location;
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            UpdateLayout();
            PositionOnScreen();
            Opacity = 1;
        }
        else
        {
            PositionOnScreen();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(HandleMonitorChange);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            HideRequested?.Invoke(this, EventArgs.Empty);
        }
        base.OnClosing(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideRequested?.Invoke(this, EventArgs.Empty);
        }
        base.OnPreviewKeyDown(e);
    }

    private void HideClick(object sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);

    private void DragHandleClick(object sender, MouseButtonEventArgs e)
    {
        var window = new WindowInteropHelper(this).Handle;
        if (e.ClickCount != 1 || !GetWindowRect(window, out var before) || Mouse.LeftButton != MouseButtonState.Pressed)
            return;

        e.Handled = true;
        dragging = true;
        try
        {
            DragMove();
        }
        finally
        {
            dragging = false;
        }

        if (!GetWindowRect(window, out var after) || before.TopLeft == after.TopLeft)
            return;

        preferredLocation = after.TopLeft;
        PositionOnScreen(Screen.FromRectangle(DrawingRectangle.FromLTRB(after.Left, after.Top, after.Right, after.Bottom)));
        if (GetWindowRect(window, out var saved) && before.TopLeft != saved.TopLeft)
        {
            preferredLocation = saved.TopLeft;
            PositionChangedByDrag?.Invoke(this, saved.TopLeft);
        }
    }

    private IntPtr HandleMonitorChange(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int displayChanged = 0x007E;
        const int settingChanged = 0x001A;
        const int setWorkArea = 0x002F;
        if (message == displayChanged || (message == settingChanged && wParam == setWorkArea))
            Dispatcher.InvokeAsync(() => PositionOnScreen(), DispatcherPriority.Loaded);
        return IntPtr.Zero;
    }

    private void PositionOnScreen(Screen? screen = null)
    {
        if (!IsVisible || dragging)
            return;

        screen ??= preferredLocation is DrawingPoint point ? Screen.FromPoint(point) : Screen.PrimaryScreen;
        if (screen is null)
            return;

        var window = new WindowInteropHelper(this).Handle;
        var work = screen.WorkingArea;
        const uint noSizeOrActivation = 0x0001 | 0x0004 | 0x0010;
        // Moving to another monitor can resize the HWND, so clamp again using its new size.
        for (int pass = 0; pass < 2; pass++)
        {
            if (!GetWindowRect(window, out var rect))
                return;

            int right = Math.Max(work.Left, work.Right - (rect.Right - rect.Left));
            int bottom = Math.Max(work.Top, work.Bottom - (rect.Bottom - rect.Top));
            int offset = (int)(20 * GetDpiForWindow(window) / 96);
            var origin = preferredLocation ?? new DrawingPoint(right - offset, work.Top + offset);
            int x = Math.Clamp(origin.X, work.Left, right);
            int y = Math.Clamp(origin.Y, work.Top, bottom);
            if (rect.Left != x || rect.Top != y)
                SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, noSizeOrActivation);
        }
    }

    private static DeviceRow ToRow(BatteryDevice device)
    {
        int? value = device.Percent is >= 0 and <= 100 ? device.Percent : null;
        string batteryLabel = value is int percent ? $"电量 {percent}%" : "电量未知";
        string connection = device.Connection == "2.4G 接收器" ? "2.4G" : device.Connection;
        return new DeviceRow(device.Name, connection, device.Connection == "蓝牙" ? "#A4C9FA" : "#AAC2BD",
            value is int level ? $"{level}%" : "—",
            value switch { null => "#A9B7BD", <= 20 => "#FFAE99", _ => "#93E9C5" },
            batteryLabel, $"{device.Name} · {device.Connection} · {batteryLabel}");
    }

    private sealed record DeviceRow(
        string Name, string Connection, string ConnectionTone, string Battery, string BatteryTone,
        string BatteryLabel, string Description);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly DrawingPoint TopLeft => new(Left, Top);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out WindowRect bounds);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
