using System.Buffers.Binary;
using Something.Windows;

namespace Something.Ntfs.Tests;

public sealed class WindowsReferenceMetadataTests
{
    [Theory]
    [InlineData(0uL)]
    [InlineData(0x0003_0000_0000_002AuL)]
    [InlineData(ulong.MaxValue)]
    public void Descriptor_EncodesFullUnsignedReferenceAndZeroedUnionTail(ulong value)
    {
        byte[] descriptor = WindowsFileIdDescriptor.Encode(new(value));

        Assert.Equal(24, descriptor.Length);
        Assert.Equal(24u, BinaryPrimitives.ReadUInt32LittleEndian(descriptor));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(4)));
        Assert.Equal(value, BinaryPrimitives.ReadUInt64LittleEndian(descriptor.AsSpan(8)));
        Assert.Equal(0uL, BinaryPrimitives.ReadUInt64LittleEndian(descriptor.AsSpan(16)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    [InlineData(9, false)]
    [InlineData(10, false)]
    public void Consistency_RejectsReuseWrongVolumeChangesAndMixedObservations(
        int scenario,
        bool expected
    )
    {
        var reference = FileReference.Create(42, 3);
        var before = new WindowsFileInfo(reference, 123, 5000, 1, false, 99)
        {
            FileAttributes = 0x800,
        };
        var storage = new WindowsStorageInfo(
            new(8192, 5000, 1, false, false),
            new(4096, 2, 4, 12, 12),
            0x800
        );
        before = scenario switch
        {
            1 => before with { Reference = FileReference.Create(42, 4) },
            2 => before with { VolumeSerialNumber = 124 },
            3 => before with { IsDirectory = true },
            _ => before,
        };
        storage = scenario switch
        {
            4 => storage with { Standard = storage.Standard with { DeletePending = true } },
            5 => storage with { Standard = storage.Standard with { IsDirectory = true } },
            6 => storage with { Standard = storage.Standard with { LogicalBytes = 4999 } },
            7 => storage with { FileAttributes = 0 },
            _ => storage,
        };
        var after = scenario == 8 ? before with { LastWriteTime = 100 } : before;
        var storageAfter = scenario switch
        {
            9 => storage with { Compression = storage.Compression with { ReportedBytes = 8192 } },
            10 => storage with { Standard = storage.Standard with { DeletePending = true } },
            _ => storage,
        };

        bool stable = WindowsFileObservationConsistency.IsStable(
            reference,
            123,
            before,
            storage,
            after,
            storageAfter
        );

        Assert.Equal(expected, stable);
    }

    [Fact]
    public void OpenByReference_PreCancelled_DoesNotAccessFilesystem()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            if (OperatingSystem.IsWindows())
                WindowsFileMetadata.OpenByReference(
                    "invalid",
                    FileReference.Create(42, 3),
                    cancellation.Token
                );
        });
    }

    [Fact]
    public void OpenByReference_OwnTemporaryFile_SurvivesRenameAndRejectsStaleSequence()
    {
        if (!OperatingSystem.IsWindows())
            return;
        string path = Path.Combine(
            Path.GetTempPath(),
            $"something-reference-{Guid.NewGuid():N}.tmp"
        );
        string renamed = path + ".renamed";
        string drive = Path.GetPathRoot(path)![..2];
        if (
            !string.Equals(
                new DriveInfo(drive).DriveFormat,
                "NTFS",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            Assert.Skip("The private temporary-file integration fixture requires NTFS.");
            return;
        }
        try
        {
            File.WriteAllBytes(path, new byte[12345]);
            using var byPath = WindowsFileMetadata.Open(drive, path);
            var original = byPath.Observe(TestContext.Current.CancellationToken);
            using var byId = WindowsFileMetadata.OpenByReference(
                drive,
                original.Reference,
                TestContext.Current.CancellationToken
            );
            var storage = byId.ObserveStorage(TestContext.Current.CancellationToken);
            File.Move(path, renamed);

            Assert.Equal(original, byId.Observe(TestContext.Current.CancellationToken));
            Assert.Equal(storage, byId.ObserveStorage(TestContext.Current.CancellationToken));
            Assert.Equal(byPath.ObserveStorage(TestContext.Current.CancellationToken), storage);
            var stale = FileReference.Create(
                original.Reference.RecordIndex,
                unchecked((ushort)(original.Reference.SequenceNumber + 1))
            );
            Assert.Throws<IOException>(() =>
            {
                if (OperatingSystem.IsWindows())
                    using (
                        WindowsFileMetadata.OpenByReference(
                            drive,
                            stale,
                            TestContext.Current.CancellationToken
                        )
                    ) { }
            });
        }
        finally
        {
            File.Delete(path);
            File.Delete(renamed);
        }
    }
}
