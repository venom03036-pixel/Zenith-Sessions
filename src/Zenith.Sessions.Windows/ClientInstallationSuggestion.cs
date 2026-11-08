using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Picks the one installation row Settings preselects as "Suggested" after Refresh installation, from the
/// snapshot's registry, protocol-handler and version-folder hints. It is a preselection only: the same
/// provider acceptance (<see cref="WindowsConfiguredNativeLaunchProvider.CanSelect"/>) decides what may be
/// suggested, the same publisher trust check runs before any start, and the user can choose another row.
/// </summary>
public static class ClientInstallationSuggestion
{
    /// <summary>
    /// The candidate to preselect, or null when the hints do not single one out. Only a native Roblox row the trial
    /// provider would accept is ever suggested: a present native direct player with Roblox-only evidence, so never the
    /// launcher, a bootstrapper-managed player, a bootstrapper's own executable (launching through one needs the user's
    /// own acceptance, R-launch-17), a missing or denied file, or a Microsoft Store build (a WindowsApps path). With one
    /// such row it is suggested. With several (leftover version folders), the one the roblox or roblox-player registration
    /// names is suggested, because Roblox itself writes that registration when it installs or updates; the folders alone
    /// cannot say which player would start. Registrations that disagree, or an unavailable or deferred snapshot, suggest
    /// nothing. Pure function of the snapshot; no file, registry or network access.
    /// </summary>
    public static ClientInstallationCandidate? Suggest(ClientInstallationSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Status is not (ClientInstallationSnapshotStatus.Complete or ClientInstallationSnapshotStatus.Partial)
            || snapshot.Candidates is null || snapshot.Issues is null) return null;
        foreach (var issue in snapshot.Issues)
            if (issue is { Code: ClientInstallationIssueCode.ConflictingProtocols }) return null;
        ClientInstallationCandidate? only = null, registered = null; int eligible = 0, registrations = 0;
        foreach (var candidate in snapshot.Candidates)
        {
            if (candidate is null || candidate.Product != ClientInstallationProduct.Roblox || !WindowsConfiguredNativeLaunchProvider.CanSelect(candidate)
                || RobloxNativePlayerFiles.IsPackagedPath(candidate.ExecutablePath)) continue;
            eligible++; only = candidate;
            if (IsRegistered(candidate)) { registrations++; registered = candidate; }
        }
        if (eligible == 1) return only;
        return eligible > 1 && registrations == 1 ? registered : null;
    }

    private static bool IsRegistered(ClientInstallationCandidate candidate)
    {
        foreach (var evidence in candidate.Evidence)
            if (evidence.Source is ClientInstallationHintSource.RobloxProtocol or ClientInstallationHintSource.RobloxPlayerProtocol) return true;
        return false;
    }
}
