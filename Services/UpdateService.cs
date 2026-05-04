using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Windows;

namespace MyNotes.Services;

public static class UpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/JacobRochford/MyNotes/releases/latest";
    private const string ReleasesPageUrl = "https://github.com/JacobRochford/MyNotes/releases/latest";

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    static UpdateService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MyNotes-UpdateChecker/1.0");
    }

    public static async Task CheckForUpdatesAsync()
    {
        try
        {
            var release = await _httpClient
                .GetFromJsonAsync<GitHubRelease>(LatestReleaseApiUrl)
                .ConfigureAwait(false);

            if (release?.TagName is null)
                return;

            if (!Version.TryParse(release.TagName.TrimStart('v'), out var latestVersion))
                return;

            var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (assemblyVersion is null)
                return;

            // Normalize to 3-part version to match tag format (e.g. 1.0.0)
            var currentVersion = new Version(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build);

            if (latestVersion <= currentVersion)
                return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var result = MessageBox.Show(
                    $"MyNotes {release.TagName} is available.\n\nYou are running v{currentVersion}. Would you like to download the update?",
                    "Update Available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = ReleasesPageUrl,
                        UseShellExecute = true
                    });
            });
        }
        catch
        {
            // Never crash the app over a failed update check.
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }
    }
}
