using System.Buffers;
using System.Drawing;
using MacroSuite.Core.Abstractions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Licensing;
using MacroSuite.Core.Models;
using MacroSuite.Core.Security;
using MacroSuite.Core.Usage;
using MacroSuite.SDK.AI;
using Xunit;

namespace MacroSuite.Core.Tests.AI;

public class AIMacroAuthoringServiceTests
{
    [Fact]
    public async Task TextOnlyDraftUsesPinnedProviderAndRecordsUsageWithoutCapture()
    {
        var fixture = new Fixture();
        AIMacroDraft draft = await fixture.Service.GenerateAsync("Press M", null, CancellationToken.None);
        Assert.True(fixture.Provider.Request!.IsMacroDraft); Assert.Null(fixture.Provider.Request.ImageBytes);
        Assert.Equal(0, fixture.Capture.Calls); Assert.Equal("fake", draft.Provider);
        Assert.Equal("fake", Assert.Single(fixture.Usage.Records).ProviderId);
        Assert.Equal(11, fixture.Usage.Records[0].InputTokens);
        Assert.Equal(0, fixture.Usage.Releases); Assert.Equal(1, fixture.Usage.Checks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSelectedProviderDoesNotSendScreenToAnotherConfiguredProvider(bool registeredWithoutKey)
    {
        var provider = new Provider();
        var router = new AIVisionRouter(new Vault(), [provider]) { DefaultProviderId = "missing" };
        if (registeredWithoutKey) router.RegisterProvider(new Provider { ProviderId = "missing" });
        var usage = new Usage();
        var service = new AIMacroAuthoringService(router, new Capture(), new Encoder(), usage, new Gate());
        using var snapshot = await service.CaptureAsync(new Rectangle(0, 0, 20, 20), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync("Click this", snapshot, CancellationToken.None));
        Assert.Equal(0, provider.Calls); Assert.Empty(usage.Records); Assert.Equal(1, usage.Releases);
    }

    [Fact]
    public async Task RouterChangesCannotChangeTheProviderAdvertisedByAnOpenDraftSession()
    {
        var fixture = new Fixture();
        fixture.Router.DefaultProviderId = "another"; fixture.Router.DefaultModelId = "different";
        Assert.Equal("fake · test", fixture.Service.ProviderSummary);
        await fixture.Service.GenerateAsync("Press M", null, CancellationToken.None);
        Assert.Equal("test", fixture.Provider.Request!.ModelId);
    }

    [Fact]
    public async Task InvalidGeneratedPlanStillRecordsBilledUsageAndNeverReturnsDraft()
    {
        var fixture = new Fixture(); fixture.Provider.Json = "invalid json";
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.GenerateAsync("Press M", null, CancellationToken.None));
        Assert.Single(fixture.Usage.Records); Assert.Equal(0, fixture.Usage.Releases);
    }

    [Fact]
    public async Task SpendCapAndLicenseFailurePreventRequests()
    {
        var fixture = new Fixture(); fixture.Usage.FailReserve = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateAsync("Press M", null, CancellationToken.None));
        Assert.Equal(0, fixture.Provider.Calls);
        fixture.Usage.FailReserve = false; fixture.Gate.Enabled = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateAsync("Press M", null, CancellationToken.None));
        Assert.Equal(0, fixture.Provider.Calls);
    }

    [Fact]
    public async Task CaptureIsExplicitOneShotSharedRegionAndDisposedByOwner()
    {
        var fixture = new Fixture();
        using var snapshot = await fixture.Service.CaptureAsync(new Rectangle(-40, 10, 80, 40), CancellationToken.None);
        Assert.Equal(1, fixture.Capture.Calls); Assert.Equal(snapshot.Area, fixture.Capture.LastArea);
        Assert.Equal(0, fixture.Provider.Calls);
        await fixture.Service.GenerateAsync("Press M", snapshot, CancellationToken.None);
        Assert.Equal(1, fixture.Capture.Calls); Assert.Same(snapshot.PreviewJpeg, fixture.Provider.Request!.ImageBytes);
        Assert.False(snapshot.Frame.IsDisposed);
        snapshot.Dispose(); Assert.True(snapshot.Frame.IsDisposed);
    }

    [Fact]
    public async Task CancellationReleasesReservationAndDoesNotReturnDraft()
    {
        var fixture = new Fixture(); fixture.Provider.Wait = true;
        using var cts = new CancellationTokenSource();
        Task generating = fixture.Service.GenerateAsync("Press M", null, cts.Token);
        await fixture.Provider.Entered.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generating);
        Assert.Equal(1, fixture.Usage.Releases); Assert.Empty(fixture.Usage.Records);
    }

    [Fact]
    public async Task PostBillingCapFailurePreventsReturningDraft()
    {
        var fixture = new Fixture(); fixture.Usage.FailCheck = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateAsync("Press M", null, CancellationToken.None));
        Assert.Single(fixture.Usage.Records);
    }

    private sealed class Fixture
    {
        public Capture Capture { get; } = new(); public Provider Provider { get; } = new(); public Usage Usage { get; } = new(); public Gate Gate { get; } = new();
        public AIMacroAuthoringService Service { get; }
        public AIVisionRouter Router { get; }
        public Fixture() { Router = new AIVisionRouter(new Vault(), [Provider]) { DefaultProviderId = "fake" }; Service = new(Router, Capture, new Encoder(), Usage, Gate); }
    }
    private sealed class Provider : IAIVisionProvider
    {
        public string ProviderId { get; init; } = "fake"; public string DisplayName => "Fake"; public IReadOnlyList<string> SupportedModels => ["test"]; public string DefaultModel => "test";
        public string Json { get; set; } = "{\"name\":\"Generated\",\"steps\":[{\"type\":\"key\",\"key\":\"M\"}]}";
        public AIRequest? Request { get; private set; } public int Calls { get; private set; } public bool Wait { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AIResponse> AnalyzeAsync(AIRequest request, string apiKey, CancellationToken ct = default)
        { Calls++; Request = request; Entered.TrySetResult(); if (Wait) await Task.Delay(Timeout.Infinite, ct); return new AIResponse { MacroDraftJson = Json, InputTokens = 11, OutputTokens = 12, EstimatedCostUsd = 0.01m }; }
        public Task<ApiKeyValidationResult> ValidateApiKeyAsync(string apiKey, string? modelId = null, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Capture : IScreenCaptureService
    {
        public int Calls { get; private set; } public Rectangle LastArea { get; private set; }
        public CaptureSource CurrentSource => CaptureSource.Gdi; public bool IsSupported => true;
        public Task<CaptureFrame> CaptureRegionAsync(Rectangle region, CancellationToken ct = default)
        { Calls++; LastArea = region; return Task.FromResult(new CaptureFrame(region.Width, region.Height, region.Width * 4, FramePixelFormat.Bgra32, CaptureSource.Gdi, MemoryPool<byte>.Shared.Rent(region.Width * region.Height * 4), originX: region.X, originY: region.Y)); }
        public Task<CaptureFrame> CaptureScreenAsync(CancellationToken ct = default) => throw new InvalidOperationException("Full-screen capture must never be used by authoring.");
        public void Dispose() { }
    }
    private sealed class Encoder : IImageEncoder
    { public byte[] EncodeJpeg(CaptureFrame frame, int quality = 80) => [1, 2, 3]; public byte[] EncodePng(CaptureFrame frame) => [4, 5, 6]; }
    private sealed class Gate : IFeatureGate
    { public bool Enabled { get; set; } = true; public bool IsAvailable(FeatureId feature) => Enabled; public void Require(FeatureId feature) { if (!Enabled) throw new InvalidOperationException("Feature unavailable"); } }
    private sealed class Vault : ISecretVault
    {
        public string? GetSecret(string name) => name == "fake" ? "test-key" : null; public IReadOnlyList<string> ListSecretKeys() => ["fake"];
        public bool HasSecret(string name) => name == "fake"; public void SetSecret(string name, string value) => throw new NotSupportedException(); public bool RemoveSecret(string name) => false;
    }
    private sealed class Usage : IUsageTracker
    {
        public event Action<UsageRecord>? UsageRecorded { add { } remove { } }
        public decimal? MonthlySpendCapUsd { get; set; } public decimal? DailySpendCapUsd { get; set; }
        public List<UsageRecord> Records { get; } = []; public bool FailReserve { get; set; } public bool FailCheck { get; set; } public int Releases { get; private set; } public int Checks { get; private set; }
        public Task<UsageReservation> ReserveAsync(decimal cost, CancellationToken ct = default) => FailReserve ? Task.FromException<UsageReservation>(new InvalidOperationException("Cap reached")) : Task.FromResult(new UsageReservation { Id = Guid.NewGuid(), EstimatedCostUsd = cost });
        public Task CommitAsync(UsageReservation reservation, UsageRecord record, CancellationToken ct = default) { Records.Add(record); reservation.IsCommitted = true; return Task.CompletedTask; }
        public Task ReleaseAsync(UsageReservation reservation, CancellationToken ct = default) { Releases++; return Task.CompletedTask; }
        public Task ThrowIfCapsExceededAsync(CancellationToken ct = default) { Checks++; return FailCheck ? Task.FromException(new InvalidOperationException("Cap reached")) : Task.CompletedTask; }
        public Task PreflightCheckAsync(decimal cost, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAsync(UsageRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<UsageTotals> GetTotalsAsync(DateTimeOffset since, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<decimal> GetTotalSpendAsync(DateTimeOffset since, CancellationToken ct = default) => Task.FromResult(0m);
        public Task<IReadOnlyList<UsageRecord>> GetRecentAsync(int count, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UsageRecord>>(Records);
    }
}
