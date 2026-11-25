using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace WirelessBattery;

internal static class ReceiverBattery
{
    private const int HidSuccess = 0x00110000;
    private const uint Overlapped = 0x40000000;

    internal static async Task<(int? Percent, string? Model)> ReadAsync(string usbInstanceId)
    {
        var isMouse = usbInstanceId.StartsWith(@"USB\VID_2023&PID_F013\", StringComparison.OrdinalIgnoreCase);
        if (!isMouse && !usbInstanceId.StartsWith(@"USB\VID_05AC&PID_024F\", StringComparison.OrdinalIgnoreCase))
            return (null, null);
        if (CM_Locate_DevNode(out var receiver, usbInstanceId, 0) != 0)
            return (null, null);

        foreach (var path in HidPaths(receiver))
        {
            // Metadata-only access avoids opening any keyboard or mouse collection for I/O.
            using var metadata = Open(path, 0, 0);
            if (metadata.IsInvalid || !MatchesReceiver(metadata, isMouse))
                continue;

            using var device = Open(path, 0xC0000000, isMouse ? 0 : Overlapped);
            if (device.IsInvalid)
                return (null, null);

            try
            {
                var percent = isMouse ? await ReadMouseAsync(device) : await ReadKeyboardAsync(device);
                string model = isMouse ? "MCHOSE A5" : "MCHOSE 无线键盘";
                return (percent, percent.HasValue ? model : null);
            }
            catch (IOException)
            {
                return (null, null);
            }
            catch (OperationCanceledException)
            {
                return (null, null);
            }
        }

        return (null, null);
    }

    private static IEnumerable<string> HidPaths(uint device)
    {
        if (CM_Get_Child(out var child, device, 0) != 0)
            yield break;

        var hidClass = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
        do
        {
            var id = new StringBuilder(200);
            if (CM_Get_Device_ID(child, id, id.Capacity, 0) == 0 &&
                id.ToString() is string instanceId && instanceId.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase) &&
                CM_Get_Device_Interface_List_Size(out var length, ref hidClass, instanceId, 0) == 0 && length > 1)
            {
                var paths = new char[length];
                if (CM_Get_Device_Interface_List(ref hidClass, instanceId, paths, length, 0) == 0)
                    foreach (var path in new string(paths).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                        yield return path;
            }

            foreach (var path in HidPaths(child))
                yield return path;
        } while (CM_Get_Sibling(out child, child, 0) == 0);
    }

    private static bool MatchesReceiver(SafeFileHandle device, bool isMouse)
    {
        if (!HidD_GetPreparsedData(device, out var data))
            return false;

        try
        {
            if (HidP_GetCaps(data, out var caps) != HidSuccess)
                return false;

            var manufacturer = new StringBuilder(128);
            var product = new StringBuilder(128);
            if (!HidD_GetManufacturerString(device, manufacturer, 256) || !HidD_GetProductString(device, product, 256))
                return false;

            if (isMouse)
                return manufacturer.ToString() == "MCHOSE" && product.ToString() == "MCHOSE A5 2.4G" &&
                    caps.Page == 0xFFFF && caps.Usage == 0 && caps.FeatureLength == 65 &&
                    caps.FeatureValues == 1 && HasReport(data, 2, 0, isValue: true);

            return manufacturer.ToString() == "Compx" && product.ToString() == "2.4G Wireless Receiver" &&
                caps.Page == 0xFF02 && caps.Usage == 2 && caps.InputLength == 20 && caps.OutputLength == 20 &&
                caps.InputButtons == 1 && caps.OutputButtons == 1 &&
                HasReport(data, 0, 0x13, isValue: false) && HasReport(data, 1, 0x13, isValue: false);
        }
        finally
        {
            HidD_FreePreparsedData(data);
        }
    }

    private static bool HasReport(nint data, int type, byte reportId, bool isValue)
    {
        ushort count = 1;
        var status = isValue ? HidP_GetValueCaps(type, out var caps, ref count, data) :
            HidP_GetButtonCaps(type, out caps, ref count, data);
        return status == HidSuccess && count == 1 && caps.ReportId == reportId;
    }

    // MCHOSE A5 first-generation feature report, from the community A5 web driver (battery page 0).
    private static async Task<int?> ReadMouseAsync(SafeFileHandle device)
    {
        var request = new byte[65];
        request[3] = 2;
        request[4] = 2;
        request[6] = 0x83;
        if (!HidD_SetFeature(device, request, request.Length))
            return null;

        var reply = new byte[request.Length];
        await Task.Delay(34);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (HidD_GetFeature(device, reply, reply.Length) && ParseMouse(reply) is int percent)
                return percent;
            reply[0] = 0;
            if (attempt == 3 && !HidD_SetFeature(device, request, request.Length))
                return null;
            await Task.Delay(30);
        }

        return null;
    }

    // Compx receiver read-battery command documented by mchose-hid-bridge.
    private static async Task<int?> ReadKeyboardAsync(SafeFileHandle device)
    {
        await using var stream = new FileStream(device, FileAccess.ReadWrite, bufferSize: 1, isAsync: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
        var report = new byte[20];
        report[0] = 0x13;
        report[1] = 0x4A;
        report[2] = 1;
        report[19] = Checksum(report);
        await stream.WriteAsync(report, timeout.Token);

        while (await stream.ReadAsync(report, timeout.Token) is int length && length > 0)
            if (ParseKeyboard(report.AsSpan(0, length)) is int percent)
                return percent;

        return null;
    }

    internal static int? ParseMouse(ReadOnlySpan<byte> reply) =>
        reply.Length == 65 && reply[0] == 0 && reply[1] == 0xA1 && reply[5] == 0 && reply[6] == 0x83 &&
        reply[8] <= 100 ? reply[8] : null;

    internal static int? ParseKeyboard(ReadOnlySpan<byte> reply) =>
        reply.Length == 20 && reply[0] == 0x13 && reply[1] == 0x4A && reply[2] == 1 && reply[3] == 0 &&
        reply[4] == 2 && reply[5] <= 100 && reply[19] == Checksum(reply) ? reply[5] : null;

    private static byte Checksum(ReadOnlySpan<byte> report)
    {
        var sum = 0;
        foreach (var value in report[..^1])
            sum += value;
        return (byte)(sum & 0xFF);
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct HidCaps
    {
        [FieldOffset(0)] public ushort Usage;
        [FieldOffset(2)] public ushort Page;
        [FieldOffset(4)] public ushort InputLength;
        [FieldOffset(6)] public ushort OutputLength;
        [FieldOffset(8)] public ushort FeatureLength;
        [FieldOffset(46)] public ushort InputButtons;
        [FieldOffset(52)] public ushort OutputButtons;
        [FieldOffset(60)] public ushort FeatureValues;
    }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct ReportCaps
    {
        [FieldOffset(2)] public byte ReportId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint attributes,
        uint disposition, uint flags, nint template);

    private static SafeFileHandle Open(string path, uint access, uint flags) =>
        CreateFile(path, access, 3, 0, 3, flags, 0);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNode(out uint device, string id, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Child(out uint child, uint device, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Sibling(out uint sibling, uint device, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(uint device, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_Size(out uint length, ref Guid deviceClass, string id, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List(ref Guid deviceClass, string id, [Out] char[] paths, uint length, uint flags);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out nint data);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(nint data);
    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(nint data, out HidCaps caps);
    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int type, out ReportCaps caps, ref ushort count, nint data);
    [DllImport("hid.dll")]
    private static extern int HidP_GetButtonCaps(int type, out ReportCaps caps, ref ushort count, nint data);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetManufacturerString(SafeFileHandle device, StringBuilder value, int bytes);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(SafeFileHandle device, StringBuilder value, int bytes);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetFeature(SafeFileHandle device, byte[] report, int length);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetFeature(SafeFileHandle device, [In, Out] byte[] report, int length);
}
