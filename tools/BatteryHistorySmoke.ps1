#requires -Version 7.6
$ErrorActionPreference = 'Stop'
$history = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../BatteryHistory.cs') -Raw

# Compile the production history logic with a compatible device fixture, without scanning hardware.
Add-Type -CompilerOptions '/nullable:enable' -TypeDefinition ($history + @'

internal sealed record BatteryDevice(string Id, string Name, string Kind, string Connection, int? Percent,
    bool? IsCharging = null, DateTimeOffset? LastKnownAt = null);

public static class BatteryHistorySmoke
{
    public static async Task Run()
    {
        await ShortGaps();
        await TargetedConfirmation();
        await RejectedConfirmation();
        await FailedConfirmation();
        await DisconnectedDevice();
        await LowBatteryCrossings();
        await IncompleteBluetoothScan();
    }

    private static async Task ShortGaps()
    {
        var fixture = new Fixture();
        DateTimeOffset measuredAt = fixture.Now;
        Check(await fixture.Refresh([Device("A", 65, true)]), 65, true);
        fixture.Now = measuredAt.AddSeconds(599);
        Check(await fixture.Refresh([Device("a", null, true)]), 65, null, measuredAt);
        fixture.Now = measuredAt.AddMinutes(10);
        Check(await fixture.Refresh([Device("A", null)]), null);
        Check(await fixture.Refresh([Device("A", 93, false)]), 93, false);
    }

    private static async Task TargetedConfirmation()
    {
        var fixture = new Fixture();
        await fixture.Refresh([Device("A", 61), Device("B", 44), Device("C", 55), Device("D", 38)]);
        var baselineAt = fixture.Now;
        fixture.ScanDuration = TimeSpan.FromSeconds(1);
        var result = await fixture.Refresh(
            [Device("A", 95, true), Device("B", 54, false), Device("C", 64, true), Device("D", 39)],
            [Device("a", 70, false), Device("b", 56, true), Device("c", null), Device("new", 60)]);
        var secondAt = fixture.Now;
        if (!fixture.Rechecked.SetEquals(["A", "B"]))
            throw new InvalidOperationException("Only the two jumping devices should be reread.");
        var first = fixture.Updates.First().Devices;
        Check([first[0]], 61, null, baselineAt);
        Check([first[1]], 44, null, baselineAt);
        Check([first[2]], 64, true);
        Check([first[3]], 39);
        if (!result.Select(device => device.Id).SequenceEqual(["a", "b", "C", "D"]))
            throw new InvalidOperationException("Targeted confirmation changed unrelated devices.");
        Check([result[0]], 70, false);
        Check([result[1]], 56, true);
        Check([result[2]], 64, true);
        Check([result[3]], 39);
        result = await fixture.Refresh([Device("a", null), Device("b", null)]);
        Check([result[0]], 70, null, secondAt);
        Check([result[1]], 56, null, secondAt);
        Check(await fixture.Refresh([Device("D", 95)]), 95);
    }

    private static async Task RejectedConfirmation()
    {
        var fixture = new Fixture();
        DateTimeOffset firstAt = fixture.Now;
        await fixture.Refresh([Device("A", 80)]);
        fixture.Now = firstAt.AddMinutes(2);
        Check(await fixture.Refresh([Device("A", 20)], [Device("a", 40)]), 80, null, firstAt);
        fixture.CheckAlerts(0);
        fixture.CheckAlerts(1);
        Check(await fixture.Refresh([Device("A", 20)], [Device("a", null)]), 80, null, firstAt);
        if ((await fixture.Refresh([Device("A", 20)], [])).Count != 0)
            throw new InvalidOperationException("A jump candidate disappeared before confirmation and must be removed.");
        Check(await fixture.Refresh([Device("A", 20)]), 20);
        fixture.CheckAlerts(0);
    }

    private static async Task FailedConfirmation()
    {
        var fixture = new Fixture();
        DateTimeOffset firstAt = fixture.Now;
        await fixture.Refresh([Device("A", 80, true), Device("B", 50)]);
        fixture.Now = firstAt.AddMinutes(1);
        var result = await fixture.Refresh([Device("A", 20, false), Device("B", 55, true)], null);
        Check([result[0]], 80, null, firstAt);
        Check([result[1]], 55, true);
    }

    private static async Task DisconnectedDevice()
    {
        var fixture = new Fixture();
        await fixture.Refresh([Device("A", 80)]);
        if ((await fixture.Refresh(new List<BatteryDevice>())).Count != 0)
            throw new InvalidOperationException("Missing devices must be removed.");
        Check(await fixture.Refresh([Device("B", 101, true)]), null);
        Check(await fixture.Refresh([Device("A", null)]), null);
    }

    private static async Task LowBatteryCrossings()
    {
        var fixture = new Fixture();
        await fixture.Refresh([Device("A", 55), Device("B", 23), Device("C", 20)]);
        fixture.CheckAlerts(0);
        await fixture.Refresh([Device("A", 17), Device("B", 20), Device("C", null)], [Device("a", 18)]);
        fixture.CheckAlerts(0, "B");
        fixture.CheckAlerts(1, "a");
        await fixture.Refresh([Device("A", 16), Device("B", null), Device("C", 19)]);
        fixture.CheckAlerts(0);
        await fixture.Refresh([Device("A", 24), Device("B", 19)]);
        fixture.CheckAlerts(0);
        await fixture.Refresh([Device("A", 20), Device("B", 21), Device("C", 18)]);
        fixture.CheckAlerts(0);
        await fixture.Refresh([Device("A", 25), Device("B", 20)]);
        fixture.CheckAlerts(0);
        await fixture.Refresh([Device("A", 20), Device("B", 19)]);
        fixture.CheckAlerts(0, "A");
    }

    private static async Task IncompleteBluetoothScan()
    {
        var fixture = new Fixture();
        var headset = new BatteryDevice("bt", "耳机", "headphones", "蓝牙", 21);
        var mouse = Device("usb", 55);
        var measuredAt = fixture.Now;
        await fixture.Refresh([headset, mouse, Device("removed-usb", 30)]);
        await fixture.Refresh([headset with { Percent = 20, IsCharging = true }, mouse, Device("removed-usb", 30)]);
        fixture.CheckAlerts(0, "bt");

        fixture.BluetoothScanIncomplete = true;
        fixture.Now = measuredAt.AddMinutes(1);
        await fixture.Refresh([mouse with { Percent = 70 }], [mouse with { Percent = 70 }]);
        if (!fixture.Rechecked.SetEquals(["usb"]))
            throw new InvalidOperationException("An unenumerated Bluetooth device was reread.");
        foreach (var (devices, alerts) in fixture.Updates)
        {
            if (devices.Count != 2 || alerts.Count != 0 || devices.Single(d => d.Id == "bt") !=
                (headset with { Percent = 20, IsCharging = null, LastKnownAt = measuredAt }))
                throw new InvalidOperationException("A partial scan lost Bluetooth history, replayed an alert or retained an absent USB receiver.");
        }
        await fixture.Refresh([headset with { Percent = 21 }]);
        fixture.CheckAlerts(0);
        await fixture.Refresh([headset with { Percent = 20 }]);
        fixture.CheckAlerts(0);
        measuredAt = fixture.Now;
        fixture.Now = measuredAt.AddSeconds(599);
        Check(await fixture.Refresh(new List<BatteryDevice>()), 20, null, measuredAt);
        fixture.Now = measuredAt.AddMinutes(10);
        if ((await fixture.Refresh(new List<BatteryDevice>())).Count != 0)
            throw new InvalidOperationException("Bluetooth history outlived its ten-minute limit.");

        await fixture.Refresh([headset]);
        fixture.BluetoothScanIncomplete = false;
        if ((await fixture.Refresh(new List<BatteryDevice>())).Count != 0)
            throw new InvalidOperationException("A complete scan retained a disconnected Bluetooth device.");
        fixture.BluetoothScanIncomplete = true;
        if ((await fixture.Refresh(new List<BatteryDevice>())).Count != 0)
            throw new InvalidOperationException("A partial scan restored a previously disconnected device.");
    }

    private static BatteryDevice Device(string id, int? percent, bool? isCharging = null) =>
        new(id, "同一型号", "mouse", "2.4G 接收器", percent, isCharging);

    private static void Check(List<BatteryDevice> readings, int? percent, bool? charging = null, DateTimeOffset? lastKnownAt = null)
    {
        var actual = readings.Single();
        if (actual.Percent != percent || actual.IsCharging != charging || actual.LastKnownAt != lastKnownAt)
            throw new InvalidOperationException($"Unexpected reading {actual}; expected {percent}, charging {charging}, last known {lastKnownAt}.");
    }

    private sealed class Fixture
    {
        private readonly BatteryHistory history = new();
        internal DateTimeOffset Now { get; set; } = new(2026, 1, 1, 8, 0, 0, TimeSpan.FromHours(8));
        internal TimeSpan ScanDuration { get; set; }
        internal bool BluetoothScanIncomplete { get; set; }
        internal IReadOnlySet<string> Rechecked { get; private set; } = new HashSet<string>();
        internal List<(List<BatteryDevice> Devices, List<BatteryDevice> Alerts)> Updates { get; } = [];

        internal void CheckAlerts(int round, params string[] ids)
        {
            if (!Updates[round].Alerts.Select(device => device.Id).SequenceEqual(ids))
                throw new InvalidOperationException("Unexpected low-battery alert.");
        }

        internal async Task<List<BatteryDevice>> Refresh(params List<BatteryDevice>?[] scans)
        {
            Updates.Clear();
            int calls = 1;
            bool publishedBeforeRecheck = false;
            Now += ScanDuration;
            await history.ApplyAsync(scans[0]!, BluetoothScanIncomplete, ids =>
            {
                publishedBeforeRecheck = Updates.Count == 1;
                Rechecked = ids;
                Now += ScanDuration;
                var scan = scans[calls++];
                return Task.FromResult(scan ?? throw new System.IO.IOException("Confirmation failed."));
            }, () => Now, (devices, alerts) => Updates.Add((devices, alerts)));
            if (calls != scans.Length || Updates.Count != calls || (calls > 1 && !publishedBeforeRecheck))
                throw new InvalidOperationException($"Unexpected scan or publish sequence ({calls} reads, {Updates.Count} updates).");
            return Updates.Last().Devices;
        }
    }
}
'@)
[void]([WirelessBattery.BatteryHistorySmoke]::Run().GetAwaiter().GetResult())
'Battery history smoke passed.'
