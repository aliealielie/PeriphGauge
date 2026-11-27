using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace WirelessBattery;

internal sealed class BatteryHistory
{
    private const int JumpThreshold = 10;
    private const int ConfirmationTolerance = 2;
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, (int Percent, DateTimeOffset At)> latest = new(StringComparer.OrdinalIgnoreCase);

    internal async Task<List<BatteryDevice>> ReadAsync(Func<Task<List<BatteryDevice>>> read, Func<DateTimeOffset> now)
    {
        var first = (await read().ConfigureAwait(false)).DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        DateTimeOffset firstAt = now();
        var present = first.Select(device => device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string id in latest.Keys.Where(id => !present.Contains(id) || !IsRecent(latest[id].At, firstAt)).ToArray())
            latest.Remove(id);

        var jumps = first.Where(device => ValidPercent(device) is int value &&
                latest.TryGetValue(device.Id, out var previous) && Math.Abs(previous.Percent - value) >= JumpThreshold)
            .Select(device => device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, BatteryDevice>? second = null;
        DateTimeOffset finishedAt = firstAt;
        if (jumps.Count > 0)
        {
            try
            {
                second = (await read().ConfigureAwait(false)).DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(device => device.Id, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception error)
            {
                Trace.TraceWarning(error.ToString());
            }
            finishedAt = now();
        }

        var devices = new List<BatteryDevice>(first.Length);
        foreach (var firstReading in first)
        {
            string id = firstReading.Id;
            BatteryDevice? confirmedReading = null;
            if (second is not null && !second.TryGetValue(id, out confirmedReading))
            {
                latest.Remove(id);
                continue;
            }

            BatteryDevice device = firstReading;
            int? percent = ValidPercent(device);
            DateTimeOffset sampledAt = firstAt;
            if (percent is int candidate && jumps.Contains(id))
            {
                int baseline = latest[id].Percent;
                percent = null;
                if (confirmedReading is not null && ValidPercent(confirmedReading) is int confirmed &&
                    (Math.Abs(confirmed - baseline) < JumpThreshold || Math.Abs(confirmed - candidate) <= ConfirmationTolerance))
                {
                    device = confirmedReading;
                    percent = confirmed;
                    sampledAt = finishedAt;
                }
            }

            if (percent is int fresh)
            {
                latest[id] = (fresh, sampledAt);
                devices.Add(device with { Percent = fresh, LastKnownAt = null });
            }
            else if (latest.TryGetValue(id, out var previous) && IsRecent(previous.At, finishedAt))
            {
                devices.Add(device with { Percent = previous.Percent, IsCharging = null, LastKnownAt = previous.At });
            }
            else
            {
                latest.Remove(id);
                devices.Add(device with { Percent = null, IsCharging = null, LastKnownAt = null });
            }
        }

        return devices;
    }

    private static int? ValidPercent(BatteryDevice device) => device.Percent is >= 0 and <= 100 ? device.Percent : null;

    private static bool IsRecent(DateTimeOffset measuredAt, DateTimeOffset now) =>
        now >= measuredAt && now - measuredAt < MaxAge;
}
