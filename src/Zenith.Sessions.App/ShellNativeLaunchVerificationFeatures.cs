using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.App;

/// <summary>
/// Ordinary launching after a verified trial (R-launch-0, owner decision of 2026-11-08). Off by default: ordinary single and group
/// launch requests are recorded blocked until the user records one explicit trial as verified for the saved native launch mode and
/// the selected executable identity. The gate is evidence: the trial's request must still be "Client started · unverified" with its
/// receipt, and its current Launch evidence must show the exact process observed and the client's own log reporting the expected
/// player in the requested place (<see cref="NativeLaunchVerification.CanRecord"/>). Recording saves the mode, product, held executable identity, time and the
/// trial's request and receipt in settings.json (no sign-in material); the configured provider reads it on every capability read and
/// reports Supported, so the queue's existing ordinary path opens with no review per account. A different saved mode or installation
/// turns it off; Forget verified trial removes the entry and pauses the queue. Nothing here proves a later client's account or arrival.
/// </summary>
public sealed partial class ShellViewModel
{
    // The most recent explicit trial this session started: the one request that may be recorded as verified, with the mode and
    // product it actually used (the review is consumed at start; the saved values may change afterwards). Memory only.
    private (Guid OperationId, NativeLaunchMode Mode, ClientInstallationProduct Product)? _lastTrialStart;
    private bool _canRecordShown;
    public AsyncCommand RecordNativeLaunchVerification { get; private set; } = null!;
    public AsyncCommand ForgetNativeLaunchVerification { get; private set; } = null!;

    private void InitializeNativeLaunchVerificationFeatures()
    {
        RecordNativeLaunchVerification = new(_ => RecordNativeLaunchVerificationAsync(), error => Fail(error, ManagerSubsystem.Settings));
        ForgetNativeLaunchVerification = new(_ => ForgetNativeLaunchVerificationAsync(), error => Fail(error, ManagerSubsystem.Settings));
    }

    /// <summary>True while the configured provider reports Supported: the saved mode and the selected installation's product have a recorded trial.</summary>
    public bool IsNativeLaunchVerified => _nativeLaunch.AccountLaunch.State == CapabilityState.Supported;

    /// <summary>The entry saved for the saved mode and the selected row's product, from the last saved settings (never a draft); null for none.</summary>
    private NativeLaunchVerification? SavedNativeLaunchVerification => SelectedInstallation?.Candidate is { } candidate
        ? NativeLaunchVerification.For(Volatile.Read(ref _savedSettings).NativeLaunchVerifications, SavedNativeLaunchMode, candidate.Product) : null;

    public bool CanRecordNativeLaunchVerification => _ready && !IsLocked && !_shuttingDown && !IsSample && _pendingSaves == 0 && QueueReady
        && !IsNativeLaunchVerified && _nativeLaunch.Selected && _lastTrialStart is { } start
        && SelectedInstallation?.Candidate is { } candidate && candidate.Product == start.Product
        && candidate.Product == ClientInstallationProduct.Roblox
        && _nativeLaunch.GetStartedLaunchMode(start.OperationId) == start.Mode
        && _nativeLaunch.GetStartedExecutableIdentity(start.OperationId) is { } identity && _nativeLaunch.MatchesSelectedExecutable(identity)
        && SelectedLaunchEvidence is { View: { } view } row && row.Id == start.OperationId
        && NativeLaunchVerification.CanRecord(row.Operation, view);
    public bool CanForgetNativeLaunchVerification => _ready && !IsLocked && !_shuttingDown && !IsSample && _pendingSaves == 0 && SavedNativeLaunchVerification is not null;

