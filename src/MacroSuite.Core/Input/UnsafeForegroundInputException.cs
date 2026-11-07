namespace MacroSuite.Core.Input;

/// <summary>
/// Thrown when the single input pipeline refuses to send synthesized input because the
/// foreground window is a blocked shell, admin tool, or higher-integrity process.
/// </summary>
public sealed class UnsafeForegroundInputException : InvalidOperationException
{
    public UnsafeForegroundInputException(string message)
        : base(message)
    {
    }
}
