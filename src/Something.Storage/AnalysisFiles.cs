using Microsoft.EntityFrameworkCore;
using Something.Application.Browsing;
using Something.Ntfs;
using Something.Statistics;

namespace Something.Storage;

internal sealed partial class ScanStore
{
    /// <summary>Stream ordinary file names in one query; each hard-link name remains a row.</summary>
    public IEnumerable<AnalysisFileName> ReadAnalysisFiles(
        Guid scanId,
        CancellationToken token = default
    )
    {
        token.ThrowIfCancellationRequested();
        RequireCompleted(scanId);
        var query =
            from name in context.Names.AsNoTracking()
            join file in context.Observations.AsNoTracking()
                on new { name.ScanId, name.RecordIndex } equals new
                {
                    file.ScanId,
                    file.RecordIndex,
                }
            join stream in context.Streams.AsNoTracking().Where(s => s.Name == "")
                on new { name.ScanId, name.RecordIndex } equals new
                {
                    stream.ScanId,
                    stream.RecordIndex,
                }
                into streams
            from stream in streams.DefaultIfEmpty()
            where
                name.ScanId == scanId
                && name.Namespace != FileNameNamespace.Dos
                && file.IsDirectory == false
                && file.Sequence != null
                && file.RecordIndex >= 16
                && file.Domain == CapacityDomain.Ordinary
            select new
            {
                name.ParentIndex,
                name.ParentSequence,
                name.RecordIndex,
                name.Ordinal,
                name.Name,
                file.Sequence,
                file.Resolution,
                file.LinksComplete,
                file.StandardFileAttributes,
                Logical = (long?)stream.Logical,
                Allocated = (long?)stream.Allocated,
            };
        if (context is DuckScanDbContext)
            query = context
                .Browser.AsNoTracking()
                .Where(row =>
                    row.ScanId == scanId && row.IsDirectory == false && row.Sequence != null
                )
                .Select(row => new
                {
                    row.ParentIndex,
                    row.ParentSequence,
                    row.RecordIndex,
                    row.Ordinal,
                    row.Name,
                    row.Sequence,
                    row.Resolution,
                    row.LinksComplete,
                    row.StandardFileAttributes,
                    Logical = row.StreamLogical,
                    Allocated = row.StreamAllocated,
                });
        foreach (var row in query.AsEnumerable())
        {
            token.ThrowIfCancellationRequested();
            CapacityAmount? capacity =
                row.Resolution == CapacityResolution.Resolved
                    ? new(
                        checked((UInt128)(row.Logical ?? 0)),
                        checked((UInt128)(row.Allocated ?? 0))
                    )
                    : null;
            yield return new(
                FileReference.Create(checked((ulong)row.ParentIndex), row.ParentSequence),
                new(
                    FileReference.Create(checked((ulong)row.RecordIndex), row.Sequence!.Value),
                    row.Ordinal,
                    row.Name,
                    false,
                    capacity,
                    row.LinksComplete ?? false,
                    false,
                    capacity is null
                )
                {
                    StandardFileAttributes = row.StandardFileAttributes,
                }
            );
        }
    }
}
