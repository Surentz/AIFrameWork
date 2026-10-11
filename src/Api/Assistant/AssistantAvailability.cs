namespace AiFramework.Api.Assistant;

/// <summary>
/// Whether the assistant is switched on (Assistant__Enabled), and how often its stream sends a
/// keep-alive (Assistant__HeartbeatSeconds). Its own Api type so the controller never depends on
/// Infrastructure's AssistantOptions (ArchitectureTests: DI only).
/// </summary>
public sealed record AssistantAvailability(bool Enabled, TimeSpan HeartbeatInterval);
