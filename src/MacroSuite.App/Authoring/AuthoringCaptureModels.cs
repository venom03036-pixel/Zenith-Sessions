using System.Drawing;

namespace MacroSuite.App.Authoring;

// UI intents and detached samples only. Native frames stay with the preparation owner.
internal enum ImageTemplateSourceKind { Screen, File }

internal sealed record ImageTemplateSourceRequest(
    ImageTemplateSourceKind Kind, string? FilePath, double Confidence, bool AutoClick);

internal readonly record struct AuthoringPixelSample(int X, int Y, byte R, byte G, byte B);

internal sealed record AuthoringVisualSample(
    Rectangle Region, int Width, int Height, byte[] Bgra32, byte R, byte G, byte B);
