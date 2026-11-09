using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <summary>
/// The one capture the native provider is composed with, delegating each preparation to the capture of the saved
/// <see cref="NativeLaunchMode"/> (R-api-7): the official-page browser by default, or the direct ticket request. The mode
/// is read once per call, so a change while a preparation runs never switches owners mid-flight. Owns both captures.
/// </summary>
public sealed class SelectableNativeHandoffCapture : IRobloxNativeHandoffCapture
{
    private readonly IRobloxNativeHandoffCapture _officialPage, _directTicket;
    private int _mode = (int)NativeLaunchModes.Default;

    public SelectableNativeHandoffCapture(IRobloxNativeHandoffCapture officialPage, IRobloxNativeHandoffCapture directTicket)
    {
        _officialPage = officialPage ?? throw new ArgumentNullException(nameof(officialPage));
        _directTicket = directTicket ?? throw new ArgumentNullException(nameof(directTicket));
    }

    public NativeLaunchMode Mode
    {
        get => (NativeLaunchMode)Volatile.Read(ref _mode);
        set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); Volatile.Write(ref _mode, (int)value); }
    }
    private IRobloxNativeHandoffCapture Current => Mode == NativeLaunchMode.DirectTicket ? _directTicket : _officialPage;

    public RobloxHandoffProbeOutcome? Refusal => Current.Refusal;
    public Task<NativeBrowserCapture> CaptureAsync(NativeHandoffContext context, RobloxSessionCredential credential, CancellationToken ct)
        => Current.CaptureAsync(context, credential, ct);
    public async ValueTask DisposeAsync()
    {
        try { await _officialPage.DisposeAsync().ConfigureAwait(false); }
        finally { await _directTicket.DisposeAsync().ConfigureAwait(false); }
    }
    public override string ToString() => "SelectableNativeHandoffCapture { Mode = " + Mode + " }";
}
