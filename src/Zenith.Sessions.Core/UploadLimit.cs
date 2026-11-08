using System.Globalization;
using System.Text;

namespace Zenith.Sessions.Core;

public enum UploadLimitAction { Apply, Remove }

public enum UploadLimitStatus
{
    /// <summary>Windows PowerShell, run as administrator, made the change and read it back as asked.</summary>
    Confirmed,
    /// <summary>The typed rate is not a whole number of kilobits per second in the accepted range; nothing ran.</summary>
    InvalidRate,
    /// <summary>The user declined Windows' administrator prompt; nothing ran.</summary>
    Declined,
    /// <summary>The administrator run ended with an error (its exit code is in the result); the record is uncertain.</summary>
    Refused,
    /// <summary>The administrator run finished but a policy it read back afterwards did not match; the record is uncertain.</summary>
    NotConfirmed,
    /// <summary>Windows PowerShell could not be started (not found); nothing ran.</summary>
    Unavailable,
    /// <summary>The administrator run did not finish within the wait; it may still finish on its own; the record is uncertain.</summary>
    TimedOut,
    /// <summary>The wait was stopped (Cancel, Panic, Lock or exit); an approved run may still finish on its own; the record is uncertain.</summary>
    Cancelled,
    Busy, Closed,
    /// <summary>An older external writer can still mutate policy; another mutation is refused.</summary>
    HelperUnresolved,
    /// <summary>The durable external-operation marker could not be established; no helper started.</summary>
    OwnershipUnavailable
}

/// <summary>
/// Zenith's own record of the last upload limit change it asked for: the action, the rate asked for (for a Remove, the last
/// known rate), when, and whether Windows confirmed it. Not a readback: reading the active policy store needs administrator
/// rights, so this is what the user sees between clicks. It lives in one Windows session; the policy ends when Windows restarts.
/// </summary>
public sealed record UploadLimitRecord(UploadLimitAction LastAction, int KilobitsPerSecond, DateTimeOffset AtUtc, bool Confirmed,
    bool HelperUnresolved = false);
public sealed record UploadLimitReconciliation(long OperationRevision, UploadLimitResult Result);

public sealed record UploadLimitResult(UploadLimitAction Action, UploadLimitStatus Status, int KilobitsPerSecond, int ExitCode,
    UploadLimitRecord? Current, bool RecordSaved = true)
{
    public string Explanation => Status switch
    {
        UploadLimitStatus.Confirmed when Action == UploadLimitAction.Apply => "Windows confirmed the upload limit: all Roblox clients together may send at most "
            + UploadLimitPolicy.FormatRate(KilobitsPerSecond) + " until Windows restarts or you choose Remove." + UnsavedNote,
        UploadLimitStatus.Confirmed => "Windows confirmed that Zenith's upload limit policies are gone. Zenith no longer limits what Roblox clients send." + UnsavedNote,
        UploadLimitStatus.InvalidRate => UploadLimitPolicy.RateRequirement,
        UploadLimitStatus.Declined => "The administrator prompt was declined, so nothing was changed. Applying or removing an upload limit needs administrator approval." + UnsavedNote,
        UploadLimitStatus.Refused => "Windows PowerShell could not change the upload limit (exit code " + ExitCode.ToString(CultureInfo.InvariantCulture) + "). " + Uncertain,
        UploadLimitStatus.NotConfirmed => "Windows did not confirm the change: a policy Zenith read back afterwards did not match. " + Uncertain,
        UploadLimitStatus.Unavailable => "Windows PowerShell could not be started, so nothing was changed." + UnsavedNote,
        UploadLimitStatus.TimedOut => "Windows did not answer within " + UploadLimitPolicy.HelperTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
            + " seconds. " + PendingHelper,
        UploadLimitStatus.Cancelled => "The upload limit wait was stopped. " + (Current?.HelperUnresolved == true ? PendingHelper : "No unresolved helper remains; review the current result before retrying."),
        UploadLimitStatus.HelperUnresolved => PendingHelper,
        UploadLimitStatus.OwnershipUnavailable => "The upload-limit ownership record could not be saved or read safely. No helper started. Review the data folder before trying again.",
        UploadLimitStatus.Busy => "Another upload limit operation is still running. Wait for it or choose Cancel.",
        _ => "Upload limit controls are closed."
    };
    private const string AgainToBeSure = "choose Apply upload limit or Remove again to be sure.";
    public const string PendingHelper = "An earlier administrator prompt or PowerShell may still change the upload limit. New changes are blocked until its no-start or exit is observed. If that owner was lost when Zenith closed, restart Windows and reopen Zenith promptly. Changes remain blocked if Zenith cannot establish the restart; review the record for recovery.";
    private const string Uncertain = "Whether a limit is in force is now uncertain; " + AgainToBeSure;
    private string UnsavedNote => RecordSaved ? "" : " Zenith could not save its record of this change (" + UploadLimitPolicy.RecordFileName
        + " in the data folder), so the next run of Zenith will not show it.";
}

