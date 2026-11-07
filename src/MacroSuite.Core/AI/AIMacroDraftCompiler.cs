using System.Buffers;
using System.Drawing;
using System.Text.Json;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.Execution;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;

namespace MacroSuite.Core.AI;

public sealed record AIMacroDraft(MacroModel Macro, IReadOnlyList<AIMacroDraftRow> Rows, string Provider, string Model, decimal EstimatedCostUsd);
public sealed record AIMacroDraftRow(int Number, int Depth, string Action, string Detail)
{
    /// <summary>The same bounded, locally cropped PNG embedded in this image action.</summary>
    public byte[]? TemplatePng { get; init; }
}

/// <summary>Strict allow-list compiler. No reflection, polymorphic deserialization or file/network access.</summary>
public sealed class AIMacroDraftCompiler
{
    private readonly CaptureFrame? _frame;
    private readonly IImageEncoder? _encoder;
    private readonly int _sentWidth;
    private readonly int _sentHeight;
    private readonly List<AIMacroDraftRow> _rows = [];
    private int _sourceCount;
    private int _expandedCount;
    private long _templatePixels;
    private long _templateBytes;
    private readonly CancellationToken _ct;

    public AIMacroDraftCompiler(CaptureFrame? frame = null, IImageEncoder? encoder = null, int sentWidth = 0, int sentHeight = 0, CancellationToken ct = default)
    {
        _frame = frame;
        _encoder = encoder;
        _sentWidth = sentWidth;
        _sentHeight = sentHeight;
        _ct = ct;
    }

