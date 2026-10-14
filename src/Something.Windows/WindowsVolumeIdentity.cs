using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Something.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsVolumeIdentity
{
    public static string ReadHistoryKey(string drive)
    {
        string key = ReadKey(drive);
        using var source = WindowsMftRecordSource.Open(drive);
        if (key != ReadKey(drive))
            throw new IOException("Volume mount changed while reading identity.");
        return $"{key}:{source.Volume.SerialNumber:X16}";
    }

    public static string ReadKey(string drive)
    {
        _ = WindowsMftRecordSource.DevicePath(drive);
        char[] buffer = new char[64];
        if (!GetVolumeNameForVolumeMountPointW(drive + "\\", buffer, (uint)buffer.Length))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Read volume identity failed.");
        string value = new(
            buffer,
            0,
            Array.IndexOf(buffer, '\0') is > 0 and var length ? length : buffer.Length
        );
        if (
            !value.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
            || !value.EndsWith(@"}\", StringComparison.Ordinal)
            || !Guid.TryParse(value[10..^1], out var id)
        )
            throw new InvalidDataException("Windows returned an invalid volume identity.");
        return $"volume:{id:N}";
    }

    [DllImport(
        "kernel32.dll",
        ExactSpelling = true,
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string mountPoint,
        [Out] char[] volumeName,
        uint bufferLength
    );
}
