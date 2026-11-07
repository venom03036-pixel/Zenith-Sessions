namespace MacroSuite.Infrastructure.Storage;

/// <summary>
/// Thrown when a <c>.zmacro</c> binary container file fails format validation.
/// Possible causes: wrong magic bytes, unsupported container version, or CRC32 mismatch
/// indicating file corruption or tampering.
/// </summary>
public sealed class MacroFileFormatException : IOException
{
    /// <summary>Initializes a new instance with a descriptive message.</summary>
    public MacroFileFormatException(string message)
        : base(message) { }

    /// <summary>Initializes a new instance with a message and inner cause.</summary>
    public MacroFileFormatException(string message, Exception innerException)
        : base(message, innerException) { }
}
