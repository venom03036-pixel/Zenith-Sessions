namespace MacroSuite.Core.AI;

/// <summary>
/// Coordinate space of an AI vision response relative to the image we sent.
/// </summary>
public enum VisionCoordinateSpace
{
    /// <summary>Pixels in the JPEG we uploaded (OpenAI tiled, Anthropic standard).</summary>
    PixelRelativeToSentImage = 0,

    /// <summary>1:1 with the uploaded pixels (Anthropic Opus 4.7+ when pre-sized).</summary>
    PixelAbsolute = 1,

    /// <summary>Normalized 0..N, typically 1000 (Gemini, some NVIDIA models).</summary>
    Normalized = 2
}
