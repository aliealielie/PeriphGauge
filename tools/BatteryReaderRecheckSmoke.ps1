#requires -Version 7.6
param(
    [string] $AssemblyPath = (Join-Path $PSScriptRoot '../bin/Release/net10.0-windows10.0.19041.0/PeriphGauge.dll')
)

$ErrorActionPreference = 'Stop'
Add-Type -CompilerOptions '/nullable:enable' -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

public static class BatteryReaderRecheckSmoke
{
    public static async Task Run(Assembly assembly)
    {
        var device = assembly.GetType("WirelessBattery.BatteryDevice", throwOnError: true)!;
        var scan = assembly.GetType("WirelessBattery.BatteryScan", throwOnError: true)!;
        var reader = assembly.GetType("WirelessBattery.BatteryReader", throwOnError: true)!;
        await (Task)typeof(BatteryReaderRecheckSmoke).GetMethod(nameof(CheckTargetSelection), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(device).Invoke(null, [scan, reader])!;
        await CheckEnumerationStatus(reader);
    }

    private static async Task CheckEnumerationStatus(Type reader)
    {
        var nodes = Array.CreateInstance(reader.GetNestedType("BluetoothNode", BindingFlags.NonPublic)!, 0);
        var empty = Array.CreateInstance(reader.GetNestedType("BluetoothEndpoint", BindingFlags.NonPublic)!, 0);
        var completed = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(empty.GetType());
        var read = reader.GetMethod("ReadBluetoothDevicesAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var le in new Array?[] { empty, null })
        foreach (var classic in new Array?[] { empty, null })
        {
            var task = (Task)read.Invoke(null, [nodes, completed.Invoke(null, [le]), completed.Invoke(null, [classic])])!;
            await task;
            var result = (System.Runtime.CompilerServices.ITuple)task.GetType().GetProperty("Result")!.GetValue(task)!;
            if (((Array)result[0]!).Length != 0 || (bool)result[1]! != (le is null || classic is null))
                throw new InvalidOperationException("Bluetooth query failure and a successful empty scan must be distinct.");
        }
    }

    private static async Task CheckTargetSelection<T>(Type scanType, Type readerType) where T : class
    {
        T Device(string id, int? percent) => (T)Activator.CreateInstance(typeof(T), [id, id, "mouse", "蓝牙", percent, null, null])!;
        var matched = Device("requested", 61);
        var unavailable = Device("unavailable", null);
        var tapped = new List<string>();
        Func<Task<T?>> Reader(string id, T? device) => () =>
        {
            tapped.Add(id);
            return Task.FromResult(device);
        };
        var callbacks = new Dictionary<string, Func<Task<T?>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["requested"] = Reader("requested", matched),
            ["unavailable"] = Reader("unavailable", unavailable),
            ["disconnected"] = Reader("disconnected", null),
            ["unrelated"] = Reader("unrelated", Device("unrelated", 72))
        };
        var scan = Activator.CreateInstance(scanType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [new List<T> { matched, unavailable }, callbacks, true], culture: null)!;
        if (!(bool)scanType.GetProperty("BluetoothScanIncomplete", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(scan)!)
            throw new InvalidOperationException("Scan omitted incomplete Bluetooth status.");
        var recheck = scanType.GetMethod("RecheckAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = await (Task<List<T>>)recheck.Invoke(scan,
        [
            new HashSet<string>(StringComparer.Ordinal)
            {
                "requested", "REQUESTED", "unavailable", "disconnected", "unmatched"
            }
        ])!;

        if (tapped.Count != 3 || !tapped.Contains("requested") || !tapped.Contains("unavailable") ||
            !tapped.Contains("disconnected") || result.Count != 2 || !result.Contains(matched) || !result.Contains(unavailable))
        {
            throw new InvalidOperationException("Recheck did not isolate matching IDs or distinguish unavailable from disconnected targets.");
        }

        var targetType = readerType.GetNestedType("DeviceReading", BindingFlags.NonPublic)!;
        var aliases = readerType.GetMethod("RecheckBluetoothAliasesAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var alias = Device("requested", 62);
        var unreadableAlias = Device("requested", null);
        var preferredUnreadable = Device("requested", null);
        object Target(Func<Task<T?>> read) => Activator.CreateInstance(targetType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null,
            args: [matched, read], culture: null)!;
        Task<T?> Recheck(bool incomplete, T? preferred, params T?[] alternatives)
        {
            var targets = Array.CreateInstance(targetType, alternatives.Length);
            for (int i = 0; i < alternatives.Length; i++)
            {
                T? candidate = alternatives[i];
                targets.SetValue(Target(() => Task.FromResult(candidate)), i);
            }
            return (Task<T?>)aliases.Invoke(null, [Target(() => Task.FromResult(preferred)), targets, incomplete])!;
        }

        if (await Recheck(false, preferredUnreadable, null, alias) != alias ||
            await Recheck(false, preferredUnreadable, null, unreadableAlias) != preferredUnreadable ||
            await Recheck(false, null, unreadableAlias) != unreadableAlias ||
            await Recheck(false, null, new T?[] { null }) is not null ||
            await Recheck(true, null, alias) != alias ||
            !Equals(await Recheck(true, null, new T?[] { null }), preferredUnreadable))
        {
            throw new InvalidOperationException("Bluetooth aliases did not prefer a measured result or preserve availability.");
        }
    }
}
'@

$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).ProviderPath)
[void]([BatteryReaderRecheckSmoke]::Run($assembly).GetAwaiter().GetResult())
'Battery-reader target recheck smoke passed.'
