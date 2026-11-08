using System.Reflection;
using Zenith.Sessions.App;

namespace Zenith.Sessions.Tests;

/// <summary>Holds the real settings writer at its asynchronous admission boundary; no production test hook or timer.</summary>
internal sealed class HeldPreferencesWrite : IDisposable
{
    private readonly SemaphoreSlim _writer;
    private bool _released;
    public HeldPreferencesWrite(ShellViewModel shell, bool initialLoad = false)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        object owner = initialLoad ? typeof(ShellViewModel).GetField("_settingsStore", fields)!.GetValue(shell)! : shell;
        _writer = (SemaphoreSlim)owner.GetType().GetField(initialLoad ? "_writer" : "_preferencesWriter", fields)!.GetValue(owner)!;
        if (!_writer.Wait(0)) throw new InvalidOperationException("A fixture write was already pending.");
    }
    public void Dispose()
    {
        if (_released) return;
        _released = true;
        _writer.Release();
    }
}
