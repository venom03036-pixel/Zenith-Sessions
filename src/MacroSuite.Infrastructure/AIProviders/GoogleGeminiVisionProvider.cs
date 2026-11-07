using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MacroSuite.Core.AI;
using MacroSuite.SDK.AI;

namespace MacroSuite.Infrastructure.AIProviders;

/// <summary>
/// Google Gemini direct HTTP vision provider implementing <see cref="IAIVisionProvider"/> (Rule 9, Rule 16).
/// Uses Google Generative Language REST API without external client library dependencies.
/// </summary>
public sealed class GoogleGeminiVisionProvider : IAIVisionProvider
{
    public const string CurrentFlashLiteModel = "gemini-3.5-flash-lite";
    public const string LatestFlashAlias = "gemini-flash-latest";

    private volatile IReadOnlyList<string> _servedModels = [CurrentFlashLiteModel];
    private readonly ProviderHttpClientFactory? _clientFactory;
    private readonly HttpClient? _httpClient;

    public string ProviderId => "gemini";
    public string DisplayName => "Google Gemini";
    public IReadOnlyList<string> SupportedModels => _servedModels;
    public string DefaultModel => CurrentFlashLiteModel;

    public VisionEncodePlan PlanEncode(int cropWidth, int cropHeight, string? modelId)
        => VisionEncodePlanner.Gemini(cropWidth, cropHeight);

    /// <summary>
    /// Maps retired Gemini IDs onto the cheapest currently served Flash-Lite model.
    /// Live catalog names are not rewritten — only empty or retired IDs are.
    /// </summary>
    public static string ResolveModelId(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return CurrentFlashLiteModel;

        string trimmed = modelId.Trim();
        if (trimmed.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[7..];

        if (IsRetiredGeminiId(trimmed))
            return CurrentFlashLiteModel;

        return trimmed;
    }

    /// <summary>
    /// Parses Gemini <c>models.list</c> JSON into generateContent model IDs Google is actually serving.
    /// Embedding, Imagen, Veo, and retired 1.5 IDs are excluded. Names are not remapped.
    /// </summary>
    public static IReadOnlyList<string> ParseServedGenerateContentModels(string json)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        AppendServedGenerateContentModels(json, unique, list);
        list.Sort(CompareServedModels);
        return list;
    }

    public GoogleGeminiVisionProvider(ProviderHttpClientFactory clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public GoogleGeminiVisionProvider(HttpClient httpClient)
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
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var availableList = new List<string>();
            string? pageToken = null;
            int pages = 0;
            HttpStatusCode lastStatus = HttpStatusCode.OK;
            string? lastErrorDetail = null;

            do
            {
                string url = BuildListModelsUrl(pageToken);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("x-goog-api-key", apiKey);

                using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
                lastStatus = resp.StatusCode;
                string content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    lastErrorDetail = ParseErrorMessage(content) ?? resp.ReasonPhrase ?? "Authentication failed";
                    break;
                }

                AppendServedGenerateContentModels(content, unique, availableList);
                pageToken = TryReadNextPageToken(content);
                pages++;
            }
            while (!string.IsNullOrEmpty(pageToken) && pages < 10);

            sw.Stop();

            if (availableList.Count > 0)
            {
                availableList.Sort(CompareServedModels);
                _servedModels = availableList;
                return ApiKeyValidationResult.Success(
                    ProviderId,
                    effectiveModel,
                    sw.Elapsed,
                    "Google Gemini API key authenticated successfully.",
                    availableList);
            }

            if (lastStatus == HttpStatusCode.OK && pages > 0)
            {
                availableList.AddRange(_servedModels);
                return ApiKeyValidationResult.Success(
                    ProviderId,
                    effectiveModel,
                    sw.Elapsed,
                    "Google Gemini API key authenticated successfully.",
                    availableList);
            }

            return ApiKeyValidationResult.Failure(
                ProviderId,
                effectiveModel,
                $"Gemini error: {lastErrorDetail ?? "Authentication failed"}",
                sw.Elapsed,
                (int)lastStatus);
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

        string model = ResolveRequestModelId(request.ModelId);
        var client = GetClient();

