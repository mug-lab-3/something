using Something.Application;

namespace Something.Ntfs.Tests;

public sealed partial class ScanHistoryTests
{
    [Fact]
    public void DatabaseSizeIncludesWalWithoutCreatingOrOpeningDatabase()
    {
        using var fixture = new HistoryFixture(Now);
        Assert.Equal(0, fixture.Archive.ReadDatabaseInfo().TotalBytes);
        Assert.False(Directory.Exists(fixture.Root));
        Assert.Empty(fixture.Archive.List("volume:A", Token));
        Assert.Empty(fixture.Archive.ListSummaries("volume:A", Token));
        Assert.False(Directory.Exists(fixture.Root));
        fixture.Save(Now, Token);
        string path = fixture.Archive.DatabasePath;
        long expected = new FileInfo(path).Length;
        File.WriteAllBytes(path + ".wal", [1, 2, 3]);
        var info = fixture.Archive.ReadDatabaseInfo();
        Assert.Equal(expected, info.DatabaseBytes);
        Assert.Equal(3, info.WalBytes);
        Assert.Equal(expected + 3, info.TotalBytes);
        File.Delete(path + ".wal");
    }

    [Fact]
    public void DatabaseRelocationPreservesHistoryAndPersistsLocationBeforeSourceRemoval()
    {
        using var fixture = new HistoryFixture(Now);
        var entry = fixture.Save(Now, Token);
        string destination = Path.Combine(fixture.Root, "moved");
        string unrelated = Path.Combine(fixture.Root, "unrelated.txt");
        File.WriteAllText(unrelated, "keep");
        var settings = new DatabaseSettingsStore(Path.Combine(fixture.Root, "settings.json"));
        var moved = fixture.Archive.Relocate(
            destination,
            path =>
            {
                Assert.True(File.Exists(fixture.Archive.DatabasePath));
                Assert.True(
                    File.Exists(Path.Combine(path, Path.GetFileName(fixture.Archive.DatabasePath)))
                );
                settings.SaveDirectory(path);
            },
            Token
        );
        Assert.Equal(destination, settings.LoadDirectory());
        Assert.False(File.Exists(fixture.Archive.DatabasePath));
        Assert.Equal(entry.Id, Assert.Single(moved.List("volume:A", Token)).Id);
        using (var reader = moved.Open(entry.Id, Token))
            Assert.Equal(3, reader.Store.ReadFiles(entry.Id, Token).Count());
        Assert.Equal("keep", File.ReadAllText(unrelated));
        moved.DeleteDatabase(Token);
        Assert.Equal(0, moved.ReadDatabaseInfo().TotalBytes);
        Assert.Empty(moved.List("volume:A", Token));
        Assert.Empty(moved.ListSummaries("volume:A", Token));
        Assert.False(File.Exists(moved.DatabasePath));
    }

    [Fact]
    public void DatabaseRelocationFailureDoesNotOverwriteDestinationOrLoseSource()
    {
        using var fixture = new HistoryFixture(Now);
        var entry = fixture.Save(Now, Token);
        string destination = Path.Combine(fixture.Root, "moved");
        Directory.CreateDirectory(destination);
        string target = Path.Combine(destination, Path.GetFileName(fixture.Archive.DatabasePath));
        File.WriteAllText(target, "existing");
        bool committed = false;
        Assert.Throws<IOException>(() =>
            fixture.Archive.Relocate(destination, _ => committed = true, Token)
        );
        Assert.Equal("existing", File.ReadAllText(target));
        Assert.False(committed);
        File.Delete(target);
        Assert.Throws<IOException>(() =>
            fixture.Archive.Relocate(
                destination,
                _ => throw new IOException("settings failed"),
                Token
            )
        );
        Assert.True(File.Exists(fixture.Archive.DatabasePath));
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.EnumerateFiles(destination));
        Assert.Equal(entry.Id, Assert.Single(fixture.Archive.List("volume:A", Token)).Id);
    }

    [Fact]
    public void DatabaseOperationsRejectActiveReadersAndCancellation()
    {
        using var fixture = new HistoryFixture(Now);
        var entry = fixture.Save(Now, Token);
        using (var reader = fixture.Archive.Open(entry.Id, Token))
        {
            Assert.Throws<InvalidOperationException>(() => fixture.Archive.DeleteDatabase(Token));
            Assert.Throws<InvalidOperationException>(() =>
                fixture.Archive.Relocate(Path.Combine(fixture.Root, "moved"), _ => { }, Token)
            );
            Assert.Equal(3, reader.Store.ReadFiles(entry.Id, Token).Count());
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            fixture.Archive.DeleteDatabase(cancellation.Token)
        );
        Assert.Throws<OperationCanceledException>(() =>
            fixture.Archive.Relocate(
                Path.Combine(fixture.Root, "moved"),
                _ => { },
                cancellation.Token
            )
        );
        Assert.Equal(entry.Id, Assert.Single(fixture.Archive.List("volume:A", Token)).Id);
    }

    [Fact]
    public void DatabaseDeletionPreservesOtherFilesAndRejectsForeignDatabase()
    {
        using var fixture = new HistoryFixture(Now);
        fixture.Save(Now, Token);
        string other = Path.Combine(fixture.Root, "other.sqlite3");
        File.WriteAllText(other, "preserve");
        fixture.Archive.DeleteDatabase(Token);
        fixture.Archive.DeleteDatabase(Token);
        Assert.Equal("preserve", File.ReadAllText(other));
        File.WriteAllText(fixture.Archive.DatabasePath, "SQLite format 3\0preserve");
        Assert.Throws<InvalidDataException>(() => fixture.Archive.DeleteDatabase(Token));
        Assert.Equal("SQLite format 3\0preserve", File.ReadAllText(fixture.Archive.DatabasePath));
    }

    [Fact]
    public void DatabaseSettingsRoundTripAndRejectRelativeDirectory()
    {
        using var fixture = new HistoryFixture(Now);
        string path = Path.Combine(fixture.Root, "settings.json");
        var settings = new DatabaseSettingsStore(path);
        Assert.Null(settings.LoadDirectory());
        Assert.Throws<ArgumentException>(() => settings.SaveDirectory("relative"));
        settings.SaveDirectory(fixture.Root);
        Assert.Equal(fixture.Root, new DatabaseSettingsStore(path).LoadDirectory());
        File.WriteAllText(path, "invalid json");
        Assert.Null(settings.LoadDirectory());
        File.WriteAllText(path, "{\"Directory\":\"relative\"}");
        Assert.Null(settings.LoadDirectory());
    }

    [Fact]
    public void DatabaseOperationsPreserveOrphanWalAndRejectDirectories()
    {
        using var fixture = new HistoryFixture(Now);
        Directory.CreateDirectory(fixture.Root);
        string wal = fixture.Archive.DatabasePath + ".wal";
        File.WriteAllBytes(wal, [1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => fixture.Archive.DeleteDatabase(Token));
        Assert.Throws<InvalidDataException>(() =>
            fixture.Archive.Relocate(Path.Combine(fixture.Root, "moved"), _ => { }, Token)
        );
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(wal));
        File.Delete(wal);
        Directory.CreateDirectory(fixture.Archive.DatabasePath);
        Assert.Throws<InvalidOperationException>(() => fixture.Archive.ReadDatabaseInfo());
        Assert.Throws<InvalidOperationException>(() => fixture.Archive.DeleteDatabase(Token));
        Assert.True(Directory.Exists(fixture.Archive.DatabasePath));
    }
}
