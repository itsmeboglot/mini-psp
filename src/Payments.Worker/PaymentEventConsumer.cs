using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Payments.Core.Contracts;
using Payments.Core.Messaging;
using Payments.Core.Observability;
using Payments.Core.Persistence;

namespace Payments.Worker;

public sealed class ConsumerOptions
{
    public const string Section = "Consumer";

    /// <summary>
    /// Names the consumer group and the rows this worker writes to
    /// processed_events. Changing it makes the worker replay the topic from the
    /// beginning and treat every event as unseen.
    /// </summary>
    public string Name { get; set; } = "payments-worker";

    /// <summary>Times a single event is retried in process before it is set aside.</summary>
    public int MaxAttempts { get; set; } = 3;

    public string DeadLetterTopic { get; set; } = "payments.events.dlq";
}

/// <summary>
/// Consumes payment events and applies them exactly once.
/// </summary>
public sealed class PaymentEventConsumer(
    IServiceScopeFactory scopes,
    IOptions<KafkaOptions> kafka,
    IOptions<ConsumerOptions> consumer,
    TimeProvider clock,
    ILogger<PaymentEventConsumer> logger) : BackgroundService
{
    private readonly KafkaOptions _kafka = kafka.Value;
    private readonly ConsumerOptions _consumer = consumer.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Consume blocks, so the whole loop moves off the startup thread rather
        // than holding up the rest of the host.
        await Task.Yield();

        using var client = BuildConsumer();
        using var deadLetters = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = _kafka.BootstrapServers, Acks = Acks.All }).Build();

        client.Subscribe(_kafka.Topic);
        logger.LogInformation("Consuming {Topic} as {Group}", _kafka.Topic, _consumer.Name);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = client.Consume(stoppingToken);
                if (result?.Message is null)
                {
                    continue;
                }

                await HandleWithRetriesAsync(result, deadLetters, stoppingToken);

                // Committed only after the work has committed. An offset moved
                // first would turn a crash into a lost event; moved after, a crash
                // means a redelivery, which processed_events absorbs.
                client.Commit(result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ConsumeException e)
            {
                logger.LogError(e, "Consume failed: {Reason}", e.Error.Reason);
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
            }
        }

        client.Close();
        logger.LogInformation("Consumer stopped");
    }

    private async Task HandleWithRetriesAsync(
        ConsumeResult<string, string> result,
        IProducer<string, string> deadLetters,
        CancellationToken ct)
    {
        var eventType = Header(result.Message, "event-type");
        var eventId = long.TryParse(Header(result.Message, "outbox-id"), out var id) ? id : 0;
        var correlationId = Header(result.Message, CorrelationId.MessageHeader);

        // Reopened here, so work done minutes after the request that caused it
        // still logs under the same id.
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            [CorrelationId.LogProperty] = string.IsNullOrEmpty(correlationId) ? "(none)" : correlationId
        });

        if (eventId == 0)
        {
            await DeadLetterAsync(deadLetters, result, "message carries no outbox-id header", ct);
            return;
        }

        using var failureScope = scopes.CreateScope();
        var failures = failureScope.ServiceProvider.GetRequiredService<ConsumerFailureStore>();

        while (true)
        {
            try
            {
                await DispatchAsync(eventType, eventId, result.Message.Value, correlationId, ct);

                // Nothing is failing about this event any more.
                await failures.ForgetAsync(_consumer.Name, eventId, ct);
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Counted in the database rather than in a loop variable, so a
                // restart does not hand this message a fresh allowance.
                var attempts = await failures.RecordFailureAsync(_consumer.Name, eventId, e.Message, ct);

                if (attempts >= _consumer.MaxAttempts)
                {
                    // Set it aside and move on: a partition blocked on one message
                    // stops every payment behind it.
                    await DeadLetterAsync(deadLetters, result, e.Message, ct);
                    await failures.ForgetAsync(_consumer.Name, eventId, ct);
                    return;
                }

                logger.LogWarning(e,
                    "Handling event {EventId} failed, attempt {Attempts} of {MaxAttempts}",
                    eventId, attempts, _consumer.MaxAttempts);

                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempts), clock, ct);
            }
        }
    }

    private async Task DispatchAsync(
        string? eventType, long eventId, string payload, string? correlationId, CancellationToken ct)
    {
        // One scope per message: fresh handlers and a fresh connection, released
        // as soon as the message is done with.
        using var messageScope = scopes.CreateScope();
        var scope = messageScope;

        switch (eventType)
        {
            case PaymentCreatedEvent.EventType:
            {
                var processor = scope.ServiceProvider.GetRequiredService<IdempotentEventProcessor>();
                var handler = scope.ServiceProvider.GetRequiredService<PaymentCreatedHandler>();

                var outcome = await processor.ProcessAsync(
                    _consumer.Name,
                    eventId,
                    (connection, transaction, token) =>
                        handler.HandleAsync(payload, correlationId, connection, transaction, token),
                    ct);

                logger.LogDebug("Event {EventId}: {Outcome}", eventId, outcome);
                return;
            }

            case PaymentPendingEvent.EventType:
            {
                // Not wrapped in the idempotent processor, because the provider
                // call must happen outside a transaction and there is nothing to
                // commit alongside a record of having started. Redelivery is made
                // safe by two other things instead: the handler only acts on a
                // payment still in pending, and the charge carries an idempotency
                // key the provider recognises.
                await scope.ServiceProvider.GetRequiredService<PaymentPendingHandler>()
                    .HandleAsync(payload, correlationId, ct);
                return;
            }

            case PaymentResolvedEvent.EventType:
            {
                // Back through the idempotent processor: posting to the ledger is
                // a database write with nothing outside the transaction, so the
                // record of having handled it commits with the entries.
                var processor = scope.ServiceProvider.GetRequiredService<IdempotentEventProcessor>();
                var handler = scope.ServiceProvider.GetRequiredService<PaymentResolvedHandler>();

                var outcome = await processor.ProcessAsync(
                    _consumer.Name,
                    eventId,
                    (connection, transaction, token) => handler.HandleAsync(payload, connection, transaction, token),
                    ct);

                logger.LogDebug("Event {EventId}: {Outcome}", eventId, outcome);
                return;
            }

            default:
                // Every event type shares one topic so that ordering per payment
                // survives. Ignoring the rest is normal, not a failure.
                logger.LogDebug("Ignoring {EventType}", eventType);
                return;
        }
    }

    private async Task DeadLetterAsync(
        IProducer<string, string> deadLetters,
        ConsumeResult<string, string> result,
        string reason,
        CancellationToken ct)
    {
        logger.LogError("Dead lettering message at offset {Offset}: {Reason}", result.Offset.Value, reason);

        var message = new Message<string, string>
        {
            Key = result.Message.Key,
            Value = result.Message.Value,
            Headers = result.Message.Headers ?? []
        };

        message.Headers.Add("dead-letter-reason", Encoding.UTF8.GetBytes(reason));

        await deadLetters.ProduceAsync(_consumer.DeadLetterTopic, message, ct);
    }

    private IConsumer<string, string> BuildConsumer() =>
        new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _kafka.BootstrapServers,
            GroupId = _consumer.Name,

            // Offsets are committed by hand after the work commits. Automatic
            // commits move the offset on a timer, which acknowledges events the
            // handler has not finished with.
            EnableAutoCommit = false,

            // A new group reads the topic from the beginning rather than skipping
            // whatever arrived before it existed.
            AutoOffsetReset = AutoOffsetReset.Earliest
        }).SetLogHandler((_, m) => logger.LogDebug("Kafka: {Message}", m.Message)).Build();

    private static string? Header(Message<string, string> message, string key)
        => message.Headers is not null && message.Headers.TryGetLastBytes(key, out var value)
            ? Encoding.UTF8.GetString(value)
            : null;
}