    /// <summary>Whether ordinary launching is on for the saved mode and the selected installation, and what to do next, in plain words.</summary>
    public string NativeLaunchVerificationStatus
    {
        get
        {
            var saved = Volatile.Read(ref _savedSettings); var mode = SavedNativeLaunchMode; string label = NativeLaunchModes.Label(mode);
            var candidate = SelectedInstallation?.Candidate;
            if (candidate is null)
                return "Ordinary launching: off. No native installation is selected. Select one above, run Review selected-account trial in Join a game, and record that trial as verified when its Launch evidence reports the expected player in the requested place."
                    + OtherVerifications(saved.NativeLaunchVerifications, mode, null);
            if (!_nativeLaunch.Selected)
                return "Ordinary launching: off. The selected row cannot be the trial installation, so no trial can run or be recorded through it; see the trial status above."
                    + OtherVerifications(saved.NativeLaunchVerifications, mode, null);
            if (candidate.Product != ClientInstallationProduct.Roblox)
                return "Ordinary launching: off. Bootstrapper qualification is deferred until its exact Roblox player can be associated with the launch. Select the standard Roblox player for this build.";
            if (IsNativeLaunchVerified && NativeLaunchVerification.For(saved.NativeLaunchVerifications, mode, candidate.Product) is { } verified)
                return "Ordinary launching: on for " + label + " through " + candidate.Product + ", because of " + verified.Describe()
                    + ". Ordinary and group launch requests use this installation with the same publisher, sign-in, account, resource and stop checks as a trial, one start at a time, and no review per account."
                    + " A started client's account and arrival are still confirmed only by its own log reports. Choose Forget verified trial to turn ordinary launching off.";
            return "Ordinary launching: off for " + label + " through " + candidate.Product + ". In Join a game, Review selected-account trial starts one explicit trial;"
                + " when its Launch evidence shows the exact process observed and the client's own log reporting the expected player in the requested place, choose Record this trial as verified."
                + " A changed executable requires a new trial; older records without an executable identity cannot enable launching. Until then ordinary and group requests are recorded blocked." + OtherVerifications(saved.NativeLaunchVerifications, mode, candidate.Product);
        }
    }
    private static string OtherVerifications(IReadOnlyList<NativeLaunchVerification>? entries, NativeLaunchMode mode, ClientInstallationProduct? product)
    {
        if (entries is null || entries.Count == 0) return "";
        var others = entries.Where(entry => product is null || !entry.IsFor(mode, product.Value)).Select(entry => entry.ModeLabel + " through " + entry.Product).ToArray();
        return others.Length == 0 ? "" : " Verified trials saved for other choices: " + string.Join("; ", others) + "; they do not apply to this selection.";
    }
    /// <summary>The Accounts page note: ordinary launching's state in one sentence, never a claim about a client's account or arrival.</summary>
    public string AccountLaunchAvailability => IsNativeLaunchVerified && SavedNativeLaunchVerification is { } verified
        ? "Account-selecting launch is on for " + verified.ModeLabel + " through " + verified.Product + ", by a trial you recorded as verified. A client's account and arrival are still confirmed only by its own log reports."
        : "Account-selecting launch is still being verified.";
    /// <summary>The trial card's text while ordinary launching is on through the selected installation.</summary>
    private string VerifiedNativeLaunchStatus(NativeLaunchVerification verified)
        => "Ordinary launching is on for " + verified.ModeLabel + " through " + verified.Product + ", because of " + verified.Describe()
            + ". Ordinary and group requests use this installation with the same publisher, sign-in, account, resource and stop checks as a trial, one start at a time;"
            + " a started client's account and arrival are still confirmed only by its own log reports. Explicit trials are not offered while it is on; Forget verified trial in Settings → Roblox installation turns it off.";

    private void RefreshNativeLaunchVerification()
    {
        _canRecordShown = CanRecordNativeLaunchVerification;
        Raise(nameof(IsNativeLaunchVerified)); Raise(nameof(CanRecordNativeLaunchVerification)); Raise(nameof(CanForgetNativeLaunchVerification));
        Raise(nameof(NativeLaunchVerificationStatus)); Raise(nameof(AccountLaunchAvailability)); Raise(nameof(CanRunNativeTrial));
    }
    // Each presentation tick re-projects the evidence; raise the record control only when its answer changed.
    private void RefreshRecordableTrial()
    {
        bool can = CanRecordNativeLaunchVerification;
        if (can == _canRecordShown) return;
        _canRecordShown = can; Raise(nameof(CanRecordNativeLaunchVerification));
    }

