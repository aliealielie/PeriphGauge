using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace WirelessBattery;

internal sealed record BatteryDevice(string Id, string Name, string Kind, string Connection, int? Percent,
    bool? IsCharging = null, DateTimeOffset? LastKnownAt = null)
{
    internal bool IsCurrent => Percent is >= 0 and <= 100 && LastKnownAt is null;

    internal string ReadingNote => LastKnownAt is { } last
        ? $"上次 {last.ToLocalTime():HH:mm}"
        : IsCurrent && IsCharging == true ? "充电中" : string.Empty;
}

internal sealed class BatteryScan(List<BatteryDevice> devices, Dictionary<string, Func<Task<BatteryDevice?>>> rechecks)
{
    internal List<BatteryDevice> Devices { get; } = devices;

    internal async Task<List<BatteryDevice>> RecheckAsync(IReadOnlySet<string> ids)
    {
        var tasks = ids.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(rechecks.ContainsKey)
            .Select(id => rechecks[id]());
        var readings = await Task.WhenAll(tasks).ConfigureAwait(false);
        return readings.OfType<BatteryDevice>().ToList();
    }
}

internal static class BatteryReader
{
    private const string AddressProperty = "System.Devices.Aep.DeviceAddress";
    private const string CategoryProperty = "System.Devices.Aep.Category";
    private const string ContainerProperty = "System.Devices.Aep.ContainerId";
    private const string ConnectedProperty = "System.Devices.Aep.IsConnected";
    private const uint DevpropByte = 0x03;
    private const uint DevpropGuid = 0x0D;

