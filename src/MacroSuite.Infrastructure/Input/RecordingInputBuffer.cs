using System.Threading.Channels;
using MacroSuite.Core.Models;
using MacroSuite.Core.Input;

namespace MacroSuite.Infrastructure.Input;

/// <summary>A nonblocking recording queue that fails visibly instead of deleting input.</summary>
internal readonly record struct RecordedInput(InputEvent Event, long Sequence = 0, long Epoch = 0,
    int HeldButtons = 0, bool PointerConstrained = false, bool IsClientRelative = false,
    int ClientWidth = 0, int ClientHeight = 0);

internal sealed class RecordingInputBuffer : ChannelWriter<InputEvent>
{
    private readonly Channel<RecordedInput> _channel;
    private readonly Func<InputEvent, RecordedInput?>? _admit;
    private readonly object _gate = new();
    private long _sequence;
    private readonly record struct Candidate(InputEvent Input, RecordedInput? Admission, bool MiniActive);
    private readonly RecordingTransportFilter<Candidate>? _transport;
    private readonly RecordingTransportFilter<Candidate>? _miniTransport;
    private readonly Func<bool>? _miniActive;
    private readonly Exception _overflow;
    private int _completed;

    public RecordingInputBuffer(int capacity = 10_000, Func<InputEvent, RecordedInput?>? admit = null,
        IEnumerable<(uint Modifiers, uint VirtualKey)>? shortcuts = null,
        (uint Modifiers, uint VirtualKey)? miniShortcut = null, Func<bool>? miniActive = null)
    {
        _admit = admit;
        if (shortcuts != null) _transport = new(c => c.Input, shortcuts);
        if (miniShortcut is { } mini) _miniTransport = new(c => c.Input, [mini]);
        _miniActive = miniActive;
        _channel = Channel.CreateBounded<RecordedInput>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _overflow = new InvalidOperationException(
            $"Recording stopped because its {capacity:N0}-event input buffer filled. The captured prefix is preserved; the recording is incomplete.");
    }

    public ChannelReader<InputEvent> Reader => new RawReader(_channel.Reader);
    public ChannelReader<RecordedInput> CapturedReader => _channel.Reader;
    public long LastAcceptedSequence { get { lock (_gate) return _sequence; } }
    public override bool TryWrite(InputEvent item)
    {
        lock (_gate)
        {
            if (_completed != 0) return false;
            RecordedInput? accepted = _admit == null ? new RecordedInput(item) : _admit(item);
            var candidate = new Candidate(item, accepted, _miniActive?.Invoke() == true);
            if (_transport != null) _transport.Process(candidate, FilterMini); else FilterMini(candidate);
            return _completed == 0;
        }
    }

    public override bool TryComplete(Exception? error = null)
    {
        lock (_gate)
        {
            if (_completed != 0) return false;
            _transport?.FlushOnStop(FilterMini);
            _miniTransport?.FlushOnStop(Emit);
            _completed = 1;
            return _channel.Writer.TryComplete(error);
        }
    }

    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
        => _channel.Writer.WaitToWriteAsync(cancellationToken);
    private void FilterMini(Candidate candidate)
    {
        if (_miniTransport != null) _miniTransport.Process(candidate, Emit, candidate.MiniActive);
        else Emit(candidate);
    }
    private void Emit(Candidate candidate)
    {
        if (_completed != 0 || candidate.Admission is not { } packet) return;
        packet = packet with { Sequence = _sequence + 1 };
        if (_channel.Writer.TryWrite(packet)) { _sequence++; return; }
        _completed = 1; _channel.Writer.TryComplete(_overflow);
    }

    private sealed class RawReader(ChannelReader<RecordedInput> reader) : ChannelReader<InputEvent>
    {
        public override Task Completion => reader.Completion;
        public override bool TryRead(out InputEvent item) { bool found = reader.TryRead(out var packet); item = packet.Event; return found; }
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => reader.WaitToReadAsync(cancellationToken);
    }
}
