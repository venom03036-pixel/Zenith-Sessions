using FluentAssertions;
using MacroSuite.Core.AI;
using Xunit;

namespace MacroSuite.Core.Tests.AI;

public sealed class AIActionJsonParserTests
{
    [Fact]
    public void Parse_MarkdownFence_StripsWrapper()
    {
        const string raw = """
            ```json
            {"action_type":"click","x":10.4,"y":20.6,"confidence":0.91}
            ```
            """;

        var action = AIActionJsonParser.Parse(raw, "Gemini");

        action.ActionType.Should().Be("click");
        action.X.Should().Be(10);
        action.Y.Should().Be(21);
        action.Confidence.Should().BeApproximately(0.91, 0.0001);
    }

    [Fact]
    public void Parse_MissingConfidence_IsNull()
    {
        var action = AIActionJsonParser.Parse("""{"action_type":"none"}""", "Gemini");
        action.Confidence.Should().BeNull();
    }

    [Fact]
    public void Parse_FloatCoordinates_RoundAwayFromZero()
    {
        var action = AIActionJsonParser.Parse(
            """{"action_type":"click","x":100.5,"y":-3.5,"confidence":1}""",
            "OpenAI");

        action.X.Should().Be(101);
        action.Y.Should().Be(-4);
    }

    [Fact]
    public void Parse_EmptyPayload_FailsClosed()
    {
        var act = () => AIActionJsonParser.Parse("  ", "Anthropic");
        act.Should().Throw<InvalidDataException>().WithMessage("*empty*");
    }

    [Fact]
    public void Parse_MalformedJson_FailsClosed()
    {
        var act = () => AIActionJsonParser.Parse("{not-json", "Gemini");
        act.Should().Throw<InvalidDataException>().WithMessage("*malformed*");
    }

    [Fact]
    public void Parse_UnexpectedField_FailsClosed()
    {
        var act = () => AIActionJsonParser.Parse(
            """{"action_type":"click","x":1,"y":2,"confidence":0.9,"shell":"cmd.exe"}""",
            "OpenAI");
        act.Should().Throw<InvalidDataException>();
    }
}
