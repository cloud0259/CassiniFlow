using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CassiniFlow.Domain.Models;
using CassiniFlow.Domain.Ports.Inbound;

namespace CassiniFlow.Infrastructure.Adapters;

/// <summary>
/// Adaptateur interrogeant l'API GitHub Releases pour détecter les nouvelles versions de CassiniFlow.
/// </summary>
public sealed class GitHubReleaseUpdateChecker : IUpdateCheckerService, IDisposable
{
    private const string DefaultOwner = "cloud0259";
    private const string DefaultRepo = "CassiniFlow";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _owner;
    private readonly string _repo;

    public string CurrentVersion { get; }

    public GitHubReleaseUpdateChecker(
        HttpClient? httpClient = null,
        string? currentVersionOverride = null,
        string owner = DefaultOwner,
        string repo = DefaultRepo)
    {
        _owner = owner;
        _repo = repo;
        _ownsClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(6)
        };

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Contains(new ProductInfoHeaderValue("CassiniFlow-App", "1.0")))
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CassiniFlow-App", "1.0"));
        }

        CurrentVersion = currentVersionOverride ?? ResolveAssemblyVersion();
    }

    public async Task<AppUpdateInfo> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return AppUpdateInfo.None(CurrentVersion);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubReleasePayload>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return AppUpdateInfo.None(CurrentVersion);
            }

            string cleanLatest = CleanVersionString(release.TagName);
            bool isNewer = IsNewerVersion(cleanLatest, CurrentVersion);

            return new AppUpdateInfo(
                IsUpdateAvailable: isNewer,
                CurrentVersion: CurrentVersion,
                LatestVersion: cleanLatest,
                ReleaseTitle: release.Name ?? $"CassiniFlow v{cleanLatest}",
                ReleaseNotes: release.Body ?? string.Empty,
                HtmlUrl: release.HtmlUrl ?? $"https://github.com/{_owner}/{_repo}/releases/latest",
                PublishedAt: release.PublishedAt);
        }
        catch
        {
            // En cas d'absence d'accès Internet, timeout ou rate-limit GitHub : échec silencieux
            return AppUpdateInfo.None(CurrentVersion);
        }
    }

    public static bool IsNewerVersion(string latestStr, string currentStr)
    {
        string cLatest = CleanVersionString(latestStr);
        string cCurrent = CleanVersionString(currentStr);

        if (Version.TryParse(cLatest, out var vLatest) && Version.TryParse(cCurrent, out var vCurrent))
        {
            return vLatest > vCurrent;
        }

        // Découpage sémantique de secours (ex: 1.2.0-beta)
        var partsLatest = cLatest.Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries);
        var partsCurrent = cCurrent.Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries);
        int max = Math.Max(partsLatest.Length, partsCurrent.Length);

        for (int i = 0; i < max; i++)
        {
            int nLatest = (i < partsLatest.Length && int.TryParse(partsLatest[i], out int l)) ? l : 0;
            int nCurrent = (i < partsCurrent.Length && int.TryParse(partsCurrent[i], out int c)) ? c : 0;

            if (nLatest > nCurrent) return true;
            if (nLatest < nCurrent) return false;
        }

        return false;
    }

    private static string CleanVersionString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "1.0.0";
        string v = raw.Trim();
        if (v.StartsWith('v') || v.StartsWith('V'))
        {
            v = v[1..];
        }
        return v.Trim();
    }

    private static string ResolveAssemblyVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(GitHubReleaseUpdateChecker).Assembly;
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            int plusIdx = infoVer.IndexOf('+');
            if (plusIdx > 0) infoVer = infoVer[..plusIdx];
            return CleanVersionString(infoVer);
        }

        var ver = asm.GetName().Version;
        if (ver != null)
        {
            return $"{ver.Major}.{ver.Minor}.{ver.Build}";
        }

        return "1.1.0";
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private sealed class GitHubReleasePayload
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }
    }
}
