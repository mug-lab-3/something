using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Something.Ntfs;

namespace Something.Windows;

/// <summary>Metadata observation for an open handle. The logical size excludes named streams.</summary>
public sealed record WindowsFileInfo(
    FileReference Reference,
    uint VolumeSerialNumber,
    long LogicalBytes,
    uint LinkCount,
    bool IsDirectory,
    ulong LastWriteTime
)
{
    internal static WindowsFileInfo Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 52)
            throw new InvalidDataException("Invalid file metadata response length.");
        uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        uint serial = BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]);
        ulong length =
            ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]) << 32)
            | BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]);
        uint links = BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]);
        ulong identity =
            ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(bytes[44..]) << 32)
            | BinaryPrimitives.ReadUInt32LittleEndian(bytes[48..]);
        if (length > long.MaxValue)
            throw new InvalidDataException("File size exceeds the supported range.");
        return new(
            new(identity),
            serial,
            (long)length,
            links,
            (attributes & 0x10) != 0,
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[20..])
        );
    }
}

/// <summary>Holds a metadata-only handle while a diagnostic compares NTFS observations.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileMetadata : IDisposable
{
    private readonly SafeFileHandle handle;
    private readonly object gate = new();
    private bool disposed;

    private WindowsFileMetadata(SafeFileHandle handle) => this.handle = handle;

    public static WindowsFileMetadata Open(string drive, string path)
    {
        WindowsMftRecordSource.DevicePath(drive);
        ArgumentNullException.ThrowIfNull(path);
        // Do not allow device paths, UNC paths, named streams or relative resolution.
        if (
            path.Length < 3
            || !char.IsAsciiLetter(path[0])
            || path[1] != ':'
            || path[2] != '\\'
            || path.AsSpan(2).Contains(':')
            || !string.Equals(path[..2], drive, StringComparison.OrdinalIgnoreCase)
        )
            throw new ArgumentException(
                "Specify an absolute file path on the selected drive.",
                nameof(path)
            );
        var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int code = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Failure(code);
        }
        return new(handle);
    }

    public WindowsFileInfo Observe(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            byte[] output = new byte[52];
            if (!GetFileInformationByHandle(handle, output))
                throw Failure(Marshal.GetLastPInvokeError());
            cancellationToken.ThrowIfCancellationRequested();
            return WindowsFileInfo.Decode(output);
        }
    }

    private static IOException Failure(int code) =>
        new($"Read file metadata failed (Win32 error {code}).", new Win32Exception(code));

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            handle.Dispose();
        }
    }

    [DllImport(
        "kernel32.dll",
        ExactSpelling = true,
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern SafeFileHandle CreateFileW(
        string path,
        uint access,
        uint share,
        IntPtr security,
        uint disposition,
        uint flags,
        IntPtr template
    );

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        [Out] byte[] output
    );
}
