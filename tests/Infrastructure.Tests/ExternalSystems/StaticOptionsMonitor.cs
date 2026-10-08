using AiFramework.Infrastructure.ExternalSystems;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

internal sealed class StaticOptionsMonitor(ExternalSystemsOptions value) : IOptionsMonitor<ExternalSystemsOptions>
{
    public ExternalSystemsOptions CurrentValue => value;

    public ExternalSystemsOptions Get(string? name) => value;

    public IDisposable? OnChange(Action<ExternalSystemsOptions, string?> listener) => null;
}
