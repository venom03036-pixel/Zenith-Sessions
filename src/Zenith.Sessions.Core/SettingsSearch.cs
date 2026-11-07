using System.Buffers;
using System.Globalization;
using System.Text;

namespace Zenith.Sessions.Core;

public enum SettingsSearchTarget { Appearance, Performance, CustomTiming, Memory, Installations, SignIn, Save, Recovery }

/// <summary>Fixed navigation metadata. A result never changes a preference or starts an operation.</summary>
public sealed record SettingsSearchEntry(string Id, string Title, string Description,
    SettingsSearchTarget Target, IReadOnlyList<string> Keywords);

/// <summary>
/// A small authored registry for the companion's existing Settings controls and explanations.
/// No user settings, account data or query text is retained. Keep entries aligned with Settings UI.
/// </summary>
public static class SettingsSearchCatalog
{
    public const int MaximumQueryLength = 160;
    public const int MaximumTokens = 8;
    public const int MaximumResults = 10;

    // The first eight entries are useful blank-query topics, one for each navigation target.
    private static readonly SettingsSearchEntry[] Entries =
    [
        E("appearance", "Appearance", "Choose an existing theme palette. Theme changes apply immediately.",
            SettingsSearchTarget.Appearance, "theme", "palette", "colour", "color", "accent", "dark"),
        E("performance-profiles", "Manager performance profiles", "Choose one of five manager monitoring and future-start pacing profiles, then save settings.",
            SettingsSearchTarget.Performance, "presets", "efficiency", "balanced", "high performance", "main account priority", "custom"),
        E("custom-timing", "Custom timing", "Inspect process, dashboard, launch spacing and stabilization intervals. Choose Custom in the profile control to edit them.",
            SettingsSearchTarget.CustomTiming, "milliseconds", "interval", "cadence", "delay", "stagger", "stabilisation", "reset"),
        E("memory-policy", "Memory policy", "Configure conservative starting estimates and spare physical memory in GiB. Admission guards still apply.",
            SettingsSearchTarget.Memory, "ram", "physical", "commit", "headroom", "capacity", "user maximum", "clients"),
        E("installations", "Roblox installation", "Refresh installation hints and inspect version metadata. Discovery never launches or selects a provider.",
            SettingsSearchTarget.Installations, "path", "version", "metadata", "refresh", "RobloxPlayerBeta", "Froststrap", "Fishstrap", "Bloxstrap"),
        E("saved-sign-in", "Saved Roblox sign-ins", "Read how to sign in on Accounts using Roblox's own page and explicitly save the verified account session.",
            SettingsSearchTarget.SignIn, "signin", "sign in", "login", "log in", "session", "account", "user id", "WebView2"),
        E("save-settings", "Save settings", "Commit valid preferences and review the saved, unsaved or failed-write status.",
            SettingsSearchTarget.Save, "apply", "persist", "pending", "retry save", "write", "preferences"),
        E("protected-recovery", "Protected-file recovery", "Review pending protected credential cleanup and retry it. This does not restore obsolete authorization.",
            SettingsSearchTarget.Recovery, "recovery", "retry", "credential", "vault", "cleanup", "protected", "unused records"),

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
        E("webview-runtime", "Private sign-in window", "Microsoft Edge WebView2 Runtime is required for the owned official-site sign-in. Saved sign-in alone does not establish native account launching.",
            SettingsSearchTarget.SignIn, "WebView2", "runtime", "browser", "login", "session", "private", "saved signin"),
        E("settings-folder", "Open settings folder", "Open the companion's writable user settings folder. User data stays outside the application folder.",
            SettingsSearchTarget.Save, "folder", "directory", "data location", "user data", "storage"),
        E("pending-client-performance", "Client FPS and graphics: pending", "Read the capability explanation. Native FPS, graphics, render and client CPU cap/affinity policies are not configurable here. Client process priority is set in Sessions, under Client CPU priority.",
            SettingsSearchTarget.Performance, "fps", "frame rate", "graphics", "renderer", "render", "texture", "MSAA", "lowest graphics", "client cpu", "gpu", "unavailable", "unsupported")
    ];

    private static readonly IReadOnlyList<SettingsSearchEntry> DefaultTopics = Array.AsReadOnly(Entries[..8]);
    private static readonly IndexedEntry[] Index = CreateIndex();

    /// <summary>
    /// Blank/whitespace queries return eight fixed topics. Invalid UTF-16, controls/format characters,
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
