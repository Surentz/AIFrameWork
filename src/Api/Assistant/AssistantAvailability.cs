namespace AiFramework.Api.Assistant;

/// <summary>
/// Whether the assistant is switched on (Assistant__Enabled). Its own Api type so the controller
/// never depends on Infrastructure's AssistantOptions (ArchitectureTests: DI only).
/// </summary>
public sealed record AssistantAvailability(bool Enabled);
