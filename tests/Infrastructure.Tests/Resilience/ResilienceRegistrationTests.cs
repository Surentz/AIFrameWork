using AiFramework.Infrastructure.Resilience;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiFramework.Infrastructure.Tests.Resilience;

public sealed class ResilienceRegistrationTests
{
    // No AddLogging here, unlike CachingRegistrationTests: nothing AddResilience registers takes
    // an ILogger. That it resolves from a genuinely bare ServiceCollection is the point of the
    // "does not bind configuration" decision, so the absence is the assertion.
    private static ServiceProvider Build(Action<ResilienceOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddResilience();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        return services.BuildServiceProvider();
    }

    private static ResilienceOptions Resolve(ServiceProvider provider) =>
        provider.GetRequiredService<IOptions<ResilienceOptions>>().Value;

    [Fact]
    public void AddResilience_ByDefault_IsEnabled()
    {
        using var provider = Build();

        Resolve(provider).Enabled.Should().BeTrue(
            "resilience is on unless a host turns it off, the same posture as the cache");
    }

    [Fact]
    public void AddResilience_ByDefault_KeepsTheAttemptTimeoutInsideTheTotal()
    {
        using var provider = Build();

        var options = Resolve(provider);

        options.AttemptTimeout.Should().BeLessThan(options.TotalRequestTimeout,
            "the per-attempt timeout runs inside the total one; an attempt allowed to outlive " +
            "the whole call could never fire");
    }

