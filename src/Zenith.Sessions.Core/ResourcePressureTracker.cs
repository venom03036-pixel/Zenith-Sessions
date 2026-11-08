namespace Zenith.Sessions.Core;

public sealed record PressureDecision(bool Held,string Explanation);

/// <summary>One monotonic observation sequence. Short spikes do not establish recovery or sustained load.</summary>
public sealed class ResourcePressureTracker
{
    private DateTimeOffset? _last,_cpuHighSince,_healthySince,_gpuObserved,_gpuHighSince;
    // False until one coherent sample is measured: a cold start (Windows CPU has no baseline yet) has lost nothing,
    // so it holds that sample only and does not latch the thirty-second recovery hysteresis.
    // _cpuKnown/_gpuKnown stay false until a measured reading below the hold threshold: before that the tracker has
    // no history, so high load may already be sustained. It holds without latching and releases once the load drops;
    // fifteen seconds of continued high load latches the normal sustained hold.
    private bool _held,_gpuHeld,_measured,_cpuKnown,_gpuKnown;
    public PressureDecision Observe(ResourceSnapshot sample,CapacityDecision memory,DateTimeOffset now)
    {
        bool fresh=memory.SamplesHealthy && sample.CpuPercent is >=0 and <=100 && sample.ObservedUtc<=now.AddSeconds(1);
        bool discontinuity=_last is { } previous && (sample.ObservedUtc<=previous || sample.ObservedUtc-previous>CapacityPolicy.ResourceSampleExpiry);
        if(discontinuity || !fresh) { _cpuHighSince=null; _healthySince=null; _gpuHighSince=null; _gpuObserved=null; }
        if(!fresh || discontinuity)
        { _held|=_measured; _last=sample.ObservedUtc; return new(true,"Resource admission held: waiting for coherent memory, commit and CPU measurements."); }
        _last=sample.ObservedUtc; _measured=true;
        if(sample.AvailablePhysical<Math.Max(CapacityPolicy.GiB,sample.TotalPhysical/20)
            || (UInt128)sample.CommitUsed*100>(UInt128)sample.CommitLimit*95)
        { _held=true; _healthySince=null; return new(true,"Critical memory pressure: new launches are held immediately. Existing clients remain under user control."); }
        if(memory.MemoryOnlySlots==0)
        { _held=true; _healthySince=null; return new(true,"Resource admission held: physical memory, commit headroom or your client limit is exhausted."); }
        var gpu=ReadReliableGpu(sample.Gpu,now);
        if(gpu is { } observed)
        {
            if(_gpuObserved is { } prior && (observed.At<prior || observed.At-prior>TimeSpan.FromSeconds(3))) _gpuHighSince=null;
            if(observed.Percent>=85)
            {
                if(_gpuObserved!=observed.At) _gpuHighSince??=observed.At;
                if(_gpuHighSince is { } since && observed.At-since>=TimeSpan.FromSeconds(15)) _gpuHeld=true;
            }
            else { _gpuHighSince=null; _gpuKnown=true; }
            _gpuObserved=observed.At;
        }
        else { _gpuHighSince=null; _gpuObserved=null; }
        if(_gpuHeld) _held=true;
        bool gpuUnknownHigh=gpu is {Percent:>=85} && !_gpuKnown;
        if(sample.CpuPercent>=80)
        {
            _healthySince=null; _cpuHighSince??=sample.ObservedUtc;
            if(sample.ObservedUtc-_cpuHighSince>=TimeSpan.FromSeconds(15)) _held=true;
            if(_held) return new(true,"Resource admission held: CPU or established GPU pressure has not recovered.");
            if(!_cpuKnown) return new(true,"CPU load is at 80% or above and Zenith has no earlier reading to show it is only a brief spike. New launches wait until it drops below 80%.");
            if(gpuUnknownHigh) return GpuUnknownHigh();
            return new(false,"CPU load is elevated. Measuring whether it remains above 80% for fifteen seconds.");
        }
        _cpuHighSince=null; _cpuKnown=true;
        if(gpu is {Percent:>=85})
        {
            _healthySince=null;
            if(_gpuHeld) _held=true;
            if(_held) return new(true,"Resource admission held: a measured GPU engine remains at 85% or above.");
            return gpuUnknownHigh?GpuUnknownHigh()
                :new(false,"A GPU engine is elevated. Measuring whether it remains at 85% for fifteen seconds.");
        }
        if(_gpuHeld && (gpu is null || !gpu.Value.AllMeasured))
        {
            _held=true; _healthySince=null;
            return new(true,"Resource admission held: previously high GPU load cannot be rechecked. Waiting for fresh reliable GPU evidence.");
        }
        if(_held)
        {
            if(sample.CpuPercent>=65 || gpu is {Percent:>=70} || !RecoveryHeadroom(sample,memory))
            {
                _healthySince=null;
                return new(true,"Resource admission recovering: require CPU below 65%, measured GPU below 70%, and memory/commit recovery headroom for thirty continuous seconds.");
            }
            _healthySince??=sample.ObservedUtc;
            var remaining=TimeSpan.FromSeconds(30)-(sample.ObservedUtc-_healthySince.Value);
            if(remaining>TimeSpan.Zero) return new(true,$"Resource admission recovering: {Math.Ceiling(remaining.TotalSeconds):0} healthy seconds remaining.");
            _held=false; _gpuHeld=false; _healthySince=null;
        }
        return new(false,"Resource samples are healthy. Provider eligibility and verified session identity are still required.");
    }

    private static PressureDecision GpuUnknownHigh()
        =>new(true,"A GPU engine is at 85% or above and Zenith has no earlier reading to show it is only a brief spike. New launches wait until it drops below 85%.");

    private static bool RecoveryHeadroom(ResourceSnapshot sample,CapacityDecision memory)
    {
        static ulong Sum(ulong a,ulong b)=>a>ulong.MaxValue-b?ulong.MaxValue:a+b;
        ulong physicalBase=Sum(memory.PhysicalHeadroom,memory.ReservedPhysical);
        ulong commitBase=Sum(memory.CommitHeadroom,memory.ReservedCommit);
        // At most 512 MiB or 5% of the space above the configured guard/reservations.
        // The margin never asks a small machine to recover beyond its usable capacity.
        ulong physicalMargin=Math.Min(CapacityPolicy.GiB/2,sample.TotalPhysical>physicalBase?(sample.TotalPhysical-physicalBase)/20:0);
        ulong commitMargin=Math.Min(CapacityPolicy.GiB/2,sample.CommitLimit>commitBase?(sample.CommitLimit-commitBase)/20:0);
        return sample.AvailablePhysical>Sum(physicalBase,physicalMargin)
            && sample.CommitLimit-sample.CommitUsed>Sum(commitBase,commitMargin);
    }

    private static (DateTimeOffset At,double Percent,bool AllMeasured)? ReadReliableGpu(GpuTelemetrySnapshot? gpu,DateTimeOffset now)
    {
        if(gpu?.IsFresh(now)!=true || gpu.ObservedUtc is not { } at) return null;
        double? busiest=null; bool allMeasured=gpu.Adapters.Count>0;
        foreach(var adapter in gpu.Adapters)
            if(adapter.EngineDataComplete && adapter.BusiestEnginePercent is { } percent && double.IsFinite(percent) && percent is >=0 and <=100)
                busiest=busiest is { } prior?Math.Max(prior,percent):percent;
            else allMeasured=false;
        return busiest is { } value?(at,value,allMeasured):null;
    }
}
