using System.Diagnostics;
using System.Globalization;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

public sealed record PerformanceProfileChoice(ManagerPerformanceProfile Profile,string Name);

public sealed partial class ShellViewModel
{
    private ManagerPerformanceProfile _draftPerformanceProfile=ManagerPerformanceProfile.Balanced;
    private Guid? _draftMainAccount;
    private string _mainAccountDisplay="No main account selected";
    private string _customProcessSample="1000",_customPresentation="1000",_customStagger="2000",_customStabilization="20000";
    private ManagerPerformancePolicy _activePerformance=new ManagerPerformanceSettings().Resolve();
    private readonly SemaphoreSlim _sampleSerial=new(1,1);
    private ResourceSnapshot? _pressureObserved;
    // Written by the serialized sampler on the pool; dispatcher readers use Volatile.Read.
    private PressureDecision _lastPressure=new(true,"Waiting for resource observations.");
    private long? _presentationAt;
    public IReadOnlyList<PerformanceProfileChoice> PerformanceProfiles {get;}=Enum.GetValues<ManagerPerformanceProfile>()
        .Select(p=>new PerformanceProfileChoice(p,new ManagerPerformanceSettings(p).Resolve().DisplayName)).ToArray();
    public PerformanceProfileChoice SelectedPerformanceProfile
    {
        get=>PerformanceProfiles.First(p=>p.Profile==_draftPerformanceProfile);
        set
        {
            if(value is null || !PerformanceProfiles.Contains(value) || !Set(ref _draftPerformanceProfile,value.Profile)) return;
            Raise(nameof(SelectedPerformanceProfile));RaisePerformanceDraft();PreferencesEdited();
        }
    }
    public string CustomProcessSample {get=>_customProcessSample;set {if(Set(ref _customProcessSample,value)) {RaisePerformanceDraft();PreferencesEdited();}}}
    public string CustomPresentation {get=>_customPresentation;set {if(Set(ref _customPresentation,value)) {RaisePerformanceDraft();PreferencesEdited();}}}
    public string CustomStagger {get=>_customStagger;set {if(Set(ref _customStagger,value)) {RaisePerformanceDraft();PreferencesEdited();}}}
    public string CustomStabilization {get=>_customStabilization;set {if(Set(ref _customStabilization,value)) {RaisePerformanceDraft();PreferencesEdited();}}}
    public bool IsCustomPerformance=>_draftPerformanceProfile==ManagerPerformanceProfile.Custom;
    public Guid? MainAccountProfileId=>_draftMainAccount;
    public string MainAccountDisplay {get=>_mainAccountDisplay;private set=>Set(ref _mainAccountDisplay,value);}
    public string PerformanceExplanation=>TryReadPerformanceDraft(out var settings)?settings.Resolve().Explanation:"Enter valid timing values in the displayed ranges before saving.";
    public string PerformancePendingCapabilities=>_activePerformance.PendingCapabilities;
    public string PerformanceMonitorStatus=>_monitor is IConfigurableDesktopMonitor?"Process-detail cadence is supported by this monitor.":"This monitor cannot change process-detail cadence. The saved setting is retained for a compatible monitor.";
    public string ActivePerformanceName=>_activePerformance.DisplayName+(_activePerformance.NeedsMainAccount?" · choose a main account":"");
    public string ActivePerformanceSummary=>$"{ActivePerformanceName} · process details {_activePerformance.ProcessSampleMs} ms · dashboard at most {_activePerformance.PresentationMs} ms · safety 1000 ms";
    public Command SetSelectedAsMain {get;private set;}=null!;
    public Command ClearMainAccount {get;private set;}=null!;
    public Command ResetCustomPerformance {get;private set;}=null!;
    private void InitializePerformanceFeatures()
    {
        SetSelectedAsMain=new(_=>
        {
            if(!_ready || IsLocked || SelectedAccount is not { } row || !Accounts.Contains(row)) return;
            _draftMainAccount=row.Profile.Id;MainAccountDisplay=row.Label;Raise(nameof(MainAccountProfileId));
            RaisePerformanceDraft();PreferencesEdited();Notice="Main account selected. Save settings to commit it; this does not verify a running client or change an active launch.";
        });
        ClearMainAccount=new(_=>
        {
            if(!_ready || IsLocked) return;
            _draftMainAccount=null;MainAccountDisplay="No main account selected";Raise(nameof(MainAccountProfileId));
            RaisePerformanceDraft();PreferencesEdited();
        });
        ResetCustomPerformance=new(_=>
        {
            if(!_ready || IsLocked) return;
            CustomProcessSample="1000";CustomPresentation="1000";CustomStagger="2000";CustomStabilization="20000";
        });
    }
    private void LoadPerformanceDraft(ManagerPerformanceSettings settings)
    {
        settings.Validated();_draftPerformanceProfile=settings.Profile;_draftMainAccount=settings.MainAccountProfileId;
        _customProcessSample=settings.CustomProcessSampleMs.ToString(CultureInfo.CurrentCulture);
        _customPresentation=settings.CustomPresentationMs.ToString(CultureInfo.CurrentCulture);
        _customStagger=settings.CustomStaggerMs.ToString(CultureInfo.CurrentCulture);
        _customStabilization=settings.CustomStabilizationMs.ToString(CultureInfo.CurrentCulture);
        foreach(string property in new[]{nameof(SelectedPerformanceProfile),nameof(MainAccountProfileId),nameof(CustomProcessSample),nameof(CustomPresentation),nameof(CustomStagger),nameof(CustomStabilization)}) Raise(property);
        RaisePerformanceDraft();
    }
    private void RaisePerformanceDraft() {Raise(nameof(IsCustomPerformance));Raise(nameof(PerformanceExplanation));Raise(nameof(PerformanceReviewSummary));}
    private ManagerPerformanceSettings ReadPerformanceDraft()
    {
        static int Number(string text,string label)
        {
            if(!int.TryParse(text,NumberStyles.Integer,CultureInfo.CurrentCulture,out int value)) throw new ArgumentException($"{label} must be a whole number of milliseconds.");
            return value;
        }
        return new ManagerPerformanceSettings(_draftPerformanceProfile,_draftMainAccount,
            Number(CustomProcessSample,"Process detail refresh"),Number(CustomPresentation,"Dashboard refresh"),
            Number(CustomStagger,"Launch spacing"),Number(CustomStabilization,"Launch stabilization")).Validated();
    }
    private bool TryReadPerformanceDraft(out ManagerPerformanceSettings settings)
    {
        try {settings=ReadPerformanceDraft();return true;}
        catch(ArgumentException) {settings=new();return false;}
    }
    private async Task RefreshMainAccountDisplayAsync(IReadOnlyList<AccountRow> rows,int generation,CancellationToken ct)
    {
        Guid? expected=_draftMainAccount;
        if(expected is null) {MainAccountDisplay="No main account selected";return;}
        var profile=await Task.Run(()=>_accounts.FindAsync(expected.Value,ct),ct);
        if(expected!=_draftMainAccount || generation!=_queryGeneration || ct.IsCancellationRequested) return;
        MainAccountDisplay=profile is null?"Saved main account is unavailable — choose another profile":
            rows.FirstOrDefault(a=>a.Profile.Id==expected)?.Label??(profile.Username.Length>0?"@"+profile.Username:profile.Label);
    }
    private void ApplyPerformance(ManagerPerformanceSettings settings)
    {
        var policy=settings.Resolve();
        if(_monitor is IConfigurableDesktopMonitor configurable) configurable.ConfigurePerformance(policy);
        _launchQueue.ConfigurePerformance(policy);Volatile.Write(ref _activePerformance,policy);
        Raise(nameof(ActivePerformanceName));Raise(nameof(ActivePerformanceSummary));Raise(nameof(PerformancePendingCapabilities));
    }
    private bool PresentationDue(ManagerPerformancePolicy policy,bool urgent)
    {
        long at=Stopwatch.GetTimestamp();
        if(!urgent && _presentationAt is { } prior && Stopwatch.GetElapsedTime(prior,at)<TimeSpan.FromMilliseconds(policy.PresentationMs)) return false;
        _presentationAt=at;return true;
    }
}
