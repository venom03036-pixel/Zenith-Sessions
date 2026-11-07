using System.Reflection;
using System.Windows.Threading;
using Xunit;
using Zenith.Sessions.App;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed partial class ClientWindowIntegrationTests
{
    private const long ReportedPlace = 9007199254740993;
    private const string ReportedJob = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
    private const BindingFlags ReportedPrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task ReviewingCurrentReportedServerPinsOnlyTheJoinGameDraft() => OnUi(async () =>
    {
        await using var shell = await Ready(new Monitor());
        await shell.SaveAccountAsync(AccountProfile.Create("Synthetic chosen profile"));
        shell.SelectedAccount = Assert.Single(shell.Accounts);
        var account = shell.SelectedAccount;
        shell.ServerBrowser.UsePreferredPlace(42);
        var destination = shell.ServerBrowser.Destination;
        shell.ServerBrowser.LinkInput = "previous unresolved draft";
        shell.Navigate.Execute("Sessions");
        var serial = await HoldReportedSamplerAsync(shell);
        try
        {
            var fixture = PublishReportedSession(shell);
            string? copied = shell.BuildReportedDestinationLink(fixture.Row);
            Assert.NotNull(copied);
            Assert.True(shell.ReviewReportedDestination(fixture.Row));
            Assert.True(shell.IsJoinGame);
            Assert.Equal(copied, shell.ServerBrowser.LinkInput);
            var parsed = RobloxDestinationParser.Parse(shell.ServerBrowser.LinkInput);
            Assert.Equal(ReportedPlace, parsed.RequestedDestination!.PlaceId);
            Assert.Equal(ReportedJob, parsed.RequestedDestination.JobId);
            Assert.Equal(RobloxServerKind.Unknown, parsed.RequestedDestination.ServerKind);
            Assert.Same(destination, shell.ServerBrowser.Destination);
            Assert.Same(account, shell.SelectedAccount);
            Assert.Empty(shell.Launches);
            Assert.True(fixture.Row.Log.HasReportedDestination);
            Assert.False(fixture.Row.Log.CanAuthorizeAccountAction);
        }
        finally { serial.Release(); }
    });

    [Theory]
    [InlineData("partial inventory")]
    [InlineData("stale inventory")]
    [InlineData("reused process")]
    [InlineData("missing diagnostic")]
    [InlineData("duplicate diagnostic")]
    public Task RetainedVisibleRowCannotRouteFromInvalidLatestEvidence(string kind) => OnUi(async () =>
    {
        await using var shell = await Ready(new Monitor());
        shell.ServerBrowser.UsePreferredPlace(42);
        var destination = shell.ServerBrowser.Destination;
        shell.ServerBrowser.LinkInput = "preserved text draft";
        shell.Navigate.Execute("Sessions");
        var serial = await HoldReportedSamplerAsync(shell);
        try
        {
            var fixture = PublishReportedSession(shell);
            var desktop = fixture.Desktop;
            var logs = fixture.Logs;
            if (kind == "partial inventory") desktop = desktop with { ProcessInventoryComplete = false };
            else if (kind == "stale inventory") desktop = desktop with { ProcessObservedUtc = DateTimeOffset.UtcNow.AddSeconds(-4) };
            else if (kind == "reused process") desktop = desktop with
            { Sessions = [fixture.Row.Observation with { ProcessStartUtcTicks = fixture.Row.Observation.ProcessStartUtcTicks + 1 }] };
            else if (kind == "missing diagnostic") logs = logs with { Revision = 2, Sessions = [] };
            else logs = logs with { Revision = 2, Sessions = [logs.Sessions[0], logs.Sessions[0]] };
            ReportedField(shell, "_latestDesktop").SetValue(shell, desktop);
            ReportedField(fixture.Correlator, "_snapshot").SetValue(fixture.Correlator, logs);

            Assert.True(fixture.Row.Log.HasReportedDestination);
            Assert.Null(shell.BuildReportedDestinationLink(fixture.Row));
            Assert.False(shell.ReviewReportedDestination(fixture.Row));
            Assert.True(shell.IsSessions);
            Assert.Equal("preserved text draft", shell.ServerBrowser.LinkInput);
            Assert.Same(destination, shell.ServerBrowser.Destination);
            Assert.Empty(shell.Launches);
        }
        finally { serial.Release(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StartupOrLockRejectsReportedReviewWithoutAnotherMutation(bool locked) => OnUi(async () =>
    {
        await using var shell = await Ready(new Monitor());
        var serial = await HoldReportedSamplerAsync(shell);
        try
        {
            var fixture = PublishReportedSession(shell);
            shell.ServerBrowser.LinkInput = "existing draft";
            shell.Navigate.Execute("Sessions");
            if (locked) shell.LockDashboard();
            else ReportedField(shell, "_ready").SetValue(shell, false);
            string draft = shell.ServerBrowser.LinkInput;
            string tab = shell.Tab;

            Assert.Null(shell.BuildReportedDestinationLink(fixture.Row));
            Assert.False(shell.ReviewReportedDestination(fixture.Row));
            Assert.Equal(draft, shell.ServerBrowser.LinkInput);
            Assert.Equal(tab, shell.Tab);
            Assert.Empty(shell.Launches);
        }
        finally { serial.Release(); }
    });

    [Fact]
    public Task PanicDuringDraftNotificationPreventsTheLaterNavigation() => OnUi(async () =>
    {
        await using var shell = await Ready(new Monitor());
        shell.ServerBrowser.UsePreferredPlace(42);
        shell.Navigate.Execute("Sessions");
        var serial = await HoldReportedSamplerAsync(shell);
        try
        {
            var fixture = PublishReportedSession(shell);
            bool stopped = false;
            shell.ServerBrowser.PropertyChanged += (_, args) =>
            {
                if (stopped || args.PropertyName != nameof(ServerBrowserViewModel.LinkInput)) return;
                stopped = true;
                shell.Panic.Execute(null);
            };

            Assert.False(shell.ReviewReportedDestination(fixture.Row));
            Assert.True(stopped);
            Assert.True(shell.IsSessions);
            Assert.Contains("Panic", shell.Notice, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(shell.Launches);
        }
        finally { serial.Release(); }
    });

    [Fact]
    public Task JoinSameGameSetsPlaceOnlyAndOpensReadyStep() => OnUi(async () =>
    {
        await using var shell = await Ready(new Monitor());
        shell.ServerBrowser.UsePreferredPlace(42); shell.Navigate.Execute("Sessions");
        var serial = await HoldReportedSamplerAsync(shell);
        try
        {
            var fixture = PublishReportedSession(shell);
            Assert.True(shell.JoinSameAsReported(fixture.Row, sameServer: false));
            Assert.True(shell.IsJoinGame); Assert.True(shell.IsJoinReadyStep);
            Assert.Equal(ReportedPlace, shell.ServerBrowser.Destination!.PlaceId); Assert.Null(shell.ServerBrowser.Destination.JobId);
            Assert.Equal("", shell.ServerBrowser.LinkInput); Assert.Empty(shell.Launches);
            Assert.True(shell.ServerBrowser.CanUseDestination); Assert.False(fixture.Row.Log.CanAuthorizeAccountAction);
        }
        finally { serial.Release(); }
    });

    [Fact]
    public Task JoinSameServerSetsPlaceAndJobAndOpensReadyStep() => OnUi(async () =>
    {
        await using var shell = await Ready(new Monitor());
        shell.ServerBrowser.UsePreferredPlace(42); shell.Navigate.Execute("Sessions");
        var serial = await HoldReportedSamplerAsync(shell);
        try
        {
            var fixture = PublishReportedSession(shell);
            Assert.True(shell.JoinSameAsReported(fixture.Row, sameServer: true));
            Assert.True(shell.IsJoinGame); Assert.True(shell.IsJoinReadyStep);
            Assert.Equal(ReportedPlace, shell.ServerBrowser.Destination!.PlaceId); Assert.Equal(ReportedJob, shell.ServerBrowser.Destination.JobId);
            Assert.Equal(RobloxServerKind.Unknown, shell.ServerBrowser.Destination.ServerKind);
            Assert.Equal("", shell.ServerBrowser.LinkInput); Assert.Empty(shell.Launches);
        }
        finally { serial.Release(); }
    });

    [Theory]
    [InlineData("stale inventory")]
    [InlineData("missing diagnostic")]
    [InlineData("ambiguous correlation")]
    [InlineData("expired lease")]
    [InlineData("disconnected since")]
    [InlineData("inconsistent newer batch")]
    public Task StaleOrAmbiguousLogDisablesJoinSame(string kind) => OnUi(async () =>
    {
        await using var shell = await Ready(new Monitor());
        shell.ServerBrowser.UsePreferredPlace(42); var destination = shell.ServerBrowser.Destination;
        shell.ServerBrowser.LinkInput = "preserved text draft"; shell.Navigate.Execute("Sessions");
        var serial = await HoldReportedSamplerAsync(shell);
        try
        {
            var fixture = PublishReportedSession(shell);
            var desktop = fixture.Desktop; var logs = fixture.Logs; var diagnostic = logs.Sessions[0];
            if (kind == "stale inventory") desktop = desktop with { ProcessObservedUtc = DateTimeOffset.UtcNow.AddSeconds(-4) };
            else if (kind == "missing diagnostic") logs = logs with { Revision = 2, Sessions = [] };
            else if (kind == "ambiguous correlation") logs = logs with { Revision = 2, Sessions = [diagnostic with { Correlation = SessionLogCorrelationStatus.Ambiguous }] };
            else if (kind is "disconnected since" or "inconsistent newer batch")
            {
                // The client left the server after the row was drawn. A consistent batch carries the line; an inconsistent
                // one (a fact past its own coverage) must fail closed instead of leaving the old server current.
                var at = DateTimeOffset.UtcNow; var old = diagnostic.Batch!; long covered = kind == "disconnected since" ? 40 : 30;
                var batch = old with { ReadOffset = covered, CompleteLineOffset = covered,
                    Observations = [.. old.Observations, new(old.Stream, 30, 40, at, at, ClientLogFact.Disconnected)] };
                logs = logs with { Revision = 2, ObservedUtc = at, Sessions = [diagnostic with { ObservedUtc = at, Batch = batch }] };
            }
            else logs = logs with { Revision = 2, Sessions = [diagnostic with { LeaseExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }] };
            ReportedField(shell, "_latestDesktop").SetValue(shell, desktop);
            ReportedField(fixture.Correlator, "_snapshot").SetValue(fixture.Correlator, logs);
            Assert.True(fixture.Row.Log.HasReportedDestination); // The retained row is never the authority at click time.
            Assert.False(shell.JoinSameAsReported(fixture.Row, sameServer: true));
            Assert.False(shell.JoinSameAsReported(fixture.Row, sameServer: false));
            Assert.True(shell.IsSessions); Assert.Equal("preserved text draft", shell.ServerBrowser.LinkInput);
            Assert.Same(destination, shell.ServerBrowser.Destination); Assert.Empty(shell.Launches);
        }
        finally { serial.Release(); }
    });

    private sealed record ReportedFixture(SessionRow Row, DesktopSnapshot Desktop,
        SessionLogDiscoverySnapshot Logs, WindowsSessionLogCorrelator Correlator);

    private static ReportedFixture PublishReportedSession(ShellViewModel shell)
    {
        var now = DateTimeOffset.UtcNow;
        var process = new SessionObservation(470001, now.AddSeconds(-30).UtcTicks, "Synthetic client", 0,
            "Synthetic windowless client", false, false, 10, 10, null, now);
        var key = new SessionProcessKey(null, process.ProcessId, process.ProcessStartUtcTicks);
        var stream = new ClientLogStreamId(Guid.NewGuid(), 1);
        ClientLogObservation[] facts =
        [
            new(stream, 0, 10, now, now, ClientLogFact.UserIdentityObserved, UserId: 123),
            new(stream, 10, 20, now, now, ClientLogFact.Joining, PlaceId: ReportedPlace, JobId: Guid.Parse(ReportedJob)),
            new(stream, 20, 30, now, now, ClientLogFact.Connected)
        ];
        var batch = new ClientLogBatch(stream, ClientLogReadStatus.Ready, ClientLogDiscontinuity.Initial, 30, 30, false, 0, facts);
        var logs = new SessionLogDiscoverySnapshot(1, now,
            [new(key, SessionLogCorrelationStatus.ResourceHolderObserved, stream, batch, now, now.AddSeconds(10))], 1, false);
        var desktop = new DesktopSnapshot(new(now, 32 * CapacityPolicy.GiB, 20 * CapacityPolicy.GiB,
            64 * CapacityPolicy.GiB, 10 * CapacityPolicy.GiB, 10), [process],
            ProcessObservedUtc: now, ProcessRevision: 1, ProcessInventoryComplete: true);
        var correlator = new WindowsSessionLogCorrelator(new ReportedProbe());
        ReportedField(correlator, "_snapshot").SetValue(correlator, logs);
        ReportedField(shell, "_logCorrelator").SetValue(shell, correlator);
        ReportedField(shell, "_latestDesktop").SetValue(shell, desktop);
        var projection = (SessionLogProjection)ReportedField(shell, "_logProjection").GetValue(shell)!;
        projection.Update([process], logs, now);
        var row = new SessionRow(process);
        row.UpdateLog(projection.Snapshot(now)[key]);
        shell.Sessions.Clear();
        shell.Sessions.Add(row);
        shell.SelectedSession = row;
        return new(row, desktop, logs, correlator);
    }

    private static async Task<SemaphoreSlim> HoldReportedSamplerAsync(ShellViewModel shell)
    {
        var serial = (SemaphoreSlim)ReportedField(shell, "_sampleSerial").GetValue(shell)!;
        Assert.True(await serial.WaitAsync(TimeSpan.FromSeconds(5)));
        return serial;
    }

    private static FieldInfo ReportedField(object instance, string name)
        => instance.GetType().GetField(name, ReportedPrivate) ?? throw new InvalidOperationException("Expected fixture field is unavailable.");

    private sealed class ReportedProbe : IClientLogProbe
    {
        public ClientLogCandidates Discover(int maximum, CancellationToken ct) => new([], false);
        public bool IsCurrentClient(SessionProcessKey process, ClientLogVariant variant) => false;
        public ResourceHolderResult QueryHolders(string path, CancellationToken ct)
            => throw new InvalidOperationException("A synthetic empty probe must not inspect a file.");
        public void Dispose() { }
    }
}
