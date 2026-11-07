using Xunit;

namespace MacroSuite.Infrastructure.Tests;

/// <summary>
/// Serializes tests that install hooks or synthesize real mouse input so they
/// cannot run on top of each other.
/// </summary>
[CollectionDefinition("InteractiveDesktop", DisableParallelization = true)]
public sealed class InteractiveDesktopCollection
{
}
