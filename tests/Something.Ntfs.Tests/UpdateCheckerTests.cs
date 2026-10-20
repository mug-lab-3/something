using System.Net;
using System.Text;
using Something.Application;

namespace Something.Ntfs.Tests;

public sealed class UpdateCheckerTests : IDisposable
{
    private const string LatestRelease =
        "https://api.github.com/repos/mug-lab-3/something/releases/latest";

    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"something-update-check-{Guid.NewGuid():N}"
    );
    private string SettingsPath => Path.Combine(directory, "display-settings.json");

    [Theory]
    [InlineData("1.1.4", "v1.1.5")]
    [InlineData("1.1.4", "v1.2.0")]
    [InlineData("1.1.4", "v2.0.0")]
    [InlineData("1.9.9", "v1.10.0")]
    [InlineData("1.2.0-beta.1", "v1.2.0")]
    public void Evaluate_ReportsNewerRelease(string current, string tag)
    {
        // Arrange
        var json = Release(tag);

        // Act
        var update = UpdateChecker.Evaluate(current, json);

        // Assert
        Assert.NotNull(update);
        Assert.Equal(tag[1..], update.Version);
        Assert.Equal(
            $"https://github.com/mug-lab-3/something/releases/tag/{tag}",
            update.ReleasePage.AbsoluteUri
        );
    }

    [Theory]
    [InlineData("1.1.4", "v1.1.4")]
    [InlineData("1.1.4", "v1.1.3")]
    [InlineData("1.10.0", "v1.9.9")]
    [InlineData("1.2.0-beta.1", "v1.1.9")]
    public void Evaluate_IgnoresCurrentOrOlderRelease(string current, string tag) =>
        Assert.Null(UpdateChecker.Evaluate(current, Release(tag)));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Evaluate_IgnoresDraftsAndPrereleases(bool draft, bool prerelease) =>
        Assert.Null(UpdateChecker.Evaluate("1.1.4", Release("v9.0.0", draft, prerelease)));

    [Theory]
    [InlineData("9.0.0")]
    [InlineData("v9.0")]
    [InlineData("v9.0.0.1")]
    [InlineData("v09.0.0")]
    [InlineData("v9.0.0-beta.1")]
    [InlineData("v9.0.0\n")]
    [InlineData(" v9.0.0")]
    [InlineData("v9.0.0 ")]
    [InlineData("V9.0.0")]
    [InlineData("v９.0.0")]
    [InlineData("v9999999999.0.0")]
    [InlineData("v9.0.0/../../evil")]
    [InlineData("")]
    public void Evaluate_IgnoresTagsThatAreNotPlainVersions(string tag) =>
        Assert.Null(UpdateChecker.Evaluate("1.1.4", Release(tag)));

    [Fact]
    public void Evaluate_BuildsReleasePageFromVersionInsteadOfResponseAddress()
    {
        // Arrange
        var json = Encoding.UTF8.GetBytes(
            """{"tag_name":"v9.0.0","html_url":"https://evil.example/download","draft":false,"prerelease":false}"""
        );

        // Act
        var update = UpdateChecker.Evaluate("1.1.4", json);

        // Assert
        Assert.Equal("github.com", update?.ReleasePage.Host);
        Assert.Equal("/mug-lab-3/something/releases/tag/v9.0.0", update?.ReleasePage.AbsolutePath);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"tag_name":5}""")]
    [InlineData("""{"tag_name":null}""")]
    public void Evaluate_RejectsMalformedResponses(string json) =>
        Assert.Throws<InvalidDataException>(() =>
            UpdateChecker.Evaluate("1.1.4", Encoding.UTF8.GetBytes(json))
        );

    [Fact]
    public void Evaluate_RejectsDeeplyNestedResponses()
    {
        // Arrange
        string json =
            """{"tag_name":"v9.0.0","x":""" + new string('[', 64) + new string(']', 64) + "}";

        // Act and assert
        Assert.Throws<InvalidDataException>(() =>
            UpdateChecker.Evaluate("1.1.4", Encoding.UTF8.GetBytes(json))
        );
    }

    [Theory]
    [InlineData("1.1")]
    [InlineData("1.1.4.0")]
    [InlineData("abc")]
    [InlineData("")]
    public void Evaluate_RejectsInvalidRunningVersion(string current) =>
        Assert.Throws<ArgumentException>(() => UpdateChecker.Evaluate(current, Release("v9.0.0")));

    [Fact]
    public async Task CheckAsync_SendsOneGitHubApiRequestAndReadsTheVersion()
    {
        // Arrange
        var handler = new Handler(_ => Response(HttpStatusCode.OK, Release("v9.0.0")));

        // Act
        var update = await new UpdateChecker(handler).CheckAsync(
            "1.1.4",
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal("9.0.0", update?.Version);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(LatestRelease, request.RequestUri?.AbsoluteUri);
        Assert.Equal("Something/1.1.4", request.Headers.UserAgent.ToString());
        Assert.Equal("application/vnd.github+json", request.Headers.Accept.ToString());
        Assert.Null(request.Content);
    }

    [Fact]
    public async Task CheckAsync_TreatsMissingReleaseAsCurrent()
    {
        // Arrange
        var checker = new UpdateChecker(new Handler(_ => new(HttpStatusCode.NotFound)));

        // Act
        var update = await checker.CheckAsync("1.1.4", TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(update);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task CheckAsync_ReportsFailedRequests(HttpStatusCode status)
    {
        // Arrange
        var checker = new UpdateChecker(new Handler(_ => new(status)));

        // Act and assert
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            checker.CheckAsync("1.1.4", TestContext.Current.CancellationToken)
        );
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckAsync_RejectsOversizedResponses(bool declaredLength)
    {
        // Arrange
        var body = new byte[(1 << 20) + 1];
        var checker = new UpdateChecker(
            new Handler(_ =>
                new(HttpStatusCode.OK)
                {
                    Content = declaredLength
                        ? new ByteArrayContent(body)
                        : new StreamContent(new UnknownLengthStream(body)),
                }
            )
        );

        // Act and assert
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            checker.CheckAsync("1.1.4", TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task CheckAsync_StopsWhenCancelled()
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        await cancellation.CancelAsync();
        var handler = new Handler(_ => Response(HttpStatusCode.OK, Release("v9.0.0")));

        // Act and assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UpdateChecker(handler).CheckAsync("1.1.4", cancellation.Token)
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{broken")]
    [InlineData("{\"IncludeSystem\":false,\"FollowListInTree\":false}")]
    public void CheckForUpdates_DefaultsToOnForMissingOrOlderSettings(string? json)
    {
        // Arrange
        if (json is not null)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, json);
        }

        // Act
        bool check = new DisplaySettingsStore(SettingsPath).LoadCheckForUpdates();

        // Assert
        Assert.True(check);
    }

    [Fact]
    public void CheckForUpdates_SurvivesReloadWithoutChangingOtherPreferences()
    {
        // Arrange
        var store = new DisplaySettingsStore(SettingsPath);
        store.SaveTheme(ThemePreference.Light);
        store.SaveFollowListInTree(false);

        // Act
        store.SaveCheckForUpdates(false);
        var reloaded = new DisplaySettingsStore(SettingsPath);

        // Assert
        Assert.False(reloaded.LoadCheckForUpdates());
        Assert.Equal(ThemePreference.Light, reloaded.LoadTheme());
        Assert.False(reloaded.LoadFollowListInTree());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    private static byte[] Release(string tag, bool draft = false, bool prerelease = false) =>
        Encoding.UTF8.GetBytes(
            $$"""{"tag_name":{{System.Text.Json.JsonSerializer.Serialize(tag)}},"draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}}}"""
        );

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] body) =>
        new(status) { Content = new ByteArrayContent(body) };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    // A body without Content-Length, so the size is only known while reading.
    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
