using System.IO;

namespace Zenith.Sessions.Windows;

/// <summary>
/// Atomic write of one file the user explicitly chose. The returned task owns its staging stream and
/// temporary file; nothing is retained afterwards. The previous file is kept unless the replace commits.
/// </summary>
public static class ExplicitReportFile
{
    private const int ReplaceAttempts=10;
    private static readonly TimeSpan ReplaceRetryDelay=TimeSpan.FromMilliseconds(25);

    /// <param name="validate">Checks a path and returns its full form; it runs before staging and again before commit.</param>
    public static async Task WriteAsync(string path,byte[] bytes,Func<string,string> validate,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);ArgumentNullException.ThrowIfNull(validate);
        ct.ThrowIfCancellationRequested();string destination=validate(path);
        string temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            ct.ThrowIfCancellationRequested();
            await using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.Asynchronous|FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes,ct).ConfigureAwait(false);await file.FlushAsync(ct).ConfigureAwait(false);file.Flush(true);
            }
            ct.ThrowIfCancellationRequested();validate(destination);
            if(!File.Exists(destination)) {ct.ThrowIfCancellationRequested();File.Move(temporary,destination);return;}
            for(int attempt=1;;attempt++)
            {
                ct.ThrowIfCancellationRequested();validate(destination);
                try {File.Replace(temporary,destination,null);return;}
                // A scanner or viewer can briefly hold the old file without delete sharing. These codes leave
                // both files under their original names, so a bounded, cancellable retry is safe.
                catch(IOException ex) when(IsTransientSharingFailure(ex) && attempt<ReplaceAttempts && File.Exists(temporary) && File.Exists(destination))
                {await Task.Delay(ReplaceRetryDelay,ct).ConfigureAwait(false);}
            }
        }
        finally {if(File.Exists(temporary)) File.Delete(temporary);}
    }

    // 32 sharing violation, 33 lock violation, 1175 ERROR_UNABLE_TO_REMOVE_REPLACED.
    private static bool IsTransientSharingFailure(IOException ex)=>(ex.HResult&0xffff) is 32 or 33 or 1175;
}
