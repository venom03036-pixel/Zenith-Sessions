using System.Windows.Media;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.App;

/// <summary>
/// UI-context owner of card pictures for one search page: one batch descriptor request per visible set, then one
/// download and one decode at a time. The directory is borrowed. Publication is fenced by run identity, so a late
/// provider result never reaches a cleared or replaced grid. No retries, timers or polling.
/// </summary>
public sealed class GameCardArtLoader(IPublicRobloxGameArtDirectory? directory,Action<Exception> failed):IAsyncDisposable
{
    private readonly List<Run> _runs=[];
    private Run? _current;
    private bool _disposed;

    /// <summary>
    /// Every tracked picture task; completes once all started runs have finished or been fenced out. Only unfinished
    /// runs are tracked: each run removes itself when it finishes, so finished rows and pictures are never retained here.
    /// </summary>
    public Task Active=>_runs.Count==0?Task.CompletedTask:Task.WhenAll(_runs.Select(run=>run.Task));

    /// <summary>
    /// Queue pictures for rows that have none. Rows join the run of this generation; another generation replaces
    /// (cancels) the previous run. The returned task is owned and tracked here; callers may discard it.
    /// </summary>
    public Task LoadAsync(IReadOnlyList<PublicGameSearchRow> rows,long generation)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if(_disposed || directory is null || rows.Count==0) return Task.CompletedTask;
        var run=_current;
        if(run is null || run.Generation!=generation || run.Token.IsCancellationRequested || run.Finished)
        {Cancel();run=new Run(generation);_current=run;_runs.Add(run);}
        foreach(var row in rows) if(row.Art is null && !row.ArtAttempted && run.Queued.Add(row)) run.Pending.Enqueue(row);
        if(!run.Started) {run.Started=true;run.Task=WorkAsync(run);}
        return run.Task;
    }

    /// <summary>Fence and cancel the current run. Pictures already published stay on their rows; the page owner clears those.</summary>
    public void Cancel()
    {
        var run=_current;_current=null;
        if(run is null) return;
        try {run.Stop.Cancel();} catch(ObjectDisposedException) { }
    }

    private bool Current(Run run)=>!_disposed && ReferenceEquals(_current,run) && !run.Token.IsCancellationRequested;

    private async Task WorkAsync(Run run)
    {
        try
        {
            while(run.Pending.Count!=0 && Current(run))
            {
                var batch=new List<PublicGameSearchRow>(PublicRobloxGameArtLimits.MaximumBatch);
                while(batch.Count<PublicRobloxGameArtLimits.MaximumBatch && run.Pending.Count!=0) batch.Add(run.Pending.Dequeue());
                long[] universes=batch.Select(row=>row.Result.UniverseId).Distinct().ToArray();
                var descriptors=await directory!.GetGameThumbnailsAsync(universes,run.Token);
                if(!Current(run)) return;
                if(descriptors.State!=PublicDirectoryState.Success || descriptors.Value is null)
                {foreach(var row in batch) row.ArtAttempted=true;continue;} // Deferred or unavailable: placeholders, no retry.
                var byUniverse=new Dictionary<long,PublicRobloxGameThumbnail>();
                foreach(var thumbnail in descriptors.Value) byUniverse.TryAdd(thumbnail.UniverseId,thumbnail);
                foreach(var row in batch)
                {
                    if(!Current(run)) return;
                    if(!byUniverse.TryGetValue(row.Result.UniverseId,out var thumbnail) || thumbnail.State!=PublicRobloxThumbnailState.Ready || thumbnail.ImageUri is null)
                    {row.ArtAttempted=true;continue;}
                    var encoded=await directory.FetchGameThumbnailAsync(thumbnail,run.Token);
                    if(!Current(run)) return;
                    row.ArtAttempted=true;
                    if(encoded.State!=PublicDirectoryState.Success || encoded.Value is not { } bytes) continue;
                    ImageSource? pixels;
                    try {pixels=await Task.Run(()=>PublicAccountPresentation.DecodeCardPng(bytes.Bytes,bytes.MediaType,run.Token),CancellationToken.None);}
                    catch(OperationCanceledException) {return;}
                    catch(Exception error) {if(!Current(run)) return;Report(error);continue;}
                    if(!Current(run)) return;
                    row.Art=pixels;
                }
            }
        }
        catch(OperationCanceledException) { }
        catch(Exception error) {Report(error);}
        finally
        {
            run.Finished=true;
            if(ReferenceEquals(_current,run)) _current=null;
            _runs.Remove(run); // UI context, like every other access; the run's rows and pictures belong to the page now.
            run.Stop.Dispose();
        }
    }

    private void Report(Exception error)
    {
        _=error; // Provider and decoder details never reach diagnostics; only the fixed application-owned failure does.
        try {failed(new InvalidOperationException("The public picture operation failed."));} catch(Exception) { /* Diagnostics never become a second owner. */ }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed=true;Cancel();
        foreach(var run in _runs.ToArray()) {try {await run.Task;} catch(Exception) { /* Faults were already reported per run. */ }}
        _runs.Clear();
    }

    private sealed class Run
    {
        public Run(long generation) {Generation=generation;Stop=new();Token=Stop.Token;}
        public long Generation {get;}
        public CancellationTokenSource Stop {get;}
        public CancellationToken Token {get;}
        public HashSet<PublicGameSearchRow> Queued {get;}=new(ReferenceEqualityComparer.Instance);
        public Queue<PublicGameSearchRow> Pending {get;}=new();
        public Task Task {get;set;}=Task.CompletedTask;
        public bool Started,Finished;
    }
}
