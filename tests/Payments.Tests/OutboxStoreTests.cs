using Dapper;
using System.Net.Http.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Payments.Core.Persistence;

namespace Payments.Tests;

/// <summary>
/// How the dispatcher treats failure. Nothing here talks to a broker: the
/// publish callback is the thing under test, so the failures are handed to it
/// directly and deterministically.
/// </summary>
public sealed class OutboxStoreTests(PaymentsApiFixture fixture) : IClassFixture<PaymentsApiFixture>
{
    private const int MaxAttempts = 3;

    private OutboxStore Store() => new(
        new DbConnectionFactory(fixture.ConnectionString), NullLogger<OutboxStore>.Instance);

    private static bool IsBrokerDown(Exception e)
        => e is ProduceException<string, string> { Error.Code: ErrorCode.Local_AllBrokersDown };

    private static Exception BrokerDown() => new ProduceException<string, string>(
        new Error(ErrorCode.Local_AllBrokersDown, "no brokers"),
        new DeliveryResult<string, string>());

    [Fact]
    public async Task An_unreachable_broker_leaves_every_event_exactly_as_it_was()
    {
        var merchantId = await CreatePaymentAsync();
        var before = await fixture.ReadOutboxStateAsync(merchantId);

        var result = await Store().DispatchBatchAsync(
            publish: (_, _) => throw BrokerDown(),
            isBrokerUnavailable: IsBrokerDown,
            batchSize: 100,
            maxAttempts: MaxAttempts,
            ct: CancellationToken.None);

        Assert.Equal(DispatchOutcome.BrokerUnavailable, result.Outcome);
        Assert.Equal(0, result.Published);

        // The point of the whole distinction: an outage must not spend attempts.
        // Otherwise a broker down for a few minutes would kill every event waiting.
        var after = await fixture.ReadOutboxStateAsync(merchantId);
        Assert.Equal(before.Attempts, after.Attempts);
        Assert.Null(after.PublishedAt);
        Assert.Null(after.DeadAt);
    }

    [Fact]
    public async Task A_message_the_broker_rejects_is_counted_and_eventually_set_aside()
    {
        var merchantId = await CreatePaymentAsync();
        var store = Store();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            await store.DispatchBatchAsync(
                publish: (_, _) => throw new InvalidOperationException("message rejected"),
                isBrokerUnavailable: IsBrokerDown,
                batchSize: 100,
                maxAttempts: MaxAttempts,
                ct: CancellationToken.None);

            var state = await fixture.ReadOutboxStateAsync(merchantId);
            Assert.Equal(attempt, state.Attempts);

            // Set aside only once it has had every chance, not before.
            if (attempt < MaxAttempts)
            {
                Assert.Null(state.DeadAt);
            }
            else
            {
                Assert.NotNull(state.DeadAt);
            }
        }
    }

    [Fact]
    public async Task A_dead_event_is_no_longer_claimed()
    {
        var merchantId = await CreatePaymentAsync();
        var store = Store();

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            await store.DispatchBatchAsync(
                (_, _) => throw new InvalidOperationException("message rejected"),
                IsBrokerDown, 100, MaxAttempts, CancellationToken.None);
        }

        Assert.NotNull((await fixture.ReadOutboxStateAsync(merchantId)).DeadAt);

        // It must stop holding up everything behind it.
        var claimedAfterDeath = 0;
        var result = await store.DispatchBatchAsync(
            (_, _) => { claimedAfterDeath++; return Task.CompletedTask; },
            IsBrokerDown, 100, MaxAttempts, CancellationToken.None);

        Assert.Equal(0, claimedAfterDeath);
        Assert.Equal(DispatchOutcome.Idle, result.Outcome);
    }

    [Fact]
    public async Task A_published_event_is_marked_and_not_claimed_twice()
    {
        var merchantId = await CreatePaymentAsync();
        var store = Store();

        var first = await store.DispatchBatchAsync(
            (_, _) => Task.CompletedTask, IsBrokerDown, 100, MaxAttempts, CancellationToken.None);

        Assert.Equal(DispatchOutcome.Dispatched, first.Outcome);
        Assert.Equal(1, first.Published);
        Assert.NotNull((await fixture.ReadOutboxStateAsync(merchantId)).PublishedAt);

        var second = await store.DispatchBatchAsync(
            (_, _) => Task.CompletedTask, IsBrokerDown, 100, MaxAttempts, CancellationToken.None);

        Assert.Equal(DispatchOutcome.Idle, second.Outcome);
    }

    /// <summary>
    /// Two dispatchers between them take everything, and never the same row.
    /// </summary>
    [Fact]
    public async Task Partitions_together_cover_everything_and_never_overlap()
    {
        for (var payment = 0; payment < 12; payment++)
        {
            await CreatePaymentAsync();
        }

        var store = Store();
        var byPartition = new List<long>[2];

        for (var partition = 0; partition < 2; partition++)
        {
            var claimed = new List<long>();

            // Until this partition is empty, so the comparison is about which rows
            // each one owns rather than how big a batch happened to be.
            while (true)
            {
                var result = await store.DispatchBatchAsync(
                    (record, _) => { claimed.Add(record.Id); return Task.CompletedTask; },
                    IsBrokerDown, 100, MaxAttempts, CancellationToken.None,
                    partitionIndex: partition, partitionCount: 2);

                if (result.Outcome == DispatchOutcome.Idle) break;
            }

            byPartition[partition] = claimed;
        }

        Assert.NotEmpty(byPartition[0]);
        Assert.NotEmpty(byPartition[1]);
        Assert.Empty(byPartition[0].Intersect(byPartition[1]));

        // Nothing left behind by either.
        var leftover = await store.DispatchBatchAsync(
            (_, _) => Task.CompletedTask, IsBrokerDown, 100, MaxAttempts, CancellationToken.None);

        Assert.Equal(DispatchOutcome.Idle, leftover.Outcome);
    }

    /// <summary>
    /// Ordering per payment is the reason partitioning exists, so every event
    /// about one payment has to land in the same partition.
    /// </summary>
    [Fact]
    public async Task Every_event_about_one_payment_falls_to_one_partition()
    {
        var merchantId = await CreatePaymentAsync();
        var aggregateId = await AggregateOfAsync(merchantId);

        await AppendEventsAsync(aggregateId, 4);

        var store = Store();
        var partitionsSeen = new HashSet<int>();

        for (var partition = 0; partition < 3; partition++)
        {
            var index = partition;

            await store.DispatchBatchAsync(
                (record, _) =>
                {
                    if (record.AggregateId == aggregateId) partitionsSeen.Add(index);
                    return Task.CompletedTask;
                },
                IsBrokerDown, 100, MaxAttempts, CancellationToken.None,
                partitionIndex: partition, partitionCount: 3);
        }

        Assert.Single(partitionsSeen);
    }

    private async Task<Guid> AggregateOfAsync(Guid merchantId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        return await connection.ExecuteScalarAsync<Guid>(
            "SELECT id FROM payments WHERE merchant_id = @merchantId", new { merchantId });
    }

    private async Task AppendEventsAsync(Guid aggregateId, int count)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);

        for (var i = 0; i < count; i++)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO outbox (aggregate_id, event_type, payload)
                VALUES (@aggregateId, 'test.event.v1', '{}'::jsonb)
                """, new { aggregateId });
        }
    }

    /// <summary>Creates one payment and returns its merchant, so its event can be found.</summary>
    private async Task<Guid> CreatePaymentAsync()
    {
        var merchantId = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new { merchantId, amountMinor = 1000L, currency = "USD" })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await fixture.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();

        return merchantId;
    }
}
