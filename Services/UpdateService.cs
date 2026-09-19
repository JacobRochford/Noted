using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Noted;

namespace Noted.Services;

public static class UpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/JacobRochford/Noted/releases/latest";
    private const string ReleasesPageUrl = "https://github.com/JacobRochford/Noted/releases/latest";

    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    static UpdateService()
    {
        s_httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Noted-UpdateChecker/1.0");
    }

    public static Task CheckForUpdatesAsync() => CheckForUpdatesAsync(s_httpClient);

    internal static async Task CheckForUpdatesAsync(HttpClient httpClient)
    {
        GitHubRelease? release;
        try
        {
            release = await httpClient
                .GetFromJsonAsync<GitHubRelease>(LatestReleaseApiUrl)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            ExceptionDiagnostics.Record(ex);
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

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

        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                var result = AppDialog.Show(
                    $"Noted. {release.TagName} is available.\n\nYou are running v{currentVersion}. Would you like to download the update?",
                    "Update Available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = ReleasesPageUrl,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex) when (ex is Win32Exception || FileSystemErrors.IsExpected(ex))
                    {
                        ExceptionDiagnostics.Record(ex);
                        AppDialog.Show("The download page could not be opened. Try again from the Noted releases page.",
                            "Update Download", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            });
        }
        catch (OperationCanceledException) when (dispatcher.HasShutdownStarted)
        {
            // app shut down before the update prompt could run
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }
    }
}