/// <summary>
/// The pure parts of the upload limit (R-instances-18): one Windows QoS policy per Roblox client executable, in the active
/// policy store only (gone when Windows restarts), created and removed through Windows PowerShell run as administrator with
/// the UAC prompt the user answers, never by Zenith on its own. The script text is fixed; only the checked rate goes into it.
/// The limit is one shared ceiling on what every matching client sends, not a share per client, and says nothing about downloads.
/// </summary>
public static class UploadLimitPolicy
{
    public const int MinimumKilobitsPerSecond = 100;
    public const int MaximumKilobitsPerSecond = 10_000_000;
    public const string PolicyNamePrefix = "ZenithSessionsUploadLimit-";
    public const string RecordFileName = "upload-limit.json";
    /// <summary>The administrator run's own report that a policy it read back after the change did not match.</summary>
    public const int ReadbackMismatchExitCode = 3;
    public const string RateRequirement = "Enter a whole number of kilobits per second from 100 to 10,000,000.";
    /// <summary>How long the administrator run may take once the prompt is answered; the scripts finish in seconds.</summary>
    public static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(60);
    /// <summary>Tolerance for comparing estimated UTC boot metadata; never proof that an unresolved helper has exited.</summary>
    public static readonly TimeSpan BootTolerance = TimeSpan.FromMinutes(2);
    /// <summary>The client executables Sessions observes, each the match condition of its own policy.</summary>
    public static IReadOnlyList<string> ClientExecutables { get; } = Array.AsReadOnly(new[] { "RobloxPlayerBeta.exe", "RobloxPlayer.exe", "Roblox.exe" });

    public static bool IsValidRate(int kilobitsPerSecond) => kilobitsPerSecond is >= MinimumKilobitsPerSecond and <= MaximumKilobitsPerSecond;

