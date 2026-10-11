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
    private const uint QueryStorageProperty = 0x002D1400;
    private int physicalAlignment;

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
        if (control is not (GetNtfsVolumeData or GetNtfsFileRecord or QueryStorageProperty))
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

    // Called only under the acquisition source lock, including seek+read and disposal.
    public byte[] ReadVolumeBytes(long offset, int count, int logicalSectorSize)
    {
        if (
            logicalSectorSize is < 512 or > 4096
            || !System.Numerics.BitOperations.IsPow2((uint)logicalSectorSize)
            || offset < 0
            || count is < 1 or > 2 * 1024 * 1024
            || offset % logicalSectorSize != 0
            || count % logicalSectorSize != 0
        )
            throw new ArgumentOutOfRangeException(nameof(count));
        if (physicalAlignment == 0)
        {
            byte[] query = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(query, 6); // StorageAccessAlignmentProperty
            physicalAlignment = StorageAlignment.Decode(
                Query(QueryStorageProperty, query, 28),
                logicalSectorSize
            );
        }
        byte[] clearing = new byte[Math.Min(count, 65536)];
        IntPtr allocation = Marshal.AllocHGlobal(checked(count + physicalAlignment - 1));
        IntPtr aligned = IntPtr.Zero;
        try
        {
            aligned = new IntPtr(
                checked(
                    (allocation.ToInt64() + physicalAlignment - 1) & ~((long)physicalAlignment - 1)
                )
            );
            if (!SetFilePointerEx(handle, offset, out long actual, 0))
                throw Failure("Seek read-only volume", Marshal.GetLastPInvokeError());
            if (actual != offset)
                throw new IOException("Volume seek returned an unexpected position.");
            if (!ReadFile(handle, aligned, count, out int returned, IntPtr.Zero))
                throw Failure("Read ATTRIBUTE_LIST clusters", Marshal.GetLastPInvokeError());
            if (returned != count)
                throw new IOException("Partial raw volume read.");
            byte[] output = new byte[count];
            Marshal.Copy(aligned, output, 0, count);
            return output;
        }
        finally
        {
            try
            {
                if (aligned != IntPtr.Zero)
                    for (int position = 0; position < count; position += clearing.Length)
                        Marshal.Copy(
                            clearing,
                            0,
                            IntPtr.Add(aligned, position),
                            Math.Min(clearing.Length, count - position)
                        );
            }
            finally
            {
                Marshal.FreeHGlobal(allocation);
            }
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(
        SafeFileHandle handle,
        long offset,
        out long actual,
        uint origin
    );

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(
        SafeFileHandle handle,
        IntPtr buffer,
        int count,
        out int returned,
        IntPtr overlapped
    );

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