        string systemInstruction =
            "You are an automated computer vision assistant for desktop automation. " +
            "Analyze the screenshot image and user instruction. " +
            "Return x and y as integers in [0, 1000] normalized to the attached image. " +
            "JSON x is the vertical axis (0=top, 1000=bottom). JSON y is the horizontal axis (0=left, 1000=right). " +
            (request.ImageWidth > 0 && request.ImageHeight > 0
                ? $"The attached screenshot is {request.ImageWidth}x{request.ImageHeight} pixels. "
                : string.Empty) +
            "Respond ONLY with a single valid JSON object matching this schema: " +
            "{\"action_type\": \"click\"|\"double_click\"|\"keypress\"|\"text\"|\"delay\"|\"none\", " +
            "\"x\": integer or null, \"y\": integer or null, \"key_code\": integer or null, " +
            "\"text\": string or null, \"confidence\": float between 0.0 and 1.0, \"reasoning\": string}";

        if (request.IsMacroDraft)
            systemInstruction = AIMacroDraftProtocol.SystemPrompt;

        var parts = new List<object>();

        if (request.ImageBytes != null && request.ImageBytes.Length > 0)
        {
            string base64 = Convert.ToBase64String(request.ImageBytes);
            parts.Add(new
            {
                inline_data = new
                {
                    mime_type = "image/jpeg",
                    data = base64
                }
            });
        }

        parts.Add(new { text = request.Prompt });

        var generationConfig = new Dictionary<string, object> { ["response_mime_type"] = "application/json" };
        if (request.IsMacroDraft)
            generationConfig["maxOutputTokens"] = AIMacroDraftProtocol.MaxOutputTokens;

        var payload = new
        {
            contents = new[]
            {
                new { parts }
            },
            generationConfig,
            system_instruction = new
            {
                parts = new[]
                {
                    new { text = systemInstruction }
                }
            }
        };

        string jsonPayload = JsonSerializer.Serialize(payload);
        using var resp = await SendGenerateContentAsync(client, apiKey, model, jsonPayload, ct).ConfigureAwait(false);
        string respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            if (!request.IsMacroDraft && (int)resp.StatusCode == 404
                && !string.Equals(model, LatestFlashAlias, StringComparison.OrdinalIgnoreCase))
            {
                string fallback = string.Equals(model, CurrentFlashLiteModel, StringComparison.OrdinalIgnoreCase)
                    ? LatestFlashAlias
                    : CurrentFlashLiteModel;
                using var retry = await SendGenerateContentAsync(client, apiKey, fallback, jsonPayload, ct).ConfigureAwait(false);
                string retryBody = await retry.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (retry.IsSuccessStatusCode)
                    return ParseGenerateContentResponse(retryBody, fallback, request.IsMacroDraft);

                string? retryErr = ParseErrorMessage(retryBody) ?? retry.ReasonPhrase ?? "Unknown error";
                throw new InvalidOperationException($"Gemini request failed ({(int)retry.StatusCode}): {retryErr}");
            }

