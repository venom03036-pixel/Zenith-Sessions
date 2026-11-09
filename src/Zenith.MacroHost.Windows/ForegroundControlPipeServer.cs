using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using Zenith.MacroHost.Contracts;

namespace Zenith.MacroHost.Windows;

/// <summary>One explicitly armed connection and one run. Disconnect, timeout and disposal revoke it and join cleanup.</summary>
public sealed class ForegroundControlPipeServer : IAsyncDisposable
{
    private static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HandshakeDeadline = TimeSpan.FromSeconds(3);
    private readonly NamedPipeServerStream _pipe;
    private readonly Func<int, ForegroundControlRequest, CancellationToken, Task<ForegroundControlAdmission>> _authorize;
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _grant = RandomNumberGenerator.GetBytes(16);
    private readonly object _gate = new();
    private readonly TimeSpan _heartbeat;
    private readonly long _openedAt = Stopwatch.GetTimestamp();
    private bool _codeShown;
    private Task? _dispose;
    private int _stopped;
    public Task Completion { get; }

    public ForegroundControlPipeServer(Func<int, ForegroundControlRequest, CancellationToken, Task<ForegroundControlAdmission>> authorize,
        string? pipeName = null)
        : this(authorize, pipeName ?? ForegroundControlProtocol.PipeName, TimeSpan.FromSeconds(6)) { }

    internal ForegroundControlPipeServer(Func<int, ForegroundControlRequest, CancellationToken, Task<ForegroundControlAdmission>> authorize,
        string pipeName, TimeSpan heartbeat)
    {
        _authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        _heartbeat = heartbeat;
        _pipe = new NamedPipeServerStream(MacroCatalogPipeCodec.PipeName(pipeName), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 1024, 1024);
        Completion = Task.Run(ServeAsync);
    }

    /// <summary>Show the short-lived pairing code once in the trusted Personal UI. Never persist it.</summary>
    public string TakePairingCode()
    {
        lock (_gate)
        {
            if (_codeShown || _stop.IsCancellationRequested || Completion.IsCompleted
                || Stopwatch.GetElapsedTime(_openedAt) >= PairingLifetime)
                throw new InvalidOperationException("The foreground pairing code is unavailable.");
            _codeShown = true;
            return Convert.ToHexString(_grant);
        }
    }