    private static readonly PropertyKey Battery = new("104ea319-6ee2-4701-bd47-8ddbf425bbe5", 2);
    private static readonly PropertyKey BusName = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);
    private static readonly PropertyKey Container = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
    private static readonly PropertyKey Category = new("78c34fc8-104a-4aca-9ea4-524d52996e57", 90);

    internal static List<BatteryDevice> Read() => ReadAsync().GetAwaiter().GetResult();

    internal static async Task<List<BatteryDevice>> ReadAsync() =>
        (await ScanAsync().ConfigureAwait(false)).Devices;

    internal static async Task<BatteryScan> ScanAsync()
    {
        string[] instanceIds = PresentDeviceIds();
        BluetoothNode[] bluetoothNodes = instanceIds
            .Where(id => StartsWith(id, @"BTHLE\") || StartsWith(id, @"BTHENUM\")
                || StartsWith(id, @"BTHHFENUM\") || StartsWith(id, @"BTHLEDEVICE\"))
            .Select(ReadBluetoothNode)
            .OfType<BluetoothNode>()
            .ToArray();

        var bluetooth = ReadBluetoothDevicesAsync(bluetoothNodes);
        var usb = ReadReceiversAsync(instanceIds);
        var results = await Task.WhenAll(bluetooth, usb).ConfigureAwait(false);
        var readings = results.SelectMany(group => group)
            .OrderBy(reading => reading.Device.Connection == "蓝牙" ? 0 : 1)
            .ThenBy(reading => reading.Device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new BatteryScan(readings.Select(reading => reading.Device).ToList(),
            readings.ToDictionary(reading => reading.Device.Id, reading => reading.Recheck, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<DeviceReading[]> ReadBluetoothDevicesAsync(BluetoothNode[] nodes)
    {
        var le = EnumerateBluetoothAsync(true);
        var classic = EnumerateBluetoothAsync(false);
        var endpoints = (await Task.WhenAll(le, classic).ConfigureAwait(false))
            .SelectMany(group => group);
        var devices = await Task.WhenAll(endpoints.Select(endpoint => ReadEndpointAsync(endpoint, nodes)))
            .ConfigureAwait(false);

        return devices.GroupBy(reading => reading.Device.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var preferred = group.FirstOrDefault(reading => reading.Device.Percent.HasValue) ?? group.First();
                var alternatives = group.Where(reading => reading != preferred).ToArray();
                return alternatives.Length == 0
                    ? preferred
                    : preferred with { Recheck = () => RecheckBluetoothAliasesAsync(preferred, alternatives) };
            })
            .ToArray();
    }

    private static async Task<BatteryDevice?> RecheckBluetoothAliasesAsync(DeviceReading preferred, DeviceReading[] alternatives)
    {
        var available = await preferred.Recheck().ConfigureAwait(false);
        if (available?.Percent is not null)
            return available;

        foreach (var alias in alternatives)
        {
            var reading = await alias.Recheck().ConfigureAwait(false);
            if (reading?.Percent is not null)
                return reading;
            available ??= reading;
        }
        return available;
    }

    private static async Task<BluetoothEndpoint[]> EnumerateBluetoothAsync(bool isLowEnergy)
    {
        try
        {
            string selector = isLowEnergy
                ? BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected)
                : BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var devices = await DeviceInformation.FindAllAsync(selector,
                [AddressProperty, CategoryProperty, ContainerProperty],
                DeviceInformationKind.AssociationEndpoint).AsTask(timeout.Token).ConfigureAwait(false);
            return devices.Where(device => !string.IsNullOrWhiteSpace(device.Name))
                .Select(device => new BluetoothEndpoint(device, isLowEnergy))
                .ToArray();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or OperationCanceledException)
        {
            return [];
        }
    }

    private static async Task<DeviceReading> ReadEndpointAsync(BluetoothEndpoint endpoint, BluetoothNode[] nodes)
    {
        var info = endpoint.Info;
        var properties = info.Properties;
        properties.TryGetValue(AddressProperty, out object? addressValue);
        string address = (addressValue as string ?? "").Replace(":", "").Replace("-", "");
        if (address.Length != 12 || !address.All(Uri.IsHexDigit))
            address = "";

        properties.TryGetValue(ContainerProperty, out object? containerValue);
        Guid containerId = containerValue is Guid guid ? guid : Guid.Empty;
        var addressNodes = nodes.Where(node => address.Length > 0 && Contains(node.Id, address)).ToArray();
        var primaryNode = addressNodes.FirstOrDefault(node =>
            StartsWith(node.Id, @"BTHLE\DEV_") || StartsWith(node.Id, @"BTHENUM\DEV_"));
        if (primaryNode?.ContainerId is Guid primaryContainer && primaryContainer != Guid.Empty)
            containerId = primaryContainer;

        var matchingNodes = nodes.Where(node => containerId != Guid.Empty && node.ContainerId == containerId)
            .Concat(addressNodes);
        if (primaryNode is not null)
            matchingNodes = matchingNodes.Prepend(primaryNode);
        var batteryNodes = matchingNodes.Distinct().ToArray();
        int? windowsLevel = batteryNodes.Select(node => node.Level).FirstOrDefault(level => level.HasValue);
        int? gattLevel = endpoint.IsLowEnergy
            ? (await ReadGattBatteryAsync(info.Id).ConfigureAwait(false)).Percent
            : null;

        properties.TryGetValue(CategoryProperty, out object? categoryValue);
        string categories = $"{string.Join(' ', categoryValue as string[] ?? [])} {primaryNode?.Category}";
        string stableId = containerId != Guid.Empty ? containerId.ToString() : address;
        var device = new BatteryDevice($"bluetooth:{(stableId.Length > 0 ? stableId : info.Id)}",
            info.Name.Trim(), DeviceKind(info.Name, categories), "蓝牙", gattLevel ?? windowsLevel);
        string[] batteryNodeIds = batteryNodes.Select(node => node.Id).ToArray();
        string endpointId = info.Id;
        bool isLowEnergy = endpoint.IsLowEnergy;
        return new DeviceReading(device, () => RecheckBluetoothAsync(device, endpointId, isLowEnergy, batteryNodeIds));
    }

    private static async Task<BatteryDevice?> RecheckBluetoothAsync(BatteryDevice previous,
        string endpointId, bool isLowEnergy, string[] batteryNodeIds)
    {
        (int? percent, bool? connected) = isLowEnergy
            ? await ReadGattBatteryAsync(endpointId).ConfigureAwait(false)
            : (null, await IsBluetoothConnectedAsync(endpointId).ConfigureAwait(false));
        if (connected == false)
            return null;

        if (connected == true)
        {
            percent ??= batteryNodeIds
                .Select(id => Locate(id, out uint node) ? ReadBatteryLevel(node) : null)
                .FirstOrDefault(level => level.HasValue);
        }
        return previous with { Percent = percent };
    }

    private static async Task<bool?> IsBluetoothConnectedAsync(string id)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var device = await DeviceInformation.CreateFromIdAsync(id, [ConnectedProperty], DeviceInformationKind.AssociationEndpoint)
                .AsTask(timeout.Token).ConfigureAwait(false);
            return device is not null && device.Properties.TryGetValue(ConnectedProperty, out var connected)
                ? connected as bool?
                : null;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<(int? Percent, bool? Connected)> ReadGattBatteryAsync(string id)
    {
        int? level = null;
        bool? connected = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var device = await BluetoothLEDevice.FromIdAsync(id).AsTask(timeout.Token).ConfigureAwait(false);
            if (device is null)
                return (null, null);
            connected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
            if (connected == false)
                return (null, false);

            var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached)
                .AsTask(timeout.Token).ConfigureAwait(false);
            if (services.Status != GattCommunicationStatus.Success)
                return (null, connected);

            try
            {
                foreach (var service in services.Services)
                {
                    var characteristics = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel,
                        BluetoothCacheMode.Cached).AsTask(timeout.Token).ConfigureAwait(false);
                    if (characteristics.Status != GattCommunicationStatus.Success)
                        continue;

                    foreach (var characteristic in characteristics.Characteristics)
                    {
                        var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached)
                            .AsTask(timeout.Token).ConfigureAwait(false);
                        if (result.Status != GattCommunicationStatus.Success || result.Value.Length != 1)
                            continue;
                        using var reader = DataReader.FromBuffer(result.Value);
                        byte value = reader.ReadByte();
                        if (value <= 100)
                            level = Math.Min(level ?? value, value);
                    }
                }
            }
            finally
            {
                foreach (var service in services.Services)
                    service.Dispose();
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or OperationCanceledException)
        {
            // A device can go to sleep or disconnect between enumeration and the GATT read.
        }
        return (level, connected);
    }

    private static async Task<DeviceReading[]> ReadReceiversAsync(string[] instanceIds)
    {
        var devices = new List<DeviceReading>();
        foreach (string id in instanceIds)
        {
            if (!StartsWith(id, @"USB\VID_") || Contains(id, "&MI_") || !Locate(id, out uint node))
                continue;

            string name = ReadText(node, BusName);
            bool isMchoseMouse = StartsWith(id, @"USB\VID_2023&PID_F013\");
            bool is24GHz = isMchoseMouse || Contains(name, "2.4G") || Contains(name, "2.4 GHz");
            bool isWireless = is24GHz
                || Contains(name, "wireless receiver") || Contains(name, "wireless dongle")
                || name.Contains("无线接收器", StringComparison.Ordinal);
            if (!isWireless || (!isMchoseMouse && !HasHidInterface(id, node, instanceIds)))
                continue;

            var (level, model, isCharging) = await ReceiverBattery.ReadAsync(id).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(model))
                name = model;
            else if (isMchoseMouse)
                name = "MCHOSE A5";
            else if (Contains(name, "2.4G Wireless Receiver"))
                name = "2.4G 无线接收器";

            var device = new BatteryDevice(id, name, isMchoseMouse ? "mouse" : DeviceKind(name, ReadText(node, Category)),
                is24GHz ? "2.4G 接收器" : "USB 无线接收器", level, isCharging);
            devices.Add(new DeviceReading(device, () => RecheckReceiverAsync(device)));
        }
        return devices.ToArray();
    }

    private static async Task<BatteryDevice?> RecheckReceiverAsync(BatteryDevice previous)
    {
        if (!Locate(previous.Id, out _))
            return null;

        var (level, _, isCharging) = await ReceiverBattery.ReadAsync(previous.Id).ConfigureAwait(false);
        return Locate(previous.Id, out _) ? previous with { Percent = level, IsCharging = isCharging } : null;
    }

    private static bool HasHidInterface(string id, uint node, string[] instanceIds)
    {
        int identifierEnd = id.IndexOf('\\', 4);
        Guid usbContainer = ReadContainer(node);
        if (identifierEnd < 0 || usbContainer == Guid.Empty)
            return false;

        string hidPrefix = "HID" + id[3..identifierEnd];
        return instanceIds.Any(candidate =>
            StartsWith(candidate, hidPrefix) && Locate(candidate, out uint hidNode) && ReadContainer(hidNode) == usbContainer);
    }

    private static string DeviceKind(string name, string categories)
    {
        string description = $"{categories} {name}";
        if (Contains(description, "keyboard") || name.Contains("键盘", StringComparison.Ordinal)
            || StartsWith(name, "MCHOSE K"))
            return "keyboard";
        if (Contains(description, "mouse") || name.Contains("鼠标", StringComparison.Ordinal))
            return "mouse";
        if (Contains(description, "headset") || Contains(description, "headphone")
            || Contains(description, "earbuds") || name.Contains("耳机", StringComparison.Ordinal))
            return "headphones";
        if (Contains(description, "gamepad") || Contains(description, "gamecontroller")
            || Contains(description, "controller") || name.Contains("手柄", StringComparison.Ordinal))
            return "controller";
        return "other";
    }

    private static BluetoothNode? ReadBluetoothNode(string id)
    {
        if (!Locate(id, out uint node))
            return null;
        return new BluetoothNode(id, ReadContainer(node), ReadText(node, Category), ReadBatteryLevel(node));
    }

    private static int? ReadBatteryLevel(uint node)
    {
        byte[]? battery = ReadProperty(node, Battery, 1, DevpropByte);
        return battery is [<= 100] ? battery[0] : null;
    }

    private static Guid ReadContainer(uint node)
    {
        byte[]? container = ReadProperty(node, Container, 16, DevpropGuid);
        return container is { Length: 16 } ? new Guid(container) : Guid.Empty;
    }

    private static bool StartsWith(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string value, string part) =>
        value.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static string ReadText(uint node, PropertyKey key)
    {
        const uint devpropString = 0x12;
        const uint devpropStringList = 0x2012;
        byte[]? result = ReadProperty(node, key, 512, devpropString, devpropStringList);
        return result is null ? "" : Encoding.Unicode.GetString(result).TrimEnd('\0').Replace('\0', ' ').Trim();
    }

    private static byte[]? ReadProperty(uint node, PropertyKey key, uint maxSize, uint expectedType, uint alternateType = 0)
    {
        byte[] data = new byte[maxSize];
        uint size = maxSize;
        uint code = CM_Get_DevNode_PropertyW(node, in key, out uint type, data, ref size, 0);
        return code == 0 && size <= maxSize && (type == expectedType || type == alternateType)
            ? data[..(int)size]
            : null;
    }

    private static bool Locate(string id, out uint node) => CM_Locate_DevNodeW(out node, id, 0) == 0;

    private static string[] PresentDeviceIds()
    {
        const uint filterPresent = 0x100;
        const uint bufferSmall = 0x1A;
        // A new PnP node can arrive between the size query and the list query.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint code = CM_Get_Device_ID_List_SizeW(out uint count, null, filterPresent);
            if (code != 0)
                throw new InvalidOperationException($"读取设备列表失败（0x{code:X}）。");
            if (count <= 1)
                return [];

            char[] buffer = new char[count];
            code = CM_Get_Device_ID_ListW(null, buffer, count, filterPresent);
            if (code == 0)
                return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (code != bufferSmall)
                throw new InvalidOperationException($"读取设备列表失败（0x{code:X}）。");
        }
        throw new InvalidOperationException("设备列表正在变化，请刷新后重试。");
    }

    private sealed record BluetoothEndpoint(DeviceInformation Info, bool IsLowEnergy);

    private sealed record BluetoothNode(string Id, Guid ContainerId, string Category, int? Level);

    private sealed record DeviceReading(BatteryDevice Device, Func<Task<BatteryDevice?>> Recheck);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(string format, uint id)
    {
        private readonly Guid formatId = new(format);
        private readonly uint propertyId = id;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_DevNode_PropertyW(uint node, in PropertyKey key, out uint type,
        [Out] byte[] buffer, ref uint size, uint flags);
}
