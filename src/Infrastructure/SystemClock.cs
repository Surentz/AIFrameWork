using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
