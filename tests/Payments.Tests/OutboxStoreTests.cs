using System.Net.Http.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
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
