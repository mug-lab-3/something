using Something.Application.Scanning;

namespace Something.Storage;

/// <summary>A size estimate of the rows a scan takes in the database, for the diagnostic tools and
/// the choice between a memory and a file database. The application does not depend on it.</summary>
internal static class DatabaseSizeEstimate
{
    /// <summary>Conservative heuristic for database rows, names, streams, indexes and page slack.
    /// Uses metadata lengths, never file content sizes. Not an exact serialized size or RAM limit.</summary>
    internal static ulong For(CompletedScan scan, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ulong bytes = 64 * 1024;
        checked
        {
            foreach (var item in scan.Observations)
            {
                token.ThrowIfCancellationRequested();
                bytes += 256;
                if (item.File is not { } file)
                    continue;
                foreach (var name in file.Names)
                {
                    bytes += 384 + (ulong)name.Name.Length * 2;
                    if (file.IsDirectory)
                        bytes += 256 + (ulong)name.Name.Length * 2;
                }
                foreach (var stream in file.Streams)
                    bytes += 320 + (ulong)stream.Name.Length * 2;
                if (file.IsDirectory)
                    bytes += 512;
            }
            return (bytes + bytes / 2 + 16383) / 16384 * 16384;
        }
    }
}
