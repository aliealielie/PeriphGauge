using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using DrawingPoint = System.Drawing.Point;

namespace WirelessBattery;

internal static class Program
{
    internal const string Name = "外设电量";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--diagnose")
        {
            try
            {
                var devices = BatteryReader.Read();
                var report = devices.Select(device => new
                {
                    device.Name,
                    device.Kind,
                    device.Connection,
                    device.Percent,
                    device.IsCharging
                });
                using var output = new FileStream(args[1], FileMode.CreateNew);
                JsonSerializer.Serialize(output, report, new JsonSerializerOptions { WriteIndented = true });
                return 0;
            }
            catch (Exception error)
            {
                Trace.TraceError(error.ToString());
                return 1;
            }
        }

        if (args.Length == 1 && args[0] == "--create-desktop-shortcut")
            return ApplyOption(DesktopIntegration.CreateDesktopShortcut, "无法创建桌面快捷方式") ? 0 : 1;

        bool fromStartup = args.Length == 1 && args[0] == "--startup";
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (args.Length == 1 && args[0] == "--preview")
        {
            var preview = new MainWindow { IsPreview = true, AllowClose = true, ShowInTaskbar = true };
            preview.SetDevices(
            [
                new BatteryDevice("a5", "MCHOSE A5", "mouse", "2.4G 接收器", 46, IsCharging: true),
                new BatteryDevice("k99", "MCHOSE K99", "keyboard", "2.4G 接收器", 82),
                new BatteryDevice("headphones", "SIMGOT EH500", "headphones", "蓝牙", 18)
            ], DateTime.Now);
            preview.Closed += (_, _) => app.Shutdown();
            preview.OpenNear();
            return app.Run();
        }

        // A second launch raises the existing panel without adding another tray icon.
        using var openRequest = new EventWaitHandle(false, EventResetMode.AutoReset,
            @"Local\WirelessBattery.OpenPanel", out bool firstInstance);
        if (!firstInstance)
        {
            if (!fromStartup)
                openRequest.Set();
            return 0;
        }

        var window = new MainWindow();
        var widget = new FloatingWindow();
        using var tray = new BatteryTray();
        bool widgetVisible = false;
        bool widgetTopmost = true;
        bool widgetExpanded = false;
        bool startsWithWindows = false;
        bool lowBatteryAlerts = true;
        DrawingPoint? widgetPosition = null;
        DockEdge widgetEdge = DockEdge.None;
        ApplyOption(() => (widgetVisible, widgetPosition, widgetTopmost, widgetExpanded, widgetEdge, lowBatteryAlerts) = WidgetSettings.Load(),
            "无法读取设置", showDialog: false);
        widget.Topmost = widgetTopmost;
        ApplyOption(() => startsWithWindows = DesktopIntegration.StartsWithWindows, "无法读取自启动状态", showDialog: false);

        void ShowOptionState()
        {
            window.SetOptions(widgetVisible, startsWithWindows, widgetTopmost, lowBatteryAlerts);
            tray.SetOptions(widgetVisible, startsWithWindows, widgetTopmost, lowBatteryAlerts);
        }

        void RestoreWidget()
        {
            if (widgetVisible)
                widget.OpenAt(widgetPosition, widgetExpanded, widgetEdge);
        }

        void SetWidgetVisibility(bool visible)
        {
            if (ApplyOption(() => WidgetSettings.SetVisible(visible), "无法保存悬浮窗设置"))
            {
                widgetVisible = visible;
                if (visible)
                    RestoreWidget();
                else
                    widget.Hide();
            }
            ShowOptionState();
        }

