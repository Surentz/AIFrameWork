using AiFramework.Application.Abstractions;

namespace AiFramework.Infrastructure.Tests;

/// <summary>A clock the test drives, so no test ever waits on wall time.</summary>
public sealed class TestClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}
