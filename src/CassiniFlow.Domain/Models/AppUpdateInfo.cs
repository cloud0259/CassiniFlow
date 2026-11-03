using System;

namespace CassiniFlow.Domain.Models;

/// <summary>
/// Informations décrivant l'état d'une mise à jour logicielle disponible.
/// </summary>
public sealed record AppUpdateInfo(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseTitle,
    string ReleaseNotes,
    string HtmlUrl,
    DateTimeOffset? PublishedAt = null)
{
    public static AppUpdateInfo None(string currentVersion) => new(
        IsUpdateAvailable: false,
        CurrentVersion: currentVersion,
        LatestVersion: currentVersion,
        ReleaseTitle: string.Empty,
        ReleaseNotes: string.Empty,
        HtmlUrl: string.Empty);
}
