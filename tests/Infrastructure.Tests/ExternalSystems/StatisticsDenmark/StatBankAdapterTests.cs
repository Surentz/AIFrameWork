using System.Collections.Concurrent;
using AiFramework.Application.Abstractions;
using AiFramework.Application.Statistics;
using AiFramework.Infrastructure.ExternalSystems;
using AiFramework.Infrastructure.Resilience;
using AiFramework.PartnerSimulator;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AiFramework.Infrastructure.Tests.ExternalSystems.StatisticsDenmark;

/// <summary>
/// The pilot adapter through the real chain, against the simulator standing in for StatBank. The
/// canned bodies are trimmed copies of what api.statbank.dk answered on 2026-10-10, served as
/// <c>text/json</c> as StatBank serves them. The simulator demands a client certificate, so these
/// tests configure one; the real StatBank takes none.
/// </summary>
public sealed class StatBankAdapterTests : IAsyncLifetime, IDisposable
{
    private const string Copenhagen = """
        {"dataset":{"dimension":{
          "OMRÅDE":{"label":"region","category":{"index":{"101":0},"label":{"101":"Copenhagen"}}},
          "ContentsCode":{"label":"Indhold","category":{"index":{"FOLK1A":0},"label":{"FOLK1A":"Population at the first day of the quarter"}}},
          "Tid":{"label":"time","category":{"index":{"2026K3":0},"label":{"2026K3":"2026Q3"}}},
          "id":["OMRÅDE","ContentsCode","Tid"],"size":[1,1,1],"role":{"geo":["OMRÅDE"],"metric":["ContentsCode"],"time":["Tid"]}},
          "label":"Population at the first day of the quarter by region, Indhold and time","source":"Statistics Denmark",
          "updated":"2026-08-10T06:00:00Z","value":[670389]}}
        """;

    private const string TableInfo = """
        {"id":"FOLK1A","text":"Population at the first day of the quarter","unit":"Number","active":true,
         "variables":[
           {"id":"OMRÅDE","text":"region","elimination":true,"time":false,
            "values":[{"id":"000","text":"All Denmark"},{"id":"084","text":"Region Hovedstaden"},{"id":"101","text":"Copenhagen"}]},
           {"id":"KØN","text":"sex","elimination":true,"time":false,"values":[{"id":"TOT","text":"Total"}]},
           {"id":"Tid","text":"time","elimination":false,"time":true,"values":[{"id":"2026K3","text":"2026Q3"}]}]}
        """;

    private readonly TestPki _pki = TestPki.Create();
    private readonly string _directory = Directory.CreateTempSubdirectory("aif-statbank-").FullName;
    private readonly ConcurrentQueue<IResult> _answers = new();
    private readonly ConcurrentQueue<IQueryCollection> _queries = new();
    private PartnerSimulatorApp _simulator = null!; // set in InitializeAsync.

    public async Task InitializeAsync() =>
        _simulator = await PartnerSimulatorApp.StartAsync(
            new PartnerSimulatorOptions
            {
                ServerCertificate = TestPki.Usable(_pki.IssueServer()),
                TrustedClientRoot = _pki.Root,
                Endpoints = routes =>
                {
                    routes.MapGet("/v1/data/FOLK1A/JSONSTAT", (HttpRequest request) => Answer(request, Copenhagen));
                    routes.MapGet("/v1/tableinfo/FOLK1A", (HttpRequest request) => Answer(request, TableInfo));
                },
            },
            CancellationToken.None);

    public async Task DisposeAsync() => await _simulator.DisposeAsync();

    public void Dispose()
    {
        _pki.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private IResult Answer(HttpRequest request, string standard)
    {
        _queries.Enqueue(request.Query);
        return _answers.TryDequeue(out var answer) ? answer : StatBank(standard);
    }

    private static IResult StatBank(string body, int status = StatusCodes.Status200OK) =>
        Results.Text(body, "text/json; charset=utf-8", statusCode: status);

    private void AnswerEveryAttemptWith(IResult answer)
    {
        // MaxRetryAttempts defaults to 2: three attempts reach the partner.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            _answers.Enqueue(answer);
        }
    }

