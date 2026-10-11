using AiFramework.Application.Abstractions;
using AiFramework.Application.Assistant;
using AiFramework.Infrastructure.Assistant;
using AiFramework.PartnerSimulator;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AiFramework.Infrastructure.Tests.Assistant;

public sealed class ClaudeAssistantModelTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    private readonly string _keyFile = Path.GetTempFileName();
    private readonly List<string> _text = [];
    private FakeMessagesApi _fake = null!;
    private ServiceProvider _services = null!;

    private IAssistantOutput Output
    {
        get
        {
            var output = Substitute.For<IAssistantOutput>();
            output.TextAsync(Arg.Do<string>(_text.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            return output;
        }
    }

    public async Task InitializeAsync()
    {
        await File.WriteAllTextAsync(_keyFile, "sk-test-key\n");
        _fake = await FakeMessagesApi.StartAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Substitute.For<ITrafficRecorder>());
        services.AddSingleton(Options.Create(new AssistantOptions
        {
            Enabled = true,
            ApiKeyFile = _keyFile,
            BaseUrl = _fake.BaseAddress.ToString().TrimEnd('/'),
            MaxRetries = 0,
        }));
        services.AddAssistant();
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _fake.DisposeAsync();
        File.Delete(_keyFile);
    }

    private static AssistantRequest Request(bool allowTools = true, params AssistantTurn[] transcript) => new(
        "You are a test.",
        [new AssistantToolDefinition("job_health", "Counts.", """{"type":"object","properties":{"window_hours":{"type":"integer"}},"required":["window_hours"],"additionalProperties":false}""")],
        transcript.Length > 0 ? transcript : [new UserQuestionTurn("Why?", Now)],
        allowTools);

    private Task<Result<AssistantModelTurn>> NextAsync(AssistantRequest request) =>
        _services.GetRequiredService<IAssistantModel>().NextTurnAsync(request, Output, CancellationToken.None);

    [Fact]
    public async Task NextTurnAsync_Text_StreamsEachDeltaAndEndsTheTurn()
    {
        _fake.Enqueue(FakeReply.Text("Three jobs ", "failed."));

        var result = await NextAsync(Request());

        _text.Should().Equal("Three jobs ", "failed.");
        result.Value.StopReason.Should().Be(AssistantStopReason.EndTurn);
        result.Value.ToolCalls.Should().BeEmpty();
        result.Value.Usage.InputTokens.Should().Be(12);
        result.Value.Usage.OutputTokens.Should().Be(7);
    }

    [Fact]
    public async Task NextTurnAsync_ToolUse_ReturnsTheParsedCall()
    {
        _fake.Enqueue(FakeReply.ToolUse("toolu_9", "job_health", """{"window_hours":24}"""));

        var result = await NextAsync(Request());

        result.Value.StopReason.Should().Be(AssistantStopReason.ToolUse);
        var call = result.Value.ToolCalls.Should().ContainSingle().Subject;
        call.Id.Should().Be("toolu_9");
        call.Input.GetProperty("window_hours").GetInt32().Should().Be(24);
    }

    [Fact]
    public async Task NextTurnAsync_SendsTheConfiguredRequest()
    {
        _fake.Enqueue(FakeReply.Text("ok"));

        await NextAsync(Request());

        var sent = _fake.Requests.Should().ContainSingle().Subject;
        sent.ApiKey.Should().Be("sk-test-key");
        sent.Beta.Should().Contain(AnthropicRequestMapper.FallbackBeta);
        sent.Body.GetProperty("model").GetString().Should().Be("claude-opus-5-5");
        sent.Body.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("medium");
        sent.Body.GetProperty("thinking").GetProperty("type").GetString().Should().Be("adaptive");
        sent.Body.GetProperty("fallbacks").GetString().Should().Be("default");
        sent.Body.GetProperty("stream").GetBoolean().Should().BeTrue();
        sent.Body.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("auto");
        var tool = sent.Body.GetProperty("tools")[0];
        tool.GetProperty("strict").GetBoolean().Should().BeTrue();
        tool.GetProperty("input_schema").GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        sent.Body.GetProperty("messages")[0].GetProperty("content").ToString().Should().Contain("Why?").And.Contain("2026-10-11T09:00:00");
    }

    [Fact]
    public async Task NextTurnAsync_ToolsNotAllowed_SendsToolChoiceNone()
    {
        _fake.Enqueue(FakeReply.Text("ok"));

        await NextAsync(Request(allowTools: false));

        _fake.Requests[^1].Body.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("none");
    }

    [Fact]
    public async Task NextTurnAsync_ReplaysThinkingSignatureAndToolUseUnchanged()
    {
        _fake.Enqueue(FakeReply.ToolUse("toolu_1", "job_health", """{"window_hours":24}""", thinkingSignature: "EqQB/+x=="));
        var first = await NextAsync(Request());
        _fake.Enqueue(FakeReply.Text("done"));

        await NextAsync(Request(
            true,
            new UserQuestionTurn("Why?", Now),
            first.Value.Turn,
            new ToolResultsTurn([new AssistantToolResult("toolu_1", """{"failed":3}""", false)])));

        var messages = _fake.Requests[^1].Body.GetProperty("messages");
        var assistant = messages[1].GetProperty("content");
        assistant[0].GetProperty("type").GetString().Should().Be("thinking");
        assistant[0].GetProperty("signature").GetString().Should().Be("EqQB/+x==");
        assistant[1].GetProperty("type").GetString().Should().Be("tool_use");
        assistant[1].GetProperty("id").GetString().Should().Be("toolu_1");
        assistant[1].GetProperty("input").GetProperty("window_hours").GetInt32().Should().Be(24);
        var result = messages[2].GetProperty("content")[0];
        result.GetProperty("type").GetString().Should().Be("tool_result");
        result.GetProperty("tool_use_id").GetString().Should().Be("toolu_1");
    }

    [Fact]
    public async Task NextTurnAsync_ReplaysABlockTypeItDoesNotKnowVerbatim()
    {
        const string raw = """{"type":"fallback","from_model":"claude-opus-5-5","to_model":"claude-opus-5"}""";
        _fake.Enqueue(FakeReply.UnknownBlockThenText(raw, "answer"));
        var first = await NextAsync(Request());
        _fake.Enqueue(FakeReply.Text("next"));

        await NextAsync(Request(true, new UserQuestionTurn("Why?", Now), first.Value.Turn, new UserQuestionTurn("And?", Now)));

        var replayed = _fake.Requests[^1].Body.GetProperty("messages")[1].GetProperty("content")[0];
        replayed.GetProperty("type").GetString().Should().Be("fallback");
        replayed.GetProperty("to_model").GetString().Should().Be("claude-opus-5");
    }

    [Theory]
    [InlineData(429, "assistant.unavailable")]
    [InlineData(500, "assistant.unavailable")]
    [InlineData(529, "assistant.unavailable")]
    [InlineData(400, "assistant.rejected")]
    public async Task NextTurnAsync_ErrorStatus_IsUnavailableWithItsCode(int status, string code)
    {
        _fake.Enqueue(FakeReply.Status(status));

        var result = await NextAsync(Request());

        result.Error.Kind.Should().Be(ErrorKind.Unavailable);
        result.Error.Code.Should().Be(code);
    }

    [Fact]
    public async Task NextTurnAsync_Rejected_TellsTheAdminToStartANewConversation()
    {
        _fake.Enqueue(FakeReply.Status(400));

        var result = await NextAsync(Request());

        result.Error.Message.Should().Contain("new conversation");
    }

    [Fact]
    public async Task NextTurnAsync_Refusal_ReturnsARefusalStop()
    {
        _fake.Enqueue(FakeReply.Refusal());

        var result = await NextAsync(Request());

        result.Value.StopReason.Should().Be(AssistantStopReason.Refusal);
    }
}
