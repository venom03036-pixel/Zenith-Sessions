using Xunit;

namespace Zenith.Sessions.Tests;

/// <summary>
/// These fixtures load the same WPF application resource packages on separate STA threads.
/// Keep their package access in one test collection; unrelated tests remain parallel, and
/// each fixture's explicitly controlled concurrency remains unchanged. The exact competing
/// fixture in the retained package-cache failure was not instrumented or identified.
/// </summary>
[CollectionDefinition(WpfProductionResourceCollection.Name)]
public sealed class WpfProductionResourceCollection
{
    public const string Name="WPF production resource packages";
}
