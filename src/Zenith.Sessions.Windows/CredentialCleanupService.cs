using System.Security.Cryptography;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

public sealed record CredentialCleanupResult(int Remaining,string? ErrorType=null);

/// <summary>Profile deletion and cleanup intent commit in one SQLite transaction; encrypted-file cleanup resumes after a crash.</summary>
/// <remarks>
/// One record that cannot be removed (held, read-only, ACL-denied, linked or damaged) must not strand every other
/// deleted profile's protected file: a run tries each pending record at most once, keeps the journal row of each
/// failure for a later retry, and reports the first failure. A busy vault affects every record, so it ends the run.
/// Known limit: the repository pages the oldest 100 journal rows, so 100 or more permanently failing records hide later ones.
/// </remarks>
public sealed class CredentialCleanupService(IAccountRepository profiles,IAccountCredentialVault vault) : IDisposable
{
    private readonly SemaphoreSlim _owner=new(1,1);
    public async Task<CredentialCleanupResult> RunAsync(CancellationToken ct)
    {
        await _owner.WaitAsync(ct);
        try
        {
            var failed=new HashSet<Guid>(); string? error=null;
            while(true)
            {
                var page=await profiles.ReadCredentialCleanupAsync(ct);
                bool attempted=false;
                foreach(Guid id in page.ProfileIds)
                {
                    if(failed.Contains(id)) continue;
                    ct.ThrowIfCancellationRequested(); attempted=true;
                    try { await vault.DeleteAsync(id,ct); await profiles.CompleteCredentialCleanupAsync(id,ct); }
                    catch(AuthorizationVaultBusyException ex)
                    { return new((await profiles.ReadCredentialCleanupAsync(ct)).Total,error??ex.GetType().Name); }
                    // A damaged or inconsistent vault file (InvalidDataException) belongs to a profile that is already deleted:
                    // keep its journal row and originals for recovery and report it, instead of failing library startup or a delete.
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException)
                    { failed.Add(id); error??=ex.GetType().Name; }
                }
                // Every listed record was already tried in this run (or none is pending): stop instead of re-reading the same page.
                if(!attempted) return new(page.Total,page.Total==0?null:error);
            }
        }
        finally { _owner.Release(); }
    }
    public void Dispose()=>_owner.Dispose();
}
