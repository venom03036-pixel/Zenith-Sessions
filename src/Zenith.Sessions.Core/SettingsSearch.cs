using System.Buffers;
using System.Globalization;
using System.Text;

namespace Zenith.Sessions.Core;

// The first eight targets are sections of the Settings page; the rest are preferences on Sessions and Diagnostics.
public enum SettingsSearchTarget
{
    Appearance, Performance, CustomTiming, Memory, Installations, SignIn, Save, Recovery,
    SessionsLayout, ContentSize, ClientPriority, Previews, ClientSound, DiagnosticsFilters, UploadLimit
}

/// <summary>Fixed navigation metadata. A result never changes a preference or starts an operation.</summary>
public sealed record SettingsSearchEntry(string Id, string Title, string Description,
    SettingsSearchTarget Target, IReadOnlyList<string> Keywords);

/// <summary>
/// A small authored registry for the companion's existing Settings controls and explanations, plus the preferences that
/// live on Sessions and Diagnostics. No user settings, account data or query text is retained. Keep entries aligned with the UI.
/// </summary>
public static class SettingsSearchCatalog
{
    public const int MaximumQueryLength = 160;
    public const int MaximumTokens = 8;
    public const int MaximumResults = 10;

    // The first eight entries are useful blank-query topics, one for each Settings page section. Preferences on other
    // pages are found by typing, which keeps the blank list within the ten-result bound.
    private static readonly SettingsSearchEntry[] Entries =
    [
        E("appearance", "Appearance", "Choose a dark theme, Light, or Follow Windows to match the Windows app mode. Windows High Contrast replaces the theme while it is on. Theme changes apply immediately.",
            SettingsSearchTarget.Appearance, "theme", "palette", "colour", "color", "accent", "dark", "light", "dark mode", "light mode", "follow windows", "system", "high contrast"),
        E("performance-profiles", "Manager performance profiles", "Choose one of five manager monitoring and future-start pacing profiles, then save settings.",
            SettingsSearchTarget.Performance, "presets", "efficiency", "balanced", "high performance", "main account priority", "custom"),
        E("custom-timing", "Custom timing", "Inspect process, dashboard, launch spacing and stabilization intervals. Choose Custom in the profile control to edit them.",
            SettingsSearchTarget.CustomTiming, "milliseconds", "interval", "cadence", "delay", "stagger", "stabilisation", "reset"),
        E("memory-policy", "Memory policy", "Configure conservative starting estimates and spare physical memory in GiB. Admission guards still apply.",
            SettingsSearchTarget.Memory, "ram", "physical", "commit", "headroom", "capacity", "user maximum", "clients"),
        E("installations", "Roblox installation", "Refresh installation hints and inspect version metadata. Discovery never launches or selects a provider.",
            SettingsSearchTarget.Installations, "path", "version", "metadata", "refresh", "RobloxPlayerBeta", "Froststrap", "Fishstrap", "Bloxstrap", "Voidstrap"),
        E("saved-sign-in", "Saved Roblox sign-ins", "Read how to sign in on Accounts using Roblox's own page and explicitly save the verified account session.",
            SettingsSearchTarget.SignIn, "signin", "sign in", "login", "log in", "session", "account", "user id", "WebView2"),
        E("save-settings", "Save settings", "Commit valid preferences and review the saved, unsaved or failed-write status.",
            SettingsSearchTarget.Save, "apply", "persist", "pending", "retry save", "write", "preferences"),
        E("protected-recovery", "Protected-file recovery", "Review pending protected credential cleanup and retry it. This does not restore obsolete authorization.",
            SettingsSearchTarget.Recovery, "recovery", "retry", "credential", "vault", "cleanup", "protected", "unused records"),

        E("language", "Language", "Choose English, Follow Windows, or a pseudo-locale for testing translated labels and right-to-left layout. Language changes apply immediately; untranslated pages stay in English.",
            SettingsSearchTarget.Appearance, "language", "localization", "translation", "culture", "right to left", "rtl", "pseudo"),
        E("theme-graphite-mint", "Graphite Mint", "Find the existing graphite theme with a mint accent.",
            SettingsSearchTarget.Appearance, "theme", "palette", "default"),
        E("theme-midnight", "Midnight", "Find the existing near-black theme with green accents.",
            SettingsSearchTarget.Appearance, "theme", "palette", "black", "green", "dark"),
        E("theme-amber-night", "Amber Night", "Find the existing warm dark theme with yellow-orange controls.",
            SettingsSearchTarget.Appearance, "theme", "palette", "orange", "yellow"),
        E("theme-ocean-blue", "Ocean Blue", "Find the existing navy theme with blue controls.",
            SettingsSearchTarget.Appearance, "theme", "palette", "navy"),
        E("theme-violet-dusk", "Violet Dusk", "Find the existing plum theme with violet highlights.",
            SettingsSearchTarget.Appearance, "theme", "palette", "purple", "plum"),
        E("close-button", "Close button exits the manager", "Choose whether the window's close button keeps the manager in the tray (the default) or exits it like Exit. Save settings to apply.",
            SettingsSearchTarget.Appearance, "tray", "close to tray", "minimize to tray", "notification area", "quit", "background"),
        E("idle-lock", "Lock when idle", "Choose whether the manager locks like Lock after a time without keyboard or mouse input. Off by default; save settings to apply.",
            SettingsSearchTarget.Appearance, "idle", "auto lock", "automatic lock", "inactivity", "timeout", "away", "privacy", "security"),
        E("keep-awake", "Keep clients awake", "Off by default. Choose a gap of 5 to 19 minutes and save: each client is brought forward, sent one F13 press through the input owner, and the earlier window is put back. Refused until the live input isolation trial passes.",
            SettingsSearchTarget.Appearance, "anti-afk", "anti afk", "afk", "away from keyboard", "idle kick", "idle disconnect", "keep awake", "keepalive", "f13", "20 minutes", "automation"),
        E("panic-shortcut", "Panic shortcut", "Choose the global shortcut for Panic stop: F12 by default, or another such as Ctrl+Alt+Shift+P. Save settings to register it; F12 still works while the manager is focused.",
            SettingsSearchTarget.Appearance, "panic", "hotkey", "hot key", "keyboard shortcut", "global shortcut", "emergency stop", "F12", "key combination"),
        E("scheduled-shutdown", "Scheduled shutdown", "Ask Windows to shut down this PC after a chosen time, with a countdown and Cancel shutdown. Nothing is scheduled unless you ask; Panic and Exit cancel it.",
            SettingsSearchTarget.Appearance, "shutdown", "shut down", "power off", "turn off", "switch off", "timer", "countdown", "sleep timer", "schedule", "auto shutdown"),
        E("public-network-guard", "Hold launches on a public network", "Choose whether new launches are held and the dashboard locks while Windows files the active connection as Public (café, hotel or airport Wi-Fi). Off by default; held requests start again once the network is private. Save settings to apply.",
            SettingsSearchTarget.Appearance, "public network", "public wifi", "wifi", "wi-fi", "hotspot", "café", "cafe", "hotel", "airport", "network profile", "untrusted network", "guard", "security", "privacy"),
        E("discord-alerts", "Send Discord alerts", "Choose whether Zenith posts an alert to one saved Discord webhook when a client crashes, stops responding or is disconnected, and when a launch batch has finished. Off by default; the webhook URL is kept protected. Save settings to apply.",
            SettingsSearchTarget.Appearance, "discord", "webhook", "web hook", "alert", "alerts", "notification", "notifications", "notify", "crash", "hang", "disconnect", "batch finished", "message", "ping", "channel"),
        E("discord-presence", "Show Zenith in your Discord status", "Choose whether the Discord app on this PC shows how many Roblox clients Zenith sees running and, if you also choose it, the game they are in. Off by default; needs a Discord application ID; cleared on Lock, Panic and exit. Save settings to apply.",
            SettingsSearchTarget.Appearance, "discord", "status", "rich presence", "presence", "playing", "activity", "game status", "show game", "client count", "application id", "now playing"),

        E("profile-maximum-efficiency", "Maximum Efficiency", "Choose the manager profile with less frequent process observations and wider eligible-start spacing.",
            SettingsSearchTarget.Performance, "performance preset", "monitoring", "cadence", "sampling"),
        E("profile-balanced", "Balanced", "Choose the manager profile with normal observation cadence and eligible-start pacing.",
            SettingsSearchTarget.Performance, "performance preset", "monitoring", "default"),
        E("profile-high-performance", "High Performance", "Choose more frequent manager observations, with additional monitoring overhead. Game performance gains are not established.",
            SettingsSearchTarget.Performance, "performance preset", "cadence", "sampling", "monitoring"),
        E("profile-main-account-priority", "Main Account Priority", "Choose a bounded eligible launch-order boost for the selected main saved account. Waiting-age fairness and admission remain active.",
            SettingsSearchTarget.Performance, "performance preset", "priority", "fairness", "queue", "main account"),
        E("profile-custom", "Custom", "Find the Custom profile and its timing fields. Going to this result does not select the profile or change a draft.",
            SettingsSearchTarget.CustomTiming, "performance preset", "custom timing", "reset"),
        E("main-account", "Main saved account", "Use the selected library account as main, or clear the choice. This selection does not verify a running client.",
            SettingsSearchTarget.Performance, "main account choice", "library", "priority", "clear main"),
        E("client-gpu", "Show each client's GPU use", "Off by default. Turn it on and save to show each client's busiest GPU engine and dedicated GPU memory as a column of the Sessions table. Unknown values stay Unavailable, never 0.",
            SettingsSearchTarget.Performance, "gpu", "graphics card", "video memory", "vram", "dedicated memory", "engine", "utilization", "per client", "monitor"),
        E("process-timing", "Process details interval", "Custom process sampling interval: 500 to 5000 milliseconds. Cached measurements keep their original time.",
            SettingsSearchTarget.CustomTiming, "process sample", "polling", "cadence", "milliseconds", "monitor", "refresh"),
        E("dashboard-timing", "Dashboard interval", "Custom dashboard presentation interval: 250 to 2000 milliseconds.",
            SettingsSearchTarget.CustomTiming, "presentation", "refresh", "cadence", "milliseconds", "ui"),
        E("launch-spacing", "Launch spacing", "Custom spacing between eligible future starts: 1000 to 30000 milliseconds. Capacity and provider checks remain active.",
            SettingsSearchTarget.CustomTiming, "stagger", "launch delay", "pacing", "milliseconds"),
        E("stabilization-timing", "Launch stabilization", "Custom stabilization interval: 20000 to 120000 milliseconds. An active start keeps its original pacing.",
            SettingsSearchTarget.CustomTiming, "stabilisation", "settle", "timing", "milliseconds"),
        E("performance-transfer", "Share manager performance settings", "Copy a code, save a file, or review an import before using it in the draft. Save settings is still required; accounts and credentials are excluded.",
            SettingsSearchTarget.Performance, "export", "import", "share code", "review code", "review file", "transfer", "portable", "use in draft"),
        E("shorten-wait-after-join", "Shorten the wait after a client joins", "Choose whether a startup stabilization wait ends early once the started client's own log reports it joined, never before the launch spacing. Off by default; save settings to apply.",
            SettingsSearchTarget.Performance, "join", "joined", "connected", "wait", "shorten", "early", "next start", "between starts", "faster launches"),

        E("spare-ram", "Minimum spare RAM", "Set the physical memory headroom to leave available, in GiB.",
            SettingsSearchTarget.Memory, "spare physical", "reserve", "memory", "headroom", "ram"),
        E("physical-per-client", "New-client physical estimate", "Set the conservative physical memory estimate for a new client, in GiB. It is not a game requirement or measured capacity.",
            SettingsSearchTarget.Memory, "per client physical", "ram", "memory", "starting estimate"),
        E("commit-per-client", "New-client commit estimate", "Set the conservative committed-memory estimate for a new client, in GiB. Physical memory and commit are separate limits.",
            SettingsSearchTarget.Memory, "per client commit", "memory", "committed", "starting estimate"),
        E("maximum-clients", "Optional maximum clients", "Set a positive user cap, or leave the field blank for no user cap. Resource and provider limits still apply.",
            SettingsSearchTarget.Memory, "user maximum", "client limit", "max alts", "capacity", "blank cap"),
        E("publisher-verification", "Verify Roblox publisher", "Request offline verification using cached Windows trust information. Missing chain or revocation evidence remains unknown.",
            SettingsSearchTarget.Installations, "publisher", "signature", "Authenticode", "offline", "trust", "certificate", "verification"),
        E("extra-client-arguments", "Extra client arguments", "Add command-line arguments after Roblox's own launch request when Zenith starts the native player itself: a default here, or one account's own set in Edit account profile. Off until entered; never through a bootstrapper. Save settings to apply.",
            SettingsSearchTarget.Installations, "arguments", "command line", "flags", "launch flags", "switches", "parameters", "client arguments", "launch arguments", "per account", "RobloxPlayerBeta"),
        E("native-launch-mode", "Native launch mode", "Direct ticket is the default: launch with the encrypted saved sign-in, without a browser page or recorded trial. Official page remains a diagnostic alternative that captures the game page request when you choose Play. Save to apply.",
            SettingsSearchTarget.Installations, "launch mode", "direct ticket", "official page", "ticket", "authentication ticket", "hands-free", "no browser", "no click", "Play", "trial", "RobloxPlayerBeta"),
        E("bootstrapper-launch", "Launch through a bootstrapper", "Start Roblox through an installed Bloxstrap, Fishstrap, Froststrap or Voidstrap: select its own row, verify its publisher and accept it once. The bootstrapper applies its own client settings and FFlags; Zenith never writes client files. Off by default.",
            SettingsSearchTarget.Installations, "bootstrapper", "Bloxstrap", "Fishstrap", "Froststrap", "Voidstrap", "FFlags", "fast flags", "accept publisher", "thumbprint", "certificate", "launch through", "signer"),
        E("verified-launch", "Ordinary launching after a verified trial", "Direct ticket needs no recorded trial. Official page diagnostic mode can be enabled for ordinary launches with Record this trial as verified, after Launch evidence reports the expected player in the requested place. Forget verified trial removes that record and pauses the queue.",
            SettingsSearchTarget.Installations, "verified", "verify", "verified trial", "record trial", "ordinary launching", "ordinary launch", "group launch", "batch launch", "launch all", "hands-free", "enable launching", "blocked", "not available", "forget verified"),
        E("webview-runtime", "Private sign-in window", "Microsoft Edge WebView2 Runtime is required for the owned official-site sign-in. Saved sign-in alone does not establish native account launching.",
            SettingsSearchTarget.SignIn, "WebView2", "runtime", "browser", "login", "session", "private", "saved signin"),
        E("settings-folder", "Open settings folder", "Open the companion's writable user settings folder. User data stays outside the application folder.",
            SettingsSearchTarget.Save, "folder", "directory", "data location", "user data", "storage"),
        E("sessions-layout", "Tile layout preferences", "On Sessions, under Layout preferences & results: tile columns (blank is automatic), gap, and minimum client width and height. Save all settings to keep them.",
            SettingsSearchTarget.SessionsLayout, "tile", "tiling", "arrange", "columns", "gap", "minimum width", "minimum height", "layout preferences", "grid"),
        E("client-window-size", "Client window size", "On Sessions, under Client window size: enter a content width and height in physical pixels, then choose Apply size for checked clients. Nothing is resized until you choose Apply size.",
            SettingsSearchTarget.ContentSize, "resize", "content size", "window size", "width", "height", "pixels", "resolution", "tiny", "small window"),
        E("client-priority", "Client CPU priority", "On Sessions, under Client CPU priority: set checked clients to Normal or Below normal, turn on efficiency mode, spread them over cores, cap their share of all CPU time, or restore their original settings. Nothing changes until you choose a button.",
            SettingsSearchTarget.ClientPriority, "cpu priority", "process priority", "below normal", "restore original", "scheduling", "lower priority", "efficiency mode", "ecoqos", "affinity", "cores", "cpu cap", "cap cpu", "limit cpu", "job object", "throttle"),
        E("client-previews", "Client previews", "On Sessions, choose Previews, then Preview checked clients to show live thumbnails. Previews start only when you choose them and stop when you leave the page, hide the window or lock.",
            SettingsSearchTarget.Previews, "preview", "thumbnails", "live view", "capture", "watch clients", "stop previews"),
        E("client-sound", "Client sound", "On Sessions, under Client sound: Mute or Unmute checked clients, the same switch as Volume mixer, or turn on Mute clients when they appear and save all settings for clients that start later. Nothing changes until you choose Mute or Unmute, or save.",
            SettingsSearchTarget.ClientSound, "mute", "unmute", "sound", "audio", "volume", "volume mixer", "silence", "quiet", "mute when they appear", "mute new clients"),
        E("upload-limit", "Upload limit", "On Sessions, under Upload limit: enter kilobits per second and choose Apply upload limit, or choose Remove. Each runs Windows PowerShell as administrator after Windows' own prompt; nothing changes until you approve it.",
            SettingsSearchTarget.UploadLimit, "upload", "upload limit", "bandwidth", "qos", "network", "kbps", "kbit", "outbound", "send rate", "traffic", "administrator"),
        E("diagnostics-filters", "Diagnostics filters", "On Diagnostics: filter manager events by text, subsystem or error class, and show or hide errors, warnings and information. Filters change only this view; logging continues.",
            SettingsSearchTarget.DiagnosticsFilters, "filter", "events", "logs", "errors", "warnings", "information", "level", "error class", "subsystem"),
        E("roblox-updates", "Check for Roblox updates automatically", "Under Roblox installation: ask Roblox's public version service for its current release 3 seconds after start and every 30 minutes, and say when the installed player is behind. Off by default; save to apply. Download and Install Roblox update stay explicit.",
            SettingsSearchTarget.Installations, "update", "updates", "roblox update", "automatic check", "version check", "installer", "install roblox", "download update", "30 minutes", "out of date", "new version", "client version"),
        E("pending-client-performance", "Client FPS and graphics: pending", "Read the capability explanation. Native FPS, graphics, rendering and automatic affinity policies are not configurable here. Client process priority, efficiency mode, core spreading and a per-client CPU cap are set in Sessions, under Client CPU priority.",
            SettingsSearchTarget.Performance, "fps", "frame rate", "graphics", "renderer", "render", "texture", "MSAA", "lowest graphics", "client cpu", "gpu", "unavailable", "unsupported")
    ];

