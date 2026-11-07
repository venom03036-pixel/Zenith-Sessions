using System.Net;
using System.Net.Http;
using System.Text.Json;
using MacroSuite.Core.AI;
using MacroSuite.Infrastructure.AIProviders;
using MacroSuite.SDK.AI;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.AIProviders;

public class MacroDraftProviderTests
{
    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    public async Task DraftRequestsUseDraftProtocolAndPreserveUsageWithoutDispatchingAction(string providerId)
    {
        const string draft = "{\"name\":\"Generated\",\"steps\":[{\"type\":\"key\",\"key\":\"M\"}]}";
        using var handler = new Handler(providerId, draft);
        using var client = new HttpClient(handler);
        IAIVisionProvider provider = providerId switch { "openai" => new OpenAiVisionProvider(client), "anthropic" => new AnthropicVisionProvider(client), _ => new GoogleGeminiVisionProvider(client) };
        AIResponse result = await provider.AnalyzeAsync(new AIRequest { IsMacroDraft = true, Prompt = "Make a macro", ImageBytes = [1, 2], ImageWidth = 80, ImageHeight = 60 }, "test-key");
        Assert.Equal(draft, result.MacroDraftJson);
        Assert.Equal("none", result.Action.ActionType);
        Assert.Equal(30, result.InputTokens); Assert.Equal(20, result.OutputTokens);
        Assert.Contains("reusable Zenith Macros", handler.Body);
        Assert.Contains("Never use normalized", handler.Body);
        Assert.Contains("8000", handler.Body);
    }
    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    public async Task LiveRequestsStillParseSingleActionsAndDoNotExposeDrafts(string providerId)
    {
        const string action = "{\"action_type\":\"keypress\",\"key_code\":77,\"confidence\":0.9}";
        using var handler = new Handler(providerId, action); using var client = new HttpClient(handler);
        IAIVisionProvider provider = providerId switch { "openai" => new OpenAiVisionProvider(client), "anthropic" => new AnthropicVisionProvider(client), _ => new GoogleGeminiVisionProvider(client) };
        AIResponse result = await provider.AnalyzeAsync(new AIRequest { Prompt = "Press M" }, "test-key");
        Assert.Null(result.MacroDraftJson); Assert.Equal("keypress", result.Action.ActionType); Assert.Equal(77, result.Action.KeyCode);
        Assert.DoesNotContain("reusable Zenith Macros", handler.Body);
    }
    private sealed class Handler(string provider, string response) : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            string text = JsonSerializer.Serialize(response);
            string body = provider switch
            {
                "openai" => "{\"choices\":[{\"message\":{\"content\":" + text + "}}],\"usage\":{\"prompt_tokens\":30,\"completion_tokens\":20}}",
                "anthropic" => "{\"content\":[{\"type\":\"text\",\"text\":" + text + "}],\"usage\":{\"input_tokens\":30,\"output_tokens\":20}}",
                _ => "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":" + text + "}]}}],\"usageMetadata\":{\"promptTokenCount\":30,\"candidatesTokenCount\":20}}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
