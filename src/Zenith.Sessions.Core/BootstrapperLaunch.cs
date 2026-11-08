namespace Zenith.Sessions.Core;

/// <summary>
/// Launching through an installed bootstrapper (Bloxstrap, Fishstrap, Froststrap or Voidstrap; R-launch-17, owner decision of
/// 2026-11-08). Zenith starts the bootstrapper's own executable with Roblox's launch request as its only argument; the
/// bootstrapper then starts the Roblox player and applies its own client settings and FFlags. Zenith never writes a client
/// settings or FFlag file itself. Off by default: a bootstrapper row becomes the trial installation only when the user
/// selects it and has accepted its publisher once for that product (<see cref="AcceptedBootstrapperPublisher"/>). The
/// executable must carry a valid Authenticode signature whose certificate thumbprint is the accepted one; an unsigned or
/// differently signed file is refused. Extra client arguments (R-launch-16) are never passed through a bootstrapper.
/// </summary>
public static class BootstrapperProducts
{
    /// <summary>The bootstrappers Zenith knows, each found by its own <c>&lt;Product&gt;.exe</c>; Roblox is the player, not one of them.</summary>
    public static IReadOnlyList<ClientInstallationProduct> All { get; } = Array.AsReadOnly(new[]
    { ClientInstallationProduct.Bloxstrap, ClientInstallationProduct.Fishstrap, ClientInstallationProduct.Froststrap, ClientInstallationProduct.Voidstrap });

    public static bool IsBootstrapper(ClientInstallationProduct product) => Enum.IsDefined(product) && product != ClientInstallationProduct.Roblox;

    /// <summary>The bootstrapper's own executable file name, such as <c>Bloxstrap.exe</c>; throws for the Roblox player.</summary>
    public static string ExecutableName(ClientInstallationProduct product)
        => IsBootstrapper(product) ? product + ".exe" : throw new ArgumentOutOfRangeException(nameof(product));

    /// <summary>True when <paramref name="path"/> names the bootstrapper's own executable (file name only, ignoring case); a player under its folder is not.</summary>
    public static bool IsOwnExecutable(ClientInstallationProduct product, string? path)
        => IsBootstrapper(product) && !string.IsNullOrEmpty(path) && Path.GetFileName(path).Equals(ExecutableName(product), StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a saved product name (case and surrounding spaces ignored); only the bootstrappers parse, never Roblox or an unknown name.</summary>
    public static bool TryParse(string? name, out ClientInstallationProduct product)
    {
        product = default;
        if (name is null) return false;
        ReadOnlySpan<char> text = name.AsSpan().Trim();
        foreach (var candidate in All)
            if (text.Equals(candidate.ToString(), StringComparison.OrdinalIgnoreCase)) { product = candidate; return true; }
        return false;
    }
}

/// <summary>
/// The publisher the user accepted once for one bootstrapper: the product, the SHA-256 thumbprint of the signing certificate
/// Windows verified, and the certificate's organization for display. Saved in settings.json (optional within schema 1); a
/// thumbprint identifies a public certificate and is not a secret. One entry per product.
/// </summary>
public sealed record AcceptedBootstrapperPublisher(string Product, string CertificateSha256, string Publisher)
{
    public const int MaximumPublisherCharacters = 256;

    public bool IsFor(ClientInstallationProduct product) => BootstrapperProducts.TryParse(Product, out var own) && own == product;

    /// <summary>True when the verified signer's certificate thumbprint is the accepted one (hex, ignoring case).</summary>
    public bool Matches(ClientExecutableSigner? signer)
        => signer is not null && IsThumbprint(signer.CertificateSha256) && string.Equals(signer.CertificateSha256, CertificateSha256, StringComparison.OrdinalIgnoreCase);

    public static bool IsThumbprint(string? value) => value is { Length: 64 } && value.AsSpan().IndexOfAnyExcept("0123456789ABCDEFabcdef") < 0;

    /// <summary>The canonical form: the product's enum name, an upper-case thumbprint and a trimmed publisher. Throws <see cref="InvalidDataException"/>.</summary>
    public AcceptedBootstrapperPublisher Validated()
    {
        if (!BootstrapperProducts.TryParse(Product, out var product)) throw new InvalidDataException("An accepted bootstrapper publisher names an unknown bootstrapper.");
        if (!IsThumbprint(CertificateSha256)) throw new InvalidDataException("An accepted bootstrapper publisher has an invalid certificate thumbprint.");
        string publisher = Publisher?.Trim() ?? "";
        if (publisher.Length is < 1 or > MaximumPublisherCharacters || publisher.Any(char.IsControl))
            throw new InvalidDataException("An accepted bootstrapper publisher has an invalid publisher name.");
        return new(product.ToString(), CertificateSha256.ToUpperInvariant(), publisher);
    }

    /// <summary>Validates every entry and refuses a second entry for the same product; null means none. Returns a read-only copy.</summary>
    public static IReadOnlyList<AcceptedBootstrapperPublisher> Validate(IReadOnlyList<AcceptedBootstrapperPublisher>? entries)
    {
        if (entries is null || entries.Count == 0) return Array.Empty<AcceptedBootstrapperPublisher>();
        if (entries.Count > BootstrapperProducts.All.Count) throw new InvalidDataException("Too many accepted bootstrapper publishers.");
        var validated = new AcceptedBootstrapperPublisher[entries.Count];
        for (int index = 0; index < validated.Length; index++)
        {
            var entry = entries[index] ?? throw new InvalidDataException("An accepted bootstrapper publisher is missing.");
            validated[index] = entry.Validated();
            for (int earlier = 0; earlier < index; earlier++)
                if (string.Equals(validated[earlier].Product, validated[index].Product, StringComparison.Ordinal))
                    throw new InvalidDataException("A bootstrapper has more than one accepted publisher.");
        }
        return Array.AsReadOnly(validated);
    }

    /// <summary>The accepted publisher for <paramref name="product"/>, or null when none is saved.</summary>
    public static AcceptedBootstrapperPublisher? For(IReadOnlyList<AcceptedBootstrapperPublisher>? entries, ClientInstallationProduct product)
    {
        if (entries is null) return null;
        foreach (var entry in entries) if (entry is not null && entry.IsFor(product)) return entry;
        return null;
    }

    public override string ToString() => $"AcceptedBootstrapperPublisher {{ Product = {Product}, Publisher = {Publisher}, CertificateSha256 = {CertificateSha256} }}";
}

/// <summary>
/// Reads the publisher the user accepted for a bootstrapper from saved state at verification time (never a draft). The App
/// implements it over the saved settings; a trust verifier without one accepts no bootstrapper, so launching through one
/// stays off. Called on the verifier's worker; a failure leaves that file's trust unknown and starts nothing.
/// </summary>
public interface IAcceptedBootstrapperPublisherSource
{
    AcceptedBootstrapperPublisher? Find(ClientInstallationProduct product);
}