    /// <summary>Whole kilobits per second typed by the user; spaces and commas between digits are allowed as group separators.</summary>
    public static bool TryParseRate(string? text, out int kilobitsPerSecond)
    {
        kilobitsPerSecond = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16) return false;
        var digits = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is ' ' or ',') continue;
            if (c is < '0' or > '9') return false;
            digits.Append(c);
        }
        if (digits.Length == 0 || !int.TryParse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || !IsValidRate(parsed)) return false;
        kilobitsPerSecond = parsed;
        return true;
    }

    public static string FormatRate(int kilobitsPerSecond) => kilobitsPerSecond.ToString("N0", CultureInfo.InvariantCulture) + " kbit/s";

    /// <summary>
    /// Run as administrator: for each client executable, removes Zenith's policy if present, creates it again at the rate in the
    /// active store, reads it back and exits <see cref="ReadbackMismatchExitCode"/> on a mismatch; every error stops the run
    /// with exit 1 ($ErrorActionPreference Stop); exit 0 only when every policy was read back as asked.
    /// </summary>
    public static string ApplyScript(int kilobitsPerSecond)
    {
        if (!IsValidRate(kilobitsPerSecond)) throw new ArgumentOutOfRangeException(nameof(kilobitsPerSecond));
        long bits = (long)kilobitsPerSecond * 1000;
        return "$ErrorActionPreference = 'Stop'; $rate = " + bits.ToString(CultureInfo.InvariantCulture) + "; foreach ($app in " + ExecutableList + ") { "
            + "$name = '" + PolicyNamePrefix + "' + $app; "
            + "Remove-NetQosPolicy -Name $name -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue; "
            + "New-NetQosPolicy -Name $name -AppPathNameMatchCondition $app -ThrottleRateActionBitsPerSecond $rate -PolicyStore ActiveStore | Out-Null; "
            + "$read = Get-NetQosPolicy -Name $name -PolicyStore ActiveStore; "
            + "if ($read.ThrottleRateAction -ne $rate -or $read.AppPathNameMatchCondition -ne $app) { exit " + ReadbackMismatchExitCode + " } }; exit 0";
    }

    /// <summary>
    /// Run as administrator: removes Zenith's policy for each client executable (a missing one is fine) and lists the active
    /// store to confirm it is gone, exiting <see cref="ReadbackMismatchExitCode"/> if one is still there. The listing is read
    /// with Stop, so a denied read ends the run with exit 1 instead of passing for an absent policy.
    /// </summary>
    public static string RemoveScript()
        => "$ErrorActionPreference = 'Stop'; foreach ($app in " + ExecutableList + ") { "
            + "$name = '" + PolicyNamePrefix + "' + $app; "
            + "Remove-NetQosPolicy -Name $name -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue; "
            + "if (@(Get-NetQosPolicy -PolicyStore ActiveStore | Where-Object { $_.Name -eq $name }).Count -ne 0) { exit " + ReadbackMismatchExitCode + " } }; exit 0";

    private static string ExecutableList => "@(" + string.Join(",", ClientExecutables.Select(executable => "'" + executable + "'")) + ")";

    public static UploadLimitStatus StatusForExit(int exitCode) => exitCode switch
    {
        0 => UploadLimitStatus.Confirmed,
        ReadbackMismatchExitCode => UploadLimitStatus.NotConfirmed,
        _ => UploadLimitStatus.Refused
    };

    /// <summary>Compares estimated UTC boot metadata for display only; clock changes invalidate it as an ownership boundary. Readings lie within <see cref="BootTolerance"/>.</summary>
    public static bool SameWindowsSession(DateTimeOffset recordedBootUtc, DateTimeOffset currentBootUtc)
        => (recordedBootUtc - currentBootUtc).Duration() <= BootTolerance;
}

/// <summary>
/// Applies or removes Zenith's upload limit policies through an administrator run the user approves, one operation at a time,
/// and keeps the record of what it asked for. Cancellation stops the wait and cannot recall a run the user already approved.
/// </summary>
public interface IUploadLimitControl : IAsyncDisposable
{
    /// <summary>An abandoned operation later proved no-start or reached actual process exit. Reports reconciliation of the latest operation only.</summary>
    event Action<UploadLimitReconciliation>? Reconciled;
    /// <summary>Changes when a new operation starts; consumers discard an older queued reconciliation.</summary>
    long OperationRevision { get; }
    /// <summary>Zenith's record for this Windows session after <see cref="LoadRecordAsync"/> or the last operation; null when none.</summary>
    UploadLimitRecord? Current { get; }
    /// <summary>True when the saved record could not be read, so whether a limit is in force cannot be said; the next change replaces it.</summary>
    bool RecordUnreadable { get; }
    /// <summary>Once, at startup: reads the record and drops one from an earlier Windows session. Nothing is changed.</summary>
    Task<UploadLimitRecord?> LoadRecordAsync(CancellationToken ct);
    Task<UploadLimitResult> ApplyAsync(int kilobitsPerSecond, CancellationToken ct);
    Task<UploadLimitResult> RemoveAsync(CancellationToken ct);
    void CancelOperations();
}
