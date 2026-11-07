using System.Globalization;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>Projects only system and selected-client snapshots, at the existing UI cadence.</summary>
public sealed class TelemetryDisplay(TelemetryHistory history) : Observable
{
    private SystemTelemetrySnapshot? _system;
    private ClientTelemetrySnapshot? _client;
    private SessionProcessKey? _selected;
    private string _systemStatus="Waiting for actual measurements.",_clientStatus="Select an observed client to see its measured history.",_clientIoStatus="No process I/O measurement yet.";
    private string _readValue="Unavailable",_writeValue="Unavailable",_otherValue="Unavailable",_clientOperations="Operation rates unavailable";
    public IReadOnlyList<ResourceChartPoint> PhysicalPoints {get;private set;}=[];
    public IReadOnlyList<ResourceChartPoint> CommitPoints {get;private set;}=[];
    public IReadOnlyList<ResourceChartPoint> CpuPoints {get;private set;}=[];
    public IReadOnlyList<ResourceChartPoint> ClientCpuPoints {get;private set;}=[];
    public IReadOnlyList<ResourceChartPoint> ClientWorkingPoints {get;private set;}=[];
    public IReadOnlyList<ResourceChartPoint> ClientCommitPoints {get;private set;}=[];
    public IReadOnlyList<ResourceChartPoint> ReadPoints {get;private set;}=[];
    public IReadOnlyList<ResourceChartPoint> WritePoints {get;private set;}=[];
    public IReadOnlyList<ResourceChartPoint> OtherPoints {get;private set;}=[];
    public double PhysicalCeiling {get;private set;}=1;
    public double CommitCeiling {get;private set;}=1;
    public double ClientMemoryCeiling {get;private set;}=1;
    public double IoCeiling {get;private set;}=1;
    public string PhysicalRange=>$"0–{PhysicalCeiling:0.#} GiB · measured available RAM";
    public string CommitRange=>$"0–{CommitCeiling:0.#} GiB · system commitment";
    public string ClientMemoryRange=>$"0–{ClientMemoryCeiling:0.##} GiB · shared chart scale";
    public string IoRange=>$"0–{IoCeiling:0.##} KiB/s · shared chart scale";
    public string ClientCpuValue {get;private set;}="Unavailable";
    public string ClientWorkingValue {get;private set;}="Unavailable";
    public string ClientCommitValue {get;private set;}="Unavailable";
    public string ReadValue {get=>_readValue;private set=>Set(ref _readValue,value);}
    public string WriteValue {get=>_writeValue;private set=>Set(ref _writeValue,value);}
    public string OtherValue {get=>_otherValue;private set=>Set(ref _otherValue,value);}
    public string ClientFaults {get;private set;}="Page-fault rate unavailable";
    public string ClientOperations {get=>_clientOperations;private set=>Set(ref _clientOperations,value);}
    public string SystemStatus {get=>_systemStatus;private set=>Set(ref _systemStatus,value);}
    public string ClientStatus {get=>_clientStatus;private set=>Set(ref _clientStatus,value);}
    public string ClientIoStatus {get=>_clientIoStatus;private set=>Set(ref _clientIoStatus,value);}