    private async Task<T> WithStatisticsAsync<T>(Func<IPopulationStatistics, Task<T>> act)
    {
        var pfx = Path.Combine(_directory, "client.pfx");
        await File.WriteAllBytesAsync(pfx, _pki.ExportPfx(_pki.IssueClient("statbank-client"), password: null));
        var ca = Path.Combine(_directory, "ca.pem");
        await File.WriteAllTextAsync(ca, _pki.RootPem);
        var config = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Systems:StatisticsDenmark:BaseAddress"] = new Uri(_simulator.BaseAddress, "v1/").ToString(),
            ["Systems:StatisticsDenmark:ClientCertificate:Path"] = pfx,
            ["Systems:StatisticsDenmark:ServerTrust:CaBundlePath"] = ca,
            ["Systems:StatisticsDenmark:ServerTrust:CheckRevocation"] = "false",
            ["Systems:StatisticsDenmark:Resilience:BaseDelay"] = "00:00:00.020",
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ITrafficRecorder>());
        services.AddResilience();
        services.AddExternalSystems(ExternalSystemsTestConfiguration.Section(config));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        return await act(scope.ServiceProvider.GetRequiredService<IPopulationStatistics>());
    }

    private Task<Result<AreaPopulation>> GetLatestAsync(string area = "101") =>
        WithStatisticsAsync(statistics => statistics.GetLatestAsync(area, CancellationToken.None));

    private Task<Result<IReadOnlyList<StatisticsArea>>> GetAreasAsync() =>
        WithStatisticsAsync(statistics => statistics.GetAreasAsync(CancellationToken.None));

    [Fact]
    public async Task GetLatestAsync_ForAPublishedArea_MapsTheJsonStatCell()
    {
        var result = await GetLatestAsync();

        result.Value.Should().Be(new AreaPopulation("101", "Copenhagen", "2026Q3", 670_389));
    }

    [Fact]
    public async Task GetLatestAsync_AsksFolk1aForTheAreaAndTheLatestPeriodInEnglish()
    {
        await GetLatestAsync();

        var query = _queries.Single();
        query["OMRÅDE"].ToString().Should().Be("101");
        query["Tid"].ToString().Should().Be("(1)");
        query["lang"].ToString().Should().Be("en");
    }

    [Fact]
    public async Task GetLatestAsync_WhenStatBankDoesNotKnowTheArea_IsNotFound()
    {
        _answers.Enqueue(StatBank("""{"errorTypeCode":"EXTRACT-NOTFOUND","message":"Can't find value: 999 (OMRÅDE)"}""", StatusCodes.Status400BadRequest));

        var result = await GetLatestAsync("999");

        result.Error.Code.Should().Be("statistics.area_not_found");
        result.Error.Message.Should().NotContain("Can't find");
    }

    [Fact]
    public async Task GetLatestAsync_WhenStatBankRejectsTheRequestItself_IsUnavailable()
    {
        _answers.Enqueue(StatBank("""{"errorTypeCode":"REQUEST-MISSING","message":"Format not found or not valid."}""", StatusCodes.Status400BadRequest));

        var result = await GetLatestAsync();

        result.Error.Code.Should().Be(ExternalSystemCall.UnavailableCode);
    }

    [Fact]
    public async Task GetLatestAsync_WhenA400IsNotStatBanksErrorShape_IsUnavailable()
    {
        _answers.Enqueue(Results.Text("<html>Bad Request</html>", "text/html", statusCode: StatusCodes.Status400BadRequest));

        var result = await GetLatestAsync();

        result.Error.Code.Should().Be(ExternalSystemCall.UnavailableCode);
    }

    [Fact]
    public async Task GetLatestAsync_WhenStatBankFails_IsUnavailable()
    {
        AnswerEveryAttemptWith(Results.StatusCode(StatusCodes.Status500InternalServerError));

        var result = await GetLatestAsync();

        result.Error.Code.Should().Be(ExternalSystemCall.UnavailableCode);
    }

    [Fact]
    public async Task GetLatestAsync_WhenTheFigureIsSuppressed_IsNotFound()
    {
        _answers.Enqueue(StatBank(Copenhagen.Replace("\"value\":[670389]", "\"value\":[null]", StringComparison.Ordinal)));

        var result = await GetLatestAsync();

        result.Error.Code.Should().Be("statistics.population_not_published");
    }

    [Fact]
    public async Task GetLatestAsync_WhenTheAnswerIsNotOneCell_IsUnavailable()
    {
        _answers.Enqueue(StatBank(Copenhagen.Replace("\"value\":[670389]", "\"value\":[670389,1]", StringComparison.Ordinal)));

        var result = await GetLatestAsync();

        result.Error.Code.Should().Be(ExternalSystemCall.UnavailableCode);
    }

    [Fact]
    public async Task GetLatestAsync_WhenTheCellIsNotLabelledWithTheArea_IsUnavailable()
    {
        _answers.Enqueue(StatBank(Copenhagen.Replace("\"label\":{\"101\":\"Copenhagen\"}", "\"label\":{\"147\":\"Frederiksberg\"}", StringComparison.Ordinal)));

        var result = await GetLatestAsync();

        result.Error.Code.Should().Be(ExternalSystemCall.UnavailableCode);
    }

    [Fact]
    public async Task GetAreasAsync_ListsTheAreaVariablesValuesInOrder()
    {
        var result = await GetAreasAsync();

        result.Value.Should().Equal(
            new StatisticsArea("000", "All Denmark"), new StatisticsArea("084", "Region Hovedstaden"), new StatisticsArea("101", "Copenhagen"));
    }

    [Fact]
    public async Task GetAreasAsync_AsksForFolk1aInEnglishJson()
    {
        await GetAreasAsync();

        var query = _queries.Single();
        query["format"].ToString().Should().Be("JSON");
        query["lang"].ToString().Should().Be("en");
    }

    [Fact]
    public async Task GetAreasAsync_WhenTheTableHasNoAreaVariable_IsUnavailable()
    {
        _answers.Enqueue(StatBank("""{"id":"FOLK1A","variables":[{"id":"Tid","values":[{"id":"2026K3","text":"2026Q3"}]}]}"""));

        var result = await GetAreasAsync();

        result.Error.Code.Should().Be(ExternalSystemCall.UnavailableCode);
    }

    [Fact]
    public async Task GetAreasAsync_WhenStatBankFails_IsUnavailable()
    {
        AnswerEveryAttemptWith(Results.StatusCode(StatusCodes.Status500InternalServerError));

        var result = await GetAreasAsync();

        result.Error.Code.Should().Be(ExternalSystemCall.UnavailableCode);
    }
}
