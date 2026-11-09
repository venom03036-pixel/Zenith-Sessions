using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using Xunit;
using Zenith.MacroHost.Contracts;
using Zenith.MacroHost.Windows;

namespace Zenith.Sessions.Tests;

public sealed class ForegroundControlPipeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task BadProofCannotReachAuthorization()
    {
        using var ct = new CancellationTokenSource(Deadline);
        var name = PipeName();
        int calls = 0;
        await using var server = new ForegroundControlPipeServer((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new ForegroundControlAdmission(null, ForegroundControlOutcome.InvalidRequest));
        }, name);
        string code = server.TakePairingCode();
        Assert.Throws<InvalidOperationException>(() => server.TakePairingCode());
        using var pipe = await ConnectAsync(name, ct.Token);
        using var wire = await ProveAsync(pipe, code, badProof: true, ct.Token);
        try { await wire.WriteAsync(Start(FixtureRequest()), ct.Token); }
        catch (IOException) { } // The host can close before the attempted Start arrives.
        await server.Completion.WaitAsync(ct.Token);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AnOperationIdFromAnotherRunCannotStopThisRun()
    {
        using var ct = new CancellationTokenSource(Deadline);
        var name = PipeName();
        var operation = new Operation();
        var request = new ForegroundControlRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0x6543, 912, 51);
        await using var server = new ForegroundControlPipeServer((peer, value, _) =>
        {
            Assert.Equal(Environment.ProcessId, peer);
            Assert.Equal(request, value);
            return Task.FromResult(new ForegroundControlAdmission(operation, ForegroundControlOutcome.None));
        }, name);
        string code = server.TakePairingCode();
        using var pipe = await ConnectAsync(name, ct.Token);
        using var wire = await ProveAsync(pipe, code, badProof: false, ct.Token);
        try
        {
            await wire.WriteAsync(Start(request), ct.Token);
            Assert.Equal(operation.Id, ReplyId(await wire.ReadAsync(ct.Token)));
            await wire.WriteAsync(Command(ForegroundCommand.Stop, Guid.NewGuid()), ct.Token);
            var stale = await wire.ReadAsync(ct.Token);
            Assert.Equal(Guid.Empty, ReplyId(stale));
            Assert.Equal((byte)ForegroundControlState.Rejected, stale[1]);
            Assert.False(operation.Stopped.Task.IsCompleted);
            await wire.WriteAsync(Command(ForegroundCommand.Stop, operation.Id), ct.Token);
            Assert.Equal((byte)ForegroundControlState.Stopping, (await wire.ReadAsync(ct.Token))[1]);
            await operation.Stopped.Task.WaitAsync(ct.Token);
        }
        finally { operation.Cleaned.TrySetResult(); }
    }

    [Fact]
    public async Task DisconnectSignalsStopWhileAnUnfinishedControlIsStillBeingJoined()
    {
        using var ct = new CancellationTokenSource(Deadline);
        var name = PipeName();
        var operation = new Operation();
        await using var server = new ForegroundControlPipeServer((_, _, _) =>
            Task.FromResult(new ForegroundControlAdmission(operation, ForegroundControlOutcome.None)), name);
        string code = server.TakePairingCode();
        using var pipe = await ConnectAsync(name, ct.Token);
        using var wire = await ProveAsync(pipe, code, badProof: false, ct.Token);
        try
        {
            await wire.WriteAsync(Start(FixtureRequest()), ct.Token);
            Assert.Equal(operation.Id, ReplyId(await wire.ReadAsync(ct.Token)));
            await wire.WriteAsync(Command(ForegroundCommand.Pause, operation.Id), ct.Token);
            await operation.PauseEntered.Task.WaitAsync(ct.Token);
            pipe.Dispose();
            await operation.Stopped.Task.WaitAsync(ct.Token);
            Assert.False(server.Completion.IsCompleted);
            operation.PauseFinished.TrySetResult();
            Assert.False(server.Completion.IsCompleted);
        }
        finally
        {
            operation.PauseFinished.TrySetResult();
            operation.Cleaned.TrySetResult();
        }
        await server.Completion.WaitAsync(ct.Token);
    }

    [Fact]
    public async Task AnAuthorizationThatReturnsAfterDisconnectIsStoppedAndJoined()
    {
        using var ct = new CancellationTokenSource(Deadline);
        var name = PipeName();
        var operation = new Operation();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLateResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ForegroundControlPipeServer(async (_, _, token) =>
        {
            using var registration = token.Register(() => disconnected.TrySetResult());
            entered.TrySetResult();
            await allowLateResult.Task;
            return new ForegroundControlAdmission(operation, ForegroundControlOutcome.None);
        }, name);
        string code = server.TakePairingCode();
        using var pipe = await ConnectAsync(name, ct.Token);
        using var wire = await ProveAsync(pipe, code, badProof: false, ct.Token);
        try
        {
            await wire.WriteAsync(Start(FixtureRequest()), ct.Token);
            await entered.Task.WaitAsync(ct.Token);
            pipe.Dispose();
            await disconnected.Task.WaitAsync(ct.Token);
            Assert.False(server.Completion.IsCompleted);
            allowLateResult.TrySetResult();
            await operation.Stopped.Task.WaitAsync(ct.Token);
            Assert.False(server.Completion.IsCompleted);
        }
        finally
        {
            allowLateResult.TrySetResult();
            operation.Cleaned.TrySetResult();
        }
        await server.Completion.WaitAsync(ct.Token);
    }

    [Fact]
    public async Task AnAuthenticatedButSilentConnectionStopsAndJoins()
    {
        using var ct = new CancellationTokenSource(Deadline);
        var name = PipeName();
        var operation = new Operation();
        await using var server = new ForegroundControlPipeServer((_, _, _) =>
            Task.FromResult(new ForegroundControlAdmission(operation, ForegroundControlOutcome.None)), name, TimeSpan.FromSeconds(1));
        string code = server.TakePairingCode();
        using var pipe = await ConnectAsync(name, ct.Token);
        using var wire = await ProveAsync(pipe, code, badProof: false, ct.Token);
        try
        {
            await wire.WriteAsync(Start(FixtureRequest()), ct.Token);
            Assert.Equal(operation.Id, ReplyId(await wire.ReadAsync(ct.Token)));
            await operation.Stopped.Task.WaitAsync(ct.Token);
            Assert.False(server.Completion.IsCompleted);
        }
        finally { operation.Cleaned.TrySetResult(); }
        await server.Completion.WaitAsync(ct.Token);
    }

    [Fact]
    public async Task ReplayedAuthenticatedFrameRevokesTheConnection()
    {
        using var ct = new CancellationTokenSource(Deadline);
        var name = PipeName();
        var operation = new Operation();
        await using var server = new ForegroundControlPipeServer((_, _, _) =>
            Task.FromResult(new ForegroundControlAdmission(operation, ForegroundControlOutcome.None)), name);
        string code = server.TakePairingCode();
        using var pipe = await ConnectAsync(name, ct.Token);
        byte[]? sessionKey = null;
        using var wire = await ProveAsync(pipe, code, badProof: false, ct.Token, key => sessionKey = key);
        using var replay = new ForegroundControlWire(pipe, sessionKey!, server: false);
        try
        {
            byte[] request = Start(FixtureRequest());
            await wire.WriteAsync(request, ct.Token);
            Assert.Equal(operation.Id, ReplyId(await wire.ReadAsync(ct.Token)));
            await replay.WriteAsync(request, ct.Token); // The same body and authentication tag for sequence 1.
            await operation.Stopped.Task.WaitAsync(ct.Token);
        }
        finally { operation.Cleaned.TrySetResult(); }
        await server.Completion.WaitAsync(ct.Token);
    }

    private static Guid ReplyId(byte[] reply) => new(reply.AsSpan(4), bigEndian: true);
    private static string PipeName() => $"{ForegroundControlProtocol.PipeName}.fixture-{Guid.NewGuid():N}";
    private static ForegroundControlRequest FixtureRequest() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0x1234, 42, 51);

    private static async Task<NamedPipeClientStream> ConnectAsync(string name, CancellationToken ct)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        try { await client.ConnectAsync(ct); return client; }
        catch { client.Dispose(); throw; }
    }

    private static async Task<ForegroundControlWire> ProveAsync(Stream stream, string code, bool badProof, CancellationToken ct,
        Action<byte[]>? recordKey = null)
    {
        byte[] hello = RandomNumberGenerator.GetBytes(36);
        "ZMC1"u8.CopyTo(hello);
        byte[] key = Convert.FromHexString(code), challenge = new byte[64];
        await stream.WriteAsync(hello, ct);
        await stream.ReadExactlyAsync(challenge, ct);
        Assert.Equal(ForegroundControlWire.Proof(key, 1, hello, challenge.AsSpan(0, 32)), challenge[32..]);
        byte[] proof = ForegroundControlWire.Proof(key, 2, hello, challenge.AsSpan(0, 32));
        if (badProof) proof[0] ^= 1;
        await stream.WriteAsync(proof, ct);
        byte[] sessionKey = ForegroundControlWire.Proof(key, 3, hello, challenge.AsSpan(0, 32));
        recordKey?.Invoke(sessionKey.ToArray());
        CryptographicOperations.ZeroMemory(key);
        return new(stream, sessionKey, server: false);
    }

    private static byte[] Start(ForegroundControlRequest request)
    {
        byte[] frame = new byte[69];
        frame[0] = (byte)ForegroundCommand.Start;
        request.HostSessionId.TryWriteBytes(frame.AsSpan(1), true, out _);
        request.MacroId.TryWriteBytes(frame.AsSpan(17), true, out _);
        request.SavedRevision.TryWriteBytes(frame.AsSpan(33), true, out _);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(49), request.Window);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(57), request.ProcessId);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(61), request.CreationFileTime);
        return frame;
    }

    private static byte[] Command(ForegroundCommand command, Guid operation)
    {
        byte[] frame = new byte[17];
        frame[0] = (byte)command;
        operation.TryWriteBytes(frame.AsSpan(1), true, out _);
        return frame;
    }

    private sealed class Operation : IForegroundControlOperation
    {
        public Guid Id { get; } = Guid.NewGuid();
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cleaned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PauseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PauseFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ForegroundControlSnapshot Snapshot => Cleaned.Task.IsCompleted
            ? new(ForegroundControlState.Finished, ForegroundControlOutcome.Stopped) : new(ForegroundControlState.Running);
        public Task Completion => Cleaned.Task;
        public void Stop() => Stopped.TrySetResult();
        public async Task<bool> PauseAsync(CancellationToken ct) { PauseEntered.TrySetResult(); await PauseFinished.Task; return false; }
        public Task<bool> ResumeAsync(CancellationToken ct) => Task.FromResult(false);
    }
}
