using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Something.Ntfs;

namespace Something.Windows;

internal static class WindowsFileIdDescriptor
{
    // Two DWORDs followed by the 16-byte union in FILE_ID_DESCRIPTOR; FileIdType = 0.
    internal static byte[] Encode(FileReference reference)
    {
        byte[] descriptor = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, (uint)descriptor.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(8), reference.Value);
        return descriptor;
    }
}

/// <summary>Checks independently acquired handle observations without implying a point-in-time snapshot.</summary>
public static class WindowsFileObservationConsistency
{
    public static bool IsStable(
        FileReference expectedReference,
        uint expectedVolumeSerial,
        WindowsFileInfo before,
        WindowsStorageInfo storageBefore,
        WindowsFileInfo after,
        WindowsStorageInfo storageAfter
    ) =>
        before.Reference == expectedReference
        && before.VolumeSerialNumber == expectedVolumeSerial
        && before == after
        && storageBefore == storageAfter
        && !before.IsDirectory
        && !storageBefore.Standard.IsDirectory
        && !storageBefore.Standard.DeletePending
        && storageBefore.Standard.LogicalBytes == before.LogicalBytes
        && storageBefore.FileAttributes == before.FileAttributes;
}

/// <summary>FILE_COMPRESSION_INFO for the open stream. Excludes filesystem metadata overhead.</summary>
public sealed record WindowsCompressionInfo(
    long ReportedBytes,
    ushort CompressionFormat,
    byte CompressionUnitShift,
    byte ChunkShift,
    byte ClusterShift
)
{
    internal const int ResponseSize = 16;

    internal static WindowsCompressionInfo Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ResponseSize)
            throw new InvalidDataException("Invalid file storage response length.");
        long allocated = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        if (allocated < 0)
            throw new InvalidDataException("Invalid file storage size.");
        // Keep unknown format/shift values as observations; never shift by untrusted values.
        return new(
            allocated,
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]),
            bytes[10],
            bytes[11],
            bytes[12]
        );
    }
}

public sealed record WindowsStandardInfo(
    long AllocationBytes,
    long LogicalBytes,
    uint LinkCount,
    bool DeletePending,
    bool IsDirectory
)
{
    internal const int ResponseSize = 24;

    internal static WindowsStandardInfo Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ResponseSize)
            throw new InvalidDataException("Invalid standard file response length.");
        long allocation = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        long logical = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        if (allocation < 0 || logical < 0 || bytes[20] > 1 || bytes[21] > 1)
            throw new InvalidDataException("Invalid standard file response fields.");
        return new(
            allocation,
            logical,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
            bytes[20] != 0,
            bytes[21] != 0
        );
    }
}

/// <summary>Separate API observations; selection does not round or infer missing capacity.</summary>
public sealed record WindowsStorageInfo(
    WindowsStandardInfo Standard,
    WindowsCompressionInfo Compression,
    uint FileAttributes
)
{
    public bool UsesCompressionInfo =>
        (FileAttributes & (0x200u | 0x800u)) != 0 || Compression.CompressionFormat != 0;
    public long AllocatedBytes =>
        UsesCompressionInfo ? Compression.ReportedBytes : Standard.AllocationBytes;
}

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
    public uint FileAttributes { get; init; }

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
            ((System.IO.FileAttributes)attributes).HasFlag(System.IO.FileAttributes.Directory),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[20..])
        )
        {
            FileAttributes = attributes,
        };
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

    /// <summary>Opens the exact NTFS reference, including its sequence, without following reparse points.</summary>
    public static WindowsFileMetadata OpenByReference(
        string drive,
        FileReference reference,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        // OpenFileById accepts a handle to any file on the selected volume. A root handle needs no raw-volume access.
        using var hint = Open(drive, drive + @"\");
        cancellationToken.ThrowIfCancellationRequested();
        var handle = OpenFileById(
            hint.handle,
            WindowsFileIdDescriptor.Encode(reference),
            0,
            7,
            IntPtr.Zero,
            0x02200000 // BACKUP_SEMANTICS | OPEN_REPARSE_POINT; no write access.
        );
        if (handle.IsInvalid)
        {
            int code = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Failure(code);
        }
        var file = new WindowsFileMetadata(handle);
        try
        {
            if (file.Observe(cancellationToken).Reference != reference)
                throw new InvalidDataException(
                    "Opened file reference differs from the requested identity."
                );
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public static WindowsFileMetadata Open(string drive, string path) =>
        OpenCore(drive, path, 7, false);

    internal static WindowsFileMetadata OpenForRecycle(string drive, string path, uint share = 7) =>
        OpenCore(drive, path, share, true);

    private static WindowsFileMetadata OpenCore(
        string drive,
        string path,
        uint share,
        bool noFollow
    )
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
        var handle = CreateFileW(
            path,
            0,
            share,
            IntPtr.Zero,
            3,
            0x02000000u | (noFollow ? 0x00200000u : 0),
            IntPtr.Zero
        );
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

    public WindowsStorageInfo ObserveStorage(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            byte[] standard = new byte[WindowsStandardInfo.ResponseSize];
            byte[] compression = new byte[WindowsCompressionInfo.ResponseSize];
            // Ordinary allocation: FileStandardInfo (1). Sparse/compressed: FileCompressionInfo (8).
            if (!GetFileInformationByHandleEx(handle, 1, standard, (uint)standard.Length))
                throw Failure(Marshal.GetLastPInvokeError());
            cancellationToken.ThrowIfCancellationRequested();
            if (!GetFileInformationByHandleEx(handle, 8, compression, (uint)compression.Length))
                throw Failure(Marshal.GetLastPInvokeError());
            cancellationToken.ThrowIfCancellationRequested();
            return new(
                WindowsStandardInfo.Decode(standard),
                WindowsCompressionInfo.Decode(compression),
                Observe(cancellationToken).FileAttributes
            );
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
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint,
        [In] byte[] descriptor,
        uint access,
        uint share,
        IntPtr security,
        uint flags
    );

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        [Out] byte[] output
    );

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle,
        int informationClass,
        [Out] byte[] output,
        uint bufferSize
    );
}
