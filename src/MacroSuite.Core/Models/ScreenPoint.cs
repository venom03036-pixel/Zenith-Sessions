namespace MacroSuite.Core.Models;

/// <summary>Absolute virtual-desktop pixel. Same space as recorded clicks and playback.</summary>
public readonly record struct ScreenPoint(int X, int Y);
