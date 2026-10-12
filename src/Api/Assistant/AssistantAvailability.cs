namespace AiFramework.Api.Assistant;

/// <summary>
/// Which halves of the assistant are on: the AI (Assistant__Enabled, which needs a key) and instant
/// answers (Assistant__Quick__Enabled, which needs only the local model), and how often the AI's
/// stream sends a keep-alive (Assistant__HeartbeatSeconds). Its own Api type so the controllers
/// never depend on Infrastructure's options (ArchitectureTests: DI only).
/// </summary>
public sealed record AssistantAvailability(bool AiEnabled, bool QuickEnabled, TimeSpan HeartbeatInterval)
{
    public bool AnyEnabled => AiEnabled || QuickEnabled;
}