    private async Task RecordNativeLaunchVerificationAsync()
    {
        // The evidence is the latest projection (at most one presentation tick old, and itself bounded to fresh process and log facts).
        if (!CanRecordNativeLaunchVerification || _lastTrialStart is not { } start || SelectedLaunchEvidence is not { View: { } view } row) return;
        var identity = _nativeLaunch.GetStartedExecutableIdentity(start.OperationId);
        var mode = _nativeLaunch.GetStartedLaunchMode(start.OperationId);
        if (identity is null || mode != start.Mode || !_nativeLaunch.MatchesSelectedExecutable(identity)) return;
        var entry = NativeLaunchVerification.Record(mode.Value, start.Product, row.Operation, view, DateTimeOffset.UtcNow, identity);
        var kept = Volatile.Read(ref _savedSettings).NativeLaunchVerifications ?? Array.Empty<NativeLaunchVerification>();
        var list = kept.Where(item => !item.IsFor(start.Mode, start.Product)).Append(entry).ToArray();
        // One settings write of the saved values plus this entry; unsaved drafts of other preferences stay unsaved.
        await SaveCurrentSettingsAsync(_savedSettings with { NativeLaunchVerifications = NativeLaunchVerification.Validate(list) });
        if (IsLocked || _shuttingDown) return;
        RefreshNativeLaunchVerification();
        string label = NativeLaunchModes.Label(start.Mode);
        if (IsNativeLaunchVerified)
        {
            NativeTrialStatus = VerifiedNativeLaunchStatus(entry);
            Notice = "Recorded the trial as verified for " + label + " through " + start.Product + ". Ordinary and group launch requests now use this installation with the same checks as a trial;"
                + " requests already recorded as blocked stay blocked, so queue them again.";
        }
        else NativeTrialStatus = Notice = "Recorded the trial as verified for " + label + " through " + start.Product + ". The saved launch mode or the selected installation is different now, so ordinary launching stays off until they match it.";
        _launchQueue.Signal();
    }

    private async Task ForgetNativeLaunchVerificationAsync()
    {
        if (!CanForgetNativeLaunchVerification || SelectedInstallation?.Candidate is not { } candidate || SavedNativeLaunchVerification is not { } saved) return;
        var mode = SavedNativeLaunchMode;
        var kept = (Volatile.Read(ref _savedSettings).NativeLaunchVerifications ?? Array.Empty<NativeLaunchVerification>()).Where(item => !item.IsFor(mode, candidate.Product)).ToArray();
        await SaveCurrentSettingsAsync(_savedSettings with { NativeLaunchVerifications = NativeLaunchVerification.Validate(kept) });
        if (IsLocked || _shuttingDown) return;
        // Off again: ordinary requests are recorded blocked from now on, and the queue stops the way a selection change stops it.
        ConfigureNativeTrialInstallation();
        NativeTrialStatus = Notice = "Ordinary launching is off for " + saved.ModeLabel + " through " + candidate.Product + ": the verified trial was forgotten and the launch queue was paused."
            + " Explicit trials are available again; run one and record it to turn ordinary launching back on.";
        RefreshNativeLaunchVerification();
    }

    /// <summary>
    /// What the configured provider reads when its capability is read: the verified trial for the saved launch mode and the given product
    /// in the last saved settings, never a draft. Runs on the reader's thread; the settings reference is read once and the record is immutable.
    /// </summary>
    private sealed class SavedNativeLaunchVerifications(ShellViewModel owner) : INativeLaunchVerificationSource
    {
        public NativeLaunchVerification? Find(ClientInstallationProduct product)
        {
            var saved = Volatile.Read(ref owner._savedSettings);
            var mode = NativeLaunchModes.TryParse(saved.NativeLaunchMode, out var parsed) ? parsed : NativeLaunchModes.Default;
            return NativeLaunchVerification.For(saved.NativeLaunchVerifications, mode, product);
        }
    }
}
