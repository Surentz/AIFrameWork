namespace AiFramework.Infrastructure.Assistant.Quick;

/// <summary>
/// Where the embedding model is (Assistant__Quick__ModelPath). Bound by the API's Program.cs; the
/// default is the assistant-model folder the Api project copies beside its binaries. ADR 0036.
/// </summary>
public sealed class QuickModelOptions
{
    public const string SectionName = "Assistant:Quick";

    public string ModelPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "assistant-model");
}
