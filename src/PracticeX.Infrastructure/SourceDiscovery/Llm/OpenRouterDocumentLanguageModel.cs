using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PracticeX.Application.SourceDiscovery.Llm;
using PracticeX.Discovery.Llm;

namespace PracticeX.Infrastructure.SourceDiscovery.Llm;

/// <summary>
/// IDocumentLanguageModel backed by OpenRouter (https://openrouter.ai). Speaks
/// the OpenAI chat-completions schema, so any model on OpenRouter (Claude,
/// GPT-4, Gemini, etc.) routes through this single client. Use ApiKey +
/// DefaultModel to point at the right one.
/// </summary>
public sealed class OpenRouterDocumentLanguageModel : IDocumentLanguageModel
{
    private const string ChatCompletionsUrl = "https://openrouter.ai/api/v1/chat/completions";

    private readonly OpenRouterOptions _options;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OpenRouterDocumentLanguageModel> _logger;

    public OpenRouterDocumentLanguageModel(
        IOptions<OpenRouterOptions> options,
        IHttpClientFactory httpFactory,
        ILogger<OpenRouterDocumentLanguageModel> logger)
    {
        _options = options.Value;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public string Name => "openrouter";
    public bool IsConfigured => _options.Enabled && !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async Task<LanguageModelResponse> CompleteAsync(
        LanguageModelRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "OpenRouter is not configured. Set OpenRouter:Enabled=true and OpenRouter:ApiKey via user-secrets.");
        }

        var client = _httpFactory.CreateClient("openrouter");
        client.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            messages.Add(new ChatMessage("system", request.System));
        }
        foreach (var m in request.Messages)
        {
            messages.Add(new ChatMessage(m.Role, m.Content));
        }

        // Force JSON output via OpenRouter's response_format passthrough
        // (Anthropic ignores but tolerates; OpenAI honors). Combined with a
        // schema-shaped prompt, this nudges Claude into clean JSON without
        // requiring native tool-use binding.
        ChatCompletionRequest body;
        if (!string.IsNullOrWhiteSpace(request.JsonSchema))
        {
            body = new ChatCompletionRequest(
                Model: _options.DefaultModel,
                Messages: messages,
                MaxTokens: request.MaxTokens,
                Temperature: request.Temperature,
                ResponseFormat: new ResponseFormat("json_object")
            );
        }
        else
        {
            body = new ChatCompletionRequest(
                Model: _options.DefaultModel,
                Messages: messages,
                MaxTokens: request.MaxTokens,
                Temperature: request.Temperature,
                ResponseFormat: null
            );
        }

        using var http = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUrl);
        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        http.Headers.TryAddWithoutValidation("HTTP-Referer", _options.AppUrl);
        http.Headers.TryAddWithoutValidation("X-Title", _options.AppName);
        http.Content = JsonContent.Create(body, options: SerializerOptions);

        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("OpenRouter call: model={Model} purpose={Purpose} maxTokens={Max}",
            _options.DefaultModel, request.Purpose, request.MaxTokens);

        var response = await client.SendAsync(http, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenRouter non-2xx: status={Status} body={Body}",
                (int)response.StatusCode, raw.Length > 500 ? raw[..500] : raw);
            throw new HttpRequestException($"OpenRouter returned {(int)response.StatusCode}: {raw}");
        }

        ChatCompletionResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(raw, SerializerOptions);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"OpenRouter returned non-JSON or unexpected shape: {raw}", ex);
        }

        if (parsed is null || parsed.Choices is null || parsed.Choices.Count == 0)
        {
            throw new InvalidOperationException("OpenRouter returned no choices.");
        }

        var choice = parsed.Choices[0];
        var text = choice.Message?.Content ?? string.Empty;

        var tokensIn = parsed.Usage?.PromptTokens ?? 0;
        var tokensOut = parsed.Usage?.CompletionTokens ?? 0;
        var modelUsed = parsed.Model ?? _options.DefaultModel;

        _logger.LogInformation(
            "OpenRouter response: model={Model} purpose={Purpose} tokensIn={In} tokensOut={Out} latencyMs={Lat}",
            modelUsed, request.Purpose, tokensIn, tokensOut, stopwatch.ElapsedMilliseconds);

        return new LanguageModelResponse(
            Text: text,
            TokensIn: tokensIn,
            TokensOut: tokensOut,
            ProviderName: Name,
            Model: modelUsed,
            LatencyMs: stopwatch.ElapsedMilliseconds,
            StopReason: choice.FinishReason);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ResponseFormat(
        [property: JsonPropertyName("type")] string Type);

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("response_format")] ResponseFormat? ResponseFormat);

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice>? Choices,
        [property: JsonPropertyName("usage")] ChatUsage? Usage);

    private sealed record ChatChoice(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("message")] ChatChoiceMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record ChatChoiceMessage(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("content")] string? Content);

    private sealed record ChatUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
        [property: JsonPropertyName("total_tokens")] int TotalTokens);
}
