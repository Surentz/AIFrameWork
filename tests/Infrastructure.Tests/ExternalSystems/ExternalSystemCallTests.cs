using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.Resilience;
using AiFramework.Infrastructure.Tests.Messaging;
using AiFramework.PartnerSimulator;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Refit;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public interface IThingsApi
{
    [Get("/things/{id}")]
    public Task<IApiResponse<ThingBody>> GetThingAsync(string id, CancellationToken cancellationToken);

    [Delete("/things/{id}")]
    public Task<IApiResponse> DeleteThingAsync(string id, CancellationToken cancellationToken);
}

public sealed record ThingBody(string Id, string Name);

/// <summary>
/// <see cref="ExternalSystemCall"/> through the real chain — Refit, resilience, the client
/// certificate — against the simulator serving a partner's own routes. What every adapter's
/// failure mapping rests on: Refit 16 reports a send that never got a response INSIDE the
/// IApiResponse (ApiRequestException) instead of throwing it, except the caller's own
/// cancellation, which it rethrows.
/// </summary>
/// <remarks>
/// <see cref="IThingsApi"/> is a test-only interface, so these tests register it with
/// <c>AddClient</c> themselves; a real partner's adapter tests do not, because
/// <c>AddExternalSystems</c> already registers every partner.
/// </remarks>
public sealed class ExternalSystemCallTests : IAsyncLifetime, IDisposable
{
    private const string System = "Things";

    private readonly TestPki _pki = TestPki.Create();
    private readonly string _directory = Directory.CreateTempSubdirectory("aif-call-").FullName;
    private readonly ConcurrentQueue<IResult> _answers = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private PartnerSimulatorApp _simulator = null!; // set in InitializeAsync.

    public ExternalSystemCallTests()
    {
        _loggers = LoggerFactory.Create(logging => logging.AddProvider(_logs));
        _logger = _loggers.CreateLogger<ExternalSystemCallTests>();
    }

    public async Task InitializeAsync() =>
        _simulator = await PartnerSimulatorApp.StartAsync(
            new PartnerSimulatorOptions
            {
                ServerCertificate = TestPki.Usable(_pki.IssueServer()),
                TrustedClientRoot = _pki.Root,
                Endpoints = routes =>
                {
                    routes.MapGet("/things/{id}", async (string id, HttpContext context) =>
                    {
                        if (string.Equals(id, "slow", StringComparison.Ordinal))
                        {
                            await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
                        }

                        return _answers.TryDequeue(out var answer) ? answer : Results.Json(new ThingBody(id, $"thing {id}"));
                    });
                    routes.MapDelete("/things/{id}", (string id) =>
                        _answers.TryDequeue(out var answer) ? answer : Results.NoContent());
                },
            },
            CancellationToken.None);

    public async Task DisposeAsync() => await _simulator.DisposeAsync();

