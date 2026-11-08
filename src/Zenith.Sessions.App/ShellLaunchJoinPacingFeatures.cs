using System.Collections.Generic;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

// R-launch-2 (owner decision 2026-11-08): optional, off by default. When the saved preference is on, each presentation
// step hands the tick's evidence snapshot to the launch queue, which ends the startup stabilization wait early only for
// the exact started client (receipt PID and creation time) whose own log reports Connected, never below the launch
// spacing and never beyond the full stabilization. The report is diagnostic (HANDOFF.md): it skips no admission check,
// starts nothing itself and the queue still waits for a fresh exact observation of the started client. No timer, thread
// or polling is added: this rides the existing sample cadence (Rule 14), and Panic or Lock pause the queue, which then
// starts nothing however short the wait (Rule 11). Only the saved value acts; the draft waits for Save settings.
public sealed partial class ShellViewModel
{
    private bool _shortenWaitAfterJoin;
    /// <summary>The draft of the saved "Shorten the wait between starts when the previous client reports it joined" preference.</summary>
    public bool ShortenWaitAfterJoin { get => _shortenWaitAfterJoin; set { if (Set(ref _shortenWaitAfterJoin, value)) PreferencesEdited(); } }

    // Dispatcher thread, from the presentation step, with the same evidence snapshot the rows and Launch evidence show.
    private void ObserveJoinedClientForPacing(IReadOnlyList<SessionEvidenceSnapshot> evidence)
    {
        if (!_savedSettings.ShortenWaitAfterJoin || !_ready || IsLocked || _shuttingDown) return;
        if (_launchQueue.ReleasePacingEarly(evidence))
            AddEvent("The last started client reported it joined, so the wait before the next start was shortened to the launch spacing. Fresh resources, provider eligibility and one start at a time are still required.");
    }
}
