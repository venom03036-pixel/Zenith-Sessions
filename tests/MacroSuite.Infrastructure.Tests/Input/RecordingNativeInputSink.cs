using System.Diagnostics;
using MacroSuite.Infrastructure.Input;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.Infrastructure.Tests.Input;

internal sealed class RecordingNativeInputSink : INativeInputSink
{
    public readonly List<NativeMethods.INPUT> Inputs = [];
    public readonly List<long> Timestamps = [];
    public readonly HashSet<int> PhysicallyHeld = [];
    public int AcceptLimit { get; set; } = int.MaxValue;
    public int SendCalls { get; private set; }
    public Action? OnSend { get; set; }
    public bool SuppressAbsoluteMoves { get; set; }
    public (int X, int Y) CursorPosition => (0, 0);
    public (int X, int Y, int Width, int Height) VirtualScreen => (-1920, 0, 3840, 1080);
    public bool IsPressed(int virtualKey) => PhysicallyHeld.Contains(virtualKey);

    public uint Send(NativeMethods.INPUT[] inputs, int count)
    {
        SendCalls++;
        int accepted = Math.Min(count, AcceptLimit);
        for (int i = 0; i < accepted; i++)
        {
            Inputs.Add(inputs[i]);
            Timestamps.Add(Stopwatch.GetTimestamp());
        }
        OnSend?.Invoke();
        return (uint)accepted;
    }
}
