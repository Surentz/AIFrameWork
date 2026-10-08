using Microsoft.Extensions.Configuration;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

internal static class ExternalSystemsTestConfiguration
{
    /// <summary>An "ExternalSystems" section built from flat keys relative to it, e.g. "Systems:Sim:BaseAddress".</summary>
    public static IConfigurationSection Section(IDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(
                pair => $"ExternalSystems:{pair.Key}", pair => pair.Value, StringComparer.Ordinal))
            .Build()
            .GetSection("ExternalSystems");
}
