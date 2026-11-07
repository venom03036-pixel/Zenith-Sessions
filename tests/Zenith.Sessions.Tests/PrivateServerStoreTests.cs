using System.IO;
using System.Text;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

// Saved private servers: metadata plus a protected link code. The code never reaches the disk in clear text.
public sealed class PrivateServerStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zenith-private-servers-" + Guid.NewGuid().ToString("N"));
    private const string LinkCode = "98765432109876543210";
    private PrivateServerStore Store() { Directory.CreateDirectory(_root); return new(_root); }
    private static ProtectedPrivateServerCode Code(string value = LinkCode) => new(value);

    [Fact]
    public async Task RoundTripsMetadataAndCode()
    {
        using var store = Store(); using var code = Code();
        var saved = await store.AddAsync("  Weekend VIP  ", 5, 6, 7, code, default);
        Assert.Equal("Weekend VIP", saved.Name); Assert.Equal(1, saved.Revision); Assert.Equal("3210", saved.Last4); Assert.Equal(7, saved.OwnerUserId);
        Assert.Equal(5, saved.PlaceId); Assert.Equal(6, saved.UniverseId); Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.Equal(saved, Assert.Single(await store.ReadAsync(default)));
        using var revealed = await store.ReadCodeAsync(saved.Id, default);
        string value = ""; revealed.Reveal(span => value = new string(span)); Assert.Equal(LinkCode, value); Assert.Equal("3210", revealed.Last4);
        Assert.DoesNotContain("Weekend", saved.ToString(), StringComparison.Ordinal); Assert.DoesNotContain("9876", saved.ToString(), StringComparison.Ordinal);
        using var reopened = new PrivateServerStore(_root); Assert.Equal(saved, Assert.Single(await reopened.ReadAsync(default)));
        await Assert.ThrowsAsync<PrivateServerConflictException>(() => store.ReadCodeAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task FileNeverContainsTheCodeOrNameInPlainText()
    {
        using var store = Store(); using var code = Code("13579246801357924680");
        await store.AddAsync("Hidden server", 5, 6, null, code, default);
        byte[] bytes = await File.ReadAllBytesAsync(store.Path); Assert.NotEmpty(bytes);
        foreach (string secret in new[] { "13579246801357924680", "Hidden server", "\"Items\"" })
        {
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)));
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(secret)));
        }
    }

    [Fact]
    public async Task CapacityIsOneHundred()
    {
        using var store = Store();
        for (int index = 0; index < 100; index++) { using var code = Code(); await store.AddAsync("Server " + index, 5, 6, null, code, default); }
        using var extra = Code();
        await Assert.ThrowsAsync<PrivateServerCapacityException>(() => store.AddAsync("Too many", 5, 6, null, extra, default));
        Assert.Equal(100, (await store.ReadAsync(default)).Count);
    }

    [Fact]
    public async Task StaleRevisionDeleteIsAConflict()
    {
        using var store = Store(); using var code = Code(); var saved = await store.AddAsync("Delete me", 5, 6, null, code, default);
        await Assert.ThrowsAsync<PrivateServerConflictException>(() => store.DeleteAsync(saved.Id, saved.Revision + 1, default));
        await Assert.ThrowsAsync<PrivateServerConflictException>(() => store.DeleteAsync(Guid.NewGuid(), 1, default));
        Assert.Single(await store.ReadAsync(default));
        await store.DeleteAsync(saved.Id, saved.Revision, default); Assert.Empty(await store.ReadAsync(default));
        await Assert.ThrowsAsync<PrivateServerConflictException>(() => store.ReadCodeAsync(saved.Id, default));
    }

    [Fact]
    public async Task CorruptFileIsPreservedAndReported()
    {
        using var store = Store(); byte[] garbage = Encoding.UTF8.GetBytes("{\"Version\":1,\"Revision\":1,\"Items\":[]}"); // Clear JSON is not a protected payload.
        await File.WriteAllBytesAsync(store.Path, garbage);
        var error = await Assert.ThrowsAsync<PrivateServerStorageException>(() => store.ReadAsync(default));
        Assert.Equal(PrivateServerStorageProblem.Damaged, error.Problem);
        using var code = Code();
        await Assert.ThrowsAsync<PrivateServerStorageException>(() => store.AddAsync("Blocked", 5, 6, null, code, default));
        Assert.Equal(garbage, await File.ReadAllBytesAsync(store.Path)); Assert.False(File.Exists(store.Path + ".tmp"));
    }

    [Fact]
    public async Task TornCodeIsRefusedAndExistingServersStayReadable()
    {
        using var store = Store(); using (var first = Code()) await store.AddAsync("Kept", 5, 6, null, first, default);
        using var torn = Code("12345678");
        // Simulates a holder cleared while its characters were being copied: the store must not persist a damaged code.
        typeof(ProtectedCode).GetField("_value", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(torn, new[] { '1', '2', '3', '4', '\0', '\0', '\0', '\0' });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync("Torn", 5, 6, null, torn, default));
        Assert.DoesNotContain("1234", error.ToString(), StringComparison.Ordinal);
        Assert.Equal("Kept", Assert.Single(await store.ReadAsync(default)).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("name\nsecond line")]
    [InlineData("privateServerLinkCode=secret-marker")]
    [InlineData("https://www.roblox.com/share?code=secret-marker&type=Server")]
    public async Task NamesAreBoundedAndNeverCodes(string name)
    {
        using var store = Store(); using var code = Code();
        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(name, 5, 6, null, code, default));
        Assert.DoesNotContain("secret-marker", error.ToString(), StringComparison.Ordinal); Assert.Empty(await store.ReadAsync(default));
        using var longCode = Code();
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(new string('x', 81), 5, 6, null, longCode, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync("Bad place", 0, 6, null, longCode, default));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
