using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>Dedicated candidate flow with its own actual browser owner; never starts a native client.</summary>
public sealed class OwnedRobloxNativeHandoffCapture(string dataRoot,Dispatcher dispatcher):IRobloxNativeHandoffCapture
{
    private readonly OwnedRobloxHandoffProbe _browser=new(dataRoot,dispatcher);
    public Task<NativeBrowserCapture> CaptureAsync(NativeHandoffContext context,RobloxSessionCredential credential,CancellationToken ct)
        =>_browser.CaptureNativeAsync(context,credential,ct);
    public RobloxHandoffProbeOutcome? Refusal=>_browser.Refusal;
    public ValueTask DisposeAsync()=>_browser.DisposeAsync();
}
