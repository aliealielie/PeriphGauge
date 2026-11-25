using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

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
                    device.Percent
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

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (args.Length == 1 && args[0] == "--preview")
        {
            var preview = new MainWindow { IsPreview = true, AllowClose = true, ShowInTaskbar = true };
            preview.SetDevices(
            [
                new BatteryDevice("a5", "MCHOSE A5", "mouse", "2.4G 接收器", 46),
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
            openRequest.Set();
            return 0;
        }

        var window = new MainWindow();
        using var tray = new BatteryTray();
        tray.OpenRequested += (_, anchor) => window.OpenNear(anchor);
        tray.ExitRequested += (_, _) =>
        {
            window.AllowClose = true;
            app.Shutdown();
        };

        bool reading = false;
        async Task RefreshAsync(bool showScanning)
        {
            if (reading)
                return;

            reading = true;
            if (showScanning)
                window.SetScanning();
            try
            {
                List<BatteryDevice> devices = await Task.Run(BatteryReader.Read);
                if (app.Dispatcher.HasShutdownStarted)
                    return;

                window.SetDevices(devices, DateTime.Now);
                tray.Update(devices);
            }
            catch (Exception error)
            {
                Trace.TraceError(error.ToString());
                if (!app.Dispatcher.HasShutdownStarted)
                {
                    window.SetError();
                    tray.ShowError();
                }
            }
            finally
            {
                reading = false;
            }
        }

        window.RefreshRequested += async (_, _) => await RefreshAsync(true);
        tray.RefreshRequested += async (_, _) => await RefreshAsync(true);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += async (_, _) => await RefreshAsync(false);
        timer.Start();

        var openListener = ThreadPool.RegisterWaitForSingleObject(openRequest, (_, _) =>
            app.Dispatcher.InvokeAsync(() => window.OpenNear()), null, Timeout.Infinite, false);
        window.SetScanning();
        window.OpenNear();
        app.Dispatcher.InvokeAsync(() => RefreshAsync(true));
        int exitCode = app.Run();
        timer.Stop();
        openListener.Unregister(null);
        return exitCode;
    }
}