    public void SignalStop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0) _stop.Cancel();
    }

    private async Task ServeAsync()
    {
        try
        {
            using var pairing = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            pairing.CancelAfter(PairingLifetime);
            await _pipe.WaitForConnectionAsync(pairing.Token).ConfigureAwait(false);
            int peer = LocalPeer();
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(pairing.Token);
            long connectedAt = Stopwatch.GetTimestamp();
            handshake.CancelAfter(HandshakeDeadline);
            using var wire = await ForegroundControlWire.AcceptAsync(_pipe, _grant, handshake.Token).ConfigureAwait(false);
            if (Stopwatch.GetElapsedTime(_openedAt) >= PairingLifetime || Stopwatch.GetElapsedTime(connectedAt) >= HandshakeDeadline)
                throw ForegroundControlWire.Invalid();
            handshake.Token.ThrowIfCancellationRequested();
            ClearGrant();
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var requests = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
            {
                SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
            });
            Task reader = ReadRequestsAsync(wire, requests.Writer, connection);
            try { await ExecuteRequestsAsync(peer, wire, requests.Reader, connection.Token).ConfigureAwait(false); }
            finally
            {
                connection.Cancel();
                await reader.ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException) { }
        finally
        {
            ClearGrant();
            _pipe.Dispose();
        }
    }

    private void ClearGrant()
    {
        lock (_gate)
        {
            _codeShown = true;
            CryptographicOperations.ZeroMemory(_grant);
        }
    }

    private async Task ReadRequestsAsync(ForegroundControlWire wire, ChannelWriter<byte[]> writer, CancellationTokenSource connection)
    {
        try
        {
            while (!connection.IsCancellationRequested)
            {
                using var frame = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
                long began = Stopwatch.GetTimestamp();
                frame.CancelAfter(_heartbeat);
                byte[] body = await wire.ReadAsync(frame.Token).ConfigureAwait(false);
                if (frame.IsCancellationRequested || Stopwatch.GetElapsedTime(began) >= _heartbeat || !writer.TryWrite(body))
                    throw ForegroundControlWire.Invalid();
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException) { }
        finally
        {
            connection.Cancel();
            writer.TryComplete();
        }
    }

    private async Task ExecuteRequestsAsync(int peer, ForegroundControlWire wire, ChannelReader<byte[]> reader, CancellationToken ct)
    {
        IForegroundControlOperation? run = null;
        CancellationTokenRegistration connectionStop = default;
        bool started = false, stopping = false;
        try
        {
            await foreach (byte[] body in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                var command = (ForegroundCommand)body[0];
                if (!Enum.IsDefined(command)) throw ForegroundControlWire.Invalid();
                if (command == ForegroundCommand.Start)
                {
                    var request = ForegroundControlWire.ParseStart(body);
                    if (started || peer != LocalPeer()) throw ForegroundControlWire.Invalid();
                    started = true;
                    var result = await _authorize(peer, request, ct).ConfigureAwait(false);
                    run = result.Operation;
                    if (run is not null)
                        connectionStop = ct.UnsafeRegister(static value => ((IForegroundControlOperation)value!).Stop(), run);
                    ct.ThrowIfCancellationRequested();
                    if (run?.Id == Guid.Empty) throw ForegroundControlWire.Invalid();
                    var snapshot = run?.Snapshot ?? Rejected(result.Rejection);
                    await wire.WriteAsync(ForegroundControlWire.Reply(command, run?.Id ?? Guid.Empty, snapshot, run is not null), ct).ConfigureAwait(false);
                    continue;
                }
                if (body.Length != 17 || !started) throw ForegroundControlWire.Invalid();
                var id = new Guid(body.AsSpan(1, 16), bigEndian: true);
                if (run is null || id != run.Id)
                {
                    await wire.WriteAsync(ForegroundControlWire.Reply(command, Guid.Empty, Rejected(ForegroundControlOutcome.InvalidRequest), false), ct).ConfigureAwait(false);
                    continue;
                }
                bool accepted = command == ForegroundCommand.Status;
                if (command == ForegroundCommand.Stop)
                {
                    run.Stop();
                    stopping = accepted = true;
                }
                else if (!stopping && !run.Completion.IsCompleted)
                {
                    if (command == ForegroundCommand.Pause) accepted = await run.PauseAsync(ct).ConfigureAwait(false);
                    if (command == ForegroundCommand.Resume) accepted = await run.ResumeAsync(ct).ConfigureAwait(false);
                }
                var current = run.Snapshot;
                if (stopping && !run.Completion.IsCompleted) current = new(ForegroundControlState.Stopping);
                await wire.WriteAsync(ForegroundControlWire.Reply(command, id, current, accepted), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (run is not null)
                {
                    run.Stop();
                    await run.Completion.ConfigureAwait(false);
                }
            }
            finally { await connectionStop.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private static ForegroundControlSnapshot Rejected(ForegroundControlOutcome outcome)
        => new(ForegroundControlState.Rejected,
            outcome == ForegroundControlOutcome.None ? ForegroundControlOutcome.InvalidRequest : outcome);

    private int LocalPeer()
    {
        if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out uint pid) || pid is 0 or > int.MaxValue
            || !GetNamedPipeClientSessionId(_pipe.SafePipeHandle, out uint session))
            throw ForegroundControlWire.Invalid();
        using var host = Process.GetCurrentProcess();
        if (session != host.SessionId) throw ForegroundControlWire.Invalid();
        return (int)pid;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            SignalStop();
            return new(_dispose = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { await Completion.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }
}
