using System.Numerics;
using Something.Ntfs;
using Something.Storage;

namespace Something.Application;

/// <summary>Common display facts for recursive folder and per-name file analysis.</summary>
public sealed record CapacityAnalysisItem(
    FileReference Reference,
    string RelativePath,
    bool IsDirectory,
    UInt128? AllocatedBytes,
    uint? StandardFileAttributes
)
{
    public bool Comparing { get; init; }
    public bool CapacityIncomplete { get; init; }
    public FolderChange? FolderChange { get; init; }
    public EntryChange? FileChange { get; init; }
    public BigInteger? Delta => FolderChange?.AllocatedDelta ?? FileChange?.Delta;
    public FolderChangeStatus? Status => FolderChange?.Status ?? FileChange?.Status;
    public UInt128? BeforeBytes => FolderChange?.BeforeAllocatedBytes ?? FileChange?.BeforeBytes;
    public UInt128? AfterBytes => FolderChange?.AfterAllocatedBytes ?? FileChange?.AfterBytes;
    public bool HasBefore => FolderChange?.BeforePath is not null || FileChange?.Before is not null;
    public bool HasAfter => FolderChange?.AfterPath is not null || FileChange?.After is not null;

    public static CapacityAnalysisItem FromFolder(
        RankedFolder folder,
        uint? attributes = null,
        FolderChange? change = null,
        bool comparing = false
    ) =>
        new(
            folder.Sample.Reference,
            folder.RelativePath,
            true,
            folder.Data.AllocatedBytes,
            attributes
        )
        {
            FolderChange = change,
            Comparing = comparing,
        };

    public static CapacityAnalysisItem FromChange(FolderChange change, uint? attributes = null) =>
        new(
            change.Reference,
            change.AfterPath ?? change.BeforePath ?? "",
            true,
            change.AfterAllocatedBytes,
            attributes
        )
        {
            FolderChange = change,
            Comparing = true,
        };

    public static CapacityAnalysisItem FromFile(FileAnalysisEntry file) =>
        new(
            file.Entry.Reference,
            file.RelativePath,
            false,
            file.Entry.Data?.AllocatedBytes,
            file.Entry.StandardFileAttributes
        )
        {
            CapacityIncomplete = file.Entry.CapacityIncomplete || !file.Entry.NamesComplete,
        };

    /// <summary>Compared file names are full observed relative paths, not just basenames.</summary>
    public static CapacityAnalysisItem FromFileChange(EntryChange change) =>
        new(
            change.Entry.Reference,
            change.Entry.Name,
            false,
            change.After?.Data?.AllocatedBytes,
            change.Entry.StandardFileAttributes
        )
        {
            FileChange = change,
            Comparing = true,
            CapacityIncomplete = change.Entry.CapacityIncomplete || !change.Entry.NamesComplete,
        };
}
