using Something.Application.Browsing;
using Something.Ntfs;

namespace Something.Application;

public sealed record FileAnalysisEntry(
    string RelativePath,
    FileReference Parent,
    FolderEntry Entry
);

public sealed partial class ScanSession
{
    private FileAnalysisEntry[]? fileAnalysisCache;

    public async Task<FileAnalysisEntry[]> ReadFileAnalysisAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(
                () =>
                {
                    if (fileAnalysisCache is null)
                    {
                        _ = ReadAnalysis(new(), token, FolderEntrySort.AllocatedBytes);
                        var paths = analysisCache[new()]
                            .ToDictionary(f => f.Sample.Reference, f => f.RelativePath);
                        paths.Add(Root, "");
                        var files = new List<FileAnalysisEntry>();
                        foreach (var file in snapshot.ReadAnalysisFiles(token))
                        {
                            token.ThrowIfCancellationRequested();
                            // Only a validated parent identity can give a file an attributed path.
                            if (paths.TryGetValue(file.Parent, out var parent))
                                files.Add(
                                    new(
                                        parent.Length == 0
                                            ? file.Entry.Name
                                            : parent + "\\" + file.Entry.Name,
                                        file.Parent,
                                        file.Entry
                                    )
                                );
                        }
                        token.ThrowIfCancellationRequested();
                        fileAnalysisCache = files.ToArray();
                    }
                    var result = fileAnalysisCache
                        .Where(file =>
                            visibility.Includes(file.Entry.StandardFileAttributes)
                            && !(visibleScan?.ExcludedFolders.Contains(file.Parent) ?? false)
                        )
                        .ToArray();
                    token.ThrowIfCancellationRequested();
                    return result;
                },
                token
            );
        }
        finally
        {
            gate.Release();
        }
    }
}
