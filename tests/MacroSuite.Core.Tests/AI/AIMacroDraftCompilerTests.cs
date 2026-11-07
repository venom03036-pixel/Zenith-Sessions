using System.Buffers;
using System.Drawing;
using System.Text.Json;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;
using MacroSuite.Core.Storage;
using Xunit;

namespace MacroSuite.Core.Tests.AI;

public class AIMacroDraftCompilerTests
{
    private static string Draft(string steps, bool repeat = false) => "{\"name\":\"Generated\",\"description\":\"Review this macro\",\"repeatUntilStopped\":" + (repeat ? "true" : "false") + ",\"steps\":[" + steps + "]}";
    private const string Image = "\"name\":\"OK\",\"template\":{\"x\":2,\"y\":3,\"width\":4,\"height\":2},\"search\":{\"x\":1,\"y\":1,\"width\":10,\"height\":8}";

    [Fact]
    public void KeyboardCombosAreBalancedAndSavedWithoutAnActiveShortcut()
    {
        var (macro, rows) = new AIMacroDraftCompiler().Compile(Draft("""{"type":"key","key":"L","modifiers":["Ctrl","Shift"],"holdMs":50,"delayMs":20},{"type":"wait","ms":150}"""));
        var keys = macro.Steps.OfType<KeyPressStep>().ToArray();
        Assert.Equal(new[] { 17, 16, 76, 76, 16, 17 }, keys.Select(x => x.KeyCode));
        Assert.Equal(new[] { "Down", "Down", "Down", "Up", "Up", "Up" }, keys.Select(x => x.Stroke));
        Assert.False(macro.Settings.IsActive);
        Assert.Equal("", macro.Settings.Hotkey);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void IndependentImagesCompileToSiblingTriggersWithRealCroppedPixels()
    {
        using var frame = Frame(); var encoder = new Encoder();
        var compiler = new AIMacroDraftCompiler(frame, encoder, 20, 10);
        string group = "{\"type\":\"repeat_image\"," + Image + ",\"intervalMs\":50,\"body\":[{\"type\":\"click_image\"}]}";
        var (macro, rows) = compiler.Compile(Draft(group + "," + group, true));
        Assert.Equal(4, macro.Steps.Count);
        Assert.All(macro.Steps.OfType<WaitUntilStep>(), w => Assert.True(w.RepeatWhileVisible));
        var condition = Assert.IsType<ImageCondition>(((WaitUntilStep)macro.Steps[0]).Condition);
        Assert.Null(condition.TemplatePath);
        Assert.NotEmpty(condition.TemplateBytes!);
        Assert.Equal(new Rectangle(-98, 52, 20, 16), condition.SearchRegion);
        Assert.Equal(8, encoder.Crops[0].Width); Assert.Equal(4, encoder.Crops[0].Height);
        Assert.Equal((byte)6, encoder.Crops[0].Bytes[0]); // source row 6
        Assert.Equal(ImageCondition.LastMatchXVariable, Assert.IsType<ClickStep>(macro.Steps[1]).XVariable);
        var serializer = new MacroJsonSerializer();
        string json = serializer.Serialize(macro);
        var restored = serializer.Deserialize(json);
        Assert.Equal(condition.TemplateBytes, Assert.IsType<ImageCondition>(Assert.IsType<WaitUntilStep>(restored.Steps[0]).Condition).TemplateBytes);
        Assert.Equal(4, rows.Count);
        Assert.Same(condition.TemplateBytes, rows[0].TemplatePng);
        Assert.Null(rows[1].TemplatePng);
        Assert.Contains("50 ms", rows[0].Detail);
    }

    [Fact]
    public void ScreenClicksMapFromSentPixelsToNegativeDesktopOrigin()
    {
        using var frame = Frame();
        var (macro, _) = new AIMacroDraftCompiler(frame, new Encoder(), 20, 10).Compile(Draft("""{"type":"click","x":19,"y":9}"""));
        Assert.Equal(new Point(-62, 68), Assert.IsType<ClickStep>(macro.Steps[0]).Position);
    }

    [Fact]
    public void IfAndWhileAreTypedEditableBlocksWithContinueOnAbsent()
    {
        using var frame = Frame();
        var (macro, _) = new AIMacroDraftCompiler(frame, new Encoder(), 20, 10).Compile(Draft("{\"type\":\"if_image\"," + Image + ",\"then\":[{\"type\":\"click_image\"}],\"else\":[{\"type\":\"key\",\"key\":\"M\"}]},{\"type\":\"while_image\"," + Image + ",\"count\":8,\"body\":[{\"type\":\"click_image\"}]}"));
        var conditional = Assert.IsType<IfThenElseStep>(macro.Steps[0]);
        Assert.Equal(FlowRoute.Continue, conditional.Routing.OnNotSatisfied);
        Assert.NotEmpty(conditional.ElseSteps);
        var loop = Assert.IsType<LoopStep>(macro.Steps[1]); Assert.Equal(8, loop.MaxIterations);
        Assert.Equal(750, loop.MinBodyDelayMs);
    }

    [Theory]
    [InlineData("{\"type\":\"click\",\"x\":0,\"y\":0}")]
    [InlineData("{\"type\":\"click_image\"}")]
    [InlineData("{\"type\":\"shell\",\"command\":\"something\"}")]
    [InlineData("{\"type\":\"key\",\"key\":\"M\",\"path\":\"untrusted\"}")]
    [InlineData("{\"type\":\"key\",\"key\":\"M\",\"modifiers\":[\"Ctrl\",\"Ctrl\"]}")]
    [InlineData("{\"type\":\"wait\",\"ms\":999999999}")]
    [InlineData("{\"type\":\"wait\",\"ms\":50,\"ms\":60}")]
    [InlineData("{\"type\":\"repeat\",\"count\":2,\"body\":[]}")]
    [InlineData("{\"type\":\"scroll\",\"delta\":12}")]
    public void UntrustedOrUnboundedActionsAreRejected(string steps)
        => Assert.Throws<InvalidDataException>(() => new AIMacroDraftCompiler().Compile(Draft(steps)));

    [Theory]
    [InlineData("{\"type\":\"click\",\"x\":20,\"y\":0}")]
    [InlineData("{\"type\":\"click\",\"x\":-1,\"y\":0}")]
    [InlineData("{\"type\":\"wait_image\",\"name\":\"bad\",\"template\":{\"x\":19,\"y\":0,\"width\":2,\"height\":2},\"search\":{\"x\":0,\"y\":0,\"width\":20,\"height\":10}}")]
    [InlineData("{\"type\":\"wait_image\",\"name\":\"bad\",\"template\":{\"x\":5,\"y\":0,\"width\":2,\"height\":2},\"search\":{\"x\":0,\"y\":0,\"width\":2,\"height\":2}}")]
    public void InvalidCoordinatesAreRejectedInsteadOfClamped(string steps)
    {
        using var frame = Frame();
        Assert.Throws<InvalidDataException>(() => new AIMacroDraftCompiler(frame, new Encoder(), 20, 10).Compile(Draft(steps)));
    }

    [Fact]
    public void RejectsMoreThan100StepsAndDeepNesting()
    {
        string steps = string.Join(',', Enumerable.Repeat("{\"type\":\"wait\",\"ms\":1}", 101));
        Assert.Throws<InvalidDataException>(() => new AIMacroDraftCompiler().Compile(Draft(steps)));
        steps = "{\"type\":\"wait\",\"ms\":1}";
        for (int i = 0; i < 5; i++) steps = "{\"type\":\"repeat\",\"count\":1,\"body\":[" + steps + "]}";
        Assert.Throws<InvalidDataException>(() => new AIMacroDraftCompiler().Compile(Draft(steps)));
    }

    [Fact]
    public void RejectsAmbiguousImageMonitorLayout()
    {
        using var frame = Frame();
        string group = "{\"type\":\"repeat_image\"," + Image + ",\"body\":[{\"type\":\"click_image\"}]}";
        var compiler = new AIMacroDraftCompiler(frame, new Encoder(), 20, 10);
        Assert.Throws<InvalidDataException>(() => compiler.Compile(Draft(group)));
        Assert.Throws<InvalidDataException>(() => compiler.Compile(Draft(group + ",{\"type\":\"wait\",\"ms\":1}", true)));
    }

    private static CaptureFrame Frame()
    {
        var owner = MemoryPool<byte>.Shared.Rent(40 * 20 * 4);
        for (int row = 0; row < 20; row++) owner.Memory.Span.Slice(row * 160, 160).Fill((byte)row);
        return new CaptureFrame(40, 20, 160, FramePixelFormat.Bgra32, CaptureSource.Gdi, owner, originX: -100, originY: 50);
    }
    private sealed class Encoder : IImageEncoder
    {
        public List<(int Width, int Height, byte[] Bytes)> Crops { get; } = [];
        public byte[] EncodeJpeg(CaptureFrame frame, int quality = 80) => [1, 2];
        public byte[] EncodePng(CaptureFrame frame) { byte[] data = frame.Span.ToArray(); Crops.Add((frame.Width, frame.Height, data)); return data; }
    }
}
