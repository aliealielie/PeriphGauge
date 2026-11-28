using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace WirelessBattery;

internal sealed class BatteryHistory
{
    internal const int LowThreshold = 20;
    private const int JumpThreshold = 10;
    private const int ConfirmationTolerance = 2;
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, (int Percent, DateTimeOffset At)> latest = new(StringComparer.OrdinalIgnoreCase);

    internal async Task ApplyAsync(List<BatteryDevice> readings,
        Func<IReadOnlySet<string>, Task<List<BatteryDevice>>> recheck, Func<DateTimeOffset> now,
        Action<List<BatteryDevice>, List<BatteryDevice>> publish)
    {
        var first = readings.DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        DateTimeOffset firstAt = now();
        var present = first.Select(device => device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string id in latest.Keys.Where(id => !present.Contains(id) || !IsRecent(latest[id].At, firstAt)).ToArray())
            latest.Remove(id);

        var jumps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var devices = new List<BatteryDevice>(first.Length);
        var low = new List<BatteryDevice>();
        foreach (var device in first)
        {
            int? percent = ValidPercent(device);
            if (percent is int candidate && latest.TryGetValue(device.Id, out var previous) &&
                Math.Abs(previous.Percent - candidate) >= JumpThreshold)
            {
                jumps.Add(device.Id);
                percent = null;
            }
            devices.Add(Record(device, percent, firstAt, low));
        }
        publish(devices, low);
        if (jumps.Count == 0)
            return;

        Dictionary<string, BatteryDevice>? second = null;
        try
        {
            second = (await recheck(jumps)).DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error)
        {
            Trace.TraceWarning(error.ToString());
        }

        DateTimeOffset finishedAt = now();
        var updated = new List<BatteryDevice>(first.Length);
        var confirmedLow = new List<BatteryDevice>();
        for (int i = 0; i < first.Length; i++)
        {
            var device = first[i];
            if (!jumps.Contains(device.Id))
            {
                updated.Add(devices[i]);
                continue;
            }
            BatteryDevice? reread = null;
            if (second is not null && !second.TryGetValue(device.Id, out reread))
            {
                latest.Remove(device.Id);
                continue;
            }
            if (reread is not null && ValidPercent(reread) is int confirmed && device.Percent is int candidate &&
                (Math.Abs(confirmed - latest[device.Id].Percent) < JumpThreshold || Math.Abs(confirmed - candidate) <= ConfirmationTolerance))
                updated.Add(Record(reread, confirmed, finishedAt, confirmedLow));
            else
                updated.Add(Record(device, null, finishedAt, confirmedLow));
        }
        publish(updated, confirmedLow);
    }

    private BatteryDevice Record(BatteryDevice device, int? percent, DateTimeOffset at, List<BatteryDevice> low)
    {
        if (percent is int fresh)
        {
            var current = device with { Percent = fresh, LastKnownAt = null };
            if (fresh <= LowThreshold && latest.TryGetValue(device.Id, out var last) && last.Percent > LowThreshold)
                low.Add(current);
            latest[device.Id] = (fresh, at);
            return current;
        }
        if (latest.TryGetValue(device.Id, out var previous) && IsRecent(previous.At, at))
            return device with { Percent = previous.Percent, IsCharging = null, LastKnownAt = previous.At };

        latest.Remove(device.Id);
        return device with { Percent = null, IsCharging = null, LastKnownAt = null };
    }

    private static int? ValidPercent(BatteryDevice device) => device.Percent is >= 0 and <= 100 ? device.Percent : null;

    private static bool IsRecent(DateTimeOffset measuredAt, DateTimeOffset now) =>
        now >= measuredAt && now - measuredAt < MaxAge;
}
