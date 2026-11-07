using MacroSuite.Core.Models;
using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.Infrastructure.Input;

/// <summary>Read-only desktop observations used to filter recording input.</summary>
public interface IRecordingDesktopState
{
    bool IsOwnWindowAt(int x, int y);
    bool IsOwnForegroundWindow();
    bool IsAnyMouseButtonDown();
    bool IsPointerConstrained();
    bool IsMouseButtonDown(MouseButton button);
}

public sealed class RecordingDesktopState : IRecordingDesktopState
{
    public bool IsOwnWindowAt(int x, int y) => RecordingWindowFilter.IsOwnWindowAt(x, y);
    public bool IsOwnForegroundWindow() => RecordingWindowFilter.IsOwnForegroundWindow();
    public bool IsAnyMouseButtonDown() => RecordingWindowFilter.IsAnyMouseButtonDown();
    public bool IsPointerConstrained() => RecordingWindowFilter.IsPointerConstrained();
    public bool IsMouseButtonDown(MouseButton button) => RecordingWindowFilter.IsMouseButtonDown(button);
}
