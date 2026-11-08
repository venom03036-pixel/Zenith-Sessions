using System.IO;
using Zenith.Sessions.Windows;
using Xunit;

namespace Zenith.Sessions.Tests;

public sealed partial class SessionLogCorrelatorTests
{
    [Fact]
    public async Task WatchedLogPathsNameEveryDiscoveredLogAndEmptyOnceNoClientIsOffered()
    {
        Write(SyntheticClientLogFixtures.Identity);
        var probe = new Probe(Log, Key());
        await using var worker = new WindowsSessionLogCorrelator(probe);
        Assert.Empty(worker.WatchedLogPaths);

        worker.Offer(Desktop(Key()), Key().HostId);
        var first = await After(worker);
        // The cleanup must leave every discovered candidate alone, bound or not, so the list is the reader set, not the bound set.
        Assert.Equal([Path.GetFullPath(Log)], worker.WatchedLogPaths.Select(Path.GetFullPath));

        worker.Offer(Desktop());
        await After(worker, first.Revision);
        Assert.Empty(worker.WatchedLogPaths);
    }
}
