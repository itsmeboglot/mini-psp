using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Payments.Core.Contracts;
using Payments.Core.Observability;
using Payments.Core.Persistence;
using Payments.Core.Providers;
using Payments.Worker;

namespace Payments.Tests;

/// <summary>
/// A consumer that dies between committing its work and committing its offset.
/// </summary>
/// <remarks>
/// This is the window the whole design accepts on purpose. The offset is
/// committed after the work, so a crash in between means Kafka has not been told,
/// and the event arrives again. The claim is that the second delivery changes
/// nothing, and until now that was true by construction and by nothing else.
///
/// Killing a real process mid-transaction is not something a test can do
/// reliably. What it can do is reproduce the state a crash leaves behind exactly:
/// the work committed, the offset not, and the same event delivered again. That
/// is the whole of the scenario, because nothing else about the crash is
/// observable afterwards.
/// </remarks>
public sealed class CrashSafetyTests(PaymentsApiFixture fixture) : IClassFixture<PaymentsApiFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_redelivered_event_after_a_crash_repeats_no_work()
    {
        var merchantId = await CreatePaymentAsync();
        var (eventId, payload) = await CreatedEventAsync(merchantId);

        // The delivery that got its work committed. The process dies here, before
        // the offset moves.
        var first = await ProcessCreatedAsync(eventId, payload);
        Assert.Equal(ProcessOutcome.Applied, first);

        var afterCrash = await SnapshotAsync(merchantId);

        // Kafka never heard, so it delivers again.
        var second = await ProcessCreatedAsync(eventId, payload);

        Assert.Equal(ProcessOutcome.AlreadyProcessed, second);
        Assert.Equal(afterCrash, await SnapshotAsync(merchantId));
    }

    /// <summary>
    /// The other half: a crash before the work commits must lose the work, not
    /// half of it, and the redelivery must then do it properly.
    /// </summary>
    [Fact]
    public async Task A_crash_before_the_commit_leaves_nothing_behind()
    {
        var merchantId = await CreatePaymentAsync();
        var (eventId, payload) = await CreatedEventAsync(merchantId);

        var db = new DbConnectionFactory(fixture.ConnectionString);
        var processor = new IdempotentEventProcessor(db, NullLogger<IdempotentEventProcessor>.Instance);
        var store = new PaymentStore(db, NullLogger<PaymentStore>.Instance);
        var handler = new PaymentCreatedHandler(store, NullLogger<PaymentCreatedHandler>.Instance);

        // The handler does its work and then the process dies, so the transaction
        // is never committed.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            processor.ProcessAsync("crash-test", eventId, async (connection, transaction, ct) =>
            {
                await handler.HandleAsync(payload, "crash", connection, transaction, ct);
                throw new OperationCanceledException("the process died here");
            }, CancellationToken.None));

        // Neither the work nor the record of it survived.
        var state = await SnapshotAsync(merchantId);
        Assert.Equal("created", state.Status);
        Assert.Equal(1, state.OutboxEvents);
        Assert.Equal(0, await ProcessedCountAsync("crash-test", eventId));

        // And the redelivery does the work properly.
        Assert.Equal(ProcessOutcome.Applied, await ProcessCreatedAsync(eventId, payload, "crash-test"));
        Assert.Equal("pending", (await SnapshotAsync(merchantId)).Status);
    }

    private async Task<ProcessOutcome> ProcessCreatedAsync(
        long eventId, string payload, string consumer = "crash-consumer")
    {
        var db = new DbConnectionFactory(fixture.ConnectionString);
        var processor = new IdempotentEventProcessor(db, NullLogger<IdempotentEventProcessor>.Instance);
        var store = new PaymentStore(db, NullLogger<PaymentStore>.Instance);
        var handler = new PaymentCreatedHandler(store, NullLogger<PaymentCreatedHandler>.Instance);

        return await processor.ProcessAsync(consumer, eventId,
            (connection, transaction, ct) => handler.HandleAsync(payload, "crash", connection, transaction, ct),
            CancellationToken.None);
    }

    /// <summary>Everything a repeated delivery could possibly change.</summary>
    private async Task<Snapshot> SnapshotAsync(Guid merchantId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);

        return await connection.QuerySingleAsync<Snapshot>(
            """
            SELECT p.status AS Status, p.version AS Version,
                   (SELECT count(*) FROM outbox o WHERE o.aggregate_id = p.id) AS OutboxEvents
            FROM payments p WHERE p.merchant_id = @merchantId
            """, new { merchantId });
    }

    private sealed record Snapshot(string Status, int Version, long OutboxEvents);

    private async Task<long> ProcessedCountAsync(string consumer, long eventId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM processed_events WHERE consumer = @consumer AND event_id = @eventId",
            new { consumer, eventId });
    }

    private async Task<Guid> CreatePaymentAsync()
    {
        var merchantId = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new { merchantId, amountMinor = 2_500L, currency = "USD" }, options: Json)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        (await fixture.CreateClient().SendAsync(request)).EnsureSuccessStatusCode();

        return merchantId;
    }

    private async Task<(long Id, string Payload)> CreatedEventAsync(Guid merchantId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);

        return await connection.QuerySingleAsync<(long, string)>(
            """
            SELECT id, payload::text FROM outbox
            WHERE payload->>'merchantId' = @merchantId AND event_type = @type
            """,
            new { merchantId = merchantId.ToString(), type = PaymentCreatedEvent.EventType });
    }
}