        void ToggleWidget(object? sender, EventArgs args) => SetWidgetVisibility(!widgetVisible);
        void ToggleTopmost(object? sender, EventArgs args)
        {
            if (ApplyOption(() => WidgetSettings.SetTopmost(!widgetTopmost), "无法保存悬浮窗置顶设置"))
                widget.Topmost = widgetTopmost = !widgetTopmost;
            ShowOptionState();
        }
        void ToggleStartup(object? sender, EventArgs args)
        {
            if (ApplyOption(() => DesktopIntegration.SetStartsWithWindows(!startsWithWindows), "无法修改开机自启动"))
                startsWithWindows = !startsWithWindows;
            ShowOptionState();
        }
        void ToggleLowBatteryAlerts(object? sender, EventArgs args)
        {
            if (ApplyOption(() => WidgetSettings.SetLowBatteryAlerts(!lowBatteryAlerts), "无法保存低电量提醒设置"))
                lowBatteryAlerts = !lowBatteryAlerts;
            ShowOptionState();
        }
        void CreateShortcut(object? sender, EventArgs args)
        {
            if (ApplyOption(DesktopIntegration.CreateDesktopShortcut, "无法创建桌面快捷方式"))
                MessageBox.Show("桌面上的「外设电量」快捷方式已就绪。", Name, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        void AllowWindowClose()
        {
            window.AllowClose = true;
            widget.AllowClose = true;
        }

        window.WidgetToggleRequested += ToggleWidget;
        tray.WidgetToggleRequested += ToggleWidget;
        window.TopmostToggleRequested += ToggleTopmost;
        tray.TopmostToggleRequested += ToggleTopmost;
        window.StartupToggleRequested += ToggleStartup;
        tray.StartupToggleRequested += ToggleStartup;
        window.LowBatteryAlertsToggleRequested += ToggleLowBatteryAlerts;
        tray.LowBatteryAlertsToggleRequested += ToggleLowBatteryAlerts;
        window.DesktopShortcutRequested += CreateShortcut;
        tray.DesktopShortcutRequested += CreateShortcut;
        widget.HideRequested += (_, _) => SetWidgetVisibility(false);
        widget.ExpansionChangedByUser += (_, expanded) =>
        {
            widgetExpanded = expanded;
            ApplyOption(() => WidgetSettings.SetExpanded(expanded), "无法保存悬浮窗展开状态");
        };
        widget.PlacementChangedByUser += (_, placement) =>
        {
            (widgetPosition, widgetEdge) = placement;
            ApplyOption(() => WidgetSettings.SetPosition(placement.Position, placement.Edge), "无法保存悬浮窗位置");
        };
        ShowOptionState();

        tray.OpenRequested += (_, anchor) => window.OpenNear(anchor);
        tray.ExitRequested += (_, _) =>
        {
            AllowWindowClose();
            app.Shutdown();
        };

        var history = new BatteryHistory();
        bool reading = false;
        bool refreshPending = false;

        void Publish(List<BatteryDevice> devices, List<BatteryDevice> newlyLow)
        {
            if (app.Dispatcher.HasShutdownStarted)
                return;
            window.SetDevices(devices, DateTime.Now);
            widget.SetDevices(devices);
            tray.Update(devices);
            if (lowBatteryAlerts && newlyLow.Count > 0)
                tray.ShowLowBatteryAlert(newlyLow);
        }

        async Task RefreshAsync(bool showScanning = false, bool queueIfBusy = true)
        {
            if (reading)
            {
                refreshPending |= queueIfBusy;
                return;
            }
            do
            {
                refreshPending = false;
                reading = true;
                try
                {
                    if (showScanning)
                        window.SetScanning();
                    var scan = await Task.Run(BatteryReader.ScanAsync);
                    if (app.Dispatcher.HasShutdownStarted)
                        return;
                    await history.ApplyAsync(scan.Devices, ids => Task.Run(() => scan.RecheckAsync(ids)),
                        () => DateTimeOffset.UtcNow, Publish);
                }
                catch (Exception error)
                {
                    Trace.TraceError(error.ToString());
                    if (!app.Dispatcher.HasShutdownStarted)
                    {
                        window.SetError();
                        widget.SetError();
                        tray.ShowError();
                    }
                }
                finally
                {
                    reading = false;
                }
                showScanning = false;
            }
            while (refreshPending && !app.Dispatcher.HasShutdownStarted);
        }

        window.RefreshRequested += async (_, _) => await RefreshAsync(true);
        tray.RefreshRequested += async (_, _) => await RefreshAsync(true);
        widget.RefreshRequested += async (_, _) => await RefreshAsync();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += async (_, _) => await RefreshAsync(queueIfBusy: false);
        timer.Start();

        void RefreshAfterResume() => app.Dispatcher.InvokeAsync(() =>
        {
            RestoreWidget();
            _ = RefreshAsync();
        });
        void SessionChanged(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect
                or SessionSwitchReason.RemoteConnect)
                RefreshAfterResume();
        }
        void PowerChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
                RefreshAfterResume();
        }
        SystemEvents.SessionSwitch += SessionChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        var openListener = ThreadPool.RegisterWaitForSingleObject(openRequest, (_, _) =>
            app.Dispatcher.InvokeAsync(() => { RestoreWidget(); window.OpenNear(); }), null, Timeout.Infinite, false);
        app.Startup += (_, _) =>
        {
            RestoreWidget();
            if (!fromStartup)
                window.OpenNear();
            app.Dispatcher.InvokeAsync(() => RefreshAsync(true));
        };
        int exitCode = app.Run();
        SystemEvents.SessionSwitch -= SessionChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        timer.Stop();
        openListener.Unregister(null);
        return exitCode;
    }

    private static bool ApplyOption(Action change, string failure, bool showDialog = true)
    {
        try
        {
            change();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            SecurityException or COMException or InvalidOperationException)
        {
            Trace.TraceWarning(error.ToString());
            if (showDialog)
                MessageBox.Show($"{failure}。\n{error.Message}", Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