    public (MacroModel Macro, IReadOnlyList<AIMacroDraftRow> Rows) Compile(string? json)
    {
        _rows.Clear();
        _sourceCount = _expandedCount = 0;
        _templatePixels = _templateBytes = 0;
        _ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(json) || json.Length > AIMacroDraftProtocol.MaxResponseCharacters)
            throw new InvalidDataException("AI returned an empty or oversized macro draft. Try a shorter task.");
        try
        {
            using var document = JsonDocument.Parse(AIActionJsonParser.StripCodeFences(json), new JsonDocumentOptions { MaxDepth = 24 });
            JsonElement root = document.RootElement;
            Fields(root, "name", "description", "repeatUntilStopped", "steps");
            string name = Text(root, "name", 80);
            string description = Text(root, "description", 2000, required: false);
            bool repeat = root.TryGetProperty("repeatUntilStopped", out var repeatValue) && repeatValue.GetBoolean();
            var steps = CompileList(root.GetProperty("steps"), 0, false, false);
            if (steps.Count == 0)
                throw new InvalidDataException("No usable actions were generated. " + (description.Length > 0 ? description : "Describe the keys and waits, or attach a screen area for visual targets."));
            if (steps.Any(RepeatingImageDispatcher.StartsMonitoring) && !repeat)
                throw new InvalidDataException("Independent image monitoring needs Repeat until stopped. Ask for continuous monitoring explicitly.");
            RepeatingImageDispatcher.Validate(steps);
            var macro = new MacroModel
            {
                Name = name, Description = description,
                Settings = new MacroSettings { IsActive = false, Hotkey = string.Empty, RepeatUntilStopped = repeat },
                Steps = steps
            };
            return (macro, _rows.ToArray());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The AI draft was not a valid supported macro. No actions were saved or run.", ex);
        }
    }

    private List<MacroStep> CompileList(JsonElement list, int depth, bool imageAvailable, bool inMonitor)
    {
        _ct.ThrowIfCancellationRequested();
        if (list.ValueKind != JsonValueKind.Array || depth > 4) throw Invalid("Action lists must be arrays with at most four nested levels.");
        var result = new List<MacroStep>();
        bool monitorStarted = false;
        foreach (JsonElement item in list.EnumerateArray())
        {
            _ct.ThrowIfCancellationRequested();
            if (++_sourceCount > 100) throw Invalid("A draft may contain at most 100 actions.");
            string type = Text(item, "type", 32);
            if (monitorStarted && type != "repeat_image") throw Invalid("Put regular actions before independent image groups; each group's actions belong in its body.");
            int before = result.Count;
            switch (type)
            {
                case "key":
                    Fields(item, "type", "key", "modifiers", "holdMs", "delayMs");
                    string key = Text(item, "key", 16);
                    int vk = KeyCode(key);
                    int hold = Number(item, "holdMs", 0, 60_000, 0);
                    int delay = Number(item, "delayMs", 0, 60_000, 50);
                    var modifiers = ReadModifiers(item);
                    foreach (int modifier in modifiers) result.Add(new KeyPressStep(modifier, 0) { Stroke = "Down" });
                    if (hold > 0)
                    {
                        result.Add(new KeyPressStep(vk, 0) { Stroke = "Down" });
                        result.Add(new DelayStep(TimeSpan.FromMilliseconds(hold)));
                        result.Add(new KeyPressStep(vk, 0) { Stroke = "Up" });
                    }
                    else result.Add(new KeyPressStep(vk, 0));
                    for (int i = modifiers.Count - 1; i >= 0; i--) result.Add(new KeyPressStep(modifiers[i], 0) { Stroke = "Up" });
                    if (delay > 0) result.Add(new DelayStep(TimeSpan.FromMilliseconds(delay)));
                    Row(depth, "Press " + (modifiers.Count > 0 ? string.Join("+", modifiers.Select(ModifierName)) + "+" : "") + key, $"Hold {hold} ms · After {delay} ms");
                    break;
                case "wait":
                    Fields(item, "type", "ms");
                    int ms = Number(item, "ms", 0, 60_000);
                    result.Add(new DelayStep(TimeSpan.FromMilliseconds(ms)));
                    Row(depth, "Wait", $"{ms} ms");
                    break;
                case "click":
                case "move":
                    Fields(item, type == "click" ? ["type", "x", "y", "button", "count", "delayMs"] : ["type", "x", "y"]);
                    Point point = PointAt(item);
                    result.Add(type == "click" ? Click(item, point) : new MouseMoveStep(point));
                    Row(depth, type == "click" ? $"{(Number(item, "count", 1, 2, 1) == 2 ? "Double click" : "Click")} {Button(item)}" : "Move pointer",
                        $"X {point.X}, Y {point.Y}" + (type == "click" ? $" · After {Number(item, "delayMs", 0, 60_000, 50)} ms" : ""));
                    break;
                case "click_image":
                    Fields(item, "type", "button", "count", "delayMs");
                    if (!imageAvailable) throw Invalid("Click detected image must follow a positive image match in the same action block.");
                    var click = Click(item, Point.Empty);
                    result.Add(new ClickStep(click.Position, click.Button, click.ClickCount, click.DelayAfterMs)
                    { XVariable = ImageCondition.LastMatchXVariable, YVariable = ImageCondition.LastMatchYVariable, Label = "Click detected image" });
                    Row(depth, "Click detected image", $"{click.Button} · {click.ClickCount} click(s) · Center of latest match · After {click.DelayAfterMs} ms");
                    break;
                case "scroll":
                    Fields(item, "type", "delta");
                    int delta = Number(item, "delta", -1200, 1200);
                    if (delta == 0 || delta % 120 != 0) throw Invalid("Scroll delta must be a nonzero multiple of 120, up to 10 notches.");
                    result.Add(new MouseWheelStep(delta));
                    Row(depth, "Scroll", $"{delta / 120} notches");
                    break;
                case "repeat":
                    Fields(item, "type", "count", "body");
                    int count = Number(item, "count", 1, 1000);
                    Row(depth, "Repeat", $"{count} times");
                    var repeated = CompileList(item.GetProperty("body"), depth + 1, imageAvailable, inMonitor);
                    Nonempty(repeated);
                    result.Add(new LoopStep(count, repeated) { MinBodyDelayMs = 50 });
                    // Nested bodies can change LastImageMatch: do not assume their final match.
                    imageAvailable = false;
                    break;
                case "wait_image":
                case "repeat_image":
                case "if_image":
                case "while_image":
                    string[] fields = type switch
                    {
                        "wait_image" => ["type", "name", "template", "search", "timeoutMs"],
                        "repeat_image" => ["type", "name", "template", "search", "intervalMs", "body"],
                        "if_image" => ["type", "name", "template", "search", "then", "else"],
                        _ => ["type", "name", "template", "search", "count", "intervalMs", "body"]
                    };
                    Fields(item, fields);
                    if (inMonitor && type == "wait_image") throw Invalid("Independent image bodies cannot contain blocking image waits.");
                    if (type == "repeat_image" && (depth != 0 || inMonitor)) throw Invalid("Independent image groups must be beside each other at the top level.");
                    string imageName = Text(item, "name", 80);
                    var image = BuildImage(item, imageName);
                    Rectangle area = image.SearchRegion!.Value;
                    string timing = type switch
                    {
                        "wait_image" => $" · Timeout {Number(item, "timeoutMs", 100, 60_000, 15_000)} ms",
                        "repeat_image" => $" · Between runs {Number(item, "intervalMs", 50, 60_000, 750)} ms",
                        "while_image" => $" · At most {Number(item, "count", 1, 1000, 100)} runs · Delay {Number(item, "intervalMs", 50, 60_000, 750)} ms",
                        _ => ""
                    };
                    Row(depth, type switch { "wait_image" => "Wait for " + imageName, "repeat_image" => "Whenever " + imageName + " is visible", "if_image" => "If " + imageName + " is visible", _ => "While " + imageName + " is visible" }, $"Search {area.Width} × {area.Height} at ({area.X}, {area.Y})" + timing, image.TemplateBytes);
                    if (type == "wait_image")
                    {
                        result.Add(new WaitUntilStep(image) { Timeout = TimeSpan.FromMilliseconds(Number(item, "timeoutMs", 100, 60_000, 15_000)), PollInterval = TimeSpan.FromMilliseconds(100), ThrowOnTimeout = true, Label = "Wait for " + imageName });
                        imageAvailable = true;
                    }
                    else if (type == "repeat_image")
                    {
                        monitorStarted = true;
                        result.Add(new WaitUntilStep(image) { RepeatWhileVisible = true, RepeatDelayMs = Number(item, "intervalMs", 50, 60_000, 750), PollInterval = TimeSpan.FromMilliseconds(100), Label = "Repeat while visible: " + imageName });
                        var body = CompileList(item.GetProperty("body"), depth + 1, true, true);
                        Nonempty(body);
                        result.AddRange(body);
                    }
                    else if (type == "if_image")
                    {
                        var thenSteps = CompileList(item.GetProperty("then"), depth + 1, true, inMonitor);
                        Nonempty(thenSteps);
                        List<MacroStep> elseSteps = [];
                        if (item.TryGetProperty("else", out var other))
                        {
                            if (other.ValueKind != JsonValueKind.Array) throw Invalid("An ELSE body must be an action list.");
                            if (other.GetArrayLength() > 0) Row(depth + 1, "Otherwise", "When the image is not visible");
                            elseSteps = CompileList(other, depth + 1, false, inMonitor);
                        }
                        result.Add(new IfThenElseStep(image, thenSteps, elseSteps) { Label = "If " + imageName + " is visible", Routing = new BlockRoutingOptions { OnNotSatisfied = FlowRoute.Continue } });
                        imageAvailable = false;
                    }
                    else
                    {
                        var body = CompileList(item.GetProperty("body"), depth + 1, true, inMonitor);
                        Nonempty(body);
                        result.Add(new LoopStep(Number(item, "count", 1, 1000, 100), body, image)
                        { MinBodyDelayMs = Number(item, "intervalMs", 50, 60_000, 750), CheckIntervalMs = 100, Label = "While " + imageName + " is visible", Routing = new BlockRoutingOptions { OnNotSatisfied = FlowRoute.Continue } });
                        imageAvailable = false;
                    }
                    break;
                default: throw Invalid("The draft contains an unsupported action. Only keyboard, mouse, waits and image conditions are available.");
            }
            // Count conservatively; flattened monitor bodies are counted twice to keep allocation bounded.
            _expandedCount += result.Count - before;
            if (_expandedCount > 1000) throw Invalid("The expanded macro exceeds 1,000 actions.");
        }
        return result;
    }

    private ImageCondition BuildImage(JsonElement item, string name)
    {
        RequireFrame();
        Rectangle crop = RectangleAt(item.GetProperty("template"));
        Rectangle search = RectangleAt(item.GetProperty("search"));
        if (!search.Contains(crop)) throw Invalid("The search area must contain the entire template crop.");
        _templatePixels += (long)crop.Width * crop.Height;
        if (_templatePixels > 4_000_000) throw Invalid("Image templates are too large. Choose tighter crops.");
        int bpp = CaptureFrame.GetBytesPerPixel(_frame!.Format);
        int stride = checked(crop.Width * bpp);
        byte[] template = EncodeCrop(crop, bpp, stride);
        _templateBytes += template.Length;
        if (_templateBytes > 16_000_000) throw Invalid("Image templates exceed the draft's storage limit.");
        return new ImageCondition(template, 0.85, search, name);
    }

    private byte[] EncodeCrop(Rectangle crop, int bpp, int stride)
    {
        var owner = MemoryPool<byte>.Shared.Rent(checked(stride * crop.Height));
        try
        {
            for (int y = 0; y < crop.Height; y++)
            {
                int offset = ((crop.Y - _frame!.OriginY + y) * _frame.Stride) + (crop.X - _frame.OriginX) * bpp;
                _frame.Span.Slice(offset, stride).CopyTo(owner.Memory.Span.Slice(y * stride, stride));
            }
            using var frame = new CaptureFrame(crop.Width, crop.Height, stride, _frame!.Format, _frame.Source, owner, originX: crop.X, originY: crop.Y);
            owner = null!; // frame owns and disposes the buffer.
            return _encoder!.EncodePng(frame);
        }
        finally { owner?.Dispose(); }
    }

    private Point PointAt(JsonElement item)
    {
        RequireFrame();
        int x = Number(item, "x", 0, _sentWidth - 1);
        int y = Number(item, "y", 0, _sentHeight - 1);
        return new Point(checked(_frame!.OriginX + (int)((long)x * _frame.Width / _sentWidth)), checked(_frame.OriginY + (int)((long)y * _frame.Height / _sentHeight)));
    }

    private Rectangle RectangleAt(JsonElement item)
    {
        Fields(item, "x", "y", "width", "height");
        int x = Number(item, "x", 0, _sentWidth - 1);
        int y = Number(item, "y", 0, _sentHeight - 1);
        int w = Number(item, "width", 1, _sentWidth);
        int h = Number(item, "height", 1, _sentHeight);
        if ((long)x + w > _sentWidth || (long)y + h > _sentHeight) throw Invalid("An image region extends outside the attached screenshot.");
        int left = (int)((long)x * _frame!.Width / _sentWidth);
        int top = (int)((long)y * _frame.Height / _sentHeight);
        int right = (int)Math.Ceiling((x + w) * (double)_frame.Width / _sentWidth);
        int bottom = (int)Math.Ceiling((y + h) * (double)_frame.Height / _sentHeight);
        return new Rectangle(checked(_frame.OriginX + left), checked(_frame.OriginY + top), right - left, bottom - top);
    }

    private void RequireFrame()
    {
        if (_frame is null || _frame.IsDisposed || _encoder is null || _sentWidth <= 0 || _sentHeight <= 0)
            throw Invalid("Clicks and image conditions need an attached screen area. Choose the area, then generate again.");
    }

    private static ClickStep Click(JsonElement item, Point point) => new(point, Button(item), Number(item, "count", 1, 2, 1), Number(item, "delayMs", 0, 60_000, 50));
    private static MouseButton Button(JsonElement item) => Text(item, "button", 10, false) switch
    { "" or "left" => MouseButton.Left, "right" => MouseButton.Right, "middle" => MouseButton.Middle, _ => throw Invalid("Mouse button must be left, right or middle.") };
    private void Row(int depth, string action, string detail, byte[]? template = null)
        => _rows.Add(new AIMacroDraftRow(_rows.Count + 1, depth, action, detail) { TemplatePng = template });
    private static void Nonempty(IReadOnlyCollection<MacroStep> steps) { if (steps.Count == 0) throw Invalid("Action blocks cannot be empty."); }
    private static InvalidDataException Invalid(string message) => new(message);

    private static void Fields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("Each draft item must be a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!seen.Add(field.Name) || !allowed.Contains(field.Name, StringComparer.Ordinal))
                throw Invalid("The draft contains an unexpected or duplicate field.");
    }
    private static string Text(JsonElement item, string name, int max, bool required = true)
    {
        if (!item.TryGetProperty(name, out var field)) { if (!required) return ""; throw Invalid("A required draft field is missing."); }
        if (field.ValueKind != JsonValueKind.String) throw Invalid("A draft text field has an invalid type.");
        string text = field.GetString()!.Trim();
        if ((required && text.Length == 0) || text.Length > max || text.Any(char.IsControl)) throw Invalid("A draft text field is empty, too long or contains control characters.");
        return text;
    }
    private static int Number(JsonElement item, string name, int min, int max, int? fallback = null)
    {
        if (!item.TryGetProperty(name, out var value)) return fallback ?? throw Invalid("A required numeric draft field is missing.");
        if (!value.TryGetInt32(out int number) || number < min || number > max) throw Invalid($"Draft {name} must be an integer between {min} and {max}.");
        return number;
    }
    private static List<int> ReadModifiers(JsonElement item)
    {
        var result = new List<int>();
        if (!item.TryGetProperty("modifiers", out var mods)) return result;
        if (mods.ValueKind != JsonValueKind.Array || mods.GetArrayLength() > 3) throw Invalid("At most three keyboard modifiers are allowed.");
        foreach (var value in mods.EnumerateArray())
        {
            int vk = value.GetString() switch { "Ctrl" => 17, "Alt" => 18, "Shift" => 16, _ => throw Invalid("Only Ctrl, Alt and Shift modifiers are supported.") };
            if (result.Contains(vk)) throw Invalid("Keyboard modifiers must be unique.");
            result.Add(vk);
        }
        return result;
    }
    private static string ModifierName(int key) => key switch { 17 => "Ctrl", 18 => "Alt", _ => "Shift" };
    private static int KeyCode(string key)
    {
        string upper = key.ToUpperInvariant();
        if (upper.Length == 1 && (upper[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) return upper[0];
        if (upper.StartsWith('F') && int.TryParse(upper.AsSpan(1), out int f) && f is >= 1 and <= 24) return 111 + f;
        return upper switch { "ENTER" => 13, "ESCAPE" => 27, "SPACE" => 32, "TAB" => 9, "BACKSPACE" => 8, "DELETE" => 46, "INSERT" => 45, "HOME" => 36, "END" => 35, "PAGEUP" => 33, "PAGEDOWN" => 34, "LEFT" => 37, "UP" => 38, "RIGHT" => 39, "DOWN" => 40, _ => throw Invalid("The draft uses an unsupported keyboard key.") };
    }
}
