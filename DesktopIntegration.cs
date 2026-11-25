using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WirelessBattery;

internal static class DesktopIntegration
{
    private const string ShortcutName = "外设电量.lnk";
    private const string Marker = "外设电量（PeriphGauge）";
    private const string StartupArguments = "--startup";

    internal static bool StartsWithWindows
    {
        get
        {
            var shortcut = ReadShortcut(ShortcutPath(Environment.SpecialFolder.Startup));
            return shortcut is { Owned: true, Arguments: StartupArguments } &&
                string.Equals(shortcut.Value.Target, ExecutablePath(), StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static void SetStartsWithWindows(bool enabled)
    {
        string path = ShortcutPath(Environment.SpecialFolder.Startup);
        if (enabled)
            SaveShortcut(path, StartupArguments);
        else
            RemoveShortcut(path);
    }

    internal static void CreateDesktopShortcut() =>
        SaveShortcut(ShortcutPath(Environment.SpecialFolder.DesktopDirectory), "");

    private static string ShortcutPath(Environment.SpecialFolder folder)
    {
        string directory = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(directory))
            throw new IOException("找不到 Windows 快捷方式文件夹。");
        return Path.Combine(directory, ShortcutName);
    }

    private static string ExecutablePath()
    {
        string executable = Environment.ProcessPath ?? throw new IOException("找不到外设电量程序。");
        if (string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            string? assemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
            if (assemblyName is null)
                throw new IOException("找不到外设电量程序。");
            executable = Path.Combine(AppContext.BaseDirectory, assemblyName + ".exe");
        }
        if (!File.Exists(executable))
            throw new IOException("请通过外设电量的 EXE 文件启动程序。");
        return executable;
    }

    private static void SaveShortcut(string path, string arguments)
    {
        EnsureOwnedOrMissing(path);
        string executable = ExecutablePath();
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string pending = Path.Combine(directory, $".PeriphGauge-{Guid.NewGuid():N}.lnk");

        try
        {
            UseShellLink(pending, link =>
            {
                link.TargetPath = executable;
                link.Arguments = arguments;
                link.Description = Marker;
                link.WorkingDirectory = Path.GetDirectoryName(executable);
                link.IconLocation = executable + ",0";
                link.Save();
                return true;
            });
            bool replace = EnsureOwnedOrMissing(path);
            File.Move(pending, path, overwrite: replace);
        }
        finally
        {
            File.Delete(pending);
        }
    }

    private static void RemoveShortcut(string path)
    {
        if (EnsureOwnedOrMissing(path))
            File.Delete(path);
    }

    private static bool EnsureOwnedOrMissing(string path)
    {
        var existing = ReadShortcut(path);
        if (existing is { Owned: false })
            throw new IOException("已有同名快捷方式，请先将它改名或移走。");
        return existing.HasValue;
    }

    private static (bool Owned, string Target, string Arguments)? ReadShortcut(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                return (false, "", "");
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        return UseShellLink(path, link =>
            ((string)link.Description == Marker, (string)link.TargetPath, (string)link.Arguments));
    }

    private static T UseShellLink<T>(string path, Func<dynamic, T> use)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
        object shell = Activator.CreateInstance(shellType)!;
        try
        {
            object link = ((dynamic)shell).CreateShortcut(path);
            try
            {
                return use(link);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
