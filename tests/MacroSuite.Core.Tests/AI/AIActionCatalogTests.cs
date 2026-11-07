using FluentAssertions;
using MacroSuite.Core.AI;
using Xunit;

namespace MacroSuite.Core.Tests.AI;

public sealed class AIActionCatalogTests
{
    [Fact]
    public void ThrowIfInconsistent_DoesNotThrowForCanonicalCatalog()
    {
        var act = AIActionCatalog.ThrowIfInconsistent;
        act.Should().NotThrow();
    }

    [Fact]
    public void Catalog_MatchesValidatorAndDispatcher()
    {
        AIActionCatalog.Types.Should().NotBeEmpty();
        foreach (string type in AIActionCatalog.Types)
        {
            AIActionValidator.IsAllowedAction(type).Should().BeTrue();
            AIVisionActionDispatcher.CanDispatch(type).Should().BeTrue();
        }

        foreach (string type in AIActionValidator.AllowedActions)
            AIActionCatalog.Types.Should().Contain(type);
    }
}
