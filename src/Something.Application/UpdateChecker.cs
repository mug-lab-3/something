using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Something.Application;

/// <summary>A published release that is newer than the running version.</summary>
public sealed record AvailableUpdate(string Version, Uri ReleasePage);

/// <summary>
/// Asks GitHub for the latest published release. Only its version is read; nothing is downloaded
/// or installed, and no scan data or file information is sent.
/// </summary>
public sealed partial class UpdateChecker(HttpMessageHandler? handler = null)
{
    private const string Repository = "mug-lab-3/something";
    private const int MaxResponseBytes = 1 << 20;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The newer release, or null when the running version is current.</summary>
    public async Task<AvailableUpdate?> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken
    )
    {
        using var client = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        client.Timeout = RequestTimeout;
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{Repository}/releases/latest"
        );
        // GitHub rejects API requests without a User-Agent.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Something", currentVersion));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json")
        );
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        // The repository has no published release yet.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidDataException("The release response is too large.");
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (json.Length + read > MaxResponseBytes)
                throw new InvalidDataException("The release response is too large.");
            json.Write(buffer, 0, read);
        }
        return Evaluate(currentVersion, json.GetBuffer().AsMemory(0, (int)json.Length));
    }

    /// <summary>
    /// Compares a GitHub release response with the running version. The response is untrusted:
    /// only a plain "vX.Y.Z" tag of a published, non-prerelease release is accepted, and the
    /// release page is built from that version rather than taken from the response.
    /// </summary>
    public static AvailableUpdate? Evaluate(string currentVersion, ReadOnlyMemory<byte> json)
    {
        var current = ParseCurrent(currentVersion);
        using var document = ParseDocument(json);
        var release = document.RootElement;
        if (
            release.ValueKind != JsonValueKind.Object
            || !release.TryGetProperty("tag_name", out var tag)
            || tag.ValueKind != JsonValueKind.String
        )
            throw new InvalidDataException("The release response has no tag.");
        if (IsTrue(release, "draft") || IsTrue(release, "prerelease"))
            return null;
        var match = ReleaseTag().Match(tag.GetString()!);
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var latest))
            return null;
        bool newer = latest > current.Number || latest == current.Number && current.Prerelease;
        return newer
            ? new(
                latest.ToString(),
                new Uri($"https://github.com/{Repository}/releases/tag/v{latest}")
            )
            : null;
    }

    private static (Version Number, bool Prerelease) ParseCurrent(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        int label = version.IndexOf('-');
        string number = label < 0 ? version : version[..label];
        if (number.Split('.').Length != 3 || !Version.TryParse(number, out var parsed))
            throw new ArgumentException("The running version is not X.Y.Z.", nameof(version));
        return (parsed, label >= 0);
    }

    private static JsonDocument ParseDocument(ReadOnlyMemory<byte> json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The release response is not valid JSON.", error);
        }
    }

    private static bool IsTrue(JsonElement release, string name) =>
        release.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // Each part is at most nine digits, so it always fits in an int.
    [GeneratedRegex(@"^v((?:0|[1-9][0-9]{0,8})\.(?:0|[1-9][0-9]{0,8})\.(?:0|[1-9][0-9]{0,8}))\z")]
    private static partial Regex ReleaseTag();
}
