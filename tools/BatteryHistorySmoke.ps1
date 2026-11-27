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
        await SharedConfirmation();
        await RejectedConfirmation();
        await FailedConfirmation();
        await DisconnectedDevice();
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

    private static async Task SharedConfirmation()
    {
        var fixture = new Fixture();
        await fixture.Refresh([Device("A", 61), Device("B", 44), Device("C", 55), Device("D", 38)]);
        fixture.ScanDuration = TimeSpan.FromSeconds(1);
        var result = await fixture.Refresh(
            [Device("A", 95, true), Device("B", 54, false), Device("C", 64, true), Device("D", 39)],
            [Device("a", 70, false), Device("b", 56, true), Device("c", null), Device("new", 60)]);
        var secondAt = fixture.Now;
        if (result.Count != 3 || !result.Select(device => device.Id).SequenceEqual(["a", "b", "C"]))
            throw new InvalidOperationException("Confirmation added an unseen device or restored an absent device.");
        Check([result[0]], 70, false);
        Check([result[1]], 56, true);
        Check([result[2]], 64, true);
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
        Check(await fixture.Refresh([Device("A", 20)], [Device("a", null)]), 80, null, firstAt);
        if ((await fixture.Refresh([Device("A", 20)], [])).Count != 0)
            throw new InvalidOperationException("A jump candidate disappeared before confirmation and must be removed.");
        Check(await fixture.Refresh([Device("A", 20)]), 20);
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

        internal async Task<List<BatteryDevice>> Refresh(params List<BatteryDevice>?[] scans)
        {
            int calls = 0;
            var devices = await history.ReadAsync(() =>
            {
                Now += ScanDuration;
                var scan = scans[calls++];
                return Task.FromResult(scan ?? throw new System.IO.IOException("Confirmation failed."));
            }, () => Now);
            if (calls != scans.Length)
                throw new InvalidOperationException($"Unexpected scan count {calls}; expected {scans.Length}.");
            return devices;
        }
    }
}
'@)
[void]([WirelessBattery.BatteryHistorySmoke]::Run().GetAwaiter().GetResult())
'Battery history smoke passed.'
