using System.Net;
using System.Text;
using Microsoft.UI.Xaml.Controls;
using Something.Application;

namespace Something.App;

public sealed partial class MainWindow
{
    // The update notice with synthetic GitHub responses; the smoke test never uses the network.
    private async Task VerifyUpdateNoticeAsync(string capture)
    {
        if (!demo)
            throw new InvalidOperationException("Update smoke needs the demo.");
        var previous = updateChecker;
        try
        {
            // A newer release opens a notice with a link that stays until it is closed.
            var newer = new SyntheticReleaseHandler(_ => SyntheticRelease("v99.0.0"));
            updateChecker = new(newer);
            await CheckForUpdatesAsync();
            if (
                newer.Requests is not [var request]
                || request.Method != HttpMethod.Get
                || request.RequestUri?.AbsoluteUri
                    != "https://api.github.com/repos/mug-lab-3/something/releases/latest"
                || request.Headers.UserAgent.Count == 0
            )
                throw new InvalidOperationException("The update check sent an unexpected request.");
            if (!ShowingUpdateNotice || toastTimer.IsEnabled || HasPersistentNotification)
                throw new InvalidOperationException(
                    "A newer release did not open a lasting notice."
                );
            if (!MessageBar.Message.Contains("99.0.0", StringComparison.Ordinal))
                throw new InvalidOperationException("The notice did not name the new version.");
            await Task.Delay(500, lifetime.Token);
            await CaptureStartupFrameAsync(capture + ".update");

            ShowToast("Synthetic scan completed", InfoBarSeverity.Success);
            if (!ShowingUpdateNotice)
                throw new InvalidOperationException(
                    "A transient toast replaced the update notice."
                );
            ShowToast("Synthetic failure", InfoBarSeverity.Error);
            if (ShowingUpdateNotice || MessageBar.ActionButton is not null)
                throw new InvalidOperationException("An error kept the update link.");
            await CheckForUpdatesAsync();
            if (MessageBar.Message != "Synthetic failure")
                throw new InvalidOperationException("The update notice hid an error.");
            MessageBar.IsOpen = false;

            // Current versions, failures and malformed responses stay silent.
            foreach (
                var respond in new Func<HttpRequestMessage, HttpResponseMessage>[]
                {
                    // A prerelease build is older than the release of the same number.
                    _ =>
                        SyntheticRelease(
                            AppVersion.Display.Contains('-') ? "v0.0.1" : "v" + AppVersion.Display
                        ),
                    _ => SyntheticRelease("v0.0.1"),
                    _ => SyntheticRelease("v99.0.0", prerelease: true),
                    _ => new(HttpStatusCode.NotFound),
                    _ => new(HttpStatusCode.Forbidden),
                    _ =>
                        new(HttpStatusCode.OK) { Content = new StringContent("{broken") },
                    _ => throw new HttpRequestException("Synthetic offline"),
                }
            )
            {
                updateChecker = new(new SyntheticReleaseHandler(respond));
                await CheckForUpdatesAsync();
                if (MessageBar.IsOpen)
                    throw new InvalidOperationException(
                        $"A silent update result showed a notice: {MessageBar.Message}"
                    );
            }

            // Turning the setting off through the settings page closes an open notice.
            updateChecker = new(new SyntheticReleaseHandler(_ => SyntheticRelease("v99.0.0")));
            await CheckForUpdatesAsync();
            SetSettingsOpen(true);
            SettingsPage.Search("更新");
            await Task.Delay(500, lifetime.Token);
            await CaptureStartupFrameAsync(capture + ".update-settings");
            SettingsPage.Search("");
            var box = (CheckBox)SettingsPage.FindName("CheckForUpdates");
            box.IsChecked = false;
            SetSettingsOpen(false);
            if (checkForUpdates || MessageBar.IsOpen)
                throw new InvalidOperationException("Turning update checks off left the notice.");
            await CheckForUpdatesAsync();
            if (MessageBar.IsOpen)
                throw new InvalidOperationException("A check finished while off showed a notice.");
        }
        finally
        {
            updateChecker = previous;
            checkForUpdates = true;
            SettingsPage.SetCheckForUpdates(true);
            MessageBar.IsOpen = false;
        }
        await File.WriteAllTextAsync(
            capture + ".update.result",
            "Newer release notice, request shape, toast priority, silent current/failed/malformed responses and the settings switch passed. Synthetic responses only.",
            lifetime.Token
        );
    }

    // A GitHub "latest release" response, for the smoke test and the --preview-update demo.
    private static HttpResponseMessage SyntheticRelease(string tag, bool prerelease = false) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"tag_name":"{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}},"html_url":"https://example.invalid/"}""",
                Encoding.UTF8,
                "application/json"
            ),
        };

    private sealed class SyntheticReleaseHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond
    ) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