    [Fact]
    public void AddResilience_ByDefault_MatchesTheDocumentedBudget()
    {
        using var provider = Build();

        var options = Resolve(provider);

        options.TotalRequestTimeout.Should().Be(TimeSpan.FromSeconds(30));
        options.AttemptTimeout.Should().Be(TimeSpan.FromSeconds(10));
        options.MaxRetryAttempts.Should().Be(3);
        options.BaseDelay.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void AddResilience_ResolvesFromABareServiceCollection()
    {
        // The regression this guards: moving the configuration binding into AddResilience would
        // make IOptions<ResilienceOptions> require an IConfiguration, and every unit test that
        // builds a host without one would fail at resolution rather than at registration.
        using var provider = Build();

        var act = () => Resolve(provider);

        act.Should().NotThrow(
            "AddResilience must not depend on an IConfiguration being registered");
    }

    [Fact]
    public void AddResilience_WithAnAttemptTimeoutAboveTheTotal_FailsValidation()
    {
        using var provider = Build(o =>
        {
            o.TotalRequestTimeout = TimeSpan.FromSeconds(5);
            o.AttemptTimeout = TimeSpan.FromSeconds(10);
        });

        var act = () => Resolve(provider);

        // The standard handler would otherwise reject this itself, while building the pipeline -
        // a startup failure whose message names neither property.
        act.Should().Throw<OptionsValidationException>().WithMessage("*AttemptTimeout*");
    }

    [Fact]
    public void AddResilience_WithANonPositiveTotalRequestTimeout_FailsValidation()
    {
        using var provider = Build(o => o.TotalRequestTimeout = TimeSpan.Zero);

        var act = () => Resolve(provider);

        act.Should().Throw<OptionsValidationException>().WithMessage("*TotalRequestTimeout*");
    }

    [Fact]
    public void AddResilience_WithANonPositiveAttemptTimeout_FailsValidation()
    {
        using var provider = Build(o => o.AttemptTimeout = TimeSpan.Zero);

        var act = () => Resolve(provider);

        act.Should().Throw<OptionsValidationException>().WithMessage("*AttemptTimeout*");
    }

    [Fact]
    public void AddResilience_WithNegativeRetryAttempts_FailsValidation()
    {
        using var provider = Build(o => o.MaxRetryAttempts = -1);

        var act = () => Resolve(provider);

        act.Should().Throw<OptionsValidationException>().WithMessage("*MaxRetryAttempts*");
    }

    [Fact]
    public void AddResilience_WithZeroRetryAttempts_FailsValidation()
    {
        // Discovered, not designed: this was originally the "opts out of retry" case, on the
        // assumption zero meant "never retry". It does not - Polly's own
        // RetryStrategyOptions<T>.MaxRetryAttempts validation requires at least 1, thrown as an
        // OptionsValidationException the first time a request is made, not at startup. Caught
        // here first instead. ResilienceOptions.Enabled is the real mechanism for "never retry".
        using var provider = Build(o => o.MaxRetryAttempts = 0);

        var act = () => Resolve(provider);

        act.Should().Throw<OptionsValidationException>().WithMessage("*MaxRetryAttempts*");
    }

    [Fact]
    public void AddResilience_WithOneRetryAttempt_PassesValidation()
    {
        using var provider = Build(o => o.MaxRetryAttempts = 1);

        var act = () => Resolve(provider);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddResilience_WithANonPositiveBaseDelay_FailsValidation()
    {
        using var provider = Build(o => o.BaseDelay = TimeSpan.Zero);

        var act = () => Resolve(provider);

        act.Should().Throw<OptionsValidationException>().WithMessage("*BaseDelay*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("api.frankfurter.app")]
    [InlineData("not a uri at all")]
    // Absolute but not HTTP. "/rates" is the one that matters: on Linux it parses as an
    // ABSOLUTE file:// URI, so a validator checking only UriKind.Absolute accepts it and leaves
    // HttpClient to fail at the first request on an unsupported scheme. It did, and this case
    // is what caught it.
    [InlineData("/rates")]
    [InlineData("file:///rates")]
    [InlineData("ftp://api.frankfurter.app")]
    public void AddResilience_WithABaseAddressThatIsNotAnHttpUri_FailsValidation(string address)
    {
        using var provider = Build(o => o.ExchangeRateBaseAddress = address);

        var act = () => Resolve(provider);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*ExchangeRateBaseAddress*");
    }

    [Theory]
    [InlineData("https://api.frankfurter.app")]
    // Plain http has to stay legal: ApiFactory points this at a local stub, and a stub on
    // loopback has no certificate.
    [InlineData("http://localhost:5999")]
    public void AddResilience_WithAnHttpBaseAddress_PassesValidation(string address)
    {
        using var provider = Build(o => o.ExchangeRateBaseAddress = address);

        var act = () => Resolve(provider);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddResilience_WithAnInvalidBudget_FailsTheStartupValidator()
    {
        // What ValidateOnStart buys, asserted through the mechanism it registers rather than by
        // booting a host: IStartupValidator is what the hosting layer calls before the app
        // serves anything. Without it these options are resolved by nothing until a typed client
        // makes its first request, so a misconfigured budget would surface as a failed request
        // in production instead of a host that refuses to start.
        using var provider = Build(o => o.AttemptTimeout = o.TotalRequestTimeout.Add(TimeSpan.FromSeconds(1)));

        var act = () => provider.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>().WithMessage("*AttemptTimeout*");
    }

    [Fact]
    public void AddResilience_WithTheDefaultBudget_PassesTheStartupValidator()
    {
        // The other half, and the one that would bite hardest: a default that cannot pass its own
        // validator turns ValidateOnStart into a host that never starts anywhere.
        using var provider = Build();

        var act = () => provider.GetRequiredService<IStartupValidator>().Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void AddInfrastructure_WiresResilienceIn()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Never connects: UseNpgsql does not touch the network at registration. Same placeholder
        // string the caching and completeness tests use, for the same reason.
        services.AddInfrastructure(
            "Host=localhost;Port=1;Database=unreachable;Username=none;Password=none");

        using var provider = services.BuildServiceProvider();

        var act = () => Resolve(provider);

        act.Should().NotThrow(
            "Api reaches Infrastructure only through AddInfrastructure, so the retry budget has " +
            "to be wired in there rather than left for a host to remember");
    }

    [Fact]
    public void AddInfrastructure_WiresExchangeRateClientIn()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddInfrastructure(
            "Host=localhost;Port=1;Database=unreachable;Username=none;Password=none");

        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<AiFramework.Application.Rates.IExchangeRateProvider>();

        // The regression this guards: GetExchangeRateHandler is registered in AddMessaging as
        // soon as GetExchangeRate exists in the Application assembly, ahead of this client's own
        // task in the plan - a real WebApplicationFactory host validates its whole service graph
        // at build time (ValidateOnBuild, on by default outside Production) and refuses to start
        // at all if IExchangeRateProvider has no registration, which a bare ServiceCollection
        // here would not catch on its own without this test.
        act.Should().NotThrow();
    }
}
