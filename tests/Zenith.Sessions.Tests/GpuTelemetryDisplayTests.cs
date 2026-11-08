using System.Globalization;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class GpuTelemetryDisplayTests
{
    private static GpuTelemetrySnapshot Sample(DateTimeOffset at,double? percent=30,bool complete=true)
        =>new(at,[new("0x00000000_0x00000001",0,[new(0,"3D",percent)],percent,CapacityPolicy.GiB,0,complete)]);
    [Fact]
    public void FreshCountersDistinguishMeasuredZeroAndUnavailableAndAgeWithoutAnotherSample()
    {
        var at=DateTimeOffset.UtcNow;var source=Sample(at);var display=new GpuTelemetryDisplay();
        display.Update(source,at);var row=Assert.Single(display.Adapters);
        Assert.Contains("30",row.Engine);Assert.Contains("1.00",row.Dedicated);Assert.Contains("0.00",row.Shared);Assert.Contains("0x00000000_0x00000001",row.Identity);
        var rows=display.Adapters;display.Update(source,at.AddSeconds(1));Assert.Same(rows,display.Adapters);
        display.Update(source,at.AddSeconds(4));row=Assert.Single(display.Adapters);
        Assert.Contains("unavailable",row.Engine);Assert.Contains("Unavailable",row.Dedicated);Assert.Contains("expired",row.Status);
        Assert.Contains("stale",display.Status);
    }
    [Theory]
    [InlineData(double.NaN,true)] [InlineData(101,true)] [InlineData(90,false)]
    public void InvalidOrPartialEngineIsNeverPresentedAsNumeric(double percent,bool complete)
    {
        var at=DateTimeOffset.UtcNow;var display=new GpuTelemetryDisplay();display.Update(Sample(at,percent,complete),at);
        Assert.Equal("Engine load unavailable",Assert.Single(display.Adapters).Engine);
    }
    [Fact]
    public void MissingGpuIsUnknownAndIndependentAdaptersAreNeverAdded()
    {
        var at=DateTimeOffset.UtcNow;var display=new GpuTelemetryDisplay();display.Update(null,at);
        Assert.Empty(display.Adapters);Assert.Contains("not been sampled",display.Status);
        var a=Sample(at).Adapters[0];display.Update(new(at,[a,a with{AdapterLuid="00000000_00000002",PhysicalAdapterIndex=1,BusiestEnginePercent=80}]),at);
        Assert.Equal(2,display.Adapters.Count);Assert.Contains("not added",display.Status);
        Assert.Contains("30",display.Adapters[0].Engine);Assert.Contains("80",display.Adapters[1].Engine);
    }
    [Fact]
    public void ClientGpuTextShowsOnlyFreshAttributedNumbersAndSaysWhenTheOptionIsOff()
    {
        var at=DateTimeOffset.UtcNow;
        var row=new SessionRow(new SessionObservation(10,at.AddMinutes(-1).UtcTicks,"Synthetic",0,"",false,true,1,1,null,at));
        Assert.Equal("Off in Settings",row.Gpu);
        var raised=new List<string?>();row.PropertyChanged+=(_,e)=>raised.Add(e.PropertyName);
        var client=new GpuClientTelemetry(10,row.Observation.ProcessStartUtcTicks,12.34,CapacityPolicy.GiB/2);
        string engine=12.34.ToString("0.0",CultureInfo.CurrentCulture)+"% busiest engine",memory=0.5.ToString("0.00",CultureInfo.CurrentCulture)+" GiB dedicated";
        row.UpdateGpu(client,fresh:true,enabled:true);Assert.Equal(engine+" · "+memory,row.Gpu);
        row.UpdateGpu(client,fresh:true,enabled:true);Assert.Single(raised); // Unchanged text raises nothing.
        row.UpdateGpu(client with{BusiestEnginePercent=null},true,true);Assert.Equal("Engine unavailable · "+memory,row.Gpu);
        row.UpdateGpu(client with{BusiestEnginePercent=double.NaN},true,true);Assert.Equal("Engine unavailable · "+memory,row.Gpu);
        row.UpdateGpu(client with{DedicatedBytes=null},true,true);Assert.Equal(engine+" · dedicated unavailable",row.Gpu);
        row.UpdateGpu(client with{BusiestEnginePercent=null,DedicatedBytes=null},true,true);Assert.Equal("Unavailable",row.Gpu);
        row.UpdateGpu(client,fresh:false,enabled:true);Assert.Equal("Unavailable",row.Gpu); // Expired counters are never shown as numbers.
        row.UpdateGpu(null,true,true);Assert.Equal("Unavailable",row.Gpu);
        row.UpdateGpu(client,true,enabled:false);Assert.Equal("Off in Settings",row.Gpu);
        Assert.All(raised,name=>Assert.Equal(nameof(SessionRow.Gpu),name));
    }
    [Fact]
    public void DisplayBoundsProviderRowsAndDoesNotShowArbitraryIdentityText()
    {
        var at=DateTimeOffset.UtcNow;var a=Sample(at).Adapters[0];var display=new GpuTelemetryDisplay();
        display.Update(new(at,Enumerable.Range(0,100).Select(i=>a with{AdapterLuid="unexpected\ncontent",PhysicalAdapterIndex=i}).ToArray()),at);
        Assert.Equal(16,display.Adapters.Count);Assert.All(display.Adapters,row=>Assert.Contains("Unknown identity",row.Identity));
    }
}
