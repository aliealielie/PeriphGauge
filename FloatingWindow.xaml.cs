using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using Screen = System.Windows.Forms.Screen;

namespace WirelessBattery;

public partial class FloatingWindow : Window
{
    private const double CompactWidth = 192;
    private const double CompactHeight = 68;
    private const double ExpandedWidth = 271;

    private DrawingPoint? preferredLocation;
    private PointerPress? pointerPress;
    private bool isExpanded;
    private bool dragging;

    internal event EventHandler? HideRequested;
    internal event EventHandler<bool>? ExpansionChangedByUser;
    internal event EventHandler<DrawingPoint>? PositionChangedByDrag;
    internal bool AllowClose { get; set; }

    public FloatingWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Dispatcher.InvokeAsync(() => PositionOnScreen(), DispatcherPriority.Loaded);
    }

    internal void SetDevices(IReadOnlyList<BatteryDevice> devices)
    {
        var rows = devices.Select((device, index) => ToRow(device, index > 0)).ToArray();
        DeviceCount.Text = $"{rows.Length} 台";
        DeviceItems.ItemsSource = rows;
        CompactItems.ItemsSource = rows
            .OrderBy(row => row.Kind switch { "keyboard" => 0, "mouse" => 1, _ => 2 })
            .Take(2)
            .ToArray();
        DeviceScroll.Visibility = devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Status.Visibility = devices.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        Status.Text = "暂无无线设备";
        CompactStatus.Visibility = Status.Visibility;
        CompactStatus.Text = "暂无设备";
    }

    internal void SetError()
    {
        DeviceCount.Text = string.Empty;
        DeviceItems.ItemsSource = null;
        CompactItems.ItemsSource = null;
        DeviceScroll.Visibility = Visibility.Collapsed;
        Status.Text = "暂时无法读取电量";
        Status.Visibility = Visibility.Visible;
        CompactStatus.Text = "读取失败";
        CompactStatus.Visibility = Visibility.Visible;
    }

    internal void OpenAt(DrawingPoint? location, bool expanded = false)
    {
        preferredLocation = location;
        SetExpanded(expanded, animate: false);
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        bool showing = !IsVisible;
        if (showing)
        {
            Opacity = 0;
            Show();
        }

        UpdateLayout();
        PositionOnScreen();
        if (showing)
            Opacity = 1;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        PositionOnScreen();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(HandleWindowMessage);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
            e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (isExpanded)
                SetExpandedByUser(false);
            else
                HideRequested?.Invoke(this, EventArgs.Empty);
        }
        base.OnPreviewKeyDown(e);
    }

    private void HideClick(object sender, RoutedEventArgs e) => HideRequested?.Invoke(this, EventArgs.Empty);

    private void ExpandClick(object sender, RoutedEventArgs e) => SetExpandedByUser(true);

    private void CollapseClick(object sender, RoutedEventArgs e) => SetExpandedByUser(false);

    private void SetExpandedByUser(bool expanded)
    {
        if (SetExpanded(expanded))
            ExpansionChangedByUser?.Invoke(this, expanded);
    }

    private bool SetExpanded(bool expanded, bool animate = true)
    {
        if (expanded == isExpanded)
            return false;

        double currentWidth = ActualWidth;
        isExpanded = expanded;
        CompactView.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandedView.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        double targetWidth = expanded ? ExpandedWidth : CompactWidth;
        Width = targetWidth;
        var activeView = expanded ? (FrameworkElement)ExpandedView : CompactView;
        if (animate && IsVisible && SystemParameters.ClientAreaAnimation)
        {
            var duration = TimeSpan.FromMilliseconds(160);
            BeginAnimation(WidthProperty, new DoubleAnimation(currentWidth, targetWidth, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
            activeView.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 1, duration)
            {
                FillBehavior = FillBehavior.Stop
            });
        }
        else
        {
            BeginAnimation(WidthProperty, null);
            activeView.BeginAnimation(OpacityProperty, null);
        }

        if (IsActive)
            (expanded ? (FrameworkElement)CollapseButton : CompactView).Focus();
        return true;
    }

    private void CompactMouseDown(object sender, MouseButtonEventArgs e) => StartPointerPress(CompactView, e);

    private void DragHandleClick(object sender, MouseButtonEventArgs e)
    {
        if (!e.Handled && sender is UIElement handle)
            StartPointerPress(handle, e);
    }

    private void StartPointerPress(UIElement handle, MouseButtonEventArgs e)
    {
        if (pointerPress is not null || e.ClickCount != 1)
            return;

        var window = new WindowInteropHelper(this).Handle;
        if (!GetCursorPos(out var cursor) || !GetWindowRect(window, out var bounds) || !handle.CaptureMouse())
            return;

        pointerPress = new PointerPress(handle, cursor, bounds.TopLeft);
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (pointerPress is { } press && e.LeftButton == MouseButtonState.Pressed && GetCursorPos(out var cursor)
            && TrackPointer(press, cursor))
            e.Handled = true;
    }

    private bool TrackPointer(PointerPress press, DrawingPoint cursor)
    {
        int dx = cursor.X - press.Cursor.X;
        int dy = cursor.Y - press.Cursor.Y;
        var window = new WindowInteropHelper(this).Handle;
        if (!dragging && Math.Abs(dx) < ToPixels(window, SystemParameters.MinimumHorizontalDragDistance)
            && Math.Abs(dy) < ToPixels(window, SystemParameters.MinimumVerticalDragDistance))
            return false;

        if (!dragging)
        {
            // Finish a morph before tracking the cursor so the window cannot resize mid-drag.
            BeginAnimation(WidthProperty, null);
            UpdateLayout();
            PositionOnScreen();
            if (GetWindowRect(window, out var bounds))
                press = pointerPress = press with { WindowOrigin = bounds.TopLeft };
            dragging = true;
        }

        MoveWindow(window, press.WindowOrigin.X + dx, press.WindowOrigin.Y + dy);
        return true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (pointerPress is not { } press)
            return;

        bool expand = !dragging && press.Handle == CompactView
            && new Rect(CompactView.RenderSize).Contains(e.GetPosition(CompactView));
        EndPointerPress();
        if (expand)
            SetExpandedByUser(true);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (pointerPress is { } press && !press.Handle.IsMouseCaptured)
            EndPointerPress();
    }

    private void EndPointerPress()
    {
        if (pointerPress is not { } press)
            return;

        bool moved = dragging;
        dragging = false;
        pointerPress = null;
        if (press.Handle.IsMouseCaptured)
            press.Handle.ReleaseMouseCapture();

        var window = new WindowInteropHelper(this).Handle;
        if (!moved || !GetWindowRect(window, out var bounds) || bounds.TopLeft == press.WindowOrigin)
            return;

        var screen = Screen.FromRectangle(DrawingRectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
        var work = screen.WorkingArea;
        int compactWidth = ToPixels(window, CompactWidth);
        int compactHeight = ToPixels(window, CompactHeight);
        // Persist the compact top-left even when the user drags the expanded island.
        var location = new DrawingPoint(
            Math.Clamp(bounds.Left + (bounds.Width - compactWidth) / 2, work.Left, Math.Max(work.Left, work.Right - compactWidth)),
            Math.Clamp(bounds.Top, work.Top, Math.Max(work.Top, work.Bottom - compactHeight)));
        bool changed = preferredLocation != location;
        preferredLocation = location;
        PositionOnScreen(screen);
        if (changed)
        {
            PositionChangedByDrag?.Invoke(this, location);
        }
    }

    private IntPtr HandleWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int displayChanged = 0x007E;
        const int settingChanged = 0x001A;
        const int setWorkArea = 0x002F;
        const int systemCommand = 0x0112;
        const int systemClose = 0xF060;
        if (message == systemCommand && (wParam.ToInt64() & 0xFFF0) == systemClose && !AllowClose)
        {
            handled = true;
            HideRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (message == displayChanged || (message == settingChanged && wParam == setWorkArea))
        {
            Dispatcher.InvokeAsync(() => PositionOnScreen(), DispatcherPriority.Loaded);
        }

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
        // Moving to another monitor can resize the HWND, so clamp again using its new size.
        for (int pass = 0; pass < 2; pass++)
        {
            if (!GetWindowRect(window, out var rect))
                return;

            int right = Math.Max(work.Left, work.Right - rect.Width);
            int bottom = Math.Max(work.Top, work.Bottom - rect.Height);
            int x = preferredLocation is DrawingPoint anchor
                ? anchor.X + (ToPixels(window, CompactWidth) - rect.Width) / 2
                : work.Left + (work.Width - rect.Width) / 2;
            int y = Math.Clamp(preferredLocation?.Y ?? work.Top + ToPixels(window, 20), work.Top, bottom);
            x = Math.Clamp(x, work.Left, right);
            if (rect.Left != x || rect.Top != y)
                MoveWindow(window, x, y);
        }
    }

    private static DeviceRow ToRow(BatteryDevice device, bool hasDivider)
    {
        int? value = device.Percent is >= 0 and <= 100 ? device.Percent : null;
        string batteryLabel = value switch
        {
            null => "电量未知",
            <= BatteryRingVisual.LowThreshold => $"低电量 {value}%",
            _ => $"电量 {value}%"
        };
        string connection = device.Connection == "2.4G 接收器" ? "2.4 GHz 接收器" : device.Connection;
        var (ringArc, ringTone, ringTrack) = BatteryRingVisual.Create(value);
        return new DeviceRow(device.Name, device.Kind, connection, hasDivider,
            value is int level ? $"{level}%" : "—",
            value.HasValue ? "#FFFFFF" : "#969696",
            batteryLabel, $"{device.Name} · {connection} · {batteryLabel}",
            ringArc, ringTone, ringTrack);
    }

    private sealed record DeviceRow(
        string Name, string Kind, string Connection, bool HasDivider, string Battery, string BatteryTone,
        string BatteryLabel, string Description, Geometry RingArc, string RingTone, string RingTrack);

    private sealed record PointerPress(UIElement Handle, DrawingPoint Cursor, DrawingPoint WindowOrigin);

    private static int ToPixels(IntPtr window, double value) => (int)Math.Round(value * GetDpiForWindow(window) / 96);

    private static void MoveWindow(IntPtr window, int x, int y)
    {
        const uint noSizeOrActivation = 0x0001 | 0x0004 | 0x0010;
        SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, noSizeOrActivation);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly DrawingPoint TopLeft => new(Left, Top);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out WindowRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out DrawingPoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
