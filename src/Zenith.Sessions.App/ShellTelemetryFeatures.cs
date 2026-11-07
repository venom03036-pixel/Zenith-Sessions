using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed partial class ShellViewModel
{
    private readonly TelemetryHistory _telemetryHistory=new();
    public TelemetryDisplay Telemetry {get;private set;}=null!;
    public GpuTelemetryDisplay GpuTelemetry { get; } = new();
    public Command ShowClientHistoryMode {get;private set;}=null!;
    private bool _clientHistoryMode;
    public bool IsClientHistoryMode=>_clientHistoryMode;
    private void InitializeTelemetry()
    {
        Telemetry=new(_telemetryHistory);
        ShowClientHistoryMode=new(_=>SetSessionPresentation(false,true));
    }
    private void RefreshTelemetryPresentation()
    {
        if(Telemetry is null || IsLocked || _shuttingDown || !IsOverview && !(IsSessions && IsClientHistoryMode)) return;
        var selected=SelectedSession?.Observation;
        SessionProcessKey? key=selected is {ProcessId:>0,ProcessStartUtcTicks:>0}?new(null,selected.ProcessId,selected.ProcessStartUtcTicks):null;
        Telemetry.Update(key,DateTimeOffset.UtcNow);
        GpuTelemetry.Update(Volatile.Read(ref _latestDesktop)?.Resources.Gpu, DateTimeOffset.UtcNow);
    }
}
