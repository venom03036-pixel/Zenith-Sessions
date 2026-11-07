using FluentAssertions;
using MacroSuite.Core.AI;
using MacroSuite.Core.Security;
using MacroSuite.SDK.AI;
using Xunit;

namespace MacroSuite.Core.Tests.AI;

public class AIVisionRouterTests
{
    private sealed class InMemorySecretVault : ISecretVault
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);

        public void SetSecret(string keyName, string secretValue) => _secrets[keyName] = secretValue;
        public string? GetSecret(string keyName) => _secrets.TryGetValue(keyName, out var v) ? v : null;
        public bool HasSecret(string keyName) => _secrets.ContainsKey(keyName);
        public bool RemoveSecret(string keyName) => _secrets.Remove(keyName);
        public IReadOnlyList<string> ListSecretKeys() => _secrets.Keys.ToList();
    }

    private sealed class MockVisionProvider : IAIVisionProvider
    {
        public string ProviderId { get; }
        public string DisplayName { get; }
        public IReadOnlyList<string> SupportedModels { get; }
        public string DefaultModel { get; }

        public AIRequest? LastRequest { get; private set; }
        public string? LastApiKey { get; private set; }

        public MockVisionProvider(string providerId, string defaultModel = "default-model")
        {
            ProviderId = providerId;
            DisplayName = providerId.ToUpperInvariant();
            SupportedModels = [defaultModel, "alt-model"];
            DefaultModel = defaultModel;
        }

        public Task<AIResponse> AnalyzeAsync(AIRequest request, string apiKey, CancellationToken ct = default)
        {
            LastRequest = request;
            LastApiKey = apiKey;

            return Task.FromResult(new AIResponse
            {
                Action = new AIAction { ActionType = "click", X = 123, Y = 456, Reasoning = $"Executed by {ProviderId}" },
                InputTokens = 100,
                OutputTokens = 20,
                EstimatedCostUsd = 0.001m
            });
        }

        public Task<ApiKeyValidationResult> ValidateApiKeyAsync(string apiKey, string? modelId = null, CancellationToken ct = default)
        {
            if (apiKey == "valid-key")
            {
                return Task.FromResult(ApiKeyValidationResult.Success(ProviderId, modelId ?? DefaultModel, TimeSpan.FromMilliseconds(50)));
            }

            return Task.FromResult(ApiKeyValidationResult.Failure(ProviderId, modelId ?? DefaultModel, "Invalid key", TimeSpan.FromMilliseconds(50), 401));
        }
    }

    [Fact]
    public void RegisterAndRetrieveProviders_SupportsMultipleSimultaneously()
    {
        var vault = new InMemorySecretVault();
        var router = new AIVisionRouter(vault);

        var p1 = new MockVisionProvider("openai", "gpt-4o");
        var p2 = new MockVisionProvider("anthropic", "claude-3-5-sonnet");
        var p3 = new MockVisionProvider("gemini", "gemini-1.5-flash");

        router.RegisterProvider(p1);
        router.RegisterProvider(p2);
        router.RegisterProvider(p3);

        router.GetRegisteredProviders().Should().HaveCount(3);
        router.TryGetProvider("openai").Should().BeSameAs(p1);
        router.TryGetProvider("anthropic").Should().BeSameAs(p2);
        router.TryGetProvider("gemini").Should().BeSameAs(p3);
    }

    [Fact]
    public async Task TestApiKeyAsync_WithCandidateKey_ValidatesCorrectly()
    {
        var vault = new InMemorySecretVault();
        var p1 = new MockVisionProvider("openai");
        var router = new AIVisionRouter(vault, [p1]);

        var resultValid = await router.TestApiKeyAsync("openai", "valid-key");
        resultValid.IsValid.Should().BeTrue();
        resultValid.ProviderId.Should().Be("openai");

        var resultInvalid = await router.TestApiKeyAsync("openai", "bad-key");
        resultInvalid.IsValid.Should().BeFalse();
        resultInvalid.HttpStatusCode.Should().Be(401);
    }

    [Fact]
    public async Task TestApiKeyAsync_UsesVaultKey_WhenNoCandidateProvided()
    {
        var vault = new InMemorySecretVault();
        vault.SetSecret("anthropic", "valid-key");

        var p = new MockVisionProvider("anthropic");
        var router = new AIVisionRouter(vault, [p]);

        var result = await router.TestApiKeyAsync("anthropic");

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task TestApiKeyAsync_MissingKey_ReturnsFailure()
    {
        var vault = new InMemorySecretVault();
        var p = new MockVisionProvider("openai");
        var router = new AIVisionRouter(vault, [p]);

        var result = await router.TestApiKeyAsync("openai");

        result.IsValid.Should().BeFalse();
        result.Message.Should().Contain("No API key found");
    }

    [Fact]
    public async Task AnalyzeAsync_RoutesToSpecifiedProviderAndModel()
    {
        var vault = new InMemorySecretVault();
        vault.SetSecret("anthropic", "sk-test-anthropic");

        var pOpenAi = new MockVisionProvider("openai", "gpt-4o");
        var pClaude = new MockVisionProvider("anthropic", "claude-3-5-sonnet");
        var router = new AIVisionRouter(vault, [pOpenAi, pClaude]);

        var request = new AIRequest { Prompt = "Locate save button" };

        var response = await router.AnalyzeAsync(request, providerId: "anthropic", modelId: "claude-3-7-sonnet");

        response.Action.ActionType.Should().Be("click");
        pClaude.LastRequest.Should().NotBeNull();
        pClaude.LastRequest!.ModelId.Should().Be("claude-3-7-sonnet");
        pClaude.LastApiKey.Should().Be("sk-test-anthropic");
    }

    [Fact]
    public async Task AnalyzeAsync_MissingKey_ThrowsInvalidOperationException()
    {
        var vault = new InMemorySecretVault();
        var p = new MockVisionProvider("openai");
        var router = new AIVisionRouter(vault, [p]);

        var act = async () => await router.AnalyzeAsync(new AIRequest { Prompt = "Test" }, "openai");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No API key configured*");
    }

    [Fact]
    public void GetConfiguredProviderIds_ReturnsOnlyVaultKeysPresent()
    {
        var vault = new InMemorySecretVault();
        vault.SetSecret("openai", "key-1");
        vault.SetSecret("gemini", "key-2");

        var router = new AIVisionRouter(vault, [
            new MockVisionProvider("openai"),
            new MockVisionProvider("anthropic"),
            new MockVisionProvider("gemini")
        ]);

        var configured = router.GetConfiguredProviderIds();

        configured.Should().BeEquivalentTo(["openai", "gemini"]);
    }

    [Fact]
    public async Task AnalyzeAsync_FallsBackToConfiguredProvider_WhenDefaultHasNoKey()
    {
        var vault = new InMemorySecretVault();
        vault.SetSecret("gemini", "sk-gemini");

        var openai = new MockVisionProvider("openai", "gpt-4o");
        var gemini = new MockVisionProvider("gemini", "gemini-1.5-flash");
        var router = new AIVisionRouter(vault, [openai, gemini]);

        var response = await router.AnalyzeAsync(new AIRequest { Prompt = "Click the video" });

        response.Action.Reasoning.Should().Contain("gemini");
        gemini.LastRequest.Should().NotBeNull();
        gemini.LastRequest!.ModelId.Should().Be("gemini-1.5-flash");
        openai.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_UsesDefaultProvider_WhenThatProviderHasAKey()
    {
        var vault = new InMemorySecretVault();
        vault.SetSecret("openai", "sk-openai");
        vault.SetSecret("gemini", "sk-gemini");

        var openai = new MockVisionProvider("openai", "gpt-4o");
        var gemini = new MockVisionProvider("gemini", "gemini-1.5-flash");
        var router = new AIVisionRouter(vault, [openai, gemini])
        {
            DefaultProviderId = "gemini",
            DefaultModelId = "gemini-1.5-flash"
        };

        await router.AnalyzeAsync(new AIRequest { Prompt = "Find the button" });

        gemini.LastRequest.Should().NotBeNull();
        openai.LastRequest.Should().BeNull();
        gemini.LastRequest!.ModelId.Should().Be("gemini-1.5-flash");
    }
}
