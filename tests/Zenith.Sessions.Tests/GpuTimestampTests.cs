using Xunit;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class GpuTimestampTests
{
    [Theory]
    [InlineData(-300)] [InlineData(0)] [InlineData(330)] [InlineData(840)]
    public void LocalPdhWallClockConvertsToUtcBeforeFreshnessValidation(int minutes)
    {
        var zone=TimeZoneInfo.CreateCustomTimeZone("Synthetic offset",TimeSpan.FromMinutes(minutes),"Synthetic","Synthetic");
        var wall=new DateTime(2026,11,5,19,8,20,DateTimeKind.Unspecified);
        long raw=DateTime.SpecifyKind(wall,DateTimeKind.Utc).ToFileTimeUtc();
        Assert.True(WindowsGpuTelemetryReader.TryConvertPdhTimestamp(raw,zone,out long utc));
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(wall,zone),DateTime.FromFileTimeUtc(utc));
    }
    [Fact]
    public void AmbiguousAndInvalidDaylightSavingTimesStayUnknown()
    {
        var start=TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1,1,1,2,0,0),3,2,DayOfWeek.Sunday);
        var end=TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1,1,1,2,0,0),11,1,DayOfWeek.Sunday);
        var rule=TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026,1,1),new DateTime(2026,12,31),TimeSpan.FromHours(1),start,end);
        var zone=TimeZoneInfo.CreateCustomTimeZone("Synthetic DST",TimeSpan.FromHours(-5),"Synthetic","Standard","Daylight",[rule]);
        foreach(var wall in new[]{new DateTime(2026,3,8,2,30,0),new DateTime(2026,11,1,1,30,0)})
            Assert.False(WindowsGpuTelemetryReader.TryConvertPdhTimestamp(DateTime.SpecifyKind(wall,DateTimeKind.Utc).ToFileTimeUtc(),zone,out _));
    }
    [Theory]
    [InlineData(-1)] [InlineData(long.MaxValue)]
    public void InvalidNativeTimestampsAreNotRenewed(long raw)
        =>Assert.False(WindowsGpuTelemetryReader.TryConvertPdhTimestamp(raw,TimeZoneInfo.Utc,out _));
}
