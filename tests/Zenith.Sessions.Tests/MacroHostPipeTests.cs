using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;
using Zenith.MacroHost.Contracts;
using Zenith.MacroHost.Windows;

namespace Zenith.Sessions.Tests;

/// <summary>Owned unique current-user pipes and synthetic metadata only; never opens the production Personal endpoint.</summary>
public sealed class MacroHostPipeTests
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private static string Name() => "Zenith.MacroHost.Tests." + Guid.NewGuid().ToString("N");
    private static SavedMacroDescriptor Descriptor(int id = 0) => new(Guid.NewGuid(), Guid.NewGuid(), "Macro " + id, "F4", 3, true, "Screen");
    private static MacroCatalogPage Ready(IReadOnlyList<SavedMacroDescriptor>? items = null, Guid? session = null,
        long revision = 7, int? total = null, int? next = null)
    {
        items ??= [Descriptor()];
        return new(MacroCatalogState.Ready, session ?? Guid.NewGuid(), revision, "0.34-test", items,
            total ?? items.Count, next, MacroCatalogMessages.For(MacroCatalogState.Ready));
    }

    [Fact]
    public async Task CurrentUserPipeReturnsExactSavedMetadataAndCanServeAnotherRequest()
    {
        string name = Name(); var page = Ready(); var source = new Source((_, _) => Task.FromResult(page));
        await using var server = new MacroCatalogPipeServer(source, name);
        await using var client = new MacroCatalogPipeClient(name);
        var actual = await client.GetPageAsync(new(), default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MacroCatalogState.Ready, actual.State);
        Assert.Equal(page.HostSessionId, actual.HostSessionId); Assert.Equal(page.CatalogRevision, actual.CatalogRevision);
        Assert.Equal(page.Items, actual.Items);
        var again = await client.GetPageAsync(new(HostSessionId: actual.HostSessionId, CatalogRevision: actual.CatalogRevision), default);
        Assert.Equal(MacroCatalogState.Ready, again.State); Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task MaximumPageRetainsOpaqueRevisionsAndExactCursor()
    {
        string name = Name(); var items = Enumerable.Range(0, 100).Select(Descriptor).ToArray();
        var page = Ready(items, total: 150, next: 100);
        await using var server = new MacroCatalogPipeServer(new Source((_, _) => Task.FromResult(page)), name);
        await using var client = new MacroCatalogPipeClient(name);
        var actual = await client.GetPageAsync(new(), default);
        Assert.Equal(100, actual.Items.Count); Assert.Equal(150, actual.Total); Assert.Equal(100, actual.NextOffset);
        Assert.Equal(items, actual.Items); Assert.All(actual.Items, item => Assert.NotEqual(Guid.Empty, item.SavedRevision));
    }

    [Theory]
    [InlineData(MacroCatalogState.Starting)]
    [InlineData(MacroCatalogState.Changed)]
    [InlineData(MacroCatalogState.Unavailable)]
    public async Task NonreadyStatesRemainExplicitAndEmpty(MacroCatalogState state)
    {
        string name = Name(); var expected = MacroCatalogPage.Failure(state);
        await using var server = new MacroCatalogPipeServer(new Source((_, _) => Task.FromResult(expected)), name);
        await using var client = new MacroCatalogPipeClient(name);
        var actual = await client.GetPageAsync(new(), default);
        Assert.Equal(state, actual.State); Assert.Empty(actual.Items); Assert.Equal(MacroCatalogMessages.For(state), actual.Explanation);
    }

    [Fact]
    public async Task AbsentHostIsDisconnectedAndDoesNotStartAnything()
    {
        await using var client = new MacroCatalogPipeClient(Name());
        var result = await client.GetPageAsync(new(), default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MacroCatalogState.Disconnected, result.State); Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ClientAdmitsOneCallAndReturnsBusyWithoutQueueingCompetitors()
    {
        string name = Name(); var entered = Gate(); var release = Gate();
        var source = new Source(async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return Ready(); });
        await using var server = new MacroCatalogPipeServer(source, name);
        await using var client = new MacroCatalogPipeClient(name);
        var first = client.GetPageAsync(new(), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var competitors = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => client.GetPageAsync(new(), default)));
            Assert.All(competitors, page => Assert.Equal(MacroCatalogState.Busy, page.State)); Assert.Equal(1, source.Calls);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(MacroCatalogState.Ready, (await first.WaitAsync(TimeSpan.FromSeconds(5))).State);
    }

    [Fact]
    public async Task CallerCancellationStopsAnActualConnectedReadAndLeavesClientReusable()
    {
        string name = Name(); var entered = Gate(); var release = Gate(); int calls = 0;
        var source = new Source(async (_, ct) =>
        { if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); } return Ready(); });
        await using var server = new MacroCatalogPipeServer(source, name);
        await using var client = new MacroCatalogPipeClient(name); using var cancel = new CancellationTokenSource();
        var pending = client.GetPageAsync(new(), cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, source.Calls);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(MacroCatalogState.Ready, (await client.GetPageAsync(new(), default)).State);
    }

    [Fact]
    public async Task ServerShutdownJoinsSourceThatIgnoresCancellationAndNeverPublishesLateReady()
    {
        string name = Name(); var entered = Gate(); var release = Gate();
        var source = new Source(async (_, _) => { entered.TrySetResult(); await release.Task; return Ready(); });
        var server = new MacroCatalogPipeServer(source, name);
        await using var client = new MacroCatalogPipeClient(name);
        var pending = client.GetPageAsync(new(), default); Task? disposing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); server.SignalStop();
            disposing = server.DisposeAsync().AsTask(); Assert.False(disposing.IsCompleted);
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(MacroCatalogState.Disconnected, result.State); Assert.Empty(result.Items);
        }
        finally { release.TrySetResult(); await (disposing ?? server.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5)); }
        await server.DisposeAsync(); Assert.True(server.Completion.IsCompleted);
    }

    [Fact]
    public async Task ClientDisposeCancelsAndJoinsItsCallAndRejectsLaterCalls()
    {
        string name = Name(); var entered = Gate();
        await using var server = new MacroCatalogPipeServer(new Source(async (_, ct) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Ready(); }), name);
        var client = new MacroCatalogPipeClient(name); var pending = client.GetPageAsync(new(), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(pending.IsCompleted); Assert.Equal(MacroCatalogState.Disconnected, (await pending).State);
            Assert.Equal(MacroCatalogState.Disconnected, (await client.GetPageAsync(new(), default)).State);
            await client.DisposeAsync();
        }
        finally { await client.DisposeAsync(); }
    }

    [Fact]
    public async Task ConnectedHostThatMissesTheDeadlineIsBusyNotDisconnected()
    {
        // A running host that stalls past the request deadline is present, so it must not be reported as absent.
        string name = Name(); var entered = Gate(); int calls = 0;
        var source = new Source(async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return Ready();
        });
        await using var server = new MacroCatalogPipeServer(source, name);
        await using var client = new MacroCatalogPipeClient(name);
        var pending = client.GetPageAsync(new(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stalled = await pending.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(MacroCatalogState.Busy, stalled.State); Assert.Empty(stalled.Items);
        Assert.Equal(MacroCatalogMessages.For(MacroCatalogState.Busy), stalled.Explanation);
        // The same client and endpoint answer the next explicit refresh once the host responds again.
        var again = await client.GetPageAsync(new(), default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MacroCatalogState.Ready, again.State); Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task SecondServerCannotTakeTheOwnedEndpointAndOriginalStillWorks()
    {
        string name = Name(); await using var owner = new MacroCatalogPipeServer(new Source((_, _) => Task.FromResult(Ready())), name);
        var collision = Record.Exception(() => new MacroCatalogPipeServer(new Source((_, _) => Task.FromResult(Ready())), name));
        Assert.True(collision is IOException or UnauthorizedAccessException);
        await using var client = new MacroCatalogPipeClient(name);
        Assert.Equal(MacroCatalogState.Ready, (await client.GetPageAsync(new(), default)).State);
    }

    [Fact]
    public async Task ArbitrarySourceExceptionIsSanitizedAndDoesNotKillListener()
    {
        string name = Name(); int calls = 0;
        await using var server = new MacroCatalogPipeServer(new Source((_, _) =>
            Interlocked.Increment(ref calls) == 1 ? throw new Exception("private-marker-not-on-wire") : Task.FromResult(Ready())), name);
        await using var client = new MacroCatalogPipeClient(name);
        var failure = await client.GetPageAsync(new(), default);
        Assert.Equal(MacroCatalogState.Unavailable, failure.State); Assert.DoesNotContain("private-marker", JsonSerializer.Serialize(failure));
        Assert.Equal(MacroCatalogState.Ready, (await client.GetPageAsync(new(), default)).State);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("operation")]
    [InlineData("product")]
    [InlineData("empty-id")]
    [InlineData("duplicate")]
    [InlineData("nested-duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("limit")]
    [InlineData("trailing")]
    [InlineData("utf8")]
    public async Task MalformedRequestIsClosedBeforeCallingSource(string damage)
    {
        string name = Name(); var source = new Source((_, _) => Task.FromResult(Ready()));
        await using var server = new MacroCatalogPipeServer(source, name);
        string json = RequestJson();
        json = damage switch
        {
            "future" => json.Replace("\"Version\":1", "\"Version\":99"),
            "operation" => json.Replace("\"Catalog\"", "\"Run\""),
            "product" => json.Replace(MacroHostProtocol.Product, "Another.Product"),
            "empty-id" => RequestJson(Guid.Empty),
            "duplicate" => json.Replace("\"Version\":1", "\"Version\":1,\"Version\":1"),
            "nested-duplicate" => json.Replace("\"Limit\":100", "\"Limit\":100,\"Limit\":100"),
            "unknown" => json.Replace("\"Offset\":0", "\"Unknown\":0,\"Offset\":0"),
            "missing" => json.Replace("\"Offset\":0,", ""),
            "limit" => json.Replace("\"Limit\":100", "\"Limit\":101"),
            "trailing" => json + "{}",
            _ => json
        };
        await SendMalformedAsync(name, damage == "utf8" ? [0xff] : Encoding.UTF8.GetBytes(json));
        Assert.Equal(0, source.Calls);
        await using var healthy = new MacroCatalogPipeClient(name);
        Assert.Equal(MacroCatalogState.Ready, (await healthy.GetPageAsync(new(), default)).State);
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(262145)] [InlineData(int.MaxValue)]
    public async Task InvalidFrameLengthIsRejectedWithoutBodyAllocationOrSourceCall(int length)
    {
        string name = Name(); var source = new Source((_, _) => Task.FromResult(Ready()));
        await using var server = new MacroCatalogPipeServer(source, name);
        await SendMalformedAsync(name, [], length); Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task InterruptedPrefixAndBodyDoNotPoisonTheNextConnection()
    {
        string name = Name(); var source = new Source((_, _) => Task.FromResult(Ready()));
        await using var server = new MacroCatalogPipeServer(source, name);
        using (var prefix = RawClient(name)) { await prefix.ConnectAsync(1000); await prefix.WriteAsync(new byte[] { 1, 0 }); }
        using (var body = RawClient(name))
        { await body.ConnectAsync(1000); byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, 100); await body.WriteAsync(bytes); await body.WriteAsync("{"u8.ToArray()); }
        await using var client = new MacroCatalogPipeClient(name);
        Assert.Equal(MacroCatalogState.Ready, (await client.GetPageAsync(new(), default)).State); Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task IdlePartialRequestHitsDeadlineAndReleasesEndpointWithoutCallingSource()
    {
        string name = Name(); var source = new Source((_, _) => Task.FromResult(Ready()));
        await using var server = new MacroCatalogPipeServer(source, name);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using (var idle = RawClient(name))
        {
            await idle.ConnectAsync(1000, timeout.Token);
            await idle.WriteAsync(new byte[] { 10, 0, 0, 0 }, timeout.Token);
            try { Assert.Equal(0, await idle.ReadAsync(new byte[1], timeout.Token)); } catch (IOException) { }
        }
        Assert.Equal(0, source.Calls);
        await using var client = new MacroCatalogPipeClient(name);
        Assert.Equal(MacroCatalogState.Ready, (await client.GetPageAsync(new(), default)).State);
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("future")]
    [InlineData("duplicate")]
    [InlineData("nested-duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("surrogate")]
    [InlineData("cursor")]
    [InlineData("enum")]
    [InlineData("utf8")]
    [InlineData("deep")]
    [InlineData("session")]
    [InlineData("revision")]
    [InlineData("duplicate-item")]
    [InlineData("rows")]
    public async Task ClientRejectsMalformedOrMismatchedResponseWithoutRetainingRows(string damage)
    {
        string name = Name(); var page = Ready();
        if (damage == "duplicate-item") page = Ready([page.Items[0], page.Items[0]]);
        if (damage == "rows") page = Ready(Enumerable.Range(0, 101).Select(Descriptor).ToArray());
        Task peer = ReplyAsync(name, id =>
        {
            string json = ResponseJson(damage == "wrong-id" ? Guid.NewGuid() : id, page);
            json = damage switch
            {
                "future" => json.Replace("\"Version\":1", "\"Version\":2"),
                "duplicate" => json.Replace("\"Version\":1", "\"Version\":1,\"Version\":1"),
                "nested-duplicate" => json.Replace("\"Enabled\":true", "\"Enabled\":true,\"Enabled\":false"),
                "unknown" => json.Replace("\"Total\":1", "\"Run\":true,\"Total\":1"),
                "missing" => json.Replace("\"Enabled\":true,", ""),
                "surrogate" => json.Replace("Macro 0", "\\uD800"),
                "cursor" => json.Replace("\"NextOffset\":null", "\"NextOffset\":1"),
                "enum" => json.Replace("\"Ready\"", "\"Running\""),
                "deep" => "{\"extra\":" + new string('[', 20) + "0" + new string(']', 20) + "}",
                _ => json
            };
            return damage == "utf8" ? [0xff] : Encoding.UTF8.GetBytes(json);
        });
        await using var client = new MacroCatalogPipeClient(name);
        var query = damage switch
        {
            "session" => new MacroCatalogQuery(HostSessionId: Guid.NewGuid(), CatalogRevision: page.CatalogRevision),
            "revision" => new MacroCatalogQuery(HostSessionId: page.HostSessionId, CatalogRevision: page.CatalogRevision + 1),
            _ => new MacroCatalogQuery()
        };
        var result = await client.GetPageAsync(query, default).WaitAsync(TimeSpan.FromSeconds(5));
        await peer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MacroCatalogState.Incompatible, result.State); Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(262145)] [InlineData(int.MaxValue)]
    public async Task ClientRejectsUnsafeResponseLengthBeforeAllocatingItsBody(int length)
    {
        string name = Name(); Task peer = ReplyAsync(name, _ => [], length);
        await using var client = new MacroCatalogPipeClient(name);
        var result = await client.GetPageAsync(new(), default).WaitAsync(TimeSpan.FromSeconds(5));
        await peer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MacroCatalogState.Incompatible, result.State); Assert.Empty(result.Items);
    }

    [Fact]
    public async Task CancellationBeforeAdmissionNeverConnectsOrInvokesSource()
    {
        string name = Name(); var source = new Source((_, _) => Task.FromResult(Ready()));
        await using var server = new MacroCatalogPipeServer(source, name);
        await using var client = new MacroCatalogPipeClient(name);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.GetPageAsync(new(), cancelled.Token));
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("\\\\remote\\pipe\\catalog")]
    [InlineData("")]
    [InlineData("catalog:stream")]
    public void EndpointNamesCannotBecomePathsOrRemoteServers(string name)
    {
        Assert.Throws<ArgumentException>(() => new MacroCatalogPipeClient(name));
        Assert.Throws<ArgumentException>(() => new MacroCatalogPipeServer(new Source((_, _) => Task.FromResult(Ready())), name));
    }

    [Fact]
    public async Task ClientThatClosesBeforeFirstAcceptDoesNotKillTheEndpoint()
    {
        string name = Name(); var source = new Source((_, _) => Task.FromResult(Ready())); var firstAccept = Gate();
        var server = new MacroCatalogPipeServer(source, name, firstAccept.Task);
        MacroCatalogPage page; bool workerEnded; Exception? shutdown;
        try
        {
            // Connect and close before the first ConnectNamedPipe: that accept then fails with ERROR_NO_DATA.
            using (var early = RawClient(name)) await early.ConnectAsync(1000);
            firstAccept.TrySetResult();
            await using var client = new MacroCatalogPipeClient(name);
            page = await client.GetPageAsync(new(), default).WaitAsync(TimeSpan.FromSeconds(5));
            workerEnded = server.Completion.IsCompleted;
        }
        finally
        {
            firstAccept.TrySetResult();
            shutdown = await Record.ExceptionAsync(() => server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(MacroCatalogState.Ready, page.State); Assert.Equal(1, source.Calls);
        Assert.False(workerEnded); Assert.Null(shutdown);
    }

    [Fact]
    public async Task ClientsThatConnectAndCloseInALoopCannotStopTheEndpoint()
    {
        // M-L9-3: only the window before the first accept lets a client connect early. A reset or disconnected instance
        // refuses clients until the next accept is waiting, so a connect-and-close loop causes at most one failed accept.
        string name = Name(); var source = new Source((_, _) => Task.FromResult(Ready())); var firstAccept = Gate();
        var server = new MacroCatalogPipeServer(source, name, firstAccept.Task);
        using var storm = new CancellationTokenSource(TimeSpan.FromSeconds(10)); int connections = 0;
        async Task ConnectAndCloseAsync()
        {
            while (Volatile.Read(ref connections) < 300 && !storm.IsCancellationRequested)
            {
                using var peer = RawClient(name);
                try { await peer.ConnectAsync(20, storm.Token); Interlocked.Increment(ref connections); }
                catch (TimeoutException) { }
                catch (OperationCanceledException) when (storm.IsCancellationRequested) { }
            }
        }
        MacroCatalogPage page; bool workerEnded; Exception? shutdown;
        try
        {
            Task[] peers = [Task.Run(ConnectAndCloseAsync), Task.Run(ConnectAndCloseAsync)];
            await Task.Delay(100); firstAccept.TrySetResult();
            await Task.WhenAll(peers).WaitAsync(TimeSpan.FromSeconds(15));
            workerEnded = server.Completion.IsCompleted;
            await using var client = new MacroCatalogPipeClient(name);
            page = await client.GetPageAsync(new(), default).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            firstAccept.TrySetResult();
            shutdown = await Record.ExceptionAsync(() => server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.False(workerEnded, "The endpoint stopped during the connect-and-close loop.");
        Assert.True(Volatile.Read(ref connections) >= 300, $"Only {connections} connect-and-close cycles ran.");
        Assert.Equal(MacroCatalogState.Ready, page.State); Assert.Equal(1, source.Calls);
        Assert.Null(shutdown);
    }

    [Fact]
    public async Task AcceptThatKeepsFailingEndsTheEndpointAndDisposalReportsTheFailure()
    {
        // Repeated failed accepts cannot come from a client (see the test above), only from the endpoint itself. The
        // worker must then stop after a bounded number of attempts, fault Completion and release the name. This keeps the
        // native reset: the injected accept never calls ConnectNamedPipe, so the second native reset fails
        // (ERROR_PIPE_NOT_CONNECTED) and that reset failure ends the worker. The test below covers the limit itself.
        string name = Name(); int attempts = 0;
        var server = new MacroCatalogPipeServer(new Source((_, _) => Task.FromResult(Ready())), name, Task.CompletedTask, (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException(new IOException("Injected failed accept."));
        });
        var stopped = await Record.ExceptionAsync(() => server.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        var disposal = await Record.ExceptionAsync(() => server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<IOException>(stopped); Assert.Same(stopped, disposal); Assert.True(server.Completion.IsFaulted);
        Assert.InRange(Volatile.Read(ref attempts), 2, MacroCatalogPipeServer.MaximumConsecutiveAcceptFailures + 1);
        // The name is no longer owned, so a restarted Personal serves the companion again.
        await using var restarted = new MacroCatalogPipeServer(new Source((_, _) => Task.FromResult(Ready())), name);
        await using var client = new MacroCatalogPipeClient(name);
        Assert.Equal(MacroCatalogState.Ready, (await client.GetPageAsync(new(), default).WaitAsync(TimeSpan.FromSeconds(5))).State);
    }

    [Fact]
    public async Task AcceptThatKeepsFailingWhileResetsSucceedStopsAtTheLimitAndReportsTheAcceptFailure()
    {
        // N-B9-2: with every reset succeeding, only the consecutive-failure limit can end the worker. It must make the
        // first accept plus exactly MaximumConsecutiveAcceptFailures retries, reset after each retried failure, and fault
        // Completion with the last accept failure. Past 100 attempts the accept waits for stop, so a missing limit fails
        // this test without a hot retry loop.
        string name = Name(); int attempts = 0, resets = 0; IOException? lastFailure = null;
        var server = new MacroCatalogPipeServer(new Source((_, _) => Task.FromResult(Ready())), name, Task.CompletedTask, (_, ct) =>
        {
            if (Interlocked.Increment(ref attempts) > 100) return Task.Delay(Timeout.Infinite, ct);
            var failure = new IOException("Injected failed accept.");
            Volatile.Write(ref lastFailure, failure);
            return Task.FromException(failure);
        }, _ => { Interlocked.Increment(ref resets); return true; });
        var stopped = await Record.ExceptionAsync(() => server.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        var disposal = await Record.ExceptionAsync(() => server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(MacroCatalogPipeServer.MaximumConsecutiveAcceptFailures + 1, Volatile.Read(ref attempts));
        Assert.Equal(MacroCatalogPipeServer.MaximumConsecutiveAcceptFailures, Volatile.Read(ref resets));
        Assert.NotNull(stopped); Assert.Same(Volatile.Read(ref lastFailure), stopped); Assert.Same(stopped, disposal);
        Assert.True(server.Completion.IsFaulted);
        await using var restarted = new MacroCatalogPipeServer(new Source((_, _) => Task.FromResult(Ready())), name);
        await using var client = new MacroCatalogPipeClient(name);
        Assert.Equal(MacroCatalogState.Ready, (await client.GetPageAsync(new(), default).WaitAsync(TimeSpan.FromSeconds(5))).State);
    }

    [Fact]
    public async Task StopWhileWaitingForFirstAcceptEndsCleanly()
    {
        var firstAccept = Gate();
        var server = new MacroCatalogPipeServer(new Source((_, _) => Task.FromResult(Ready())), Name(), firstAccept.Task);
        server.SignalStop(); await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(server.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StopBeforeFirstAcceptAndRepeatedDisposalAreSafe()
    {
        var server = new MacroCatalogPipeServer(new Source((_, _) => Task.FromResult(Ready())), Name());
        server.SignalStop(); await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        server.SignalStop(); await server.DisposeAsync(); Assert.True(server.Completion.IsCompleted);
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Source(Func<MacroCatalogQuery, CancellationToken, Task<MacroCatalogPage>> read) : IMacroCatalogSource
    {
        private int _calls; public int Calls => Volatile.Read(ref _calls);
        public Task<MacroCatalogPage> GetPageAsync(MacroCatalogQuery query, CancellationToken ct)
        { Interlocked.Increment(ref _calls); return read(query, ct); }
    }
    private static NamedPipeClientStream RawClient(string name) => new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    private static string RequestJson(Guid? id = null) => JsonSerializer.Serialize(new
    { Product = MacroHostProtocol.Product, Version = MacroHostProtocol.Version, RequestId = id ?? Guid.NewGuid(), Operation = "Catalog", Query = new MacroCatalogQuery() }, Json);
    private static string ResponseJson(Guid id, MacroCatalogPage page) => JsonSerializer.Serialize(new
    { Product = MacroHostProtocol.Product, Version = MacroHostProtocol.Version, RequestId = id, Operation = "Catalog", Page = page }, Json);

    private static async Task SendMalformedAsync(string name, byte[] body, int? declaredLength = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); using var pipe = RawClient(name);
        await pipe.ConnectAsync(1000, timeout.Token);
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, declaredLength ?? body.Length);
        await pipe.WriteAsync(prefix, timeout.Token); if (body.Length > 0) await pipe.WriteAsync(body, timeout.Token);
        try { Assert.Equal(0, await pipe.ReadAsync(new byte[1], timeout.Token)); } catch (IOException) { }
    }
    private static async Task ReplyAsync(string name, Func<Guid, byte[]> response, int? declaredLength = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        await pipe.WaitForConnectionAsync(timeout.Token);
        byte[] prefix = new byte[4]; await pipe.ReadExactlyAsync(prefix, timeout.Token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix); Assert.InRange(length, 1, MacroHostProtocol.MaximumFrameBytes);
        byte[] request = new byte[length]; await pipe.ReadExactlyAsync(request, timeout.Token);
        using var document = JsonDocument.Parse(request);
        byte[] bytes = response(document.RootElement.GetProperty("RequestId").GetGuid());
        BinaryPrimitives.WriteInt32LittleEndian(prefix, declaredLength ?? bytes.Length);
        await pipe.WriteAsync(prefix, timeout.Token); await pipe.WriteAsync(bytes, timeout.Token); await pipe.FlushAsync(timeout.Token);
        try { Assert.Equal(0, await pipe.ReadAsync(new byte[1], timeout.Token)); } catch (IOException) { }
    }
}
