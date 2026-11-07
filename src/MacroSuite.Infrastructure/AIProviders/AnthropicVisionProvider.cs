using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MacroSuite.Core.AI;
using MacroSuite.SDK.AI;

namespace MacroSuite.Infrastructure.AIProviders;

/// <summary>
/// Anthropic Claude direct HTTP vision provider implementing <see cref="IAIVisionProvider"/> (Rule 9, Rule 16).
/// Uses Anthropic Messages REST API with Claude 3.5/3.7 multimodal vision capabilities.
/// </summary>
public sealed class AnthropicVisionProvider : IAIVisionProvider
{
    private static readonly string[] s_supportedModels =
    [
        "claude-3-7-sonnet-20250219",
        "claude-3-5-sonnet-20241022",
        "claude-3-5-haiku-20241022"
    ];

    private readonly ProviderHttpClientFactory? _clientFactory;
    private readonly HttpClient? _httpClient;

    public string ProviderId => "anthropic";
    public string DisplayName => "Anthropic Claude";
    public IReadOnlyList<string> SupportedModels => s_supportedModels;
    public string DefaultModel => "claude-3-5-sonnet-20241022";

    public VisionEncodePlan PlanEncode(int cropWidth, int cropHeight, string? modelId)
        => VisionEncodePlanner.Claude(cropWidth, cropHeight, modelId);

    public AnthropicVisionProvider(ProviderHttpClientFactory clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public AnthropicVisionProvider(HttpClient httpClient)
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
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");

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
                            if (!string.IsNullOrEmpty(id) && id.Contains("claude", StringComparison.OrdinalIgnoreCase))
                            {
                                availableList.Add(id);
                            }
                        }
                    }
                }
                catch
                {
                    availableList.AddRange(s_supportedModels);
                }

                return ApiKeyValidationResult.Success(
                    ProviderId,
                    effectiveModel,
                    sw.Elapsed,
                    "Anthropic API key authenticated successfully.",
                    availableList);
            }

            string errContent = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string errorDetail = ParseErrorMessage(errContent) ?? resp.ReasonPhrase ?? "Authentication failed";

            return ApiKeyValidationResult.Failure(
                ProviderId,
                effectiveModel,
                $"Anthropic error: {errorDetail}",
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
            "You are an automated computer vision assistant for desktop automation. " +
            "Analyze the screenshot image and user instruction. " +
            "Return x and y in image-local pixels measured from the screenshot's top-left corner. " +
            (request.ImageWidth > 0 && request.ImageHeight > 0
                ? $"The attached screenshot is {request.ImageWidth}x{request.ImageHeight} pixels. Use this image's pixel grid, not the native monitor resolution. "
                : string.Empty) +
            "Respond ONLY with a single valid raw JSON object matching this schema without any markdown wrappers: " +
            "{\"action_type\": \"click\"|\"double_click\"|\"keypress\"|\"text\"|\"delay\"|\"none\", " +
            "\"x\": integer or null, \"y\": integer or null, \"key_code\": integer or null, " +
            "\"text\": string or null, \"confidence\": float between 0.0 and 1.0, \"reasoning\": string}";

        if (request.IsMacroDraft)
            systemPrompt = AIMacroDraftProtocol.SystemPrompt;

        var contentElements = new List<object>();

        if (request.ImageBytes != null && request.ImageBytes.Length > 0)
        {
            string base64 = Convert.ToBase64String(request.ImageBytes);
            contentElements.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = "image/jpeg",
                    data = base64
                }
            });
        }

        contentElements.Add(new
        {
            type = "text",
            text = request.Prompt
        });

        var payload = new
        {
            model,
            max_tokens = request.IsMacroDraft ? AIMacroDraftProtocol.MaxOutputTokens : 500,
            temperature = 0.2,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = contentElements }
            }
        };

        string jsonPayload = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        string respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            string? err = ParseErrorMessage(respBody) ?? resp.ReasonPhrase ?? "Unknown error";
            throw new InvalidOperationException($"Anthropic request failed ({(int)resp.StatusCode}): {err}");
        }

        var root = JsonNode.Parse(respBody);
        var contentArray = root?["content"]?.AsArray();
        string rawText = string.Empty;

        if (contentArray != null)
        {
            foreach (var item in contentArray)
            {
                if (item?["type"]?.GetValue<string>() == "text")
                {
                    rawText = item["text"]?.GetValue<string>() ?? string.Empty;
                    break;
                }
            }
        }

        int inputTokens = root?["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
        int outputTokens = root?["usage"]?["output_tokens"]?.GetValue<int>() ?? 0;
        string? requestId = root?["id"]?.GetValue<string>();

        AIAction action = request.IsMacroDraft ? new AIAction() : AIActionJsonParser.Parse(rawText, "Anthropic");
        decimal estimatedCost = (inputTokens * 0.000003m) + (outputTokens * 0.000015m);

        return new AIResponse
        {
            Action = action,
            MacroDraftJson = request.IsMacroDraft ? rawText : null,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            EstimatedCostUsd = estimatedCost,
            ProviderRequestId = requestId,
            ModelId = model
        };
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
