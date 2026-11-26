#requires -Version 7.6
param(
    [string] $AssemblyPath = (Join-Path $PSScriptRoot '../bin/Release/net10.0-windows10.0.19041.0/PeriphGauge.dll')
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Run this check with pwsh -NoProfile -STA -File tools/WidgetCloseSmoke.ps1.'
}
Add-Type -AssemblyName PresentationFramework, System.Windows.Forms
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class WidgetCloseSmokeNative
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
}
"@

$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).ProviderPath)
$type = $assembly.GetType('WirelessBattery.FloatingWindow', $true)
$members = [Reflection.BindingFlags]'Instance, NonPublic'
$app = [System.Windows.Application]::new()
$app.ShutdownMode = [System.Windows.ShutdownMode]::OnExplicitShutdown
$widget = [Activator]::CreateInstance($type)
try {
    # Keep the test window transparent after OpenAt sets its local opacity.
    $widget.IsHitTestVisible = $false
    $invisible = [System.Windows.Media.Animation.DoubleAnimation]::new(0, 0, [System.Windows.Duration]::new([TimeSpan]::Zero))
    $widget.BeginAnimation([System.Windows.UIElement]::OpacityProperty, $invisible)
    $state = @{ HideRequests = 0 }
    $hide = [EventHandler]({
        param($sender, $eventArgs)
        $state.HideRequests++
        $sender.Hide()
    }.GetNewClosure())
    $type.GetEvent('HideRequested', $members).GetAddMethod($true).Invoke($widget, @($hide))
    $type.GetMethod('OpenAt', $members).Invoke($widget, [object[]]@($null, $false))
    $handle = [System.Windows.Interop.WindowInteropHelper]::new($widget).Handle
    if (!$widget.IsVisible -or $widget.Opacity -ne 0 -or $widget.IsActive) {
        throw 'Test widget must be open, transparent and inactive.'
    }

    function Send-WidgetMessage([uint32] $Message, [intptr] $Command) {
        $result = [intptr]::Zero
        # SMTO_ABORTIFHUNG
        if ([WidgetCloseSmokeNative]::SendMessageTimeout($handle, $Message, $Command, 0, 2, 3000, [ref]$result) -eq 0) {
            throw "Could not send widget message $Message."
        }
    }

    Send-WidgetMessage 0x0010 0            # WM_CLOSE
    if ($state.HideRequests -ne 0 -or !$widget.IsVisible) {
        throw 'WM_CLOSE triggered a user hide or closed the widget.'
    }

    Send-WidgetMessage 0x0112 0xF060       # WM_SYSCOMMAND / SC_CLOSE
    if ($state.HideRequests -ne 1 -or $widget.IsVisible) {
        throw 'Explicit system close did not produce exactly one user hide.'
    }

    'Widget close smoke passed.'
}
finally {
    $type.GetProperty('AllowClose', $members).SetValue($widget, $true)
    $widget.Close()
    $app.Shutdown()
}
