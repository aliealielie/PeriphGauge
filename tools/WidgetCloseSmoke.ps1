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
    [StructLayout(LayoutKind.Sequential)]
    public struct Bounds { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out Bounds rectangle);

    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);
}
"@

$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).ProviderPath)
$type = $assembly.GetType('WirelessBattery.FloatingWindow', $true)
$dockEdgeType = $assembly.GetType('WirelessBattery.DockEdge', $true)
$members = [Reflection.BindingFlags]'Instance, NonPublic'
$previousDpi = [WidgetCloseSmokeNative]::SetThreadDpiAwarenessContext(-4) # Per-monitor V2
$app = [System.Windows.Application]::new()
$app.ShutdownMode = [System.Windows.ShutdownMode]::OnExplicitShutdown
$widget = [Activator]::CreateInstance($type)
try {
    # Keep the test window transparent after OpenAt sets its local opacity.
    $widget.IsHitTestVisible = $false
    $invisible = [System.Windows.Media.Animation.DoubleAnimation]::new(0, 0, [System.Windows.Duration]::new([TimeSpan]::Zero))
    $widget.BeginAnimation([System.Windows.UIElement]::OpacityProperty, $invisible)
    $state = @{ HideRequests = 0; ExpansionChanges = 0; Refreshes = 0; Placements = [Collections.Generic.List[string]]::new() }
    $hide = [EventHandler]({
        param($sender, $eventArgs)
        $state.HideRequests++
        $sender.Hide()
    }.GetNewClosure())
    $type.GetEvent('HideRequested', $members).GetAddMethod($true).Invoke($widget, @($hide))
    $refresh = [EventHandler]({ $state.Refreshes++ }.GetNewClosure())
    $type.GetEvent('RefreshRequested', $members).GetAddMethod($true).Invoke($widget, @($refresh))
    $expansion = [EventHandler[bool]]({
        param($sender, $expanded)
        $state.ExpansionChanges++
    }.GetNewClosure())
    $type.GetEvent('ExpansionChangedByUser', $members).GetAddMethod($true).Invoke($widget, @($expansion))
    $placementEvent = $type.GetEvent('PlacementChangedByUser', $members)
    $placement = [Management.Automation.LanguagePrimitives]::ConvertTo(({
        param($sender, $pointAndEdge)
        $state.Placements.Add($pointAndEdge.Item2.ToString())
    }.GetNewClosure()), $placementEvent.EventHandlerType)
    $placementEvent.GetAddMethod($true).Invoke($widget, @($placement))

    function Open-TestWidget($Location, [bool] $Expanded, [string] $Edge) {
        $dockEdge = [Enum]::Parse($dockEdgeType, $Edge)
        $type.GetMethod('OpenAt', $members).Invoke($widget, [object[]]@($Location, $Expanded, $dockEdge))
    }

    Open-TestWidget $null $false 'None'
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

    $compact = $widget.FindName('CompactView')
    $expandedView = $widget.FindName('ExpandedView')
    $dockHandle = $widget.FindName('DockHandle')
    $dockButton = $widget.FindName('DockButton')

    function Assert-Docked([string] $Edge) {
        if (!$widget.IsVisible -or !$dockHandle.IsVisible -or $compact.IsVisible -or $expandedView.IsVisible) {
            throw "Expected only the dock handle for $Edge."
        }
        $bounds = [WidgetCloseSmokeNative+Bounds]::new()
        if (![WidgetCloseSmokeNative]::GetWindowRect($handle, [ref]$bounds)) {
            throw 'Could not read the dock handle bounds.'
        }
        $work = [System.Windows.Forms.Screen]::FromHandle($handle).WorkingArea
        if (($Edge -eq 'Left' -and $bounds.Left -ne $work.Left) -or
            ($Edge -eq 'Right' -and $bounds.Right -ne $work.Right) -or
            $bounds.Top -lt $work.Top -or $bounds.Bottom -gt $work.Bottom) {
            throw "Dock handle is outside its $Edge work-area edge."
        }
    }

    function Click-WidgetButton($Button) {
        $Button.RaiseEvent([System.Windows.RoutedEventArgs]::new([System.Windows.Controls.Button]::ClickEvent))
    }

    $work = [System.Windows.Forms.Screen]::FromHandle($handle).WorkingArea
    foreach ($edge in 'Left', 'Right') {
        $x = if ($edge -eq 'Left') { $work.Left + $work.Width / 8 } else { $work.Right - $work.Width / 5 }
        $anchor = [System.Drawing.Point]::new([int]$x, $work.Top + 50)
        Open-TestWidget $anchor $true 'None'
        Click-WidgetButton $dockButton
        Assert-Docked $edge
        Click-WidgetButton $dockHandle
        if (!$expandedView.IsVisible -or $compact.IsVisible -or $dockHandle.IsVisible) {
            throw "Expanded view was not restored from $edge."
        }

        Open-TestWidget $anchor $false $edge
        Assert-Docked $edge
        Click-WidgetButton $dockHandle
        if (!$compact.IsVisible -or $expandedView.IsVisible -or $dockHandle.IsVisible) {
            throw "Compact view was not restored from saved $edge."
        }
    }

    if ($state.HideRequests -ne 0 -or $state.ExpansionChanges -ne 0 -or
        ($state.Placements -join ',') -ne 'Left,None,None,Right,None,None') {
        throw "Unexpected preferences changed while docking and restoring: $($state.Placements -join ',')."
    }

    $deviceType = $assembly.GetType('WirelessBattery.BatteryDevice', $true)
    function Set-TestReading($Percent, $LastKnownAt = $null) {
        $rows = [Array]::CreateInstance($deviceType, 2)
        $mouse = [Activator]::CreateInstance($deviceType, [object[]]@('mouse', '测试鼠标', 'mouse', '2.4G 接收器', 80, $null, $null))
        $row = [Activator]::CreateInstance($deviceType, [object[]]@('test', '测试键盘', 'keyboard', '蓝牙', $Percent, $null, $LastKnownAt))
        $rows.SetValue($mouse, 0)
        $rows.SetValue($row, 1)
        $type.GetMethod('SetDevices', $members).Invoke($widget, (, $rows))
    }

    function Assert-RefreshOnOpen([string] $Scenario, [int] $Expected) {
        $count = $state.Refreshes
        Open-TestWidget $null $false 'None'
        Click-WidgetButton $compact
        if ($state.Refreshes -ne $count + $Expected) { throw "$Scenario - unexpected refresh count on expand." }
        Click-WidgetButton $dockButton
        if ($state.Refreshes -ne $count + $Expected) { throw "$Scenario - docking triggered a refresh." }
        Click-WidgetButton $dockHandle
        if ($state.Refreshes -ne $count + 2 * $Expected) { throw "$Scenario - unexpected refresh count on undock." }
        Click-WidgetButton ($widget.FindName('CollapseButton'))
        if ($state.Refreshes -ne $count + 2 * $Expected) { throw "$Scenario - collapsing triggered a refresh." }
    }

    Set-TestReading 45 ([DateTimeOffset]::Now)
    Assert-RefreshOnOpen 'Historical reading' 1
    Set-TestReading $null
    Assert-RefreshOnOpen 'Unknown reading' 1
    Set-TestReading 45
    Assert-RefreshOnOpen 'Current reading' 0
    $type.GetMethod('SetError', $members).Invoke($widget, $null)
    Assert-RefreshOnOpen 'Scan failed' 1
    $emptyRows = [Array]::CreateInstance($deviceType, 0)
    $type.GetMethod('SetDevices', $members).Invoke($widget, (, $emptyRows))
    Assert-RefreshOnOpen 'No devices' 0

    Send-WidgetMessage 0x0112 0xF060       # WM_SYSCOMMAND / SC_CLOSE
    if ($state.HideRequests -ne 1 -or $widget.IsVisible) {
        throw 'Explicit system close did not produce exactly one user hide.'
    }

    'Widget close, edge docking and refresh-on-open smoke passed.'
}
finally {
    $type.GetProperty('AllowClose', $members).SetValue($widget, $true)
    $widget.Close()
    $app.Shutdown()
    if ($previousDpi -ne 0) { [void][WidgetCloseSmokeNative]::SetThreadDpiAwarenessContext($previousDpi) }
}
