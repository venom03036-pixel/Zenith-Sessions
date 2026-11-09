using System.Globalization;

namespace Zenith.Sessions.Core;

/// <summary>
/// R-launch-0 (owner decision of 2026-11-08). One explicit native trial the user recorded as verified: the native launch mode it
/// used, the installation product it started through, when it was recorded, and the trial's request and process receipt whose
/// fresh Launch evidence reported the expected player in the requested place. Saved in settings.json (optional within schema 1);
/// it holds IDs, a time, a receipt and opaque executable digests, never sign-in material, a ticket or a path. While the saved launch mode and the
/// selected executable identity match an entry, the configured native provider reports Supported, so ordinary single and
/// group launch requests are recorded Queued and start through the same preparation, checks, pacing and stop fences as a trial,
/// with no review per account. One entry per mode and product. It is the evidence the user chose to accept, not proof that any
/// later client joined as its account; that stays with each client's own log reports.
/// </summary>
public sealed record NativeLaunchVerification(string Mode, string Product, DateTimeOffset VerifiedUtc, Guid OperationId,
    int ProcessId, long ProcessStartUtcTicks, long UserId, long PlaceId, NativeLaunchExecutableIdentity? Executable = null)
{
    private static readonly ClientInstallationProduct[] Products = Enum.GetValues<ClientInstallationProduct>();
    /// <summary>One entry for each launch mode and installation product at most.</summary>
    public static int MaximumEntries => 2 * Products.Length;

    /// <summary>Parses a saved product name (case and surrounding spaces ignored): the player or one of the known bootstrappers, never a number or a file name.</summary>
    public static bool TryParseProduct(string? name, out ClientInstallationProduct product)
    {
        product = default;
        if (name is null) return false;
        ReadOnlySpan<char> text = name.AsSpan().Trim();
        foreach (var candidate in Products)
            if (text.Equals(candidate.ToString(), StringComparison.OrdinalIgnoreCase)) { product = candidate; return true; }
        return false;
    }

    public bool IsFor(NativeLaunchMode mode, ClientInstallationProduct product)
        => NativeLaunchModes.TryParse(Mode, out var own) && own == mode && TryParseProduct(Product, out var ownProduct) && ownProduct == product;

    /// <summary>The canonical form: the mode's saved spelling, the product's enum name, a UTC time. Throws <see cref="InvalidDataException"/>.</summary>
    public NativeLaunchVerification Validated()
    {
        if (!NativeLaunchModes.TryParse(Mode, out var mode)) throw new InvalidDataException("A verified native launch names an unknown launch mode.");
        if (!TryParseProduct(Product, out var product)) throw new InvalidDataException("A verified native launch names an unknown installation product.");
        if (OperationId == Guid.Empty) throw new InvalidDataException("A verified native launch names no launch request.");
        // The same bounds as the queue's receipt, with the recording time as the receipt's recorded time: a client cannot be
        // verified before it started.
        if (VerifiedUtc == default || !new LaunchProcessReceipt(ProcessId, ProcessStartUtcTicks, VerifiedUtc).IsValid)
            throw new InvalidDataException("A verified native launch has an invalid time or process receipt.");
        if (UserId <= 0 || PlaceId <= 0) throw new InvalidDataException("A verified native launch has an invalid player or place.");
        Executable?.Validated(); // Legacy records remain readable but cannot enable ordinary launching.
        var utc = VerifiedUtc.ToUniversalTime();
        string canonicalMode = NativeLaunchModes.ToSettingsValue(mode), canonicalProduct = product.ToString();
        return canonicalMode == Mode && canonicalProduct == Product && utc == VerifiedUtc && utc.Offset == VerifiedUtc.Offset ? this
            : new(canonicalMode, canonicalProduct, utc, OperationId, ProcessId, ProcessStartUtcTicks, UserId, PlaceId, Executable);
    }

    /// <summary>Validates every entry and refuses a second entry for the same mode and product; null means none. Returns a read-only copy.</summary>
    public static IReadOnlyList<NativeLaunchVerification> Validate(IReadOnlyList<NativeLaunchVerification>? entries)
    {
        if (entries is null || entries.Count == 0) return Array.Empty<NativeLaunchVerification>();
        if (entries.Count > MaximumEntries) throw new InvalidDataException("Too many verified native launches.");
        // An already validated read-only list is returned as is, so a saved value that did not change is not copied on every read.
        var validated = new NativeLaunchVerification[entries.Count]; bool unchanged = entries is System.Collections.ObjectModel.ReadOnlyCollection<NativeLaunchVerification>;
        for (int index = 0; index < validated.Length; index++)
        {
            var entry = entries[index] ?? throw new InvalidDataException("A verified native launch is missing.");
            validated[index] = entry.Validated();
            if (!ReferenceEquals(validated[index], entry)) unchanged = false;
            for (int earlier = 0; earlier < index; earlier++)
                if (string.Equals(validated[earlier].Mode, validated[index].Mode, StringComparison.Ordinal)
                    && string.Equals(validated[earlier].Product, validated[index].Product, StringComparison.Ordinal))
                    throw new InvalidDataException("A launch mode and installation have more than one verified native launch.");
        }
        return unchanged ? entries : Array.AsReadOnly(validated);
    }

    /// <summary>The verification saved for <paramref name="mode"/> through <paramref name="product"/>, or null when none is saved.</summary>
    public static NativeLaunchVerification? For(IReadOnlyList<NativeLaunchVerification>? entries, NativeLaunchMode mode, ClientInstallationProduct product)
    {
        if (entries is null) return null;
        foreach (var entry in entries) if (entry is not null && entry.IsFor(mode, product)) return entry;
        return null;
    }

    /// <summary>
    /// The evidence gate. True only for a request whose client was reported started (a valid receipt, still <c>Client started · unverified</c>)
    /// and whose current Launch evidence, for that same request and process, shows the exact process observed now and the client's own
    /// log reporting the expected player in the requested place (a reported match, not a mismatch, not unknown). Stale, reused, ambiguous
    /// or uncorrelated evidence, a different process or a request that ended never qualify.
    /// </summary>
    public static bool CanRecord(LaunchOperation? started, NativeTrialEvidenceView? evidence)
    {
        if (started is not { State: LaunchState.ClientStartedUnverified, ProcessReceipt: { IsValid: true } receipt } || evidence is null
            || started.Intent.PrivateServer is not null) return false;
        var intent = started.Intent;
        if (intent.UserId <= 0 || intent.PlaceId <= 0 || evidence.OperationId != intent.OperationId) return false;
        if (evidence.ProcessReceipt is not { } seen || seen.ProcessId != receipt.ProcessId || seen.ProcessStartUtcTicks != receipt.ProcessStartUtcTicks) return false;
        if (evidence.Presence != NativeTrialProcessPresence.Observed || evidence.Outcome != NativeTrialEvidenceOutcome.ReportedMatchUnverified) return false;
        if (evidence.ExpectedUserId != intent.UserId || evidence.RequestedPlaceId != intent.PlaceId) return false;
        return evidence.ReportedUser is { Status: NativeTrialReportedStatus.ReportedMatchUnverified } user && user.UserId == intent.UserId
            && evidence.ReportedDestination is { PlaceStatus: NativeTrialReportedStatus.ReportedMatchUnverified } destination && destination.PlaceId == intent.PlaceId;
    }

    /// <summary>The entry for a trial that passes <see cref="CanRecord"/>, recorded at <paramref name="now"/>; throws <see cref="ArgumentException"/> otherwise.</summary>
    public static NativeLaunchVerification Record(NativeLaunchMode mode, ClientInstallationProduct product, LaunchOperation? started,
        NativeTrialEvidenceView? evidence, DateTimeOffset now, NativeLaunchExecutableIdentity? executable = null)
    {
        if (!CanRecord(started, evidence)) throw new ArgumentException("Only a started trial whose fresh evidence reports the expected player in the requested place can be recorded as verified.", nameof(evidence));
        if (product != ClientInstallationProduct.Roblox || executable is null)
            throw new ArgumentException("Only a standard-player trial with its exact held executable identity can be recorded.", nameof(executable));
        var receipt = started!.ProcessReceipt!;
        return new NativeLaunchVerification(NativeLaunchModes.ToSettingsValue(mode), product.ToString(), now, started.Intent.OperationId,
            receipt.ProcessId, receipt.ProcessStartUtcTicks, started.Intent.UserId, started.Intent.PlaceId, executable).Validated();
    }

    /// <summary>The user-facing label of the saved mode, or the saved text when it does not parse. Display only; the file holds <see cref="Mode"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ModeLabel => NativeLaunchModes.TryParse(Mode, out var mode) ? NativeLaunchModes.Label(mode) : Mode;

    /// <summary>What was recorded, in plain words: when, which mode through which product, and the trial's reported facts.</summary>
    public string Describe() => "a trial recorded as verified on " + VerifiedUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
        + " for " + ModeLabel + " through " + Product + ", whose client (PID " + ProcessId.ToString(CultureInfo.InvariantCulture) + ") reported the expected player "
        + UserId.ToString(CultureInfo.InvariantCulture) + " in place " + PlaceId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The Supported capability's explanation: what ordinary requests now do and what is still not proven.</summary>
    public string ExplainSupported() => "Ordinary and group launch requests use the selected installation, with no review per account, because of "
        + Describe() + ". Each request still passes the publisher check, the saved sign-in verification, account binding, resource headroom, launch pacing, "
        + "Panic and Lock, and one start at a time; a started client's account and arrival are still confirmed only by its own log reports.";
}

/// <summary>
/// Reads the verified trial for the saved native launch mode and an installation product from saved state when the configured
/// native provider's capability is read (never a draft). The App implements it over the saved settings; a provider without one, or
/// whose source reports none or fails, stays Unverified, so ordinary launching stays off.
/// </summary>
public interface INativeLaunchVerificationSource
{
    NativeLaunchVerification? Find(ClientInstallationProduct product);
    /// <summary>The committed mode, never an unsaved UI draft. Unknown sources retain the recorded-trial policy.</summary>
    NativeLaunchMode? LaunchMode => null;
}
