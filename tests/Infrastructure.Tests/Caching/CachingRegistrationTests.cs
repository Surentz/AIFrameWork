using AiFramework.Infrastructure.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Tests.Caching;

public sealed class CachingRegistrationTests
{
    // AddLogging is not decoration: HybridCache's default implementation takes an ILogger, so a
    // bare ServiceCollection cannot construct it. Every test host in this plan that resolves
    // HybridCache adds logging for that reason.
    private static ServiceProvider Build(Action<CacheOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddCaching_ByDefault_EnablesTheCache()
    {
        using var provider = Build();

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;

        options.Enabled.Should().BeTrue("the cache is on unless a host turns it off");
    }

    [Fact]
    public void AddCaching_ByDefault_CapsDurationAtOneMinute()
    {
        using var provider = Build();

        var options = provider.GetRequiredService<IOptions<CacheOptions>>().Value;

        options.MaximumDuration.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void AddCaching_ResolvesHybridCache()
    {
        using var provider = Build();

        var cache = provider.GetService<HybridCache>();

        cache.Should().NotBeNull(
            "CachedAsync resolves HybridCache with GetRequiredService; an absent registration " +
            "would surface as a failed request rather than a failed startup");
    }

    [Fact]
    public void AddCaching_WithANonPositiveMaximumDuration_FailsValidation()
    {
        using var provider = Build(o => o.MaximumDuration = TimeSpan.Zero);

        var act = () => provider.GetRequiredService<IOptions<CacheOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*MaximumDuration*");
    }

    [Fact]
    public void AddCaching_WithAMaximumDurationAboveAnHour_FailsValidation()
    {
        using var provider = Build(o => o.MaximumDuration = TimeSpan.FromHours(2));

        var act = () => provider.GetRequiredService<IOptions<CacheOptions>>().Value;

        act.Should().Throw<OptionsValidationException>().WithMessage("*MaximumDuration*");
    }

    [Fact]
    public void AddInfrastructure_WiresCachingIn()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Never connects: UseNpgsql does not touch the network at registration. Same placeholder
        // string RegistrationCompletenessTests uses, for the same reason.
        services.AddInfrastructure(
            "Host=localhost;Port=1;Database=unreachable;Username=none;Password=none");

        using var provider = services.BuildServiceProvider();

        provider.GetService<HybridCache>().Should().NotBeNull(
            "Api reaches Infrastructure only through AddInfrastructure, so caching has to be " +
            "wired in there rather than left for a host to remember");
    }
}
