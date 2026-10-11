using DiscUtils;
using DiscUtils.Ntfs;
using Something.Ntfs;

namespace Something.Ntfs.Tests;

public sealed class IndependentSparseImageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SparseImage_EmptyHolesOrWrittenEdges_AgreesWithIndependentPhysicalClusters(
        bool writeEdges
    )
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DiscUtils NTFS image creation requires Windows; no live volume is used.");
            return;
        }
        var token = TestContext.Current.CancellationToken;
        token.ThrowIfCancellationRequested();
        // All formatting and writes target this private memory stream, never a file or volume.
        using var image = new MemoryStream(new byte[64 * 1024 * 1024], writable: true);
        using var oracle = NtfsFileSystem.Format(
            image,
            "Synthetic",
            Geometry.FromCapacity(image.Length),
            0,
            image.Length / 512
        );
        const string path = @"\sparse.bin";
        const long logical = 8 * 1024 * 1024;
        using (oracle.OpenFile(path, FileMode.CreateNew, FileAccess.ReadWrite)) { }
        oracle.SetAttributes(path, oracle.GetAttributes(path) | FileAttributes.SparseFile);
        using (var data = oracle.OpenFile(path, FileMode.Open, FileAccess.ReadWrite))
        {
            data.SetLength(logical);
            if (writeEdges)
            {
                byte[] block = Enumerable.Repeat((byte)0x71, (int)oracle.ClusterSize).ToArray();
                await data.WriteAsync(block, token);
                data.Position = logical - block.Length;
                await data.WriteAsync(block, token);
            }
        }
        var reference = new FileReference(unchecked((ulong)oracle.GetFileId(path)));
        long oraclePhysical = checked(
            oracle.PathToClusters(path).Sum(r => r.Count) * oracle.ClusterSize
        );
        using var mft = oracle.OpenFile("$MFT", FileMode.Open, FileAccess.Read);
        // The formatter's boot record declares the record size; do not assume our fixture encoder.
        image.Position = 64;
        int recordEncoding = unchecked((sbyte)image.ReadByte());
        int recordSize =
            recordEncoding < 0
                ? 1 << -recordEncoding
                : checked(recordEncoding * (int)oracle.ClusterSize);
        byte[] raw = new byte[recordSize];
        mft.Position = checked((long)reference.RecordIndex * recordSize);
        await mft.ReadExactlyAsync(raw, token);

        var record = new MftRecordParser().Parse(raw, reference.RecordIndex);
        var file = new MftFileAssembler((int)oracle.ClusterSize).Assemble(record, []);
        var stream = Assert.Single(file.DataStreams);

        Assert.Equal(reference, file.Reference);
        Assert.Equal(logical, stream.LogicalBytes);
        Assert.Equal(oraclePhysical, stream.AllocatedBytes);
        Assert.Equal(writeEdges ? 2 * oracle.ClusterSize : 0, stream.AllocatedBytes);
        Assert.Contains(stream.Runs, r => r.StartLcn is null);
        Assert.True((stream.Storage!.Flags & NtfsAttributeFlags.Sparse) != 0);
        Assert.Equal(writeEdges ? 2 : 0, stream.Runs.Count(r => r.StartLcn is not null));
    }
}
