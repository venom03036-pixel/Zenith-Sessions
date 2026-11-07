using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using MacroSuite.Infrastructure.AIProviders;
using MacroSuite.SDK.AI;
using Xunit;

namespace MacroSuite.Infrastructure.Tests.AIProviders;

public class VisionProviderTests
{
    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return _responder(request);
        }
    }

    #region OpenAI Provider Tests

    [Fact]
    public async Task OpenAi_ValidateApiKey_Success_ParsesModelsAndLatency()
    {
        var handler = new MockHttpHandler(req =>
        {
            req.RequestUri!.ToString().Should().Be("https://api.openai.com/v1/models");
            req.Headers.Authorization!.Parameter.Should().Be("sk-test-valid");

            string json = """
            {
              "data": [
                { "id": "gpt-4o" },
                { "id": "gpt-4o-mini" },
                { "id": "text-embedding-3-small" }
              ]
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var provider = new OpenAiVisionProvider(client);

        var result = await provider.ValidateApiKeyAsync("sk-test-valid");

        result.IsValid.Should().BeTrue();
        result.ProviderId.Should().Be("openai");
        result.AvailableModels.Should().Contain("gpt-4o");
        result.AvailableModels.Should().Contain("gpt-4o-mini");
        result.AvailableModels.Should().NotContain("text-embedding-3-small");
    }

    [Fact]
    public async Task OpenAi_ValidateApiKey_InvalidKey_ParsesErrorDetail()
    {
        var handler = new MockHttpHandler(req =>
        {
            string errJson = """
            {
              "error": {
                "message": "Incorrect API key provided: sk-invalid.",
                "type": "invalid_request_error"
              }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(errJson, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var provider = new OpenAiVisionProvider(client);

        var result = await provider.ValidateApiKeyAsync("sk-invalid");

        result.IsValid.Should().BeFalse();
        result.HttpStatusCode.Should().Be(401);
        result.Message.Should().Contain("Incorrect API key provided");
    }

    [Fact]
    public async Task OpenAi_AnalyzeAsync_ParsesStructuredActionAndTokens()
    {
        var handler = new MockHttpHandler(req =>
        {
            req.RequestUri!.ToString().Should().Be("https://api.openai.com/v1/chat/completions");

            string respJson = """
            {
              "id": "chatcmpl-123",
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "content": "{\"action_type\":\"click\",\"x\":640,\"y\":480,\"confidence\":0.96,\"reasoning\":\"Submit button clicked\"}"
                  }
                }
              ],
              "usage": {
                "prompt_tokens": 150,
                "completion_tokens": 30
              }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respJson, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var provider = new OpenAiVisionProvider(client);

        var request = new AIRequest
        {
            Prompt = "Click Submit",
            ImageBytes = [0xFF, 0xD8, 0xFF],
            ImageWidth = 1920,
            ImageHeight = 1080
        };

        var response = await provider.AnalyzeAsync(request, "sk-valid-123");

        response.Action.ActionType.Should().Be("click");
        response.Action.X.Should().Be(640);
        response.Action.Y.Should().Be(480);
        response.Action.Confidence.Should().Be(0.96);
        response.InputTokens.Should().Be(150);
        response.OutputTokens.Should().Be(30);
        response.EstimatedCostUsd.Should().BeGreaterThan(0m);
    }

    #endregion

    #region Anthropic Provider Tests

    [Fact]
    public async Task Anthropic_ValidateApiKey_Success_ParsesModels()
    {
        var handler = new MockHttpHandler(req =>
        {
            req.RequestUri!.ToString().Should().Be("https://api.anthropic.com/v1/models");
            req.Headers.GetValues("x-api-key").First().Should().Be("sk-ant-test");

            string json = """
            {
              "data": [
                { "id": "claude-3-5-sonnet-20241022" },
                { "id": "claude-3-5-haiku-20241022" }
              ]
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var provider = new AnthropicVisionProvider(client);

        var result = await provider.ValidateApiKeyAsync("sk-ant-test");

        result.IsValid.Should().BeTrue();
        result.ProviderId.Should().Be("anthropic");
        result.AvailableModels.Should().Contain("claude-3-5-sonnet-20241022");
    }

    [Fact]
    public async Task Anthropic_AnalyzeAsync_CleansMarkdownAndParsesAction()
    {
        var handler = new MockHttpHandler(req =>
        {
            req.RequestUri!.ToString().Should().Be("https://api.anthropic.com/v1/messages");

            string respJson = """
            {
              "id": "msg-456",
              "content": [
                {
                  "type": "text",
                  "text": "```json\n{\"action_type\":\"keypress\",\"key_code\":13,\"confidence\":0.99,\"reasoning\":\"Press Enter\"}\n```"
                }
              ],
              "usage": {
                "input_tokens": 200,
                "output_tokens": 25
              }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respJson, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var provider = new AnthropicVisionProvider(client);

        var request = new AIRequest { Prompt = "Press Enter key" };
        var response = await provider.AnalyzeAsync(request, "sk-ant-key");

        response.Action.ActionType.Should().Be("keypress");
        response.Action.KeyCode.Should().Be(13);
        response.InputTokens.Should().Be(200);
        response.OutputTokens.Should().Be(25);
    }

    #endregion

    #region Google Gemini Provider Tests

    [Fact]
    public async Task Gemini_ValidateApiKey_Success_ParsesModels()
    {
        var handler = new MockHttpHandler(req =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/v1beta/models");
            req.RequestUri.Query.Should().Contain("pageSize=100");
            req.Headers.GetValues("x-goog-api-key").Should().ContainSingle().Which.Should().Be("AIzaSyTest");

            string json = """
            {
              "models": [
                { "name": "models/gemini-3.5-flash", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-3.5-flash-lite", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-1.5-flash", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-embedding-001", "supportedGenerationMethods": ["embedContent"] },
                { "name": "models/imagen-4.0-generate-001", "supportedGenerationMethods": ["generateContent"] }
              ]
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var provider = new GoogleGeminiVisionProvider(client);

        var result = await provider.ValidateApiKeyAsync("AIzaSyTest");

        result.IsValid.Should().BeTrue();
        result.ProviderId.Should().Be("gemini");
        result.AvailableModels.Should().Equal("gemini-3.5-flash-lite", "gemini-3.5-flash");
        result.AvailableModels.Should().NotContain("gemini-1.5-flash");
        result.AvailableModels.Should().NotContain("gemini-embedding-001");
        result.AvailableModels.Should().NotContain("imagen-4.0-generate-001");
        provider.SupportedModels.Should().Equal(result.AvailableModels);
        provider.DefaultModel.Should().Be("gemini-3.5-flash-lite");
    }

    [Fact]
    public async Task Gemini_ValidateApiKey_PaginatesListModels()
    {
        int calls = 0;
        var handler = new MockHttpHandler(req =>
        {
            calls++;
            req.RequestUri!.AbsolutePath.Should().Be("/v1beta/models");
            if (calls == 1)
            {
                req.RequestUri.Query.Should().NotContain("pageToken");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "models": [
                            { "name": "models/gemini-3.5-flash", "supportedGenerationMethods": ["generateContent"] }
                          ],
                          "nextPageToken": "page-2"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            req.RequestUri.Query.Should().Contain("pageToken=page-2");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "models": [
                        { "name": "models/gemini-3.5-flash-lite", "supportedGenerationMethods": ["generateContent"] }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var provider = new GoogleGeminiVisionProvider(client);
        var result = await provider.ValidateApiKeyAsync("AIzaSyTest");

        calls.Should().Be(2);
        result.IsValid.Should().BeTrue();
        result.AvailableModels.Should().Equal("gemini-3.5-flash-lite", "gemini-3.5-flash");
    }

    [Fact]
    public void Gemini_ParseServedGenerateContentModels_KeepsServedIdsAndSortsLiteFirst()
    {
        string json = """
        {
          "models": [
            { "name": "models/gemini-2.5-pro", "supportedGenerationMethods": ["generateContent"] },
            { "name": "models/gemini-3.5-flash-lite", "supportedGenerationMethods": ["generateContent"] },
            { "name": "models/gemini-3.5-flash", "supportedGenerationMethods": ["generateContent"] },
            { "name": "models/gemini-embedding-001", "supportedGenerationMethods": ["embedContent"] }
          ]
        }
        """;

        IReadOnlyList<string> models = GoogleGeminiVisionProvider.ParseServedGenerateContentModels(json);

        models.Should().Equal("gemini-3.5-flash-lite", "gemini-3.5-flash", "gemini-2.5-pro");
    }

    [Fact]
    public void Gemini_ResolveModelId_MapsRetired15Flash()
    {
        GoogleGeminiVisionProvider.ResolveModelId("gemini-1.5-flash").Should().Be("gemini-3.5-flash-lite");
        GoogleGeminiVisionProvider.ResolveModelId("models/gemini-1.5-pro").Should().Be("gemini-3.5-flash-lite");
        GoogleGeminiVisionProvider.ResolveModelId("gemini-2.5-flash").Should().Be("gemini-2.5-flash");
        GoogleGeminiVisionProvider.ResolveModelId("gemini-3.5-flash-lite").Should().Be("gemini-3.5-flash-lite");
        GoogleGeminiVisionProvider.ResolveModelId(null).Should().Be("gemini-3.5-flash-lite");
    }

    [Fact]
    public async Task Gemini_AnalyzeAsync_RemapsRetired15FlashModelInUrl()
    {
        string? requestedPath = null;
        var handler = new MockHttpHandler(req =>
        {
            requestedPath = req.RequestUri!.AbsolutePath;
            string respJson = """
            {
              "candidates": [
                {
                  "content": {
                    "parts": [
                      { "text": "{\"action_type\":\"click\",\"x\":10,\"y\":20,\"confidence\":0.9,\"reasoning\":\"ok\"}" }
                    ]
                  }
                }
              ],
              "usageMetadata": { "promptTokenCount": 11, "candidatesTokenCount": 4 }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respJson, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var provider = new GoogleGeminiVisionProvider(client);
        var response = await provider.AnalyzeAsync(new AIRequest { Prompt = "Click", ModelId = "gemini-1.5-flash" }, "AIzaKey");

        requestedPath.Should().Contain("gemini-3.5-flash-lite");
        response.ModelId.Should().Be("gemini-3.5-flash-lite");
        response.InputTokens.Should().Be(11);
        response.OutputTokens.Should().Be(4);
    }

    [Fact]
    public async Task Gemini_AnalyzeAsync_ParsesCandidateText()
    {
        var handler = new MockHttpHandler(req =>
        {
            req.RequestUri!.ToString().Should().EndWith("generateContent");
            req.RequestUri.Query.Should().BeEmpty();
            req.Headers.GetValues("x-goog-api-key").Should().ContainSingle().Which.Should().Be("AIzaKey");

            string respJson = """
            {
              "candidates": [
                {
                  "content": {
                    "parts": [
                      {
                        "text": "{\"action_type\":\"text\",\"text\":\"admin\",\"confidence\":0.92,\"reasoning\":\"Type username\"}"
                      }
                    ]
                  }
                }
              ],
              "usageMetadata": {
                "promptTokenCount": 180,
                "candidatesTokenCount": 20
              }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respJson, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var provider = new GoogleGeminiVisionProvider(client);

        var request = new AIRequest { Prompt = "Type admin" };
        var response = await provider.AnalyzeAsync(request, "AIzaKey");

        response.Action.ActionType.Should().Be("text");
        response.Action.Text.Should().Be("admin");
        response.InputTokens.Should().Be(180);
        response.OutputTokens.Should().Be(20);
    }

    [Fact]
    public async Task Gemini_AnalyzeAsync_UsesJsonPart_WhenThinkingPartComesFirst()
    {
        var handler = new MockHttpHandler(req =>
        {
            string respJson = """
            {
              "candidates": [
                {
                  "content": {
                    "parts": [
                      { "text": "The green button is in the lower half of the image." },
                      { "text": "{\"action_type\":\"click\",\"x\":240.4,\"y\":160.6,\"confidence\":0.91,\"reasoning\":\"Green button\"}" }
                    ]
                  }
                }
              ],
              "usageMetadata": {
                "promptTokenCount": 10,
                "candidatesTokenCount": 8
              }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respJson, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var provider = new GoogleGeminiVisionProvider(client);
        var response = await provider.AnalyzeAsync(new AIRequest { Prompt = "Click the button" }, "AIzaKey");

        response.Action.ActionType.Should().Be("click");
        response.Action.X.Should().Be(240);
        response.Action.Y.Should().Be(161);
        response.Action.Confidence.Should().BeApproximately(0.91, 0.001);
    }

    #endregion
}
