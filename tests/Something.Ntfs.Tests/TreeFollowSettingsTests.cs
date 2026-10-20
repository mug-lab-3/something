using Something.Application;

namespace Something.Ntfs.Tests;

public sealed class TreeFollowSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"something-tree-follow-{Guid.NewGuid():N}"
    );
    private string SettingsPath => Path.Combine(directory, "display-settings.json");

    [Theory]
    [InlineData(null)]
    [InlineData("{broken")]
    [InlineData("{\"IncludeSystem\":false,\"Theme\":1}")]
    public void FollowListInTree_DefaultsToOnForMissingOrOlderSettings(string? json)
    {
        // Arrange
        if (json is not null)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, json);
        }

        // Act
        bool follow = new DisplaySettingsStore(SettingsPath).LoadFollowListInTree();

        // Assert
        Assert.True(follow);
    }

    [Fact]
    public void FollowListInTree_SurvivesReloadWithoutChangingOtherPreferences()
    {
        // Arrange
        var store = new DisplaySettingsStore(SettingsPath);
        store.Save(new(false, true));
        store.SaveTheme(ThemePreference.Dark);

        // Act
        store.SaveFollowListInTree(false);
        var reloaded = new DisplaySettingsStore(SettingsPath);

        // Assert
        Assert.False(reloaded.LoadFollowListInTree());
        Assert.Equal(new(false, true), reloaded.Load());
        Assert.Equal(ThemePreference.Dark, reloaded.LoadTheme());
        reloaded.Save(new(true, true));
        Assert.False(new DisplaySettingsStore(SettingsPath).LoadFollowListInTree());
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(@"C:\", @"C:\", new[] { @"C:\" })]
    [InlineData(@"C:\", @"C:\Users", new[] { @"C:\", @"C:\Users" })]
    [InlineData(@"C:\", @"C:\Users\me\", new[] { @"C:\", @"C:\Users", @"C:\Users\me" })]
    [InlineData(
        @"Demo\",
        @"Demo\Projects\Archive",
        new[] { @"Demo\", @"Demo\Projects", @"Demo\Projects\Archive" }
    )]
    public void Steps_ListEveryFolderFromTheRootDownToTheTarget(
        string root,
        string target,
        string[] expected
    ) => Assert.Equal(expected, FolderPathSteps.From(root, target));

    [Theory]
    [InlineData(@"Demo\", @"Demo2\Projects")]
    [InlineData(@"C:\", @"D:\Users")]
    [InlineData(@"C:\", @"c:\Users")]
    [InlineData(@"C:\", @"C:\Users\\me")]
    [InlineData("", @"C:\Users")]
    public void Steps_RejectPathsOutsideTheRootOrWithEmptyNames(string root, string target) =>
        Assert.Null(FolderPathSteps.From(root, target));

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }
}
