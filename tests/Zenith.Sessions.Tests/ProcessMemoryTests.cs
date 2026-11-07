using System.Diagnostics;
using Xunit;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class ProcessMemoryTests
{
    [Fact]
    public void NativeQueryReadsOnlyTheExpectedLiveProcessInstance()
    {
        using var current=Process.GetCurrentProcess();long start=current.StartTime.ToUniversalTime().Ticks;
        var measured=ProcessMemoryReader.Read(current.Id,start);
        Assert.NotNull(measured);Assert.True(measured.WorkingSetBytes>0);Assert.True(measured.PrivateCommitBytes>0);
        if(measured.PrivateWorkingSetBytes is { } resident) Assert.InRange(resident,0,measured.WorkingSetBytes);
        Assert.Null(ProcessMemoryReader.Read(current.Id,start-1));
        Assert.Null(ProcessMemoryReader.Read(current.Id,DateTime.MaxValue.Ticks));
    }
    [Theory]
    [InlineData(0,1)]
    [InlineData(-1,1)]
    [InlineData(1,0)]
    public void InvalidProcessIdentitiesCannotProduceInventedCounters(int id,long start)=>Assert.Null(ProcessMemoryReader.Read(id,start));
}
