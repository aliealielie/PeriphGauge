param([string] $ExePath = (Join-Path $PSScriptRoot '../release/PeriphGauge-win-x64.exe'))

$ErrorActionPreference = 'Stop'
$report = Join-Path ([IO.Path]::GetTempPath()) "periphgauge-smoke-$([Guid]::NewGuid()).json"
try {
    $exe = (Resolve-Path -LiteralPath $ExePath).Path
    $process = Start-Process -FilePath $exe -ArgumentList '--diagnose', ('"' + $report + '"') -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(30000)) {
        $process.Kill()
        throw 'Device scan exceeded 30 seconds.'
    }
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report)) {
        throw "The bundled app did not produce a device report (exit $($process.ExitCode))."
    }

    $json = Get-Content -LiteralPath $report -Raw -Encoding UTF8
    if (!$json.StartsWith('[')) { throw 'Expected an array of device reports.' }
    $devices = @($json | ConvertFrom-Json)
    foreach ($device in $devices) {
        $expectsCharging = $device.Name -eq 'MCHOSE A5' -and $device.Kind -eq 'mouse' -and
            $device.Connection -eq '2.4G 接收器' -and $null -ne $device.Percent
        if ([string]::IsNullOrWhiteSpace($device.Name) -or
            $null -eq $device.PSObject.Properties['IsCharging'] -or
            ($null -ne $device.IsCharging -and $device.IsCharging -isnot [bool]) -or
            (($device.IsCharging -is [bool]) -ne $expectsCharging) -or
            $device.Connection -notin '蓝牙', '2.4G 接收器', 'USB 无线接收器' -or
            $device.Kind -notin 'keyboard', 'mouse', 'headphones', 'controller', 'other' -or
            ($null -ne $device.Percent -and ($device.Percent -isnot [long] -or
             $device.Percent -lt 0 -or $device.Percent -gt 100))) {
            throw 'Unexpected device record in the scan.'
        }
    }
    $devices | Format-Table Name, Kind, Connection, Percent, IsCharging
    Write-Output "Smoke test passed ($($devices.Count) devices)."
}
finally {
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
}