    public void Update(SessionProcessKey? selected,DateTimeOffset now)
    {
        var system=history.GetSystemSnapshot();
        if(!ReferenceEquals(system,_system))
        {
            _system=system;
            PhysicalPoints=Array.AsReadOnly(system.Points.Select(point=>new ResourceChartPoint(point.ObservedUtc,
                point.TotalPhysicalBytes is { } total && point.UsedPhysicalBytes is { } used && total>=used?(total-used)/(double)CapacityPolicy.GiB:null,point.IsGap)).ToArray());
            CommitPoints=Array.AsReadOnly(system.Points.Select(point=>new ResourceChartPoint(point.ObservedUtc,point.CommitUsedBytes/(double?)CapacityPolicy.GiB,point.IsGap)).ToArray());
            CpuPoints=Array.AsReadOnly(system.Points.Select(point=>new ResourceChartPoint(point.ObservedUtc,point.CpuPercent,point.IsGap)).ToArray());
            PhysicalCeiling=system.Points.Count==0?1:Math.Max(1,system.Points.Max(point=>point.TotalPhysicalBytes??0)/(double)CapacityPolicy.GiB);
            CommitCeiling=system.Points.Count==0?1:Math.Max(1,system.Points.Max(point=>point.CommitLimitBytes??0)/(double)CapacityPolicy.GiB);
            foreach(var name in new[]{nameof(PhysicalPoints),nameof(CommitPoints),nameof(CpuPoints),nameof(PhysicalCeiling),nameof(CommitCeiling),nameof(PhysicalRange),nameof(CommitRange)}) Raise(name);
        }
        SystemStatus=system.Points.LastOrDefault() is { } last
            ? $"Last system sample {last.ObservedUtc.ToLocalTime():T} · {(Fresh(last.ObservedUtc,now,TelemetryHistory.MaximumSystemAge)&&!last.IsGap?"measured":"last-known/unavailable")} · up to 120 samples / 2 minutes. Gaps are unknown."
            :"Waiting for actual system measurements. No values have been invented.";
        var client=selected is { } process?history.GetClientSnapshot(process):null;
        if(selected!=_selected || !ReferenceEquals(client,_client))
        {
            _selected=selected;_client=client;
            var points=client?.Points??Array.Empty<ClientTelemetryPoint>();
            ClientCpuPoints=Project(points,point=>point.CpuPercent);
            ClientWorkingPoints=Project(points,point=>point.WorkingSetBytes/(double?)CapacityPolicy.GiB);
            ClientCommitPoints=Project(points,point=>point.PrivateCommitBytes/(double?)CapacityPolicy.GiB);
            ReadPoints=Project(points,point=>point.IoRates?.ReadBytesPerSecond/1024,true);
            WritePoints=Project(points,point=>point.IoRates?.WriteBytesPerSecond/1024,true);
            OtherPoints=Project(points,point=>point.IoRates?.OtherBytesPerSecond/1024,true);
            ClientMemoryCeiling=Ceiling(ClientWorkingPoints,ClientCommitPoints);
            IoCeiling=Ceiling(ReadPoints,WritePoints,OtherPoints);
            var latest=points.LastOrDefault();
            ClientCpuValue=Number(latest?.CpuPercent,"0.0","%");
            ClientWorkingValue=Number(latest?.WorkingSetBytes/(double?)CapacityPolicy.GiB,"0.00"," GiB");
            ClientCommitValue=Number(latest?.PrivateCommitBytes/(double?)CapacityPolicy.GiB,"0.00"," GiB");
            ClientFaults="Page faults · "+Number(latest?.PageFaultsPerSecond,"0.0","/s")+" · soft + hard combined";
            foreach(var name in new[]{nameof(ClientCpuPoints),nameof(ClientWorkingPoints),nameof(ClientCommitPoints),nameof(ReadPoints),nameof(WritePoints),nameof(OtherPoints),nameof(ClientMemoryCeiling),nameof(IoCeiling),nameof(ClientMemoryRange),nameof(IoRange),nameof(ClientCpuValue),nameof(ClientWorkingValue),nameof(ClientCommitValue),nameof(ClientFaults)}) Raise(name);
        }
        ClientStatus=client?.Points.LastOrDefault() is { } measured
            ? $"Client PID {client.Process.ProcessId} · last sample {measured.ObservedUtc.ToLocalTime():T} · {(Fresh(measured.ObservedUtc,now,TelemetryHistory.MaximumClientAge)&&!measured.IsGap?"measured":"last-known/unavailable")} · account unverified"
            :selected is null?"Select an observed client to see its measured history.":"This process instance has no accepted measurements yet.";
        ClientIoStatus=client?.Points.LastOrDefault()?.IoRates is { } lastIo
            ? $"Last process I/O sample {lastIo.ObservedUtc.ToLocalTime():T} · {(Fresh(lastIo.ObservedUtc,now,TelemetryHistory.MaximumClientAge)?"measured":"last-known/unavailable")}" : "No fresh process I/O rate was accepted for this sample.";
        // The row can still be fresh while its independently timed I/O reading expires.
        // Age headline values on every existing presentation tick, without rebuilding historical series.
        ProcessIoRates? freshIo=client?.Points.LastOrDefault() is {IsGap:false,IoRates:{ } acceptedIo}
            && Fresh(acceptedIo.ObservedUtc,now,TelemetryHistory.MaximumClientAge)?acceptedIo:null;
        ReadValue=Number(freshIo?.ReadBytesPerSecond/1024,"0.0"," KiB/s");
        WriteValue=Number(freshIo?.WriteBytesPerSecond/1024,"0.0"," KiB/s");
        OtherValue=Number(freshIo?.OtherBytesPerSecond/1024,"0.0"," KiB/s");
        ClientOperations="Operations/s · read "+Number(freshIo?.ReadOperationsPerSecond,"0.0","")
            +" · write "+Number(freshIo?.WriteOperationsPerSecond,"0.0","")+" · other "+Number(freshIo?.OtherOperationsPerSecond,"0.0","");
    }
    private static IReadOnlyList<ResourceChartPoint> Project(IReadOnlyList<ClientTelemetryPoint> points,Func<ClientTelemetryPoint,double?> value,bool io=false)
        =>Array.AsReadOnly(points.Select(point=>new ResourceChartPoint(io?point.IoRates?.ObservedUtc??point.ObservedUtc:point.ObservedUtc,value(point),point.IsGap)).ToArray());
    private static double Ceiling(params IReadOnlyList<ResourceChartPoint>[] series)
    {
        double max=0;
        foreach(var points in series) foreach(var point in points)
            if(!point.IsGap && point.Value is { } value && double.IsFinite(value)&&value>=0) max=Math.Max(max,value);
        return Math.Max(1,max);
    }
    internal static string Number(double? value,string format,string suffix)
        =>value is { } number && double.IsFinite(number)&&number>=0?number.ToString(format,CultureInfo.CurrentCulture)+suffix:"Unavailable";
    private static bool Fresh(DateTimeOffset time,DateTimeOffset now,TimeSpan maximum)
        =>time!=default&&time-now<=TimeSpan.FromMilliseconds(250)&&now-time<=maximum;
}
