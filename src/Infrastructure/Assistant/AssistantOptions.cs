namespace AiFramework.Infrastructure.Assistant;

/// <summary>
/// The ops assistant's provider settings, bound from "Assistant" by the API's Program.cs
/// (Assistant__Enabled, Assistant__ApiKeyFile, ...). The worker never binds them and never calls
/// the model. ADR 0033.
/// </summary>
public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    /// <summary>Off by default: CI, contract generation and a fresh checkout need no key.</summary>
    public bool Enabled { get; set; }

    /// <summary>A path to a file holding the API key — never the key itself (ADR 0031).</summary>
    public string? ApiKeyFile { get; set; }

    /// <summary>Anthropic's API; tests and e2e point it at FakeMessagesApi.</summary>
    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>Set explicitly: a model change must not silently change how hard it thinks.</summary>
    public string Effort { get; set; } = "medium";

    public int MaxTokens { get; set; } = 16_000;

    /// <summary>The SDK's own retry (429, 5xx, connection), which honours Anthropic's retry-after. 0 disables it.</summary>
    public int MaxRetries { get; set; } = 2;
}
