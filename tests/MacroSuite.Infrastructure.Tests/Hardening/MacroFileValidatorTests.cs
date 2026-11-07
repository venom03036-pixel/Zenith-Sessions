using MacroSuite.Core.Storage;
using MacroSuite.Infrastructure.Security;
using Xunit;
using FluentAssertions;

namespace MacroSuite.Infrastructure.Tests.Hardening;

/// <summary>
/// Tests for <see cref="MacroFileValidator"/>.
/// </summary>
public sealed class MacroFileValidatorTests
{
    private readonly MacroFileValidator _sut = new();

    // ── Happy path ─────────────────────────────────────────────────────────

    [Fact]
    public void Validate_CleanMacro_ReturnsValid()
    {
        string json = """
            {
                "schemaVersion": 2,
                "id": "11111111-1111-1111-1111-111111111111",
                "name": "My Macro",
                "steps": [
                    { "$type": "delay", "stepId": "22222222-2222-2222-2222-222222222222", "duration": 500 }
                ]
            }
            """;

        var result = _sut.Validate(json, "test.zmacro");

        result.IsValid.Should().BeTrue();
        result.Violations.Should().BeEmpty();
    }

    [Fact]
    public void Validate_AllKnownStepTypes_ReturnsValid()
    {
        // Every known $type should not trigger a violation
        var knownTypes = StepTypeRegistry.KnownDiscriminators;

        foreach (var type in knownTypes)
        {
            string json = $"{{\"steps\":[{{\"$type\":\"{type}\",\"stepId\":\"00000000-0000-0000-0000-000000000000\"}}]}}";
            var result = _sut.Validate(json, $"{type}.zmacro");
            result.IsValid.Should().BeTrue($"type '{type}' should be allowed");
        }
    }

    // ── Size limits ────────────────────────────────────────────────────────

    [Fact]
    public void Validate_JsonExceeding50MB_ReturnsInvalidWithViolation()
    {
        // 50 MB + 1 byte
        string json = new string('a', 50 * 1024 * 1024 + 1);

        var result = _sut.Validate(json, "huge.zmacro");

        result.IsValid.Should().BeFalse();
        result.Violations.Should().ContainMatch("*zip bomb*");
    }

    [Fact]
    public void Validate_OversizedStringField_ReturnsViolation()
    {
        string bigValue = new string('x', 600_000); // > 512 KB
        string json = $"{{\"name\":\"{bigValue}\"}}";

        var result = _sut.Validate(json, "oversized.zmacro");

        result.Violations.Should().ContainMatch("*exceeds maximum*");
    }

    // ── JSON depth ─────────────────────────────────────────────────────────

    [Fact]
    public void Validate_DeeplyNestedJson_ReturnsInvalid()
    {
        // Build 40 levels of nesting
        string json = string.Concat(Enumerable.Repeat("{\"a\":", 40)) + "1" +
                      string.Concat(Enumerable.Repeat("}", 40));

        var result = _sut.Validate(json, "deep.zmacro");

        result.IsValid.Should().BeFalse();
        result.Violations.Should().ContainMatch("*depth*");
    }

    [Fact]
    public void Validate_ShallowJson_IsNotRejectedForDepth()
    {
        // 5 levels deep — well within limits
        string json = "{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":1}}}}}";

        var result = _sut.Validate(json, "shallow.zmacro");

        result.Violations.Should().NotContain(v => v.Contains("depth"));
    }

    // ── $type discriminator ────────────────────────────────────────────────

    [Fact]
    public void Validate_AssemblyQualifiedTypeName_ReturnsInvalid()
    {
        string json = """{"steps":[{"$type":"System.Diagnostics.Process, System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"}]}""";

        var result = _sut.Validate(json, "exploit.zmacro");

        result.IsValid.Should().BeFalse();
        result.Violations.Should().ContainMatch("*.NET assembly-qualified*");
    }

    [Fact]
    public void Validate_NamespacedDotTypeName_ReturnsViolation()
    {
        // Not in known list AND contains a dot — suspicious
        string json = """{"steps":[{"$type":"Some.Malicious.Type"}]}""";

        var result = _sut.Validate(json, "namespaced.zmacro");

        result.Violations.Should().ContainMatch("*Some.Malicious.Type*");
    }

    [Fact]
    public void Validate_UnknownTypeWithoutDot_DoesNotTriggerTypeFinding()
    {
        // Unknown but no dot/plus — treated as just "unknown step" not a type-name attack
        string json = """{"steps":[{"$type":"customthing"}]}""";

        var result = _sut.Validate(json, "custom.zmacro");

        result.Violations.Should().NotContain(v => v.Contains(".NET assembly-qualified"));
    }

    // ── Path traversal ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_PathTraversal_ForwardSlash_ReturnsViolation()
    {
        string json = """{"templatePath":"../../Windows/System32/calc.exe"}""";

        var result = _sut.Validate(json, "traversal.zmacro");

        result.Violations.Should().ContainMatch("*path traversal*");
    }

    [Fact]
    public void Validate_PathTraversal_BackSlash_ReturnsViolation()
    {
        string json = "{\"templatePath\":\"..\\\\..\\\\ Windows\\\\System32\\\\\"}";

        var result = _sut.Validate(json, "traversal_back.zmacro");

        result.Violations.Should().ContainMatch("*path traversal*");
    }

    [Fact]
    public void Validate_NormalRelativePath_DoesNotTriggerTraversal()
    {
        string json = """{"templatePath":"Templates/button.png"}""";

        var result = _sut.Validate(json, "ok.zmacro");

        result.Violations.Should().NotContain(v => v.Contains("path traversal"));
    }

    // ── Embedded scripts ───────────────────────────────────────────────────

    [Fact]
    public void Validate_PowershellInStringField_ReturnsViolation()
    {
        string json = """{"description":"Run powershell -Command evil"}""";

        var result = _sut.Validate(json, "shell.zmacro");

        result.Violations.Should().ContainMatch("*powershell*");
    }

    [Fact]
    public void Validate_CmdExeInStringField_ReturnsViolation()
    {
        string json = """{"label":"C:\\Windows\\System32\\cmd.exe /c del"}""";

        var result = _sut.Validate(json, "cmd.zmacro");

        result.Violations.Should().ContainMatch("*cmd.exe*");
    }

    [Fact]
    public void Validate_EvalInStringField_ReturnsViolation()
    {
        string json = """{"prompt":"eval(atob('ZXZpbA=='))"}""";

        var result = _sut.Validate(json, "eval.zmacro");

        result.Violations.Should().ContainMatch("*eval(*");
    }

    // ── Malformed JSON ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_MalformedJson_ReturnsInvalid()
    {
        string json = "{\"name\": \"broken\""; // missing closing brace

        var result = _sut.Validate(json, "broken.zmacro");

        // Any malformed JSON must produce at least one violation and be invalid
        result.IsValid.Should().BeFalse();
        result.Violations.Should().NotBeEmpty();
    }

    // ── MacroValidationResult ──────────────────────────────────────────────

    [Fact]
    public void MacroValidationResult_Clean_IsValidAndEmpty()
    {
        MacroValidationResult.Clean.IsValid.Should().BeTrue();
        MacroValidationResult.Clean.Violations.Should().BeEmpty();
    }
}
