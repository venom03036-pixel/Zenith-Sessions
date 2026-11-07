using System.Windows.Media.Imaging;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>An observed tile, including observations which cannot form a capture identity.</summary>
public sealed class ClientOverviewRow : Observable
{
    private SessionObservation _observation;
    private bool _current;
    private bool _frameCurrent;
    private BitmapSource? _image;
    private DateTimeOffset? _imageTime;
    private long? _surfaceTime;
    private string _status="Previews are off.",_resolution="",_captured="",_age="No captured image.",_freshness="Unavailable",_rate="Refresh rate not measured.";
    internal ClientOverviewRow(SessionObservation observation) => _observation=observation;
    public int ProcessId=>_observation.ProcessId;
    public long ProcessStartUtcTicks=>_observation.ProcessStartUtcTicks;
    public nint Window=>_observation.Window;
    public string Client=>$"Client PID {ProcessId}";
    public ClientPreviewTarget? CaptureTarget=>ValidIdentity ? new(new(null,ProcessId,ProcessStartUtcTicks),Window) : null;
    private bool ValidIdentity=>new SessionProcessKey(null,ProcessId,ProcessStartUtcTicks).IsValid && Window!=0 && _observation.Problem is null;
    public bool CanFocus=>_current && ValidIdentity;
    public BitmapSource? Image=>_image;
    public bool HasImage=>_image is not null;
    public string Status=>_status;
    public string Resolution=>_resolution;
    public string Captured=>_captured;
    public string ImageAge=>_age;
    public string CompactAge=>_imageTime is not { } ? "—" : _ageSeconds<60?$"{_ageSeconds}s":$"{_ageSeconds/60}m";
    private long _ageSeconds;
    public string Freshness=>_freshness;
    public string RefreshRate=>_rate;
    public string AutomationName=>$"{Client}. {Status} {ImageAge} {Captured} Account unverified.";
    internal SessionObservation Observation=>_observation;
    internal DateTimeOffset? ImageTime=>_imageTime;
    internal bool CanCapture=>CanFocus && !_observation.Minimized && _observation.Visible;
    internal bool CanRetainLastKnown=>ValidIdentity && !_observation.Minimized && _observation.Visible;
    internal bool SameIdentity(SessionObservation value)=>ProcessId==value.ProcessId && ProcessStartUtcTicks==value.ProcessStartUtcTicks && Window==value.Window;
    internal void Observe(SessionObservation observation,bool current)
    {
        // Every sample re-reads time, memory and CPU. Those keep the pinned focus observation current
        // but feed no tile binding, so only identity, window state, problem and currency notify.
        var prior=_observation;
        bool changed=_current!=current || prior.ProcessId!=observation.ProcessId || prior.ProcessStartUtcTicks!=observation.ProcessStartUtcTicks
            || prior.Window!=observation.Window || prior.Minimized!=observation.Minimized || prior.Visible!=observation.Visible
            || prior.Problem!=observation.Problem;
        _observation=observation;_current=current;
        if(!CanCapture) Clear(UnavailableStatus());
        if(changed) {Raise(nameof(CaptureTarget));Raise(nameof(CanFocus));Raise(nameof(AutomationName));}
    }
    internal void MarkLastKnown()
    {
        const string message="Not reported in the partial scan; last-known observation.";
        if(!_current && !_frameCurrent && _status==message) return;
        _current=false;_frameCurrent=false;_status=message;if(HasImage) _freshness="Last-known image · not current";
        Raise(nameof(Status));Raise(nameof(Freshness));Raise(nameof(CanFocus));Raise(nameof(AutomationName));
    }
    /// <summary>Why Focus client refuses this tile. A current minimized or hidden window can still be focused, so only identity and currency matter.</summary>
    internal string FocusRefusal()=>ValidIdentity ? "Focus not sent. This tile is last-known; wait for a current process scan, then try again."
        : "Focus not sent. "+UnavailableStatus();
    internal string UnavailableStatus()=>_observation.Problem is not null || !new SessionProcessKey(null,ProcessId,ProcessStartUtcTicks).IsValid ? "Process identity is unavailable; no capture or focus is permitted."
        : Window==0 ? "No client window is available." : _observation.Minimized ? "Client is minimized; no current image is available."
        : !_observation.Visible ? "Client is hidden; no current image is available." : !_current ? "Last-known process observation; waiting for a current scan." : "Waiting for a window image.";
    internal void Clear(string status)
    {
        if(_image is null && _status==status && _imageTime is null) return;
        // Publish coherent fields before any notification can reenter Stop/Lock.
        _image=null;_imageTime=null;_surfaceTime=null;_frameCurrent=false;_status=status;_resolution="";_captured="";
        _age="No captured image.";_freshness="Unavailable";_rate="Refresh rate not measured.";
        NotifyImage();
    }
    internal void Publish(BitmapSource image,ClientPreviewFrame frame,string status,DateTimeOffset now,bool currentFrame)
    {
        bool current=currentFrame && _current;
        if(ReferenceEquals(_image,image) && _imageTime==frame.CapturedUtc && _status==status && _frameCurrent==current) {UpdateClock(now);return;}
        _frameCurrent=current;
        if(_surfaceTime!=frame.SurfaceTimestampTicks)
        {
            // WGC SystemRelativeTime is a compositor/QPC-relative TimeSpan: these
            // ticks are 100 ns units, not Stopwatch-frequency ticks or wall time.
            if(_surfaceTime is { } previous && previous>0 && frame.SurfaceTimestampTicks>previous)
            {
                double seconds=(frame.SurfaceTimestampTicks-previous)/(double)TimeSpan.TicksPerSecond;
                _rate=$"Measured refresh: {seconds:0.0} s between images ({1/seconds:0.00}/s).";
            }
            else _rate="Refresh rate not measured.";
            _surfaceTime=frame.SurfaceTimestampTicks;
        }
        _image=image;_imageTime=frame.CapturedUtc;_status=status;
        _resolution=$"{frame.Width} × {frame.Height} preview · source {frame.SourceWidth} × {frame.SourceHeight}";
        _captured="Captured "+frame.CapturedUtc.ToLocalTime().ToString("G")+" · account unverified";
        SetClock(now);NotifyImage();
    }
    internal void RetainLastKnown(DateTimeOffset now)
    {_frameCurrent=false;UpdateClock(now);}
    internal void UpdateClock(DateTimeOffset now)
    {
        if(_imageTime is not { } at) return;
        if(now-at>ClientPreviewLimits.MaximumOverviewFrameAge || at-now>ClientPreviewLimits.MaximumFutureSkew)
        {Clear("Last-known image expired. Waiting for a new capture.");return;}
        string age=_age,freshness=_freshness;SetClock(now);
        if(age!=_age) {Raise(nameof(ImageAge));Raise(nameof(CompactAge));}if(freshness!=_freshness) Raise(nameof(Freshness));
        if(age!=_age || freshness!=_freshness) Raise(nameof(AutomationName));
    }
    private void SetClock(DateTimeOffset now)
    {
        if(_imageTime is not { } at) return;
        _ageSeconds=Math.Max(0,(long)(now-at).TotalSeconds);_age=$"Captured {_ageSeconds} s ago.";
        _freshness=_frameCurrent && _current && now-at<=ClientPreviewLimits.MaximumFrameAge ? "Current image" : "Last-known image · not current";
    }
    private void NotifyImage()
    {
        foreach(string name in new[]{nameof(Image),nameof(HasImage),nameof(Status),nameof(Resolution),nameof(Captured),nameof(ImageAge),nameof(CompactAge),nameof(Freshness),nameof(RefreshRate),nameof(AutomationName)}) Raise(name);
    }
}
