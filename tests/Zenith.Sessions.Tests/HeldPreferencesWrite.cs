using System.Reflection;
using Zenith.Sessions.App;

namespace Zenith.Sessions.Tests;

/// <summary>Holds the real settings writer at its asynchronous admission boundary; no production test hook or timer.</summary>
internal sealed class HeldPreferencesWrite : IDisposable
{
    private readonly SemaphoreSlim _writer;
    private bool _released;
    public HeldPreferencesWrite(ShellViewModel shell)
    {
        _writer = (SemaphoreSlim)typeof(ShellViewModel).GetField("_preferencesWriter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
        if (!_writer.Wait(0)) throw new InvalidOperationException("A fixture write was already pending.");
    }
    public void Dispose()
    {
        if (_released) return;
        _released = true;
        _writer.Release();
    }
}
