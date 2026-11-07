using System.Drawing;
using MacroSuite.Core.Vision;

namespace MacroSuite.Core.Execution;

/// <summary>
/// Resolves optional <c>$Variable</c> expressions for coordinate-capable actions before input.
/// Missing or non-integer values fail clearly (no silent fallback).
/// </summary>
public static class CoordinateVariableResolver
{
    public static int Resolve(MacroContext context, string? variable, int fallback, string axis)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(variable))
            return fallback;

        string name = NormalizeName(variable);
        if (context.Variables.TryGet<int>(name, out int value))
            return value;

        if (TryResolveLastDetection(context, name, out int detected))
            return detected;

        throw new InvalidOperationException(
            $"{axis} variable '{variable}' was not found or was not an integer. Set it from a detection step (for example $LastDetectedX or $LastDetection.X) before this action.");
    }

    internal static string NormalizeName(string variable)
    {
        string name = variable[0] == '$' ? variable[1..] : variable;
        return name.Trim();
    }

    internal static bool TryResolveLastDetection(MacroContext context, string name, out int value)
    {
        value = 0;
        if (!TryGetDetection(context, out var detection))
            return false;

        string path = name;
        if (path.StartsWith("Vision.", StringComparison.OrdinalIgnoreCase))
            path = path[7..];
        if (path.StartsWith("LastDetection.", StringComparison.OrdinalIgnoreCase))
            path = path["LastDetection.".Length..];
        else
            return false;

        if (TryReadDetectionInt(detection, path, out value))
            return true;

        throw new InvalidOperationException(
            $"LastDetection path '{name}' is missing or not an integer. Run a detection step that records a match point/region first.");
    }

    private static bool TryGetDetection(MacroContext context, out ColorDetectionResult detection)
    {
        if (context.LastColorDetection is { } latest)
        {
            detection = latest;
            return true;
        }

        if (context.Variables.TryGet<ColorDetectionResult>(MacroVariableNames.LastDetection, out var stored)
            && stored is not null)
        {
            detection = stored;
            return true;
        }

        detection = null!;
        return false;
    }

    private static bool TryReadDetectionInt(ColorDetectionResult detection, string path, out int value)
    {
        value = 0;
        string key = path.Trim();
        if (key.Equals("X", StringComparison.OrdinalIgnoreCase)
            || key.Equals("BestX", StringComparison.OrdinalIgnoreCase)
            || key.Equals("BestMatchPoint.X", StringComparison.OrdinalIgnoreCase))
        {
            if (detection.BestMatchPoint is { } point)
            {
                value = point.X;
                return true;
            }

            return false;
        }

        if (key.Equals("Y", StringComparison.OrdinalIgnoreCase)
            || key.Equals("BestY", StringComparison.OrdinalIgnoreCase)
            || key.Equals("BestMatchPoint.Y", StringComparison.OrdinalIgnoreCase))
        {
            if (detection.BestMatchPoint is { } point)
            {
                value = point.Y;
                return true;
            }

            return false;
        }

        if (key.Equals("Confidence", StringComparison.OrdinalIgnoreCase))
        {
            value = (int)Math.Round(detection.Confidence);
            return true;
        }

        if (key.Equals("Percentage", StringComparison.OrdinalIgnoreCase)
            || key.Equals("MatchPercentage", StringComparison.OrdinalIgnoreCase))
        {
            value = (int)Math.Round(detection.MatchPercentage * 100);
            return true;
        }

        if (key.Equals("Distance", StringComparison.OrdinalIgnoreCase))
        {
            value = (int)Math.Round(detection.Distance);
            return true;
        }

        Rectangle region = detection.MatchingBounds ?? (detection.CaptureRegion.IsEmpty ? Rectangle.Empty : detection.CaptureRegion);
        if (region.IsEmpty)
            return false;

        if (key.Equals("Region.X", StringComparison.OrdinalIgnoreCase)
            || key.Equals("RegionX", StringComparison.OrdinalIgnoreCase)
            || key.Equals("MatchingBounds.X", StringComparison.OrdinalIgnoreCase))
        {
            value = region.X;
            return true;
        }

        if (key.Equals("Region.Y", StringComparison.OrdinalIgnoreCase)
            || key.Equals("RegionY", StringComparison.OrdinalIgnoreCase)
            || key.Equals("MatchingBounds.Y", StringComparison.OrdinalIgnoreCase))
        {
            value = region.Y;
            return true;
        }

        if (key.Equals("Region.Width", StringComparison.OrdinalIgnoreCase)
            || key.Equals("RegionWidth", StringComparison.OrdinalIgnoreCase)
            || key.Equals("MatchingBounds.Width", StringComparison.OrdinalIgnoreCase))
        {
            value = region.Width;
            return true;
        }

        if (key.Equals("Region.Height", StringComparison.OrdinalIgnoreCase)
            || key.Equals("RegionHeight", StringComparison.OrdinalIgnoreCase)
            || key.Equals("MatchingBounds.Height", StringComparison.OrdinalIgnoreCase))
        {
            value = region.Height;
            return true;
        }

        return false;
    }
}
