using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;
using Zenith.Sessions.Core;

namespace Zenith.Sessions.Tests;

public sealed class PerformanceSettingsTransferTests
{
    private static byte[] Json()=>PerformanceSettingsTransfer.SerializeJson(new());
    private static string Text()=>Encoding.UTF8.GetString(Json());
    [Theory]
    [InlineData(ManagerPerformanceProfile.MaximumEfficiency)]
    [InlineData(ManagerPerformanceProfile.Balanced)]
    [InlineData(ManagerPerformanceProfile.HighPerformance)]
    [InlineData(ManagerPerformanceProfile.MainAccountPriority)]
    [InlineData(ManagerPerformanceProfile.Custom)]
    public void EveryManagerProfileAndItsDormantCustomTimingsRoundTrip(ManagerPerformanceProfile profile)
    {
        var local=new ManagerPerformanceSettings(profile,Guid.NewGuid(),5000,250,30000,120000);
        var portable=PortablePerformanceSettings.FromSettings(local);byte[] json=PerformanceSettingsTransfer.SerializeJson(portable);
        Assert.Equal(portable,PerformanceSettingsTransfer.ParseJson(json));
        string code=PerformanceSettingsTransfer.EncodeShareCode(portable);Assert.StartsWith(PerformanceSettingsTransfer.ShareCodePrefix,code);
        Assert.Equal(portable,PerformanceSettingsTransfer.DecodeShareCode(code));Assert.InRange(code.Length,1,PerformanceSettingsTransfer.MaximumShareCodeLength);
        Assert.Equal(json,Convert.FromBase64String(code[PerformanceSettingsTransfer.ShareCodePrefix.Length..]));
        Assert.Contains("not encrypted or authenticated",PerformanceSettingsTransfer.ShareCodeNotice);
    }
    [Fact]
    public void OnlyTheFiveAllowlistedPerformanceValuesArePortable()
    {
        var first=new ManagerPerformanceSettings(ManagerPerformanceProfile.MainAccountPriority,Guid.NewGuid());
        var second=first with {MainAccountProfileId=Guid.NewGuid()};
        byte[] json=PerformanceSettingsTransfer.SerializeJson(PortablePerformanceSettings.FromSettings(first));
        Assert.Equal(json,PerformanceSettingsTransfer.SerializeJson(PortablePerformanceSettings.FromSettings(second)));
        using var document=JsonDocument.Parse(json);
        Assert.Equal(new[] {"customPresentationMs","customProcessSampleMs","customStabilizationMs","customStaggerMs","format","profile","version"},
            document.RootElement.EnumerateObject().Select(property=>property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] {"CustomPresentationMs","CustomProcessSampleMs","CustomStabilizationMs","CustomStaggerMs","Profile"},
            typeof(PortablePerformanceSettings).GetProperties(BindingFlags.Instance|BindingFlags.Public).Select(property=>property.Name).Order(StringComparer.Ordinal));
        string text=Encoding.UTF8.GetString(json);Assert.DoesNotContain(first.MainAccountProfileId!.Value.ToString(),text);Assert.DoesNotContain(second.MainAccountProfileId!.Value.ToString(),text);
        Assert.DoesNotContain("Capacity",text);Assert.DoesNotContain("Credential",text);Assert.DoesNotContain("Layout",text);Assert.DoesNotContain("Argument",text);
    }
    [Fact]
    public void ReviewRetainsOnlyTheDestinationsCurrentLocalMainSelectionAndNeverMutatesLocalSettings()
    {
        var exportedFrom=new ManagerPerformanceSettings(ManagerPerformanceProfile.MainAccountPriority,Guid.NewGuid(),2000,750,9000,40000);
        var transfer=PerformanceSettingsTransfer.DecodeShareCode(PerformanceSettingsTransfer.EncodeShareCode(PortablePerformanceSettings.FromSettings(exportedFrom)));
        var local=new ManagerPerformanceSettings(ManagerPerformanceProfile.Balanced,Guid.NewGuid(),1000,1000,2000,20000);
        var candidate=transfer.ToReviewSettings(local);Assert.NotSame(local,candidate);Assert.Equal(local.MainAccountProfileId,candidate.MainAccountProfileId);
        Assert.NotEqual(exportedFrom.MainAccountProfileId,candidate.MainAccountProfileId);Assert.Equal(ManagerPerformanceProfile.Balanced,local.Profile);
        var newerLocal=local with {MainAccountProfileId=Guid.NewGuid()};Assert.Equal(newerLocal.MainAccountProfileId,transfer.ToReviewSettings(newerLocal).MainAccountProfileId);
        var noMain=transfer.ToReviewSettings(local with {MainAccountProfileId=null});Assert.True(noMain.Resolve().NeedsMainAccount);Assert.Equal(0,noMain.Resolve().MainAccountPriorityBoost);
        Assert.Equal(1000,candidate.Resolve().EssentialResourceSampleMs);Assert.Equal(3000,candidate.Resolve().ResourceSampleExpiryMs);
        Assert.Throws<ArgumentException>(()=>transfer.ToReviewSettings(local with {MainAccountProfileId=Guid.Empty}));
    }
    [Theory]
    [InlineData(499,1000,2000,20000)]
    [InlineData(5001,1000,2000,20000)]
    [InlineData(1000,249,2000,20000)]
    [InlineData(1000,2001,2000,20000)]
    [InlineData(1000,1000,999,20000)]
    [InlineData(1000,1000,30001,20000)]
    [InlineData(1000,1000,2000,19999)]
    [InlineData(1000,1000,2000,120001)]
    public void InvalidCustomValuesAreRejectedEvenForADormantPreset(int process,int presentation,int stagger,int stabilization)
    {
        var invalid=new PortablePerformanceSettings(ManagerPerformanceProfile.Balanced,process,presentation,stagger,stabilization);
        Assert.Throws<ArgumentOutOfRangeException>(()=>invalid.Validated());
        Assert.Equal(PerformanceSettingsTransferProblem.InvalidData,Assert.Throws<PerformanceSettingsTransferException>(()=>PerformanceSettingsTransfer.SerializeJson(invalid)).Problem);
    }
    [Fact]
    public void UndefinedProfilesCannotBeExportedAndBoundaryTimingsRemainValid()
    {
        Assert.Throws<PerformanceSettingsTransferException>(()=>PerformanceSettingsTransfer.SerializeJson(new((ManagerPerformanceProfile)999)));
        foreach(var settings in new[] {new PortablePerformanceSettings(ManagerPerformanceProfile.Custom,500,250,1000,20000),new(ManagerPerformanceProfile.Custom,5000,2000,30000,120000)})
            Assert.Equal(settings,PerformanceSettingsTransfer.ParseJson(PerformanceSettingsTransfer.SerializeJson(settings)));
    }
    public static IEnumerable<object[]> MalformedJson()
    {
        string valid=Text();
        foreach(string payload in new[] {"", " ","null","[]","{}",valid+"{}",valid+"secret-marker",valid[..^1]+",}",
            valid.Replace("\"profile\":\"Balanced\"","\"profile\":\"0\"",StringComparison.Ordinal),
            valid.Replace("\"Balanced\"","\"balanced\"",StringComparison.Ordinal),valid.Replace("\"Balanced\"","\"HighPerformance, Balanced\"",StringComparison.Ordinal),
            valid.Replace("\"Balanced\"","999",StringComparison.Ordinal),valid.Replace("\"Balanced\"","\"Unknown\"",StringComparison.Ordinal),
            valid.Replace("\"version\":1","\"version\":1.0",StringComparison.Ordinal),
            valid.Replace("\"customProcessSampleMs\":1000","\"customProcessSampleMs\":\"1000\"",StringComparison.Ordinal),
            valid.Replace("\"customProcessSampleMs\":1000","\"customProcessSampleMs\":99999999999999999999999",StringComparison.Ordinal),
            valid.Replace("\"customProcessSampleMs\":1000","\"customProcessSampleMs\":499",StringComparison.Ordinal),
            valid.Replace("\"customPresentationMs\":1000","\"customPresentationMs\":0",StringComparison.Ordinal),
            valid.Replace("\"customStaggerMs\":2000","\"customStaggerMs\":-1",StringComparison.Ordinal),
            valid.Replace("\"customStabilizationMs\":20000","\"customStabilizationMs\":120001",StringComparison.Ordinal),
            valid.Replace("\"format\":\"zenith-manager-performance\"","\"format\":\"other-format\"",StringComparison.Ordinal),
            valid.Replace("\"profile\":\"Balanced\",","",StringComparison.Ordinal)}) yield return [payload];
        foreach(string extra in new[] {"\"MainAccountProfileId\":\"12345678-1234-4234-8234-123456789abc\"","\"credentials\":\"secret-marker\"",
            "\"Capacity\":{\"SparePhysicalGiB\":0}","\"launchArguments\":\"secret-marker\"","\"WindowLayout\":{}","\"Profile\":\"Balanced\""})
            yield return [valid[..^1]+","+extra+"}"];
        using var document=JsonDocument.Parse(valid);
        foreach(var property in document.RootElement.EnumerateObject()) yield return [valid[..^1]+",\""+property.Name+"\":"+property.Value.GetRawText()+"}"];
        yield return [valid[..^1]+",\"pro\\u0066ile\":\"Balanced\"}"];
    }
    [Theory]
    [MemberData(nameof(MalformedJson))]
    public void MalformedUnknownDuplicateTrailingAndWrongTypedDataFailWithoutMutatingLocalSettings(string payload)
    {
        var local=new ManagerPerformanceSettings(MainAccountProfileId:Guid.NewGuid());var before=local;
        var error=Assert.Throws<PerformanceSettingsTransferException>(()=>PerformanceSettingsTransfer.ParseJson(Encoding.UTF8.GetBytes(payload)));
        Assert.Equal(PerformanceSettingsTransferProblem.InvalidData,error.Problem);Assert.Null(error.InnerException);Assert.DoesNotContain("secret-marker",error.ToString());
        Assert.Same(before,local);Assert.Equal(ManagerPerformanceProfile.Balanced,local.Profile);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnsupportedVersionsAreExplicitAndNeverInterpretedAsDefaults(int version)
    {
        var bytes=Encoding.UTF8.GetBytes(Text().Replace("\"version\":1","\"version\":"+version,StringComparison.Ordinal));
        Assert.Equal(PerformanceSettingsTransferProblem.UnsupportedVersion,Assert.Throws<PerformanceSettingsTransferException>(()=>PerformanceSettingsTransfer.ParseJson(bytes)).Problem);
    }
    [Fact]
    public void InvalidUtf8AndOversizedBytesAreRejectedBeforeUnboundedDecoding()
    {
        Assert.Equal(PerformanceSettingsTransferProblem.InvalidData,Assert.Throws<PerformanceSettingsTransferException>(()=>PerformanceSettingsTransfer.ParseJson(new byte[] {0xff,0xfe})).Problem);
        Assert.Equal(PerformanceSettingsTransferProblem.TooLarge,Assert.Throws<PerformanceSettingsTransferException>(()=>PerformanceSettingsTransfer.ParseJson(new byte[PerformanceSettingsTransfer.MaximumJsonBytes+1])).Problem);
    }
    [Fact]
    public async Task TheExactJsonAndShareCodeLimitsAcceptACompleteDocumentWithLegalWhitespace()
    {
        var bytes=Enumerable.Repeat((byte)' ',PerformanceSettingsTransfer.MaximumJsonBytes).ToArray();Json().CopyTo(bytes,0);
        Assert.Equal(new PortablePerformanceSettings(),PerformanceSettingsTransfer.ParseJson(bytes));
        string code=PerformanceSettingsTransfer.ShareCodePrefix+Convert.ToBase64String(bytes);
        Assert.Equal(PerformanceSettingsTransfer.MaximumShareCodeLength,code.Length);Assert.Equal(new PortablePerformanceSettings(),PerformanceSettingsTransfer.DecodeShareCode(code));
        using var stream=new TrackingStream(bytes);Assert.Equal(new PortablePerformanceSettings(),await PerformanceSettingsTransfer.ReadJsonAsync(stream));
        Assert.Equal(PerformanceSettingsTransfer.MaximumJsonBytes,stream.BytesRead);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ZSPERF1:")]
    [InlineData("ZSPERF1:not-base64-secret-marker")]
    [InlineData("ZSPERF1:e30=")]
    [InlineData("ZSPERF1: eyJ9")]
    public void MalformedShareCodesHaveFixedSecretFreeErrors(string? code)
    {
        var error=Assert.Throws<PerformanceSettingsTransferException>(()=>PerformanceSettingsTransfer.DecodeShareCode(code!));Assert.DoesNotContain("secret-marker",error.ToString());
    }
    [Fact]
    public void ShareCodeRejectsWhitespaceNoncanonicalPaddingWrongVersionsAndOversizedInput()
    {
        string code=PerformanceSettingsTransfer.EncodeShareCode(new());
        foreach(string invalid in new[] {code+" "," "+code,code.Insert(code.Length/2,"\n"),code+"=",code+"secret-marker"})
            Assert.Throws<PerformanceSettingsTransferException>(()=>PerformanceSettingsTransfer.DecodeShareCode(invalid));
        Assert.Equal(PerformanceSettingsTransferProblem.UnsupportedVersion,Assert.Throws<PerformanceSettingsTransferException>(()=>PerformanceSettingsTransfer.DecodeShareCode(code.Replace("ZSPERF1:","ZSPERF2:",StringComparison.Ordinal))).Problem);
        Assert.Equal(PerformanceSettingsTransferProblem.TooLarge,Assert.Throws<PerformanceSettingsTransferException>(()=>PerformanceSettingsTransfer.DecodeShareCode(new string('A',PerformanceSettingsTransfer.MaximumShareCodeLength+1))).Problem);
        byte[] bytes=Json();while(bytes.Length%3==0) bytes=bytes.Append((byte)' ').ToArray();
        string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";var payload=Convert.ToBase64String(bytes).ToCharArray();int last=Array.FindLastIndex(payload,c=>c!='=');
        payload[last]=alphabet[alphabet.IndexOf(payload[last])+1];
        Assert.Throws<PerformanceSettingsTransferException>(()=>PerformanceSettingsTransfer.DecodeShareCode(PerformanceSettingsTransfer.ShareCodePrefix+new string(payload)));
    }
    [Fact]
    public async Task NonSeekableReadsAreBoundedAndNeverDisposeTheCallersStream()
    {
        using var valid=new TrackingStream(Json());Assert.Equal(new PortablePerformanceSettings(),await PerformanceSettingsTransfer.ReadJsonAsync(valid));Assert.False(valid.Disposed);
        using var oversized=new TrackingStream(new byte[100000]);
        Assert.Equal(PerformanceSettingsTransferProblem.TooLarge,(await Assert.ThrowsAsync<PerformanceSettingsTransferException>(()=>PerformanceSettingsTransfer.ReadJsonAsync(oversized))).Problem);
        Assert.Equal(PerformanceSettingsTransfer.MaximumJsonBytes+1,oversized.BytesRead);Assert.False(oversized.Disposed);
    }
    [Fact]
    public async Task CancellationPropagatesWithoutDefaultsOrTakingStreamOwnership()
    {
        using var stop=new CancellationTokenSource();using var stream=new WaitingStream();var reading=PerformanceSettingsTransfer.ReadJsonAsync(stream,stop.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>reading);Assert.False(stream.Disposed);
        Assert.ThrowsAny<OperationCanceledException>(()=>PerformanceSettingsTransfer.SerializeJson(new(),stop.Token));
        Assert.ThrowsAny<OperationCanceledException>(()=>PerformanceSettingsTransfer.ParseJson(Json(),stop.Token));
        Assert.ThrowsAny<OperationCanceledException>(()=>PerformanceSettingsTransfer.DecodeShareCode("secret-marker",stop.Token));
    }
    private class TrackingStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _source=new(bytes);
        public int BytesRead;public bool Disposed;
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count) {int read=_source.Read(buffer,offset,count);BytesRead+=read;return read;}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default) {int read=await _source.ReadAsync(buffer,ct);BytesRead+=read;return read;}
        public override void Flush()=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing) {Disposed=true;if(disposing) _source.Dispose();base.Dispose(disposing);}
    }
    private sealed class WaitingStream() : TrackingStream([])
    {
        public TaskCompletionSource<bool> Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
        {Started.TrySetResult(true);await Task.Delay(Timeout.InfiniteTimeSpan,ct);return 0;}
    }
}
