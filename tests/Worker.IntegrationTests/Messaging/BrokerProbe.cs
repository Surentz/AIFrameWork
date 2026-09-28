using System.Text;
using RabbitMQ.Client;

namespace AiFramework.Worker.IntegrationTests.Messaging;

/// <summary>
/// Reads and writes the test broker directly, the way an external system would: RabbitMQ.Client,
/// no Wolverine. Every queue it creates is server-named, exclusive and auto-delete, so tests in
/// one collection never see each other's messages and nothing outlives the connection.
/// </summary>
/// <remarks>
/// An identical copy of <c>Api.IntegrationTests/Messaging/BrokerProbe.cs</c>: the two test
/// assemblies share no test library, and this is too small to justify one.
/// </remarks>
internal sealed class BrokerProbe : IAsyncDisposable
{
    private readonly IConnection _connection;
    private readonly IChannel _channel;

    private BrokerProbe(IConnection connection, IChannel channel)
    {
        _connection = connection;
        _channel = channel;
    }

    public static async Task<BrokerProbe> ConnectAsync(string connectionString)
    {
        var factory = new ConnectionFactory { Uri = new Uri(connectionString) };
        var connection = await factory.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        return new BrokerProbe(connection, channel);
    }

    /// <summary>A private queue bound to <paramref name="exchange"/> with <paramref name="routingKey"/>.</summary>
    public async Task<string> BindTemporaryQueueAsync(string exchange, string routingKey)
    {
        var declared = await _channel.QueueDeclareAsync(
            queue: string.Empty, durable: false, exclusive: true, autoDelete: true);
        await _channel.QueueBindAsync(declared.QueueName, exchange, routingKey);
        return declared.QueueName;
    }

    /// <summary>
    /// The next message on <paramref name="queue"/>, waiting up to <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// A bounded real-time wait, deliberately — the one place these tests poll. Delivery is done by
    /// another process (the broker) after Wolverine's own sending agent runs, neither of which an
    /// IClock reaches. It returns as soon as a message is there, so only a failing test pays the
    /// timeout; the same trade ExchangeRateClientTests documents.
    /// </remarks>
    public async Task<BasicGetResult?> WaitForMessageAsync(string queue, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var result = await _channel.BasicGetAsync(queue, autoAck: true);
            if (result is not null || DateTime.UtcNow > deadline)
            {
                return result;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>Publishes a raw JSON body straight to a queue, as a producer with no Wolverine would.</summary>
    public async Task PublishToQueueAsync(string queue, string json, string? messageId = null)
    {
        var properties = new BasicProperties { ContentType = "application/json", Persistent = true };
        if (messageId is not null)
        {
            properties.MessageId = messageId;
        }

        await _channel.BasicPublishAsync(
            exchange: string.Empty, routingKey: queue, mandatory: true,
            basicProperties: properties, body: Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Throws if the object does not exist - which is what a topology test wants.</summary>
    public Task ExchangeExistsAsync(string exchange) => _channel.ExchangeDeclarePassiveAsync(exchange);

    public async Task<uint> MessageCountAsync(string queue) =>
        (await _channel.QueueDeclarePassiveAsync(queue)).MessageCount;

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
