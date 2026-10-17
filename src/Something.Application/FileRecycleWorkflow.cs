using Something.Windows;

namespace Something.Application;

/// <summary>Only a verified, explicitly confirmed item reaches the native recycle operation.</summary>
public sealed class FileRecycleWorkflow(IFileRecycleBackend backend)
{
    public async Task<FileRecycleResult> RunAsync(
        FileRecycleTarget target,
        Func<FileRecycleTarget, CancellationToken, Task<bool>> confirm,
        nint owner,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();
        if (FileRecyclePolicy.IsProtected(target))
            return FileRecycleResult.Protected;
        var state = await backend.InspectAsync(target, token);
        if (state == FileRecycleResult.HiddenNeedsConfirmation)
            target = target with
            {
                StandardFileAttributes =
                    target.StandardFileAttributes.GetValueOrDefault() | (uint)FileAttributes.Hidden,
            };
        if (state is not (FileRecycleResult.Ready or FileRecycleResult.HiddenNeedsConfirmation))
            return state;
        if (!await confirm(target, token))
            return FileRecycleResult.Cancelled;
        if (FileRecyclePolicy.IsHidden(target))
            target = target with { HiddenDeletionConfirmed = true };
        token.ThrowIfCancellationRequested();
        // Recheck after the dialog: a path can disappear or be reused while it is open.
        state = await backend.InspectAsync(target, token);
        if (state != FileRecycleResult.Ready)
            return state;
        return await backend.RecycleAsync(target, owner, token);
    }
}
