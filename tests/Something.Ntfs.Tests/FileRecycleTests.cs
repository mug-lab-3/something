using System.Runtime.InteropServices;
using Something.Application;
using Something.Windows;

namespace Something.Ntfs.Tests;

public sealed class FileRecycleTests
{
    private static readonly FileRecycleTarget Target = new(
        "C:",
        @"C:\data\file.txt",
        "synthetic-volume",
        123,
        FileReference.Create(100, 2),
        false
    );

    private sealed class Backend(params FileRecycleResult[] inspections) : IFileRecycleBackend
    {
        private int inspection;
        public int RecycleCalls { get; private set; }
        public FileRecycleTarget? RecycledTarget { get; private set; }

        public Task<FileRecycleResult> InspectAsync(
            FileRecycleTarget target,
            CancellationToken token
        )
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(inspections[inspection++]);
        }

        public Task<FileRecycleResult> RecycleAsync(
            FileRecycleTarget target,
            nint owner,
            CancellationToken token
        )
        {
            token.ThrowIfCancellationRequested();
            RecycleCalls++;
            RecycledTarget = target;
            Assert.Equal(Target.Reference, target.Reference);
            return Task.FromResult(FileRecycleResult.Recycled);
        }
    }

    [Theory]
    [InlineData(FileRecycleResult.Missing)]
    [InlineData(FileRecycleResult.Replaced)]
    [InlineData(FileRecycleResult.Protected)]
    public async Task UnavailableItemsNeverShowConfirmationOrReachNativeRecycle(
        FileRecycleResult status
    )
    {
        var backend = new Backend(status);
        var result = await new FileRecycleWorkflow(backend).RunAsync(
            Target,
            (_, _) => throw new InvalidOperationException("Must not ask"),
            0,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(status, result);
        Assert.Equal(0, backend.RecycleCalls);
    }

    [Fact]
    public async Task CancelledConfirmationNeverRecycles()
    {
        var backend = new Backend(FileRecycleResult.Ready);
        var result = await new FileRecycleWorkflow(backend).RunAsync(
            Target,
            (_, _) => Task.FromResult(false),
            0,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(FileRecycleResult.Cancelled, result);
        Assert.Equal(0, backend.RecycleCalls);
    }

    [Theory]
    [InlineData(FileRecycleResult.Missing)]
    [InlineData(FileRecycleResult.Replaced)]
    public async Task ChangedDuringConfirmationIsNotRecycled(FileRecycleResult status)
    {
        var backend = new Backend(FileRecycleResult.Ready, status);
        int confirmations = 0;
        var result = await new FileRecycleWorkflow(backend).RunAsync(
            Target,
            (_, _) =>
            {
                confirmations++;
                return Task.FromResult(true);
            },
            0,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(status, result);
        Assert.Equal(1, confirmations);
        Assert.Equal(0, backend.RecycleCalls);
    }

    [Fact]
    public async Task ConfirmedVerifiedItemIsRecycledExactlyOnce()
    {
        var backend = new Backend(FileRecycleResult.Ready, FileRecycleResult.Ready);
        var result = await new FileRecycleWorkflow(backend).RunAsync(
            Target,
            (_, _) => Task.FromResult(true),
            0,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(FileRecycleResult.Recycled, result);
        Assert.Equal(1, backend.RecycleCalls);
    }

    [Theory]
    [InlineData(@"C:\Windows", true, 0)]
    [InlineData(@"C:\WINDOWS\System32\sample.dll", false, 0)]
    [InlineData(@"C:\Windows\ordinary\nested", true, 0)]
    [InlineData(@"C:\Program Files", true, 0)]
    [InlineData(@"C:\Program Files (x86)", true, 0)]
    [InlineData(@"C:\Users", true, 0)]
    [InlineData(@"C:\Users\another-profile", true, 0)]
    [InlineData(@"C:\Users\another-profile\Downloads", true, 0)]
    [InlineData(@"C:\Users\another-profile\AppData\Local", true, 0)]
    [InlineData(@"C:\ProgramData", true, 0)]
    [InlineData(@"C:\data\system.txt", false, 4)]
    [InlineData(@"C:\data\system-hidden", true, 6)]
    [InlineData(@"D:\Windows\nested.txt", false, 0)]
    public async Task ProtectedItemsNeverAskOrReachBackend(
        string path,
        bool directory,
        uint attributes
    )
    {
        var backend = new Backend();
        var target = Target with
        {
            Drive = path[..2],
            Path = path,
            IsDirectory = directory,
            StandardFileAttributes = attributes,
        };
        var result = await new FileRecycleWorkflow(backend).RunAsync(
            target,
            (_, _) =>
                throw new InvalidOperationException("Protected items must not show confirmation"),
            0,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(FileRecycleResult.Protected, result);
        Assert.Equal(0, backend.RecycleCalls);
    }

    [Theory]
    [InlineData(@"C:\Windows-backup\file.txt", false)]
    [InlineData(@"C:\data\Windows\file.txt", false)]
    [InlineData(@"C:\Program Files\Example App", true)]
    [InlineData(@"C:\Program Files-old", true)]
    [InlineData(@"C:\Users\another-profile\ordinary-folder", true)]
    [InlineData(@"C:\Users\another-profile\Downloads\ordinary-folder", true)]
    public void SimilarNamesAndNonWindowsDescendantsRemainAvailable(string path, bool directory)
    {
        Assert.False(
            FileRecyclePolicy.IsProtected(Target with { Path = path, IsDirectory = directory })
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenItemsRequireWarningBeforeConfirmation(bool hiddenInScan)
    {
        var backend = new Backend(
            hiddenInScan ? FileRecycleResult.Ready : FileRecycleResult.HiddenNeedsConfirmation,
            FileRecycleResult.Ready
        );
        var target = hiddenInScan ? Target with { StandardFileAttributes = 2 } : Target;
        bool warned = false;
        var result = await new FileRecycleWorkflow(backend).RunAsync(
            target,
            (confirmed, _) =>
            {
                warned = FileRecyclePolicy.IsHidden(confirmed);
                Assert.False(confirmed.HiddenDeletionConfirmed);
                return Task.FromResult(true);
            },
            0,
            TestContext.Current.CancellationToken
        );
        Assert.True(warned);
        Assert.Equal(FileRecycleResult.Recycled, result);
        Assert.True(backend.RecycledTarget?.HiddenDeletionConfirmed);
    }

    [Theory]
    [InlineData(FileRecycleResult.Protected)]
    [InlineData(FileRecycleResult.HiddenNeedsConfirmation)]
    public async Task AttributeChangeDuringConfirmationStopsRecycle(FileRecycleResult changed)
    {
        var backend = new Backend(FileRecycleResult.Ready, changed);
        var result = await new FileRecycleWorkflow(backend).RunAsync(
            Target,
            (_, _) => Task.FromResult(true),
            0,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(changed, result);
        Assert.Equal(0, backend.RecycleCalls);
    }

    [Fact]
    public async Task CancellationWhileDialogIsOpenPreventsRecycling()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var backend = new Backend(FileRecycleResult.Ready);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FileRecycleWorkflow(backend).RunAsync(
                Target,
                (_, _) =>
                {
                    cancellation.Cancel();
                    return Task.FromResult(true);
                },
                0,
                cancellation.Token
            )
        );
        Assert.Equal(0, backend.RecycleCalls);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\data\..\file")]
    [InlineData(@"C:\data\file:stream")]
    [InlineData(@"C:\data\*.txt")]
    [InlineData(@"C:\data\trailing.")]
    [InlineData(@"C:\data\trailing ")]
    [InlineData(@"C:\data\NUL.txt")]
    [InlineData(@"C:\data\COM1")]
    [InlineData(@"C:\data\LPT¹.txt")]
    [InlineData(@"C:\data\\file")]
    [InlineData(@"C:\data/file")]
    [InlineData(@"C:\data\file" + "\0suffix")]
    [InlineData(@"D:\data\file")]
    [InlineData(@"\\?\C:\data\file")]
    [InlineData(@"\\server\share\file")]
    public void UnsafeOrAliasedPathsAreRejected(string path)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            RecyclePath.Validate(Target with { Path = path })
        );

        Assert.Contains(
            RecycleFailures.Of(error),
            new RecycleFailure?[]
            {
                RecycleFailure.UnsafePath,
                RecycleFailure.UnsafeName,
                RecycleFailure.DeviceName,
            }
        );
    }

    [Fact]
    public void OrdinaryPathsAreAcceptedAndNtfsMetadataIsRejected()
    {
        RecyclePath.Validate(Target);
        RecyclePath.Validate(Target with { Path = @"C:\data\日本語.txt" });
        var metadata = Assert.Throws<ArgumentException>(() =>
            RecyclePath.Validate(Target with { Reference = FileReference.Create(8, 1) })
        );
        Assert.Equal(RecycleFailure.UnsafePath, RecycleFailures.Of(metadata));
    }

    [Fact]
    public void HardLinkNamesAndReusedReferencesRemainIndependent()
    {
        var tracker = new FileRemovalTracker();
        var original = tracker.StateFor(Target);
        var alias = tracker.StateFor(Target with { Path = @"C:\data\alias.txt" });
        var reused = tracker.StateFor(Target with { Reference = FileReference.Create(100, 3) });
        var otherDrive = tracker.StateFor(Target with { VolumeKey = "another-volume" });
        int changes = 0;
        original.PropertyChanged += (_, _) => changes++;
        tracker.Mark(Target, FileRemovalMark.Recycled);
        Assert.True(original.IsRemoved);
        Assert.False(original.CanRecycle);
        Assert.False(alias.IsRemoved);
        Assert.False(reused.IsRemoved);
        Assert.False(otherDrive.IsRemoved);
        Assert.True(changes > 0);
        Assert.Same(original, tracker.StateFor(Target));
    }

    [Fact]
    public void FolderRemovalIncludesExistingAndLazilyCreatedDescendantsButNotPrefixSiblings()
    {
        var tracker = new FileRemovalTracker();
        var folder = Target with { Path = @"C:\data", IsDirectory = true };
        var child = tracker.StateFor(Target);
        var sibling = tracker.StateFor(Target with { Path = @"C:\database\file.txt" });
        tracker.Mark(folder, FileRemovalMark.Recycled);
        Assert.True(child.IsRemoved);
        Assert.False(sibling.IsRemoved);
        Assert.True(
            tracker.StateFor(Target with { Path = @"C:\data\nested\another.txt" }).IsRemoved
        );
        tracker.ClearVolume(Target.VolumeKey);
        Assert.False(child.IsRemoved);
        Assert.False(tracker.StateFor(Target).IsRemoved);
    }

    [Fact]
    public void BusyStateAndMissingEvidenceDoNotBecomeSuccessfulDeletion()
    {
        var tracker = new FileRemovalTracker();
        var state = tracker.StateFor(Target);
        state.SetBusy(true);
        Assert.False(state.CanRecycle);
        Assert.False(state.IsRemoved);
        state.SetBusy(false);
        Assert.True(state.CanRecycle);
        tracker.Mark(Target, FileRemovalMark.Missing);
        Assert.Equal(FileRemovalMark.Missing, state.Mark);
        Assert.False(state.CanRecycle);
    }

    public static bool NativeRecycleEnabled =>
        OperatingSystem.IsWindows()
        && Environment.GetEnvironmentVariable("SOMETHING_TEST_RECYCLE") == "1";

    [Fact(
        SkipUnless = nameof(NativeRecycleEnabled),
        Skip = "Opt-in native shell test: only creates and recycles its own GUID-named fixtures."
    )]
    public async Task NativeShellRecyclesOnlyOwnedFileAndFolderAndRejectsReusedIdentity()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var token = TestContext.Current.CancellationToken;
        string directory = Path.Combine(
            Path.GetTempPath(),
            "something-recycle-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            string canary = Path.Combine(directory, "keep.txt");
            string file = Path.Combine(directory, "recycle.txt");
            string folder = Path.Combine(directory, "recycle-folder");
            File.WriteAllText(canary, "owned canary");
            File.WriteAllText(file, "owned recycle fixture");
            string alias = Path.Combine(directory, "keep-hard-link.txt");
            Assert.True(CreateHardLinkW(alias, file, 0));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "child.txt"), "owned child");
            var backend = new WindowsRecycleBackend();
            string drive = Path.GetPathRoot(file)![..2];
            FileRecycleTarget ReadTarget(string path, bool isDirectory)
            {
                using var handle = WindowsFileMetadata.Open(drive, path);
                var info = handle.Observe(token);
                return new(
                    drive,
                    path,
                    $"{WindowsVolumeIdentity.ReadKey(drive)}:{info.VolumeSerialNumber:X16}",
                    info.VolumeSerialNumber,
                    info.Reference,
                    isDirectory
                );
            }
            var target = ReadTarget(file, false);
            Assert.Equal(
                FileRecycleResult.Replaced,
                await backend.InspectAsync(
                    target with
                    {
                        Reference = FileReference.Create(
                            target.Reference.RecordIndex,
                            unchecked((ushort)(target.Reference.SequenceNumber + 1))
                        ),
                    },
                    token
                )
            );
            Assert.True(File.Exists(file));
            File.SetAttributes(file, FileAttributes.System);
            Assert.Equal(FileRecycleResult.Protected, await backend.InspectAsync(target, token));
            Assert.Equal(FileRecycleResult.Protected, await backend.RecycleAsync(target, 0, token));
            Assert.True(File.Exists(file));
            File.SetAttributes(file, FileAttributes.Hidden);
            Assert.Equal(
                FileRecycleResult.HiddenNeedsConfirmation,
                await backend.InspectAsync(target, token)
            );
            bool hiddenWarning = false;
            Assert.Equal(
                FileRecycleResult.Recycled,
                await new FileRecycleWorkflow(backend).RunAsync(
                    target,
                    (confirmed, _) =>
                    {
                        hiddenWarning = FileRecyclePolicy.IsHidden(confirmed);
                        return Task.FromResult(true);
                    },
                    0,
                    token
                )
            );
            Assert.True(hiddenWarning);
            Assert.False(File.Exists(file));
            Assert.Equal("owned recycle fixture", File.ReadAllText(alias));
            Assert.Equal(FileRecycleResult.Missing, await backend.InspectAsync(target, token));
            Assert.Equal(
                FileRecycleResult.Recycled,
                await backend.RecycleAsync(ReadTarget(folder, true), 0, token)
            );
            Assert.False(Directory.Exists(folder));
            Assert.Equal("owned canary", File.ReadAllText(canary));
        }
        finally
        {
            string parent =
                Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string resolved = Path.GetFullPath(directory);
            if (
                !resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved)
                    .StartsWith("something-recycle-", StringComparison.Ordinal)
            )
                throw new InvalidOperationException("Invalid owned fixture cleanup path.");
            Directory.Delete(resolved, true);
        }
    }

    [DllImport(
        "kernel32.dll",
        ExactSpelling = true,
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, nint security);
}
