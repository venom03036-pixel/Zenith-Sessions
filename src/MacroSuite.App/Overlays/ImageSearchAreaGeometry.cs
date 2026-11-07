using System.Drawing;

namespace MacroSuite.App.Overlays;

/// <summary>Search-box arithmetic uses physical pixels, independently of display scaling.</summary>
public static class ImageSearchAreaGeometry
{
    public static Rectangle CenterOnDisplay(Size template, Rectangle display, Rectangle desktop)
        => Place(new Rectangle(display.X + (display.Width - template.Width) / 2,
            display.Y + (display.Height - template.Height) / 2, template.Width, template.Height), template, desktop);

    public static Rectangle ResetToTemplateSize(Rectangle area, Size template, Rectangle desktop)
        => Place(new Rectangle(area.Location, template), template, desktop);

    public static Rectangle Place(Rectangle? initial, Size template, Rectangle desktop)
    {
        if (template.Width <= 0 || template.Height <= 0 || template.Width > desktop.Width || template.Height > desktop.Height)
            throw new ArgumentException("The complete template must fit inside the connected desktop.");
        Rectangle area = initial ?? new Rectangle(desktop.X + (desktop.Width - template.Width) / 2,
            desktop.Y + (desktop.Height - template.Height) / 2, template.Width, template.Height);
        int width = Math.Clamp(area.Width, template.Width, desktop.Width);
        int height = Math.Clamp(area.Height, template.Height, desktop.Height);
        return new Rectangle(Math.Clamp(area.X, desktop.Left, desktop.Right - width),
            Math.Clamp(area.Y, desktop.Top, desktop.Bottom - height), width, height);
    }

    public static Rectangle Move(Rectangle start, int dx, int dy, Size template, Rectangle desktop)
        => Place(new Rectangle(start.X + dx, start.Y + dy, start.Width, start.Height), template, desktop);

    public static Rectangle Resize(Rectangle start, int dx, int dy, Size template, Rectangle desktop)
        => Place(new Rectangle(start.X, start.Y,
            Math.Clamp(start.Width + dx, template.Width, Math.Max(template.Width, desktop.Right - start.X)),
            Math.Clamp(start.Height + dy, template.Height, Math.Max(template.Height, desktop.Bottom - start.Y))), template, desktop);
}
