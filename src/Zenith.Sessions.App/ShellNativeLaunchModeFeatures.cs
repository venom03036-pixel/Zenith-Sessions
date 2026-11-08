using System.Collections.Generic;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

// R-api-7 (owner decision 2026-11-08): the saved native launch mode. Official page stays the default until the live trial
// verifies the direct mode. Only the saved value acts (the draft waits for Save settings); it is applied to the one
// selectable capture the native provider is composed with, read once per preparation. Choosing a mode grants nothing:
// publisher trust, account binding, resource admission, pacing, Panic fences and one start at a time are unchanged.
public sealed partial class ShellViewModel
{
    public sealed record NativeLaunchModeOption(NativeLaunchMode Mode, string Name, string Description)
    { public override string ToString() => Name; }

    public static IReadOnlyList<NativeLaunchModeOption> NativeLaunchModeOptions { get; } = Array.AsReadOnly(new NativeLaunchModeOption[]
    {
        new(NativeLaunchMode.OfficialPage, "Official page (default)",
            "A trial opens the selected account's private Roblox game page and captures the page's own launch request when you choose Play. Native account selection, compatibility and arrival remain unverified."),
        new(NativeLaunchMode.DirectTicket, "Direct ticket",
            "A trial asks Roblox for a launch ticket with the account's saved sign-in and builds the launch request itself: no page opens and no click is needed. Zenith names itself in that request and never pretends to be the Roblox website or a browser. The ticket is used once and never written anywhere. Every other check stays, the ticket proves neither the account nor arrival, and this mode is unverified until the live trial.")
    });

    private NativeLaunchModeOption _selectedNativeLaunchMode = NativeLaunchModeOptions[0];
    /// <summary>The draft of the saved native launch mode; Save settings applies it.</summary>
    public NativeLaunchModeOption SelectedNativeLaunchMode
    {
        get => _selectedNativeLaunchMode;
        set { if (value is null) return; if (Set(ref _selectedNativeLaunchMode, value)) PreferencesEdited(); }
    }
    private NativeLaunchMode SavedNativeLaunchMode => NativeLaunchModes.TryParse(_savedSettings.NativeLaunchMode, out var mode) ? mode : NativeLaunchModes.Default;
    public string NativeLaunchModeStatus => "Saved native launch mode: " + NativeLaunchModes.Label(SavedNativeLaunchMode) + ". " + DescribeNativeLaunchMode(SavedNativeLaunchMode);

    /// <summary>What a queued trial does in each mode; the same sentence in the trial status and the review. It claims no verification.</summary>
    public static string DescribeNativeLaunchMode(NativeLaunchMode mode) => mode == NativeLaunchMode.DirectTicket
        ? "Zenith requests a launch ticket with the saved sign-in and starts the selected native player itself; no Roblox page opens. A process receipt alone does not verify its account or arrival."
        : "The private Roblox game page may open. Choose Play there. A process receipt alone does not verify its account or arrival.";

    private static NativeLaunchModeOption OptionFor(NativeLaunchMode mode) => NativeLaunchModeOptions.First(option => option.Mode == mode);

    // Startup: the saved mode becomes the draft and the capture's mode before any trial can be reviewed.
    private void LoadNativeLaunchModeDraft()
    {
        _selectedNativeLaunchMode = OptionFor(SavedNativeLaunchMode);
        ApplySavedNativeLaunchMode();
        Raise(nameof(SelectedNativeLaunchMode));
    }
    // After every successful settings write, explicit or autosave: only the saved value reaches the capture. The saved mode also
    // decides which recorded trial, if any, turns ordinary launching on (R-launch-0), so that state is refreshed with it.
    private void ApplySavedNativeLaunchMode()
    {
        _nativeCapture.Mode = SavedNativeLaunchMode;
        Raise(nameof(NativeLaunchModeStatus));
        RefreshNativeLaunchVerification();
    }
}
