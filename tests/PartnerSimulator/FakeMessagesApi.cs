using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AiFramework.PartnerSimulator;

/// <summary>What the adapter sent: its key, its beta header, and the JSON body.</summary>
public sealed record FakeMessagesRequest(string? ApiKey, string? Beta, JsonElement Body);

/// <summary>
/// One scripted answer to POST /v1/messages: either an HTTP status with an Anthropic error body,
/// or a stream of server-sent events shaped like the Messages API's.
/// </summary>
public sealed record FakeReply(int StatusCode, IReadOnlyList<string> Events, string StopReason)
{
    public static FakeReply Text(params string[] chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        return new(200, [.. TextBlock(0, chunks)], "end_turn");
    }

    public static FakeReply ToolUse(string id, string name, string inputJson, string thinkingSignature = "sig") =>
        new(200, [.. ThinkingBlock(0, thinkingSignature), .. ToolUseBlock(1, id, name, inputJson)], "tool_use");

    public static FakeReply Refusal() => new(200, [], "refusal");

    public static FakeReply Status(int status) => new(status, [], "end_turn");

    /// <summary>max_tokens hit inside a thinking block: its signature never arrives.</summary>
    public static FakeReply TruncatedInThinking() =>
        new(200, [Start(0, new { type = "thinking", thinking = string.Empty, signature = string.Empty })], "max_tokens");

    /// <summary>max_tokens hit right after a text block started: the block is empty.</summary>
    public static FakeReply TruncatedAfterTextStart() =>
        new(200, [Start(0, new { type = "text", text = string.Empty })], "max_tokens");

    /// <summary>A delta for a block that never started — a protocol violation.</summary>
    public static FakeReply DeltaWithoutStart() =>
        new(200, [Delta(3, new { type = "text_delta", text = "orphan" })], "end_turn");

    /// <summary>An event whose data is not JSON.</summary>
    public static FakeReply MalformedEvent() =>
        new(200, ["event: content_block_delta\ndata: {not json\n\n"], "end_turn");

    public static FakeReply UnknownBlockThenText(string rawBlockJson, string text)
    {
        using var raw = JsonDocument.Parse(rawBlockJson);
        return new(
            200,
            [
                Event("content_block_start", new { type = "content_block_start", index = 0, content_block = raw.RootElement.Clone() }),
                Stop(0),
                .. TextBlock(1, [text]),
            ],
            "end_turn");
    }

    internal static string Event(string name, object payload) =>
        $"event: {name}\ndata: {JsonSerializer.Serialize(payload)}\n\n";

    private static string Start(int index, object block) =>
        Event("content_block_start", new { type = "content_block_start", index, content_block = block });

    private static string Delta(int index, object delta) =>
        Event("content_block_delta", new { type = "content_block_delta", index, delta });

    private static string Stop(int index) =>
        Event("content_block_stop", new { type = "content_block_stop", index });

    private static IEnumerable<string> TextBlock(int index, string[] chunks)
    {
        yield return Start(index, new { type = "text", text = string.Empty });
        foreach (var chunk in chunks)
        {
            yield return Delta(index, new { type = "text_delta", text = chunk });
        }

        yield return Stop(index);
    }

    private static IEnumerable<string> ThinkingBlock(int index, string signature)
    {
        yield return Start(index, new { type = "thinking", thinking = string.Empty, signature = string.Empty });
        yield return Delta(index, new { type = "signature_delta", signature });
        yield return Stop(index);
    }

    private static IEnumerable<string> ToolUseBlock(int index, string id, string name, string inputJson)
    {
        yield return Start(index, new { type = "tool_use", id, name, input = new { } });
        yield return Delta(index, new { type = "input_json_delta", partial_json = inputJson });
        yield return Stop(index);
    }
}

/// <summary>
/// A stand-in for Anthropic's Messages API, for tests and the e2e stack: plain HTTP on loopback,
/// scripted with <see cref="Enqueue"/>, and every request kept in <see cref="Requests"/>. Plain
/// HTTP, not the simulator's mTLS listener: the Anthropic client presents no certificate. With
/// nothing scripted it answers a short text, so a stack using it never hangs.
/// </summary>
public sealed class FakeMessagesApi : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<FakeReply> _script = new();
    private readonly ConcurrentQueue<FakeMessagesRequest> _requests = new();

    private FakeMessagesApi(WebApplication app) => _app = app;

    public Uri BaseAddress { get; private set; } = new("http://127.0.0.1/");

    public IReadOnlyList<FakeMessagesRequest> Requests => [.. _requests];

    public void Enqueue(FakeReply reply) => _script.Enqueue(reply);

    public static async Task<FakeMessagesApi> StartAsync(int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        var fake = new FakeMessagesApi(app);

        // Takes HttpRequest, not HttpContext: a lambda taking only HttpContext is a RequestDelegate
        // and its IResult would be discarded (tests/CLAUDE.md).
        app.MapPost("/v1/messages", (HttpRequest request) => fake.AnswerAsync(request));

        await app.StartAsync().ConfigureAwait(false);
        var address = (app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel exposes no server addresses.")).Addresses.Single();
        fake.BaseAddress = new UriBuilder(address).Uri;
        return fake;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private async Task<IResult> AnswerAsync(HttpRequest request)
    {
        using var body = await JsonDocument.ParseAsync(request.Body).ConfigureAwait(false);
        _requests.Enqueue(new FakeMessagesRequest(
            request.Headers["x-api-key"].FirstOrDefault(), request.Headers["anthropic-beta"].FirstOrDefault(), body.RootElement.Clone()));

        var reply = _script.TryDequeue(out var next) ? next : FakeReply.Text("The fake Messages API has nothing scripted.");
        if (reply.StatusCode != 200)
        {
            return Results.Text(
                JsonSerializer.Serialize(new { type = "error", error = new { type = "api_error", message = $"scripted {reply.StatusCode}" } }),
                "application/json",
                Encoding.UTF8,
                reply.StatusCode);
        }

        var response = request.HttpContext.Response;
        response.ContentType = "text/event-stream";
        await response.WriteAsync(MessageStart()).ConfigureAwait(false);
        foreach (var e in reply.Events)
        {
            await response.WriteAsync(e).ConfigureAwait(false);
            await response.Body.FlushAsync().ConfigureAwait(false);
        }

        await response.WriteAsync(FakeReply.Event("message_delta", new
        {
            type = "message_delta",
            delta = new { stop_reason = reply.StopReason, stop_sequence = (string?)null },
            usage = new { output_tokens = 7 },
        })).ConfigureAwait(false);
        await response.WriteAsync(FakeReply.Event("message_stop", new { type = "message_stop" })).ConfigureAwait(false);
        return Results.Empty;
    }

    private static string MessageStart() => FakeReply.Event("message_start", new
    {
        type = "message_start",
        message = new
        {
            id = "msg_fake",
            type = "message",
            role = "assistant",
            model = "claude-opus-5-5",
            content = Array.Empty<object>(),
            stop_reason = (string?)null,
            stop_sequence = (string?)null,
            usage = new { input_tokens = 12, output_tokens = 1, cache_read_input_tokens = 3, cache_creation_input_tokens = 4 },
        },
    });
}
