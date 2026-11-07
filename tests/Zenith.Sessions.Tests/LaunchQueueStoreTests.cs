using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Zenith.Sessions.Core;
using Zenith.Sessions.Windows;

namespace Zenith.Sessions.Tests;

public sealed class LaunchQueueStoreTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"zenith-queue-tests-"+Guid.NewGuid().ToString("N"));
    private static LaunchQueueSnapshot Fixture()=>new(true,[new(new(Guid.NewGuid(),Guid.NewGuid(),1,123,6564161046,0,DateTimeOffset.UtcNow),LaunchState.Queued,LaunchProblem.None,DateTimeOffset.UtcNow)]);
    [Fact]
    public async Task AtomicQueueSurvivesReopeningAndContainsNoCredentialPayload()
    {
        using var store=new LaunchQueueStore(_root);var snapshot=Fixture();await store.SaveAsync(snapshot,default);
        using var reopened=new LaunchQueueStore(_root);var read=await reopened.LoadAsync(default);
        Assert.Equal(snapshot.Paused,read.Paused);Assert.Equal(snapshot.Operations,read.Operations);
        string json=await File.ReadAllTextAsync(Path.Combine(_root,"launch-queue.json"));
        Assert.DoesNotContain("AccessToken",json);Assert.DoesNotContain("RefreshToken",json);Assert.DoesNotContain("password",json);
    }
    [Fact]
    public async Task CorruptOriginalIsPreservedAndRecoveredSnapshotDoesNotEraseTheLastValidCopy()
    {
        using var store=new LaunchQueueStore(_root);var first=Fixture();await store.SaveAsync(first,default);await store.SaveAsync(new(false,first.Operations),default);
        string path=Path.Combine(_root,"launch-queue.json");await File.WriteAllTextAsync(path,"corrupt original");
        var recovered=await store.LoadAsync(default);Assert.True(recovered.Paused);Assert.Equal("corrupt original",await File.ReadAllTextAsync(path));
        await store.SaveAsync(recovered,default);Assert.Equal("corrupt original",await File.ReadAllTextAsync(path+".damaged"));
        // A backup is one generation behind, so its queued request returns as a possible client for review.
        var forReview=first.Operations.Select(o=>o with {State=LaunchState.ReviewRequired,Problem=LaunchProblem.Interrupted}).ToArray();
        Assert.Equal(forReview,recovered.Operations);Assert.Equal(forReview,(await store.LoadAsync(default)).Operations);
    }
    [Fact]
    public async Task UnknownFormatAndCorruptQueueCannotPretendToBeEmpty()
    {
        Directory.CreateDirectory(_root);string path=Path.Combine(_root,"launch-queue.json");await File.WriteAllTextAsync(path,"{\"Version\":99,\"Queue\":{\"Paused\":true,\"Operations\":[]}}");
        using var store=new LaunchQueueStore(_root);await Assert.ThrowsAsync<InvalidDataException>(()=>store.LoadAsync(default));
        Assert.Contains("99",await File.ReadAllTextAsync(path));
    }
    [Fact]
    public async Task CancelledOrInvalidSaveCannotReplaceExistingQueue()
    {
        using var store=new LaunchQueueStore(_root);var first=Fixture();await store.SaveAsync(first,default);
        using var stop=new CancellationTokenSource();stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.SaveAsync(new(false,[]),stop.Token));
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(new(false,[first.Operations[0],first.Operations[0]]),default));
        Assert.Equal(first.Operations,(await store.LoadAsync(default)).Operations);
    }
    [Fact]
    public async Task VersionSixRoundTripPreservesTheExplicitPlaceJobKindAndAuthorizationGeneration()
    {
        var fixture=Fixture();var original=fixture.Operations[0];
        var snapshot=new LaunchQueueSnapshot(true,[original with { Intent=original.Intent with
        {
            PlaceId=42,AuthorizationGeneration=7,RequestedJobId="12345678-1234-4234-8234-123456789abc",
            RequestedServerKind=RobloxServerKind.Public,HasExplicitDestination=true
        } }]);
        using(var store=new LaunchQueueStore(_root)) await store.SaveAsync(snapshot,default);
        using var reopened=new LaunchQueueStore(_root);var loaded=await reopened.LoadAsync(default);
        Assert.Equal(snapshot.Operations,loaded.Operations);
        using var json=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root,"launch-queue.json")));
        Assert.Equal(7,json.RootElement.GetProperty("Version").GetInt32());
        var intent=json.RootElement.GetProperty("Queue").GetProperty("Operations")[0].GetProperty("Intent");
        Assert.Equal(42,intent.GetProperty("PlaceId").GetInt64());
        Assert.Equal("12345678-1234-4234-8234-123456789abc",intent.GetProperty("RequestedJobId").GetString());
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LegacyQueuesLoadPausedForReviewWithoutInferringServerDestinations(int version)
    {
        var fixture=Fixture();var saved=new LaunchQueueSnapshot(false,[fixture.Operations[0] with { State=LaunchState.Launching }]);
        var old=JsonNode.Parse(JsonSerializer.Serialize(new { Version=version,Queue=saved }))!;
        var intent=old["Queue"]!["Operations"]![0]!["Intent"]!.AsObject();
        intent.Remove("RequestedJobId");intent.Remove("RequestedServerKind");intent.Remove("HasExplicitDestination");
        if(version==1) intent.Remove("AuthorizationGeneration");
        Directory.CreateDirectory(_root);await File.WriteAllTextAsync(Path.Combine(_root,"launch-queue.json"),old.ToJsonString());
        using var store=new LaunchQueueStore(_root);var loaded=await store.LoadAsync(default);
        Assert.True(loaded.Paused);var row=Assert.Single(loaded.Operations);
        Assert.Equal(LaunchState.ReviewRequired,row.State);Assert.Equal(LaunchProblem.Interrupted,row.Problem);
        Assert.False(row.Intent.HasExplicitDestination);Assert.Null(row.Intent.RequestedJobId);
        Assert.Equal(RobloxServerKind.Unknown,row.Intent.RequestedServerKind);
        await store.SaveAsync(loaded,default);var rewritten=await store.LoadAsync(default);
        Assert.True(rewritten.Paused);Assert.Equal(LaunchState.ReviewRequired,Assert.Single(rewritten.Operations).State);
    }
    [Fact]
    public async Task MalformedOrInconsistentDestinationMetadataCannotReplaceTheQueue()
    {
        using var store=new LaunchQueueStore(_root);var first=Fixture();await store.SaveAsync(first,default);
        foreach(var intent in new[]
        {
            first.Operations[0].Intent with { RequestedJobId="not-a-job",HasExplicitDestination=true },
            first.Operations[0].Intent with { RequestedJobId="12345678-1234-4234-8234-123456789abc",HasExplicitDestination=false },
            first.Operations[0].Intent with { RequestedServerKind=RobloxServerKind.Public,HasExplicitDestination=true },
            first.Operations[0].Intent with { PlaceId=0,HasExplicitDestination=true }
        })
            await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(new(true,[first.Operations[0] with { Intent=intent }]),default));
        Assert.Equal(first.Operations,(await store.LoadAsync(default)).Operations);
    }
    [Fact]
    public async Task VersionSixKeepsPreparationFactsExactAndNeverPersistsAHandoffOrReceiptForPreparing()
    {
        var original=Fixture().Operations[0];
        var rows=new[]
        {
            original with{State=LaunchState.Preparing},
            New(LaunchState.Failed,LaunchProblem.PreparationRejected),
            New(LaunchState.Failed,LaunchProblem.PreparationTimedOut),
            New(LaunchState.Failed,LaunchProblem.PreparationExpired),
            New(LaunchState.Failed,LaunchProblem.PreparationCleanupFailed),
            New(LaunchState.Blocked,LaunchProblem.PreparationInterrupted)
        };
        var snapshot=new LaunchQueueSnapshot(false,rows);
        using var store=new LaunchQueueStore(_root);await store.SaveAsync(snapshot,default);
        using var reopened=new LaunchQueueStore(_root);var loaded=await reopened.LoadAsync(default);
        Assert.Equal(snapshot.Paused,loaded.Paused);Assert.Equal(snapshot.Operations,loaded.Operations);
        Assert.False(LaunchDescriptions.NeedsReservation(LaunchState.Preparing));
        Assert.All(loaded.Operations,operation=>Assert.Null(operation.ProcessReceipt));
        string json=await File.ReadAllTextAsync(Path.Combine(_root,"launch-queue.json"));
        using var parsed=JsonDocument.Parse(json);Assert.Equal(7,parsed.RootElement.GetProperty("Version").GetInt32());
        foreach(string forbidden in new[]{"Cookie","Credential","gameinfo","roblox-player:","AccessToken","RefreshToken"})Assert.DoesNotContain(forbidden,json,StringComparison.OrdinalIgnoreCase);
        LaunchOperation New(LaunchState state,LaunchProblem problem)=>original with
        {Intent=original.Intent with{OperationId=Guid.NewGuid(),ProfileId=Guid.NewGuid()},State=state,Problem=problem};
    }
    [Fact]
    public async Task VersionFourCompatibilityPreservesReceiptAndIntentThenExplicitSaveUpgradesEnvelopeOnly()
    {
        var original=Fixture().Operations[0];var recorded=DateTimeOffset.UtcNow;
        var receipt=new LaunchProcessReceipt(700001,recorded.AddMinutes(-1).UtcTicks,recorded);
        var operation=original with{State=LaunchState.ClientStartedUnverified,Problem=LaunchProblem.ClientStartedUnverified,
            UpdatedUtc=recorded,ProcessReceipt=receipt,Intent=original.Intent with{AuthorizationGeneration=7,HasExplicitDestination=true,
                RequestedServerKind=RobloxServerKind.Public,RequestedJobId="12345678-1234-4234-8234-123456789abc"}};
        var snapshot=new LaunchQueueSnapshot(false,[operation]);Directory.CreateDirectory(_root);
        string path=Path.Combine(_root,"launch-queue.json"),legacy=JsonSerializer.Serialize(new{Version=4,Queue=snapshot});
        await File.WriteAllTextAsync(path,legacy);using var store=new LaunchQueueStore(_root);
        var loaded=await store.LoadAsync(default);Assert.Equal(snapshot.Operations,loaded.Operations);Assert.Equal(snapshot.Paused,loaded.Paused);
        Assert.Equal(legacy,await File.ReadAllTextAsync(path));
        await store.SaveAsync(loaded,default);
        using var current=JsonDocument.Parse(await File.ReadAllTextAsync(path));Assert.Equal(7,current.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(legacy,await File.ReadAllTextAsync(path+".bak"));Assert.Equal(snapshot.Operations,(await store.LoadAsync(default)).Operations);
    }
    public static IEnumerable<object[]> OlderFormatPreparationMarkers()
    {
        foreach(int version in new[]{1,2,3,4})
        foreach(bool asText in new[]{false,true})
        {
            yield return [version,"State",(int)LaunchState.Preparing,nameof(LaunchState.Preparing),asText];
            foreach(var problem in new[]{LaunchProblem.PreparationRejected,LaunchProblem.PreparationTimedOut,LaunchProblem.PreparationExpired,
                LaunchProblem.PreparationCleanupFailed,LaunchProblem.PreparationInterrupted})
                yield return [version,"Problem",(int)problem,problem.ToString(),asText];
        }
    }
    [Theory]
    [MemberData(nameof(OlderFormatPreparationMarkers))]
    public async Task OlderEnvelopePreparationMarkersCannotSelectOrOverwriteAnOlderBackup(int version,string field,int numeric,string name,bool asText)
    {
        using var store=new LaunchQueueStore(_root);await store.SaveAsync(LaunchQueueSnapshot.Empty,default);await store.SaveAsync(LaunchQueueSnapshot.Empty,default);
        string path=Path.Combine(_root,"launch-queue.json"),backup=await File.ReadAllTextAsync(path+".bak");
        var document=JsonNode.Parse(JsonSerializer.Serialize(new{Version=version,Queue=Fixture()}))!;
        var operation=document["Queue"]!["Operations"]![0]!.AsObject();
        operation[field]=asText?JsonValue.Create(name):JsonValue.Create(numeric);
        // If format checks occur only after Deserialize, this field would cause a recoverable
        // JsonException and hide the future marker by silently selecting the old empty backup.
        operation["FuturePreparationField"]=true;
        string unsupported=document.ToJsonString();await File.WriteAllTextAsync(path,unsupported);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(LaunchQueueSnapshot.Empty,default));
        Assert.Equal(unsupported,await File.ReadAllTextAsync(path));Assert.Equal(backup,await File.ReadAllTextAsync(path+".bak"));
        Assert.False(File.Exists(path+".damaged"));Assert.False(File.Exists(path+".tmp"));
    }
    [Fact]
    public async Task APreparationMarkerInADuplicateStateFieldCannotBeHiddenByAnOlderLastValue()
    {
        using var store=new LaunchQueueStore(_root);await store.SaveAsync(LaunchQueueSnapshot.Empty,default);await store.SaveAsync(LaunchQueueSnapshot.Empty,default);
        string path=Path.Combine(_root,"launch-queue.json"),backup=await File.ReadAllTextAsync(path+".bak");
        string unsupported=JsonSerializer.Serialize(new{Version=4,Queue=Fixture()})
            .Replace("\"State\":0","\"State\":"+((int)LaunchState.Preparing).ToString(System.Globalization.CultureInfo.InvariantCulture)+",\"State\":0",StringComparison.Ordinal);
        await File.WriteAllTextAsync(path,unsupported);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(LaunchQueueSnapshot.Empty,default));
        Assert.Equal(unsupported,await File.ReadAllTextAsync(path));Assert.Equal(backup,await File.ReadAllTextAsync(path+".bak"));
    }
    [Theory]
    [InlineData("missing-version")]
    [InlineData("string-version")]
    [InlineData("unknown-version")]
    public async Task PreparationDataWithoutARecognizedCurrentEnvelopeNeverUsesBackup(string kind)
    {
        using var store=new LaunchQueueStore(_root);await store.SaveAsync(LaunchQueueSnapshot.Empty,default);await store.SaveAsync(LaunchQueueSnapshot.Empty,default);
        var document=JsonNode.Parse(JsonSerializer.Serialize(new{Version=4,Queue=Fixture()}))!;
        document["Queue"]!["Operations"]![0]!["State"]=JsonValue.Create(nameof(LaunchState.Preparing));
        if(kind=="missing-version")document.AsObject().Remove("Version");
        else if(kind=="string-version")document["Version"]="4";
        else document["Version"]=8;
        string path=Path.Combine(_root,"launch-queue.json"),unsupported=document.ToJsonString();await File.WriteAllTextAsync(path,unsupported);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(LaunchQueueSnapshot.Empty,default));
        Assert.Equal(unsupported,await File.ReadAllTextAsync(path));Assert.False(File.Exists(path+".tmp"));
    }
    [Fact]
    public async Task FutureEnvelopeWithUnknownBodyCannotFallBackOrBecomeDamagedOverwrite()
    {
        using var store=new LaunchQueueStore(_root);await store.SaveAsync(LaunchQueueSnapshot.Empty,default);await store.SaveAsync(LaunchQueueSnapshot.Empty,default);
        string path=Path.Combine(_root,"launch-queue.json"),backup=await File.ReadAllTextAsync(path+".bak");
        const string future="{\"Version\":8,\"Queue\":{\"FuturePayload\":true}}";await File.WriteAllTextAsync(path,future);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(LaunchQueueSnapshot.Empty,default));
        Assert.Equal(future,await File.ReadAllTextAsync(path));Assert.Equal(backup,await File.ReadAllTextAsync(path+".bak"));Assert.False(File.Exists(path+".damaged"));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PreReceiptFormatsStillRejectProcessFactsRatherThanInventingCompatibility(int version)
    {
        var original=Fixture().Operations[0];var recorded=DateTimeOffset.UtcNow;
        var operation=original with{State=LaunchState.ClientStartedUnverified,Problem=LaunchProblem.ClientStartedUnverified,
            UpdatedUtc=recorded,ProcessReceipt=new(700001,recorded.AddMinutes(-1).UtcTicks,recorded)};
        string old=JsonSerializer.Serialize(new{Version=version,Queue=new LaunchQueueSnapshot(false,[operation])});
        Directory.CreateDirectory(_root);string path=Path.Combine(_root,"launch-queue.json");await File.WriteAllTextAsync(path,old);
        using var store=new LaunchQueueStore(_root);await Assert.ThrowsAsync<InvalidDataException>(()=>store.LoadAsync(default));
        Assert.Equal(old,await File.ReadAllTextAsync(path));
    }
    [Fact]
    public async Task APreparingOperationCannotCarryAProcessReceiptOrReplaceTheLastValidQueue()
    {
        using var store=new LaunchQueueStore(_root);await store.SaveAsync(LaunchQueueSnapshot.Empty,default);
        string path=Path.Combine(_root,"launch-queue.json");byte[] before=await File.ReadAllBytesAsync(path);var original=Fixture().Operations[0];var recorded=DateTimeOffset.UtcNow;
        var invalid=original with{State=LaunchState.Preparing,UpdatedUtc=recorded,ProcessReceipt=new(700001,recorded.AddMinutes(-1).UtcTicks,recorded)};
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(new(false,[invalid]),default));
        Assert.Equal(before,await File.ReadAllBytesAsync(path));
    }
    [Fact]
    public async Task VersionFivePreparationLoadsWithoutRewriteAndExplicitSaveUpgradesToSix()
    {
        var operation=Fixture().Operations[0] with{State=LaunchState.Preparing};var snapshot=new LaunchQueueSnapshot(false,[operation]);
        Directory.CreateDirectory(_root);string path=Path.Combine(_root,"launch-queue.json"),original=JsonSerializer.Serialize(new{Version=5,Queue=snapshot});
        await File.WriteAllTextAsync(path,original);using var store=new LaunchQueueStore(_root);
        var loaded=await store.LoadAsync(default);Assert.Equal(snapshot.Operations,loaded.Operations);Assert.False(loaded.Paused);
        Assert.Equal(original,await File.ReadAllTextAsync(path));await store.SaveAsync(loaded,default);
        using var json=JsonDocument.Parse(await File.ReadAllTextAsync(path));Assert.Equal(7,json.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(original,await File.ReadAllTextAsync(path+".bak"));Assert.Equal(snapshot.Operations,(await store.LoadAsync(default)).Operations);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionSixRetainsExplicitManualReleaseAndItsOptionalHistoricalReceipt(bool hasReceipt)
    {
        var original=Fixture().Operations[0];var recorded=DateTimeOffset.UtcNow;
        var receipt=hasReceipt?new LaunchProcessReceipt(700001,recorded.AddMinutes(-1).UtcTicks,recorded):null;
        var operation=original with{State=LaunchState.Cancelled,Problem=LaunchProblem.ClientClosureAcknowledged,UpdatedUtc=recorded,ProcessReceipt=receipt};
        using var store=new LaunchQueueStore(_root);await store.SaveAsync(new(false,[operation]),default);
        using var reopened=new LaunchQueueStore(_root);var loaded=await reopened.LoadAsync(default);
        Assert.Equal(operation,Assert.Single(loaded.Operations));Assert.Equal(receipt,loaded.Operations[0].ProcessReceipt);
        using var json=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root,"launch-queue.json")));
        Assert.Equal(7,json.RootElement.GetProperty("Version").GetInt32());
    }
    public static IEnumerable<object[]> OlderFormatClosureMarkers()
    {
        foreach(int version in new[]{1,2,3,4,5})foreach(bool asText in new[]{false,true})yield return [version,asText];
    }
    [Theory]
    [MemberData(nameof(OlderFormatClosureMarkers))]
    public async Task OlderEnvelopeCannotInterpretManualReleaseOrSilentlyFallBack(int version,bool asText)
    {
        using var store=new LaunchQueueStore(_root);await store.SaveAsync(LaunchQueueSnapshot.Empty,default);await store.SaveAsync(LaunchQueueSnapshot.Empty,default);
        string path=Path.Combine(_root,"launch-queue.json"),backup=await File.ReadAllTextAsync(path+".bak");
        var document=JsonNode.Parse(JsonSerializer.Serialize(new{Version=version,Queue=Fixture()}))!;
        var operation=document["Queue"]!["Operations"]![0]!.AsObject();operation["State"]=(int)LaunchState.Cancelled;
        operation["Problem"]=asText?JsonValue.Create(nameof(LaunchProblem.ClientClosureAcknowledged)):JsonValue.Create((int)LaunchProblem.ClientClosureAcknowledged);
        operation["FutureReleaseField"]=true;string original=document.ToJsonString();await File.WriteAllTextAsync(path,original);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(LaunchQueueSnapshot.Empty,default));
        Assert.Equal(original,await File.ReadAllTextAsync(path));Assert.Equal(backup,await File.ReadAllTextAsync(path+".bak"));
        Assert.False(File.Exists(path+".damaged"));Assert.False(File.Exists(path+".tmp"));
    }
    [Fact]
    public async Task AManualReleaseMarkerInADuplicateProblemIsRejectedBeforeLastValueWins()
    {
        using var store=new LaunchQueueStore(_root);await store.SaveAsync(LaunchQueueSnapshot.Empty,default);await store.SaveAsync(LaunchQueueSnapshot.Empty,default);
        var snapshot=new LaunchQueueSnapshot(false,[Fixture().Operations[0] with{State=LaunchState.Cancelled}]);
        string original=JsonSerializer.Serialize(new{Version=5,Queue=snapshot})
            .Replace("\"Problem\":0","\"Problem\":"+((int)LaunchProblem.ClientClosureAcknowledged).ToString(System.Globalization.CultureInfo.InvariantCulture)+",\"Problem\":0",StringComparison.Ordinal);
        string path=Path.Combine(_root,"launch-queue.json");await File.WriteAllTextAsync(path,original);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.LoadAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(LaunchQueueSnapshot.Empty,default));
        Assert.Equal(original,await File.ReadAllTextAsync(path));
    }
    [Fact]
    public async Task AcknowledgmentMarkerCannotLabelAnActiveOperationOrReplaceTheDurableQueue()
    {
        using var store=new LaunchQueueStore(_root);await store.SaveAsync(LaunchQueueSnapshot.Empty,default);
        string path=Path.Combine(_root,"launch-queue.json");byte[] before=await File.ReadAllBytesAsync(path);
        var invalid=Fixture().Operations[0] with{State=LaunchState.Launching,Problem=LaunchProblem.ClientClosureAcknowledged};
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(new(false,[invalid]),default));Assert.Equal(before,await File.ReadAllBytesAsync(path));
    }
    public void Dispose()
    {
        string full=Path.GetFullPath(_root);
        if(Path.GetDirectoryName(full)!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("zenith-queue-tests-",StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if(Directory.Exists(full)) Directory.Delete(full,true);
    }
}
