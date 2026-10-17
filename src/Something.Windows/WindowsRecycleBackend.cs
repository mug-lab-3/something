using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Something.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsRecycleBackend : IFileRecycleBackend
{
    public Task<FileRecycleResult> InspectAsync(
        FileRecycleTarget target,
        CancellationToken token
    ) => Task.Run(() => Inspect(target, token), token);

    internal static FileRecycleResult Inspect(FileRecycleTarget target, CancellationToken token)
    {
        RecyclePath.Validate(target);
        token.ThrowIfCancellationRequested();
        if (FileRecyclePolicy.IsProtected(target))
            return FileRecycleResult.Protected;
        try
        {
            using var parents = new ParentLease(target, token);
            string key = WindowsVolumeIdentity.ReadKey(target.Drive);
            if (
                !string.Equals(
                    target.VolumeKey,
                    $"{key}:{target.VolumeSerial:X16}",
                    StringComparison.OrdinalIgnoreCase
                )
            )
                return FileRecycleResult.Replaced;
            using var item = WindowsFileMetadata.OpenForRecycle(target.Drive, target.Path);
            var current = item.Observe(token);
            if (
                current.Reference != target.Reference
                || current.IsDirectory != target.IsDirectory
                || current.VolumeSerialNumber != unchecked((uint)target.VolumeSerial)
            )
                return FileRecycleResult.Replaced;
            var live = target with { StandardFileAttributes = current.FileAttributes };
            if (FileRecyclePolicy.IsProtected(live))
                return FileRecycleResult.Protected;
            return FileRecyclePolicy.IsHidden(live) && !target.HiddenDeletionConfirmed
                ? FileRecycleResult.HiddenNeedsConfirmation
                : FileRecycleResult.Ready;
        }
        catch (IOException error)
            when (error.InnerException is Win32Exception { NativeErrorCode: 2 or 3 })
        {
            return FileRecycleResult.Missing;
        }
    }

    public Task<FileRecycleResult> RecycleAsync(
        FileRecycleTarget target,
        nint owner,
        CancellationToken token
    )
    {
        var completion = new TaskCompletionSource<FileRecycleResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Recycle(target, owner, token));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                completion.SetCanceled(token);
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        })
        {
            IsBackground = true,
            Name = "Something recycle operation",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static FileRecycleResult Recycle(
        FileRecycleTarget target,
        nint owner,
        CancellationToken token
    )
    {
        var state = Inspect(target, token);
        if (state != FileRecycleResult.Ready)
            return state;
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2));
        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            using var parents = new ParentLease(target, token);
            operation = (IFileOperation)
                Activator.CreateInstance(
                    Type.GetTypeFromCLSID(
                        new("3AD05575-8857-4850-9277-11B85BDB8E09"),
                        throwOnError: true
                    )!
                )!;
            // RECYCLEONDELETE + ADDUNDORECORD. Never request permanent deletion.
            // SILENT + NOCONFIRMATION use our confirmation; NOERRORUI + EARLYFAILURE
            // report native failures to our toast, rather than offering a destructive fallback.
            operation.SetOperationFlags(
                0x00080000 | 0x20000000 | 0x4 | 0x10 | 0x400 | 0x00100000 | 0x2000
            );
            operation.SetOwnerWindow(owner);
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(target.Path, 0, ref iid, out item);
            var sink = new RecycleSink(target, token);
            operation.DeleteItem(item, sink);
            try
            {
                operation.PerformOperations();
            }
            catch (COMException)
                when (sink.Failure is not null || sink.State != FileRecycleResult.Ready) { }
            if (sink.Failure is { } error)
                throw error;
            if (sink.State != FileRecycleResult.Ready)
                return sink.State;
            operation.GetAnyOperationsAborted(out bool aborted);
            if (aborted)
                return FileRecycleResult.Cancelled;
            if (!sink.Recycled)
                throw new IOException(
                    "ゴミ箱への移動を確認できませんでした。完全削除への切り替えは行いません。"
                );
            return FileRecycleResult.Recycled;
        }
        finally
        {
            if (item is not null)
                Marshal.FinalReleaseComObject(item);
            if (operation is not null)
                Marshal.FinalReleaseComObject(operation);
            CoUninitialize();
        }
    }

    // Pin ordinary ancestors during the shell operation so they cannot be renamed or
    // replaced by junctions. Do not traverse existing reparse ancestors.
    private sealed class ParentLease : IDisposable
    {
        private readonly List<WindowsFileMetadata> handles = [];

        internal ParentLease(FileRecycleTarget target, CancellationToken token)
        {
            try
            {
                for (int end = 2; end < target.Path.Length; end++)
                {
                    if (target.Path[end] != '\\')
                        continue;
                    token.ThrowIfCancellationRequested();
                    string path = end == 2 ? target.Path[..3] : target.Path[..end];
                    var handle = WindowsFileMetadata.OpenForRecycle(target.Drive, path, share: 3);
                    handles.Add(handle);
                    var info = handle.Observe(token);
                    if (!info.IsDirectory || (info.FileAttributes & 0x400) != 0)
                        throw new IOException(
                            "リンクやジャンクションを経由するパスは削除できません。"
                        );
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var handle in handles)
                handle.Dispose();
            handles.Clear();
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleSink(FileRecycleTarget target, CancellationToken token)
        : IFileOperationProgressSink
    {
        internal Exception? Failure { get; private set; }
        internal FileRecycleResult State { get; private set; } = FileRecycleResult.Ready;
        internal bool Recycled { get; private set; }

        public int PreDeleteItem(uint flags, IShellItem item)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                // Reject a shell operation that is no longer a recycling operation.
                if ((flags & 0x80) == 0)
                    throw new IOException(
                        "この項目はゴミ箱へ移動できません。完全削除は行いません。"
                    );
                item.GetDisplayName(0x80058000, out var name);
                string? path;
                try
                {
                    path = Marshal.PtrToStringUni(name);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(name);
                }
                if (!string.Equals(path, target.Path, StringComparison.Ordinal))
                    throw new IOException("削除対象のパスが変更されました。");
                State = Inspect(target, token);
                return State == FileRecycleResult.Ready ? 0 : unchecked((int)0x80004004);
            }
            catch (Exception error)
            {
                Failure = error;
                return unchecked((int)0x80004004);
            }
        }

        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? recycled)
        {
            if (result < 0)
                Failure =
                    Marshal.GetExceptionForHR(result)
                    ?? new IOException("ゴミ箱への移動に失敗しました。");
            else
                Recycled = recycled is not null;
            return 0;
        }

        public int StartOperations() => 0;

        public int FinishOperations(int result) => 0;

        public int PreRenameItem(uint flags, IShellItem item, string name) =>
            unchecked((int)0x80004004);

        public int PostRenameItem(
            uint flags,
            IShellItem item,
            string name,
            int result,
            IShellItem? created
        ) => 0;

        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? name) =>
            unchecked((int)0x80004004);

        public int PostMoveItem(
            uint flags,
            IShellItem item,
            IShellItem destination,
            string? name,
            int result,
            IShellItem? created
        ) => 0;

        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? name) =>
            unchecked((int)0x80004004);

        public int PostCopyItem(
            uint flags,
            IShellItem item,
            IShellItem destination,
            string? name,
            int result,
            IShellItem? created
        ) => 0;

        public int PreNewItem(uint flags, IShellItem destination, string name) =>
            unchecked((int)0x80004004);

        public int PostNewItem(
            uint flags,
            IShellItem destination,
            string name,
            string? template,
            uint attributes,
            int result,
            IShellItem? created
        ) => 0;

        public int UpdateProgress(uint total, uint completed) =>
            token.IsCancellationRequested ? unchecked((int)0x80004004) : 0;

        public int ResetTimer() => 0;

        public int PauseTimer() => 0;

        public int ResumeTimer() => 0;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint flags);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path,
        nint context,
        ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item
    );
}
