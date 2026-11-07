using MacroSuite.Core.Models;

namespace MacroSuite.Core.Input;

/// <summary>One recording consumer owns this bounded filter. Delayed packets retain their original metadata.</summary>
public sealed class RecordingTransportFilter<T>
{
    private readonly Func<T, InputEvent> _input;
    private readonly (uint Modifiers, uint VirtualKey)[] _chords;
    private readonly bool[] _held = new bool[256], _emittedModifier = new bool[256], _suppressedKey = new bool[256];
    private readonly List<(int Key, T Packet, bool Transport)> _pending = new(32);

    public RecordingTransportFilter(Func<T, InputEvent> input, IEnumerable<(uint Modifiers, uint VirtualKey)> chords)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _chords = chords.Where(c => c.VirtualKey is > 0 and < 256 && Modifier((int)c.VirtualKey) == 0 && (c.Modifiers & ~15u) == 0)
            .Distinct().Take(4097).ToArray();
        if (_chords.Length > 4096) throw new ArgumentOutOfRangeException(nameof(chords), "The transport shortcut collection exceeds its limit.");
    }

    /// <summary>Emits zero or more original packets. Call only on the serialized ingress owner, before admission fences.</summary>
    public void Process(T packet, Action<T> emit, bool shortcutsActive = true)
    {
        var input = _input(packet);
        bool keyboard = input.Type is InputEventType.KeyDown or InputEventType.KeyUp;
        if (!keyboard || input.Data is < 1 or > 255) { FlushPending(emit); emit(packet); return; }
        int key = input.Data; bool down = input.Type == InputEventType.KeyDown;
        if (Modifier(key) != 0)
        {
            if (down)
            {
                if (!_held[key])
                { if (_pending.Count == 32) FlushPending(emit); _held[key] = true; _pending.Add((key, packet, false)); }
                else if (_emittedModifier[key]) emit(packet);
                else if (!_pending.Any(p => p.Key == key && p.Transport))
                { if (_pending.Count == 32) { FlushPending(emit); emit(packet); } else _pending.Add((key, packet, false)); }
            }
            else
            {
                _held[key] = false;
                int index = _pending.FindIndex(p => p.Key == key);
                if (index >= 0 && _pending[index].Transport) { _pending.RemoveAll(p => p.Key == key); return; }
                FlushPending(emit);
                if (_emittedModifier[key]) { emit(packet); _emittedModifier[key] = false; }
                else emit(packet); // Existing ingress quarantine decides whether an initial orphan up is admissible.
            }
            return;
        }
        if (!down)
        {
            _held[key] = false;
            if (_suppressedKey[key]) { _suppressedKey[key] = false; return; }
            FlushPending(emit); emit(packet); return;
        }
        if (_held[key] && _suppressedKey[key]) return;
        if (_held[key]) { FlushPending(emit); emit(packet); return; }
        _held[key] = true;
        uint modifiers = CurrentModifiers();
        bool transport = false;
        if (shortcutsActive)
            for (int i = 0; i < _chords.Length; i++)
                if (_chords[i].VirtualKey == (uint)key && _chords[i].Modifiers == modifiers) { transport = true; break; }
        if (transport)
        {
            _suppressedKey[key] = true;
            for (int i = 0; i < _pending.Count; i++) _pending[i] = (_pending[i].Key, _pending[i].Packet, true);
            return;
        }
        FlushPending(emit); emit(packet);
    }

    /// <summary>Normal modifier taps survive Stop; uncommitted transport-only modifier pairs are excluded.</summary>
    public void FlushOnStop(Action<T> emit)
    {
        for (int i = 0; i < _pending.Count; i++) if (!_pending[i].Transport) emit(_pending[i].Packet);
        _pending.Clear(); Array.Clear(_held); Array.Clear(_emittedModifier); Array.Clear(_suppressedKey);
    }
    private void FlushPending(Action<T> emit)
    {
        for (int i = 0; i < _pending.Count; i++) { emit(_pending[i].Packet); _emittedModifier[_pending[i].Key] = true; }
        _pending.Clear();
    }
    private uint CurrentModifiers()
    {
        uint result = 0;
        for (int key = 0; key < _held.Length; key++) if (_held[key]) result |= Modifier(key);
        return result;
    }
    private static uint Modifier(int key) => key switch
    { 0x12 or 0xA4 or 0xA5 => 1, 0x11 or 0xA2 or 0xA3 => 2, 0x10 or 0xA0 or 0xA1 => 4, 0x5b or 0x5c => 8, _ => 0 };
}
