using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Payments.Core.Messaging;
using Payments.Core.Observability;
using Payments.Core.Persistence;

namespace Payments.Api.Outbox;

public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>
    /// Lets the dispatcher be turned off where it is not wanted: tests that have
    /// no broker, or a deployment that runs it as its own process rather than
    /// inside the API.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Which slice of the outbox this instance owns, and how many slices there
    /// are. Every event about one payment hashes to the same slice, so ordering
    /// per payment survives running several dispatchers.
    /// </summary>
    public int PartitionIndex { get; set; }

    public int PartitionCount { get; set; } = 1;

    /// <summary>How long to wait when there was nothing to send.</summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>How long to wait after the broker turned out to be unreachable.</summary>
    public int BrokerBackoffMs { get; set; } = 5_000;

    /// <summary>Publishing failures a single event gets before it is set aside.</summary>
    public int MaxAttempts { get; set; } = 10;
}

/// <summary>
/// Empties the outbox into Kafka.
/// </summary>
/// <remarks>
/// The store is resolved per iteration through a scope rather than injected: it
/// is scoped, this service is a singleton, and holding a scoped dependency in a
/// singleton is the captive dependency bug.
/// </remarks>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    EventPublisher publisher,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    PaymentMetrics metrics,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private readonly OutboxOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Outbox dispatcher started, batch size {BatchSize}, partition {Index} of {Count}",
            _options.BatchSize, _options.PartitionIndex, _options.PartitionCount);

        if (_options.PartitionIndex >= _options.PartitionCount)
        {
            // Would claim nothing, forever, silently. Better to refuse to start.
            throw new InvalidOperationException(
                $"Outbox partition index {_options.PartitionIndex} is outside a count of {_options.PartitionCount}.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = await DispatchOnceAsync(stoppingToken);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, clock, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // Never let the loop die. An unhandled exception here would stop
                // every event in the table from ever being published, silently.
                logger.LogError(e, "Outbox dispatch failed unexpectedly");
                await Task.Delay(TimeSpan.FromMilliseconds(_options.BrokerBackoffMs), clock, stoppingToken);
            }
        }

        logger.LogInformation("Outbox dispatcher stopped");
    }

    /// <returns>How long to wait before looking again.</returns>
    private async Task<TimeSpan> DispatchOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<OutboxStore>();

        var result = await store.DispatchBatchAsync(
            publisher.PublishAsync, IsBrokerUnavailable, _options.BatchSize, _options.MaxAttempts, ct,
            _options.PartitionIndex, _options.PartitionCount);

        if (result.Published > 0)
        {
            metrics.EventsPublished(result.Published);
        }

        return result.Outcome switch
        {
            DispatchOutcome.BrokerUnavailable => TimeSpan.FromMilliseconds(_options.BrokerBackoffMs),

            // A full batch suggests more is waiting, so come straight back.
            DispatchOutcome.Dispatched when result.Claimed >= _options.BatchSize => TimeSpan.Zero,

            _ => TimeSpan.FromMilliseconds(_options.PollIntervalMs)
        };
    }

    /// <remarks>
    /// Separating "the broker is down" from "this message is bad" is what keeps an
    /// outage from marching every waiting event towards dead.
    /// </remarks>
    private static bool IsBrokerUnavailable(Exception e) => e switch
    {
        ProduceException<string, string> produce => produce.Error.Code
            is ErrorCode.Local_AllBrokersDown
            or ErrorCode.Local_Transport
            or ErrorCode.Local_TimedOut
            or ErrorCode.Local_MsgTimedOut
            or ErrorCode.BrokerNotAvailable
            or ErrorCode.NetworkException
            or ErrorCode.RequestTimedOut,

        // The publish timeout fired rather than the request completing.
        OperationCanceledException => true,

        _ => false
    };
}