    /// <summary>The manager page that holds a target's controls: "Settings", "Sessions" or "Diagnostics". Opening a result
    /// goes to that page first, exactly like its navigation button.</summary>
    public static string PageOf(SettingsSearchTarget target) => target switch
    {
        SettingsSearchTarget.SessionsLayout or SettingsSearchTarget.ContentSize or SettingsSearchTarget.ClientPriority
            or SettingsSearchTarget.Previews or SettingsSearchTarget.ClientSound or SettingsSearchTarget.UploadLimit => "Sessions",
        SettingsSearchTarget.DiagnosticsFilters => "Diagnostics",
        _ => "Settings"
    };

    private static readonly IReadOnlyList<SettingsSearchEntry> DefaultTopics = Array.AsReadOnly(Entries[..8]);
    private static readonly IndexedEntry[] Index = CreateIndex();

    /// <summary>
    /// Blank/whitespace queries return eight fixed topics, one for each Settings page section. Invalid UTF-16, controls/format characters,
    /// more than 160 UTF-16 units or more than eight folded tokens return an empty result.
    /// Tokens match word prefixes across title, description and keywords; all tokens must match.
    /// Results contain only fixed catalog metadata, at most ten items, in deterministic relevance order.
    /// </summary>
    public static IReadOnlyList<SettingsSearchEntry> Search(string? query)
    {
        if (query is null) return DefaultTopics;
        if (query.Length > MaximumQueryLength || !TryFold(query, out string folded))
            return Array.Empty<SettingsSearchEntry>();
        if (string.IsNullOrWhiteSpace(query)) return DefaultTopics;
        if (folded.Length == 0) return Array.Empty<SettingsSearchEntry>();
        string[] tokens = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > MaximumTokens) return Array.Empty<SettingsSearchEntry>();

