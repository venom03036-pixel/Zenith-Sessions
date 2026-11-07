using System.Text.Json.Serialization;

namespace MacroSuite.Core.Models;

/// <summary>How a camera sweep distributes its total displacement over time.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MouseInterpolation
{
    Linear = 0,
    Smooth = 1,
    EaseInOut = 2,
    Bezier = 3,
    SmootherStep = 4
}

/// <summary>Named sweep presets. Individual Hz / ease / filter fields remain the source of truth.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MouseSweepProfile
{
    Precise = 0,
    Smooth = 1,
    Cinematic = 2,
    Custom = 3
}
