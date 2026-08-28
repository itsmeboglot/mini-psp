using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Payments.Core.Observability;
using Payments.Core.Persistence;

namespace Payments.Core.Messaging;

/// <summary>
/// Publishes outbox records to Kafka.
/// </summary>
/// <remarks>
/// The producer is a singleton because it owns connections, batching and an
/// internal queue; creating one per message would throw all of that away.
/// </remarks>
public sealed class EventPublisher : IAsyncDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly KafkaOptions _options;
    private readonly ILogger<EventPublisher> _logger;

    public EventPublisher(IOptions<KafkaOptions> options, ILogger<EventPublisher> logger)
    {
        _options = options.Value;
        _logger = logger;

        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = _options.BootstrapServers,

            // Wait for all in-sync replicas. A payment event that only reached the
            // leader is an event that can vanish in a failover, and the outbox has
            // already been marked published by then.
            Acks = Acks.All,

            // Kafka's own producer-side deduplication, so a retry inside the client
            // does not put the same record on the topic twice.
            EnableIdempotence = true
        }).SetLogHandler((_, message) => _logger.LogDebug("Kafka: {Message}", message.Message))
          .Build();
    }

    /// <summary>
    /// Sends one record, keyed by its aggregate so that every event about a
    /// payment lands in the same partition and stays in order relative to the
    /// others.
    /// </summary>
    public async Task PublishAsync(OutboxRecord record, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.PublishTimeoutSeconds));

        var message = new Message<string, string>
        {
            Key = record.AggregateId.ToString(),
            Value = record.Payload,
            Headers =
            [
                new Header("event-type", System.Text.Encoding.UTF8.GetBytes(record.EventType)),
                new Header("outbox-id", System.Text.Encoding.UTF8.GetBytes(record.Id.ToString())),
                new Header(CorrelationId.MessageHeader,
                    System.Text.Encoding.UTF8.GetBytes(record.CorrelationId ?? ""))
            ]
        };

        await _producer.ProduceAsync(_options.Topic, message, timeout.Token);
    }

    public ValueTask DisposeAsync()
    {
        // Give queued messages a chance to leave before the process does.
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
        return ValueTask.CompletedTask;
    }
}
