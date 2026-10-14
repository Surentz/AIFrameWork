namespace AiFramework.Api.Assistant;

/// <summary>The rate-limiter policies guarding the assistant, per user. Named once.</summary>
public static class AssistantRateLimiting
{
    /// <summary>The AI: a spend cap as much as a volume one (ADR 0033).</summary>
    public const string PolicyName = "assistant";

    /// <summary>Instant answers: no model is called, so this only bounds abuse (ADR 0036).</summary>
    public const string QuickPolicyName = "assistant-quick";
}
