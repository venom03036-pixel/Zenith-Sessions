namespace MacroSuite.Core.AI;

/// <summary>
/// Provider-chosen encode target. The adapter computes this so the engine never
/// guesses OpenAI tiles vs Claude token budgets vs Gemini normalization.
/// </summary>
public sealed record VisionEncodePlan(
    int SentWidth,
    int SentHeight,
    VisionCoordinateSpace Space,
    int? NormalizedMax = null,
    bool SwapXY = false,
    string? DetailParameter = null)
{
    public static VisionEncodePlan PixelImage(int width, int height, string? detail = null)
        => new(Math.Max(1, width), Math.Max(1, height), VisionCoordinateSpace.PixelRelativeToSentImage, null, false, detail);
}
