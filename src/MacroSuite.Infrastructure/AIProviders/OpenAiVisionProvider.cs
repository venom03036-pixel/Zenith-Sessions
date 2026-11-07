using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MacroSuite.Core.AI;
using MacroSuite.SDK.AI;

namespace MacroSuite.Infrastructure.AIProviders;

/// <summary>
/// OpenAI direct HTTP vision provider implementing <see cref="IAIVisionProvider"/> (Rule 9, Rule 16).
/// Directly communicates with OpenAI REST API using <see cref="HttpClient"/> without external SDKs.
/// </summary>
public sealed class OpenAiVisionProvider : IAIVisionProvider
{
    private static readonly string[] s_supportedModels = ["gpt-4o", "gpt-4o-mini", "gpt-4-turbo"];
    private readonly ProviderHttpClientFactory? _clientFactory;
    private readonly HttpClient? _httpClient;

    public string ProviderId => "openai";
    public string DisplayName => "OpenAI";
    public IReadOnlyList<string> SupportedModels => s_supportedModels;
    public string DefaultModel => "gpt-4o";

    public VisionEncodePlan PlanEncode(int cropWidth, int cropHeight, string? modelId)
        => VisionEncodePlanner.OpenAiTiled(cropWidth, cropHeight, modelId);

    public OpenAiVisionProvider(ProviderHttpClientFactory clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public OpenAiVisionProvider(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    private HttpClient GetClient() => _httpClient ?? _clientFactory!.GetClient(ProviderId);

    /// <inheritdoc/>
    public async Task<ApiKeyValidationResult> ValidateApiKeyAsync(
        string apiKey,
        string? modelId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        string effectiveModel = modelId ?? DefaultModel;
        var sw = Stopwatch.StartNew();

        try
        {
            var client = GetClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            sw.Stop();

            if (resp.IsSuccessStatusCode)
            {
                var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var availableList = new List<string>();

                try
                {
                    var node = JsonNode.Parse(content);
                    var dataArray = node?["data"]?.AsArray();
                    if (dataArray != null)
                    {
                        foreach (var item in dataArray)
                        {
                            string? id = item?["id"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(id) &&
                                (id.Contains("gpt-4", StringComparison.OrdinalIgnoreCase) ||
                                 id.Contains("vision", StringComparison.OrdinalIgnoreCase)))
                            {
                                availableList.Add(id);
                            }
                        }
                    }
                }
                catch
                {
                    // Fallback to supported models if model listing parse fails
                    availableList.AddRange(s_supportedModels);
                }

                return ApiKeyValidationResult.Success(
                    ProviderId,
                    effectiveModel,
                    sw.Elapsed,
                    "OpenAI API key authenticated successfully.",
                    availableList);
            }

            string errContent = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string errorDetail = ParseErrorMessage(errContent) ?? resp.ReasonPhrase ?? "Authentication failed";

            return ApiKeyValidationResult.Failure(
                ProviderId,
                effectiveModel,
                $"OpenAI error: {errorDetail}",
                sw.Elapsed,
                (int)resp.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ApiKeyValidationResult.Failure(
                ProviderId,
                effectiveModel,
                $"Connection error: {ex.Message}",
                sw.Elapsed);
        }
    }

    /// <inheritdoc/>
    public async Task<AIResponse> AnalyzeAsync(
        AIRequest request,
        string apiKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        string model = !string.IsNullOrWhiteSpace(request.ModelId) ? request.ModelId : DefaultModel;
        var client = GetClient();

        string systemPrompt =
            "You are an automated computer vision assistant for a desktop macro application. " +
            "Analyze the screenshot image and user instruction. " +
            "Return x and y in image-local pixels measured from the screenshot's top-left corner. " +
            (request.ImageWidth > 0 && request.ImageHeight > 0
                ? $"The attached screenshot is {request.ImageWidth}x{request.ImageHeight} pixels. Use this image's pixel grid, not the native monitor resolution. "
                : string.Empty) +
            "Respond ONLY with a valid JSON object matching this schema: " +
            "{\"action_type\": \"click\"|\"double_click\"|\"keypress\"|\"text\"|\"delay\"|\"none\", " +
            "\"x\": integer or null, \"y\": integer or null, \"key_code\": integer or null, " +
            "\"text\": string or null, \"confidence\": float between 0.0 and 1.0, \"reasoning\": string}";

        if (request.IsMacroDraft)
            systemPrompt = AIMacroDraftProtocol.SystemPrompt;

        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        var userContents = new List<object>
        {
            new { type = "text", text = request.Prompt }
        };

        if (request.ImageBytes != null && request.ImageBytes.Length > 0)
        {
            string base64 = Convert.ToBase64String(request.ImageBytes);
            userContents.Add(new
            {
                type = "image_url",
                image_url = new
                {
                    url = $"data:image/jpeg;base64,{base64}",
                    detail = ResolveOpenAiDetail(request)
                }
            });
        }

        messages.Add(new { role = "user", content = userContents });

        var payload = new
        {
            model,
            messages,
            response_format = new { type = "json_object" },
            max_tokens = request.IsMacroDraft ? AIMacroDraftProtocol.MaxOutputTokens : 500,
            temperature = 0.2
        };

        string jsonPayload = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        string respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            string? err = ParseErrorMessage(respBody) ?? resp.ReasonPhrase ?? "Unknown error";
            throw new InvalidOperationException($"OpenAI request failed ({(int)resp.StatusCode}): {err}");
        }

        var root = JsonNode.Parse(respBody);
        string? messageContent = root?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        int promptTokens = root?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0;
        int completionTokens = root?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0;
        string? requestId = root?["id"]?.GetValue<string>();

        AIAction action = request.IsMacroDraft ? new AIAction() : AIActionJsonParser.Parse(messageContent, "OpenAI");
        decimal estimatedCost = (promptTokens * 0.000005m) + (completionTokens * 0.000015m);

        return new AIResponse
        {
            Action = action,
            MacroDraftJson = request.IsMacroDraft ? messageContent : null,
            InputTokens = promptTokens,
            OutputTokens = completionTokens,
            EstimatedCostUsd = estimatedCost,
            ProviderRequestId = requestId,
            ModelId = model
        };
    }

    private static string ResolveOpenAiDetail(AIRequest request)
    {
        if (request.Parameters is not null
            && request.Parameters.TryGetValue("detail", out string? detail)
            && !string.IsNullOrWhiteSpace(detail))
        {
            return detail.Trim().ToLowerInvariant();
        }

        return "high";
    }

    private static string? ParseErrorMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var node = JsonNode.Parse(json);
            return node?["error"]?["message"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }
}
