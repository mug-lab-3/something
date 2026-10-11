using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Something.Windows;

[SupportedOSPlatform("windows")]
internal sealed class NativeNtfsVolumeIo(SafeFileHandle handle) : INtfsVolumeIo
{
    internal const uint GenericRead = 0x80000000;
    internal const uint SharedReadWriteDelete = 7;
    internal const uint OpenExisting = 3;
    private const uint GetNtfsVolumeData = 0x00090064;
    private const uint GetNtfsFileRecord = 0x00090068;

    public static NativeNtfsVolumeIo Open(string drive)
    {
        var handle = CreateFileW(
            WindowsMftRecordSource.DevicePath(drive),
            GenericRead,
            SharedReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero
        );
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Failure("Open read-only volume", error);
        }
        return new(handle);
    }

    public byte[] ReadVolumeData() => Query(GetNtfsVolumeData, [], 96);

    public byte[] ReadFileRecord(ulong recordIndex, int recordSize)
    {
        byte[] input = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(input, recordIndex);
        return Query(GetNtfsFileRecord, input, checked(recordSize + 16));
    }

    private byte[] Query(uint control, byte[] input, int outputSize)
    {
        if (control is not (GetNtfsVolumeData or GetNtfsFileRecord))
            throw new ArgumentOutOfRangeException(nameof(control));
        byte[] output = new byte[outputSize];
        if (
            !DeviceIoControl(
                handle,
                control,
                input,
                input.Length,
                output,
                output.Length,
                out int returned,
                IntPtr.Zero
            )
        )
            throw Failure("Read NTFS metadata", Marshal.GetLastPInvokeError());
        if (returned < 0 || returned > output.Length)
            throw new InvalidDataException("Invalid native response length.");
        return output.AsSpan(0, returned).ToArray();
    }

    private static IOException Failure(string operation, int code) =>
        new($"{operation} failed (Win32 error {code}).", new Win32Exception(code));

    public void Dispose() => handle.Dispose();

    // Classic array marshaling keeps the acquisition layer free of unsafe pointers.
    [DllImport(
        "kernel32.dll",
        ExactSpelling = true,
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern SafeFileHandle CreateFileW(
        string name,
        uint access,
        uint share,
        IntPtr security,
        uint disposition,
        uint flags,
        IntPtr template
    );

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle handle,
        uint code,
        [In] byte[] input,
        int inputSize,
        [Out] byte[] output,
        int outputSize,
        out int returned,
        IntPtr overlapped
    );
}
