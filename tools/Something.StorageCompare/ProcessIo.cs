using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Something.StorageCompare;

internal static class ProcessIo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Counters
    {
        public ulong ReadOperations,
            WriteOperations,
            OtherOperations,
            ReadBytes,
            WriteBytes,
            OtherBytes;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out Counters counters);

    internal static long WrittenBytes(Process process) =>
        OperatingSystem.IsWindows() && GetProcessIoCounters(process.Handle, out var counters)
            ? checked((long)counters.WriteBytes)
            : -1;
}
