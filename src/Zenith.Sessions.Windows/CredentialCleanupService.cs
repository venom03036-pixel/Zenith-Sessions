using System.Security.Cryptography;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed record CredentialCleanupResult(int Remaining,string? ErrorType=null);

/// <summary>Profile deletion and cleanup intent commit in one SQLite transaction; encrypted-file cleanup resumes after a crash.</summary>
public sealed class CredentialCleanupService(IAccountRepository profiles,IAccountCredentialVault vault) : IDisposable
{
    private readonly SemaphoreSlim _owner=new(1,1);
    public async Task<CredentialCleanupResult> RunAsync(CancellationToken ct)
    {
        await _owner.WaitAsync(ct);
        try
        {
            while(true)
            {
                var page=await profiles.ReadCredentialCleanupAsync(ct);
                if(page.ProfileIds.Count==0) return new(0);
                foreach(Guid id in page.ProfileIds)
                {
                    ct.ThrowIfCancellationRequested();
                    try { await vault.DeleteAsync(id,ct); await profiles.CompleteCredentialCleanupAsync(id,ct); }
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or CryptographicException)
                    { return new((await profiles.ReadCredentialCleanupAsync(ct)).Total,ex.GetType().Name); }
                }
            }
        }
        finally { _owner.Release(); }
    }
    public void Dispose()=>_owner.Dispose();
}