        var matches = new List<(int Score, int Order)>(Index.Length);
        for (int i = 0; i < Index.Length; i++)
        {
            int score = Score(Index[i], folded, tokens);
            if (score >= 0) matches.Add((score, i));
        }
        matches.Sort(static (a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score) : a.Order.CompareTo(b.Order));
        int count = Math.Min(matches.Count, MaximumResults);
        if (count == 0) return Array.Empty<SettingsSearchEntry>();
        var results = new SettingsSearchEntry[count];
        for (int i = 0; i < count; i++) results[i] = Entries[matches[i].Order];
        return Array.AsReadOnly(results);
    }

    private static int Score(IndexedEntry entry, string phrase, string[] tokens)
    {
        int maximumField = 0, totalField = 0;
        foreach (string token in tokens)
        {
            int field = HasWordPrefix(entry.Title, token) ? 0
                : HasWordPrefix(entry.Keywords, token) ? 1
                : HasWordPrefix(entry.Description, token) ? 2 : -1;
            if (field < 0) return -1;
            maximumField = Math.Max(maximumField, field);
            totalField += field;
        }
        if (entry.Title.Equals(phrase, StringComparison.Ordinal)) return 0;
        if (entry.Title.StartsWith(phrase, StringComparison.Ordinal)) return 10;
        return 20 + maximumField * 10 + totalField;
    }

    private static bool HasWordPrefix(string text, string token)
    {
        int start = 0;
        while (start < text.Length)
        {
            int found = text.IndexOf(token, start, StringComparison.Ordinal);
            if (found < 0) return false;
            if (found == 0 || text[found - 1] == ' ') return true;
            start = found + 1;
        }
        return false;
    }

    private static IndexedEntry[] CreateIndex()
    {
        var index = new IndexedEntry[Entries.Length];
        for (int i = 0; i < Entries.Length; i++)
            index[i] = new(FoldFixed(Entries[i].Title), FoldFixed(Entries[i].Description), FoldFixed(string.Join(" ", Entries[i].Keywords)));
        return index;
    }

    private static string FoldFixed(string text) => TryFold(text, out string folded) ? folded
        : throw new InvalidOperationException("A fixed settings-search entry is invalid.");

    private static bool TryFold(string text, out string folded)
    {
        folded = string.Empty;
        for (int at = 0; at < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(at), out Rune rune, out int consumed) != OperationStatus.Done) return false;
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return false;
            at += consumed;
        }
        string normalized;
        try { normalized = text.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { return false; }
        var builder = new StringBuilder(normalized.Length);
        bool space = false;
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            if (Rune.IsLetterOrDigit(rune))
            {
                if (space && builder.Length != 0) builder.Append(' ');
                builder.Append(Rune.ToLowerInvariant(rune).ToString());
                space = false;
            }
            else space = true;
        }
        folded = builder.ToString();
        return true;
    }

    private static SettingsSearchEntry E(string id, string title, string description, SettingsSearchTarget target, params string[] keywords)
        => new(id, title, description, target, Array.AsReadOnly(keywords));

    private sealed record IndexedEntry(string Title, string Description, string Keywords);
}
