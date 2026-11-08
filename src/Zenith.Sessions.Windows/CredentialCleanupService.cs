using System.Security.Cryptography;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Windows;

/// <param name="ErrorType">Type name of the reported (first) failure of the run, or null when nothing failed.</param>
/// <param name="RecordDamaged">True only when the reported failure is one removed profile's damaged or inconsistent
/// protected files (the vault tags it); retrying cannot repair it. False when the first failure was store-wide or storage.</param>
/// <param name="StoreDamaged">True when the run ended on a store-wide InvalidDataException (one the vault did not tag,
/// such as its invalid coordination file): every record fails the same way, and retrying cannot repair it.</param>
public sealed record CredentialCleanupResult(int Remaining,string? ErrorType=null,bool RecordDamaged=false,bool StoreDamaged=false);

/// <summary>Profile deletion and cleanup intent commit in one SQLite transaction; encrypted-file cleanup resumes after a crash.</summary>
/// <remarks>
/// One record that cannot be removed (held, read-only, ACL-denied, linked or damaged) must not strand every other
/// deleted profile's protected file: a run tries each pending record at most once, keeps the journal row of each
/// failure for a later retry, and reports the first failure. A busy vault, or a store-wide InvalidDataException
/// (one the vault did not tag as a single record's damage), affects every record the same way, so it ends the run;
/// the run still reports its first failure, and a store-wide one also sets StoreDamaged.
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
            // damaged: the first reported failure (error) is one record's tagged damage.
            var failed=new HashSet<Guid>(); string? error=null; bool damaged=false;
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
                    { return new((await profiles.ReadCredentialCleanupAsync(ct)).Total,error??ex.GetType().Name,damaged); }
                    // A damaged or inconsistent vault file of a profile that is already deleted: keep its journal row and
                    // originals for recovery and report it, instead of failing library startup or a delete.
                    catch(InvalidDataException ex) when(AuthorizationRecordDamage.IsMarked(ex))
                    { failed.Add(id); if(error is null) { error=ex.GetType().Name; damaged=true; } }
                    // Untagged, it is store-wide (the vault's coordination file, or the library): every record would fail
                    // the same way, so stop after one attempt rather than blaming a removed profile's file. An earlier failure
                    // stays the reported one, with its damage tag, as on the busy path.
                    catch(InvalidDataException ex)
                    { return new((await profiles.ReadCredentialCleanupAsync(ct)).Total,error??ex.GetType().Name,damaged,StoreDamaged:true); }
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or CryptographicException)
                    { failed.Add(id); error??=ex.GetType().Name; }
                }
                // Every listed record was already tried in this run (or none is pending): stop instead of re-reading the same page.
                if(!attempted) return page.Total==0?new(0):new(page.Total,error,damaged);
            }
        }
        finally { _owner.Release(); }
    }
    public void Dispose()=>_owner.Dispose();
}
