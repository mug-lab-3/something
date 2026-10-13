using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Something.Windows;

public sealed record WindowsMemorySnapshot(
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    ulong AvailableCommitBytes,
    ulong AvailableVirtualBytes
);

/// <summary>Current machine/process headroom, not installed RAM or a reservation.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsMemoryAvailability
{
    public static WindowsMemorySnapshot? TryRead()
    {
        var status = new MemoryStatus { Length = checked((uint)Marshal.SizeOf<MemoryStatus>()) };
        if (!GlobalMemoryStatusEx(ref status))
            return null;
        return new(
            status.TotalPhysical,
            status.AvailablePhysical,
            status.AvailablePageFile,
            status.AvailableVirtual
        );
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