            string? err = ParseErrorMessage(respBody) ?? resp.ReasonPhrase ?? "Unknown error";
            throw new InvalidOperationException($"Gemini request failed ({(int)resp.StatusCode}): {err}");
        }

        return ParseGenerateContentResponse(respBody, model, request.IsMacroDraft);
    }

    private string ResolveRequestModelId(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return DefaultModel;

        string trimmed = requested.Trim();
        if (trimmed.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[7..];

        IReadOnlyList<string> served = _servedModels;
        for (int i = 0; i < served.Count; i++)
        {
            if (string.Equals(served[i], trimmed, StringComparison.OrdinalIgnoreCase))
                return served[i];
        }

        return ResolveModelId(trimmed);
    }

    private static string BuildListModelsUrl(string? pageToken)
    {
        const string baseUrl = "https://generativelanguage.googleapis.com/v1beta/models?pageSize=100";
        if (string.IsNullOrEmpty(pageToken))
            return baseUrl;

        return baseUrl + "&pageToken=" + Uri.EscapeDataString(pageToken);
    }

    private static string? TryReadNextPageToken(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var node = JsonNode.Parse(json);
            string? token = node?["nextPageToken"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AppendServedGenerateContentModels(string json, HashSet<string> unique, List<string> list)
    {
        if (string.IsNullOrWhiteSpace(json) || unique is null || list is null)
            return;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        var modelsArray = node?["models"]?.AsArray();
        if (modelsArray is null)
            return;

        foreach (var item in modelsArray)
        {
            string? name = item?["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name))
                continue;

            string cleanName = name.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? name[7..] : name;
            if (!IsServedGenerateContentModel(cleanName, item?["supportedGenerationMethods"]?.AsArray()))
                continue;

            if (unique.Add(cleanName))
                list.Add(cleanName);
        }
    }

    private static bool IsServedGenerateContentModel(string modelId, JsonArray? methods)
    {
        if (!modelId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsRetiredGeminiId(modelId) || IsNonGenerateGeminiFamily(modelId))
            return false;

        if (methods is { Count: > 0 })
        {
            bool generateContent = false;
            foreach (var method in methods)
            {
                if (string.Equals(method?.GetValue<string>(), "generateContent", StringComparison.OrdinalIgnoreCase))
                {
                    generateContent = true;
                    break;
                }
            }

            if (!generateContent)
                return false;
        }

        return true;
    }

    public static bool IsRetiredGeminiId(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;
        if (modelId.Contains("1.5", StringComparison.OrdinalIgnoreCase))
            return true;
        if (modelId.Equals("gemini-pro", StringComparison.OrdinalIgnoreCase))
            return true;
        if (modelId.Equals("gemini-pro-vision", StringComparison.OrdinalIgnoreCase))
            return true;
        if (modelId.Equals("gemini-flash", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsNonGenerateGeminiFamily(string modelId)
    {
        return modelId.Contains("embedding", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("imagen", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("veo", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("aqa", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("tts", StringComparison.OrdinalIgnoreCase);
    }

    internal static int CompareServedModels(string left, string right)
    {
        int rank = RankServedModel(left).CompareTo(RankServedModel(right));
        if (rank != 0)
            return rank;

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static int RankServedModel(string modelId)
    {
        string name = modelId.ToLowerInvariant();
        if (name.Contains("flash-lite", StringComparison.Ordinal))
            return 0;
        if (name.Contains("flash", StringComparison.Ordinal) && !name.Contains("pro", StringComparison.Ordinal))
            return 1;
        if (name.Contains("pro", StringComparison.Ordinal))
            return 2;
        return 3;
    }

    private static async Task<HttpResponseMessage> SendGenerateContentAsync(
        HttpClient client,
        string apiKey,
        string model,
        string jsonPayload,
        CancellationToken ct)
    {
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-goog-api-key", apiKey);
        return await client.SendAsync(req, ct).ConfigureAwait(false);
    }

    private static AIResponse ParseGenerateContentResponse(string respBody, string model, bool isMacroDraft = false)
    {
        var root = JsonNode.Parse(respBody);
        string? candidateText = ExtractCandidateText(root);
        int promptTokens = root?["usageMetadata"]?["promptTokenCount"]?.GetValue<int>() ?? 0;
        int completionTokens = root?["usageMetadata"]?["candidatesTokenCount"]?.GetValue<int>() ?? 0;

        AIAction action = isMacroDraft ? new AIAction() : AIActionJsonParser.Parse(candidateText, "Gemini");
        decimal estimatedCost = (promptTokens * 0.00000015m) + (completionTokens * 0.0000006m);

        return new AIResponse
        {
            Action = action,
            MacroDraftJson = isMacroDraft ? candidateText : null,
            InputTokens = promptTokens,
            OutputTokens = completionTokens,
            EstimatedCostUsd = estimatedCost,
            ModelId = model
        };
    }

    private static string? ExtractCandidateText(JsonNode? root)
    {
        var parts = root?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
        if (parts is null || parts.Count == 0)
            return null;

        string? fallback = null;
        foreach (var part in parts)
        {
            string? text = part?["text"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            string trimmed = text.Trim();
            if (trimmed.StartsWith('{') || trimmed.StartsWith("```", StringComparison.Ordinal))
                return text;

            fallback ??= text;
        }

        return fallback;
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
