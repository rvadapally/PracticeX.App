namespace PracticeX.Application.SourceDiscovery.Llm;

/// <summary>
/// Configuration for the OpenRouter LLM gateway. OpenRouter sits in front of
/// Anthropic + OpenAI + others; this lets us reach Claude and GPT-4 with a
/// single API key while we wait on the PracticeX-tenant Azure OpenAI provision.
/// </summary>
public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";

    /// <summary>Master kill-switch. Default false so a missing key never silently no-ops.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>OpenRouter API key (sk-or-v1-...). Set via user-secrets only.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Default model slug (e.g. "anthropic/claude-haiku-4-5", "openai/gpt-5-haiku").</summary>
    public string DefaultModel { get; set; } = "anthropic/claude-haiku-4-5";

    /// <summary>Per-call timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>Optional HTTP-Referer + X-Title headers for OpenRouter telemetry / leaderboard credit.</summary>
    public string AppName { get; set; } = "PracticeX Command Center";
    public string AppUrl { get; set; } = "https://app.practicex.ai";
}
