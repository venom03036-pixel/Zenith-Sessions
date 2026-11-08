using System.Text.Json.Serialization;

namespace Zenith.Sessions.Core;

/// <summary>Nonsecret reference to one exact protected record. Never a Roblox job ID.</summary>
public sealed record PrivateServerReference(Guid Id, long Revision)
{
    public bool IsValid => Id != Guid.Empty && Revision > 0;
    public override string ToString() => "Saved private server reference { Code = [protected] }";
}

/// <summary>
/// Owned reservation of a protected record through the one start boundary. The store keeps its
/// writer and file lease until disposal, so removal/replacement cannot race the selected code.
/// No code getter exists. No network work or polling is performed here.
/// </summary>
public sealed class PrivateServerLaunchLease : IDisposable
{
    private readonly object _gate = new();
    private ProtectedPrivateServerCode? _code;
    private Action? _release;
    public PrivateServerReference Reference { get; }
    public long PlaceId { get; }
    [JsonIgnore] public bool IsActive { get { lock (_gate) return _code is not null; } }
    public PrivateServerLaunchLease(PrivateServerReference reference, long placeId, ProtectedPrivateServerCode code, Action release)
    {
        ArgumentNullException.ThrowIfNull(reference); ArgumentNullException.ThrowIfNull(code); ArgumentNullException.ThrowIfNull(release);
        if (!reference.IsValid || placeId <= 0) throw new ArgumentException("A protected private-server lease needs an exact saved reference and place.");
        Reference = reference; PlaceId = placeId; _code = code; _release = release;
    }
    internal bool MatchesCode(string? value)
    {
        lock (_gate)
        {
            if (_code is null || value is null) return false;
            bool matches = false; _code.Reveal(span => matches = span.SequenceEqual(value.AsSpan())); return matches;
        }
    }
    internal string InitialPage()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_code is null, this);
            string page = "";
            _code.Reveal(span => page = "https://www.roblox.com/games/" + PlaceId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "?privateServerLinkCode=" + Uri.EscapeDataString(new string(span)));
            return page;
        }
    }
    internal bool MatchesPage(string address)
    {
        lock (_gate) return _code is not null && string.Equals(address, InitialPage(), StringComparison.Ordinal);
    }
    public void Dispose()
    {
        Action? release;
        lock (_gate) { _code?.Dispose(); _code = null; release = _release; _release = null; }
        release?.Invoke();
    }
    public override string ToString() => "Private server launch lease { Code = [protected] }";
}
