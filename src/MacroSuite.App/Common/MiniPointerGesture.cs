namespace MacroSuite.App.Common;

/// <summary>Physical-pixel gesture state. Commands remain ordinary release-clicks until movement crosses the native threshold.</summary>
public sealed class MiniPointerGesture
{
    private double _startX, _startY;
    private bool _pressed, _dragged, _cancelled;
    public bool IsDragging => _pressed && _dragged;
    public void Begin(double x, double y) { _startX = x; _startY = y; _pressed = true; _dragged = false; _cancelled = false; }
    public bool Move(double x, double y, double horizontalThreshold, double verticalThreshold)
    {
        if (!_pressed || _dragged || _cancelled) return false;
        if (Math.Abs(x - _startX) < horizontalThreshold && Math.Abs(y - _startY) < verticalThreshold) return false;
        return _dragged = true;
    }
    public bool End() { bool suppress = _pressed && (_dragged || _cancelled); _pressed = false; return suppress; }
    public void Cancel() { if (_pressed) _cancelled = true; }
}
