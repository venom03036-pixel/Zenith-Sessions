using System.Diagnostics;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed class ProcessIoReaderTests
{
    // This is only a query of the executing test process's numeric Windows counters. It is not
    // Roblox integration, a process-memory read, a workload benchmark or proof of separated disk/network use.
    [Fact]
    public void NumericAccountingQueryUsesOnlyTheExpectedOwnProcessInstance()
    {
        using var current = Process.GetCurrentProcess(); long start = current.StartTime.ToUniversalTime().Ticks;
        var first = ProcessIoReader.Read(current.Id, start);
        Assert.NotNull(first);
        var next = ProcessIoReader.Read(current.Id, start);
        Assert.NotNull(next);
        Assert.True(next.ReadBytes >= first.ReadBytes); Assert.True(next.WriteBytes >= first.WriteBytes);
        Assert.True(next.OtherBytes >= first.OtherBytes); Assert.True(next.ReadOperations >= first.ReadOperations);
        Assert.True(next.WriteOperations >= first.WriteOperations); Assert.True(next.OtherOperations >= first.OtherOperations);
        Assert.Null(ProcessIoReader.Read(current.Id, start - 1));
        Assert.Null(ProcessIoReader.Read(current.Id, DateTime.MaxValue.Ticks));
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(-1, 1)] [InlineData(1, 0)] [InlineData(1, -1)] [InlineData(1, long.MaxValue)]
    public void InvalidIdentitiesHaveNoInventedCounters(int pid, long start) => Assert.Null(ProcessIoReader.Read(pid, start));

    [Fact]
    public void PreCancelledNativeReadStopsBeforeOpeningAnyProcess()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProcessIoReader.Read(0, 0, canceled.Token));
    }
}