    public void Dispose()
    {
        _loggers.Dispose();
        _pki.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private ServiceProvider Build(string? baseAddress = null)
    {
        var pfx = Path.Combine(_directory, "client.pfx");
        File.WriteAllBytes(pfx, _pki.ExportPfx(_pki.IssueClient("things-client"), password: null));
        var ca = Path.Combine(_directory, "ca.pem");
        File.WriteAllText(ca, _pki.RootPem);

        // A cold mTLS handshake under a parallel test run can take most of a second; a 2 s
        // attempt keeps a scripted answer from being consumed by an attempt that then times out.
        var config = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"Systems:{System}:BaseAddress"] = baseAddress ?? _simulator.BaseAddress.ToString(),
            [$"Systems:{System}:ClientCertificate:Path"] = pfx,
            [$"Systems:{System}:ServerTrust:CaBundlePath"] = ca,
            [$"Systems:{System}:ServerTrust:CheckRevocation"] = "false",
            [$"Systems:{System}:Resilience:BaseDelay"] = "00:00:00.020",
            [$"Systems:{System}:Resilience:MaxRetryAttempts"] = "1",
            [$"Systems:{System}:Resilience:AttemptTimeout"] = "00:00:02",
            [$"Systems:{System}:Resilience:TotalRequestTimeout"] = "00:00:05",
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ITrafficRecorder>());
        services.AddResilience();
        services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(config)).AddClient<IThingsApi>(System);
        return services.BuildServiceProvider();
    }

    private static Error? NotFoundIs404(HttpStatusCode status) =>
        status == HttpStatusCode.NotFound ? new Error(ErrorKind.NotFound, "things.not_found", "No such thing.") : null;

    private Task<Result<string>> GetNameAsync(IThingsApi api, string id, CancellationToken cancellationToken) =>
        ExternalSystemCall.SendAsync(
            System, _logger, token => api.GetThingAsync(id, token), body => Result.Success(body.Name), NotFoundIs404, cancellationToken);

    private async Task<Result<string>> GetNameAsync(string id)
    {
        await using var provider = Build();
        return await GetNameAsync(provider.GetRequiredService<IThingsApi>(), id, CancellationToken.None);
    }

    [Fact]
    public async Task SendAsync_WhenThePartnerAnswers_MapsTheBody()
    {
        var result = await GetNameAsync("42");

        result.Value.Should().Be("thing 42");
    }

    [Fact]
    public async Task SendAsync_ForAStatusTheAdapterExpects_ReturnsTheAdaptersError()
    {
        _answers.Enqueue(Results.NotFound());

        var result = await GetNameAsync("missing");

        result.Error.Code.Should().Be("things.not_found");
    }

    [Fact]
    public async Task SendAsync_WhenTheFailureOutlastsTheRetries_IsUnavailable()
    {
        _answers.Enqueue(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        _answers.Enqueue(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

        var result = await GetNameAsync("42");

        result.Error.Should().Be(new Error(ErrorKind.Unavailable, ExternalSystemCall.UnavailableCode, "Things is unavailable."));
    }

    [Fact]
    public async Task SendAsync_WhenItIsUnavailableOnAStatus_LogsTheStatus()
    {
        _answers.Enqueue(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        _answers.Enqueue(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

        await GetNameAsync("42");

        _logs.Records.Should().ContainSingle(record => record.Message == "Things is unavailable: it answered 503");
    }

    [Fact]
    public async Task SendAsync_WhenThePartnerSaysRetryAfter_CarriesIt()
    {
        // One second, because the standard retry honours the header before its own last attempt.
        _answers.Enqueue(new TooManyRequests(seconds: 1));
        _answers.Enqueue(new TooManyRequests(seconds: 1));

        var result = await GetNameAsync("42");

        result.Error.RetryAfter.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    public async Task SendAsync_ForAStatusAnAdapterMayNotName_IsUnavailableWhateverTheAdapterSays(int status)
    {
        // 500 is retried once, so it is scripted twice; 401 and 403 are not retried.
        _answers.Enqueue(Results.StatusCode(status));
        _answers.Enqueue(Results.StatusCode(status));
        await using var provider = Build();
        var api = provider.GetRequiredService<IThingsApi>();

        var result = await ExternalSystemCall.SendAsync(
            System,
            _logger,
            token => api.GetThingAsync("42", token),
            body => Result.Success(body.Name),
            _ => new Error(ErrorKind.Validation, "things.rejected", "Rejected."),
            CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
    }

    [Fact]
    public async Task SendAsync_WhenNothingListens_IsUnavailable()
    {
        await using var provider = Build(baseAddress: "https://127.0.0.1:1/");

        var result = await GetNameAsync(provider.GetRequiredService<IThingsApi>(), "42", CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
    }

    [Fact]
    public async Task SendAsync_WhenNothingListens_LogsTheCausesTypeOnly()
    {
        await using var provider = Build(baseAddress: "https://127.0.0.1:1/");

        await GetNameAsync(provider.GetRequiredService<IThingsApi>(), "42", CancellationToken.None);

        // The cause is a refused connection on Linux and an attempt timeout on Windows, which
        // retries a SYN to a closed port for about two seconds: either way, a type and no address.
        var line = _logs.Records.Should().ContainSingle().Which.Message;
        line.Should().MatchRegex(@"^Things is unavailable: no response \([\w.]+Exception\)$");
        line.Should().NotContain("127.0.0.1");
    }

    [Fact]
    public async Task SendAsync_WhenThePartnerIsTooSlow_IsUnavailable()
    {
        var result = await GetNameAsync("slow");

        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
    }

    [Fact]
    public async Task SendAsync_WhenTheBodyIsNotJson_IsUnavailable()
    {
        _answers.Enqueue(Results.Text("<html>maintenance</html>", "application/json"));

        var result = await GetNameAsync("42");

        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
    }

    [Fact]
    public async Task SendAsync_WhenTheBodyLacksAFieldTheContractGuarantees_IsUnavailable()
    {
        _answers.Enqueue(Results.Text("{}", "application/json"));

        var result = await GetNameAsync("42");

        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
    }

    [Fact]
    public async Task SendAsync_WhenTheBodyIsNullWhereTheContractSaysNot_IsUnavailable()
    {
        _answers.Enqueue(Results.Text("""{"id":"42","name":null}""", "application/json"));

        var result = await GetNameAsync("42");

        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
    }

    [Fact]
    public async Task SendAsync_WhenTheBodyIsNotTheContract_LogsAWarning()
    {
        _answers.Enqueue(Results.Text("{}", "application/json"));

        await GetNameAsync("42");

        _logs.Records.Should().ContainSingle(record => record.Level == LogLevel.Warning)
            .Which.Message.Should().StartWith("Things answered 200 with a body that is not its contract");
    }

    [Fact]
    public async Task SendAsync_WhenASuccessHasNoBody_IsUnavailable()
    {
        _answers.Enqueue(Results.Ok());

        var result = await GetNameAsync("42");

        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
    }

    [Fact]
    public async Task SendAsync_WhenTheCallerCancels_Throws()
    {
        await using var provider = Build();
        var api = provider.GetRequiredService<IThingsApi>();
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        var act = () => GetNameAsync(api, "slow", cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SendAsync_ForASystemThatIsNotConfigured_IsUnavailable()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ITrafficRecorder>());
        services.AddResilience();
        services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(new Dictionary<string, string?>(StringComparer.Ordinal)))
            .AddClient<IThingsApi>(System);
        await using var provider = services.BuildServiceProvider();

        var result = await GetNameAsync(provider.GetRequiredService<IThingsApi>(), "42", CancellationToken.None);

        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
    }

    [Fact]
    public async Task SendAsync_ForACallWithoutABody_SucceedsOnA2xx()
    {
        await using var provider = Build();
        var api = provider.GetRequiredService<IThingsApi>();

        var result = await ExternalSystemCall.SendAsync(
            System, _logger, token => api.DeleteThingAsync("42", token), NotFoundIs404, CancellationToken.None);

        result.Value.Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_ForACallWithoutABody_ReturnsTheAdaptersErrorForAnExpectedStatus()
    {
        _answers.Enqueue(Results.NotFound());
        await using var provider = Build();
        var api = provider.GetRequiredService<IThingsApi>();

        var result = await ExternalSystemCall.SendAsync(
            System, _logger, token => api.DeleteThingAsync("missing", token), NotFoundIs404, CancellationToken.None);

        result.Error.Code.Should().Be("things.not_found");
    }

    private sealed class TooManyRequests(int seconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        }
    }
}
