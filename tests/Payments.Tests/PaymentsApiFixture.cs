using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Payments.Core.Persistence;
using Payments.Worker;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Payments.Tests;

/// <summary>
/// Runs the real application against a real PostgreSQL in a container. The
/// behaviour under test — a unique index rejecting a concurrent insert — cannot
/// be observed against a fake or an in-memory provider, so there is no version
/// of these tests worth writing without a database.
/// </summary>
public sealed class PaymentsApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("minipsp")
        .WithUsername("minipsp")
        .WithPassword("minipsp")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // The same runner the application uses, rather than a copy of the schema
        // that could drift from it.
        await new MigrationRunner(
                new DbConnectionFactory(ConnectionString),
                NullLogger<MigrationRunner>.Instance)
            .RunAsync(CancellationToken.None);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Payments", ConnectionString);

        // No broker in this fixture, so the dispatcher would spend every test
        // backing off against nothing. Dispatching has its own fixture.
        builder.UseSetting("Outbox:Enabled", "false");
    }

    public async Task<long> CountPaymentsAsync(Guid merchantId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM payments WHERE merchant_id = @merchantId", new { merchantId });
    }

    public async Task<long> CountOutboxAsync(Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM outbox WHERE aggregate_id = @aggregateId", new { aggregateId });
    }

    public async Task<long> CountUnpublishedOutboxAsync(Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM outbox WHERE aggregate_id = @aggregateId AND published_at IS NULL",
            new { aggregateId });
    }

    /// <summary>
    /// Counts events by the merchant inside the payload, not by aggregate id.
    /// A rolled back attempt carries a payment id nobody ever sees, so counting by
    /// aggregate would miss exactly the orphan these tests exist to catch.
    /// This is also what the jsonb payload column buys: events are queryable by
    /// content.
    /// </summary>
    public async Task<long> CountOutboxForMerchantAsync(Guid merchantId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM outbox WHERE payload->>'merchantId' = @merchantId",
            new { merchantId = merchantId.ToString() });
    }

    /// <summary>
    /// Events whose payment does not exist. Zero is an invariant of the outbox:
    /// an event is only ever written in the transaction that creates the payment
    /// it describes, so one without the other means that transaction was broken
    /// apart.
    /// </summary>
    public async Task<long> CountOrphanEventsAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<long>(
            """
            SELECT count(*)
            FROM outbox o
            LEFT JOIN payments p ON p.id = o.aggregate_id
            WHERE p.id IS NULL
            """);
    }

    /// <summary>The bookkeeping columns of a merchant's single outbox event.</summary>
    public async Task<OutboxState> ReadOutboxStateAsync(Guid merchantId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleAsync<OutboxState>(
            """
            SELECT attempts AS Attempts, published_at AS PublishedAt,
                   dead_at AS DeadAt, last_error AS LastError
            FROM outbox
            WHERE payload->>'merchantId' = @merchantId
            """, new { merchantId = merchantId.ToString() });
    }

    public sealed record OutboxState(
        int Attempts, DateTimeOffset? PublishedAt, DateTimeOffset? DeadAt, string? LastError);

    /// <summary>Reads the columns these tests assert on.</summary>
    public async Task<PaymentState> ReadPaymentAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleAsync<PaymentState>(
            """
            SELECT status AS Status, version AS Version, provider AS Provider,
                   provider_payment_id AS ProviderReference,
                   reconciliation_attempts AS ReconciliationAttempts
            FROM payments WHERE id = @id
            """, new { id });
    }

    public sealed record PaymentState(
        string Status, int Version, string? Provider, string? ProviderReference, int ReconciliationAttempts);

    /// <summary>
    /// Moves a merchant's single payment to pending through the real handler, so
    /// tests start from a state the system actually produces.
    /// </summary>
    public async Task<Payments.Core.Domain.Payment> MoveToPendingAsync(Guid merchantId)
    {
        var db = new DbConnectionFactory(ConnectionString);
        var store = new PaymentStore(db, NullLogger<PaymentStore>.Instance);
        var processor = new IdempotentEventProcessor(db, NullLogger<IdempotentEventProcessor>.Instance);
        var handler = new PaymentCreatedHandler(store, NullLogger<PaymentCreatedHandler>.Instance);

        var row = await ReadOutboxForMerchantAsync(merchantId);

        await processor.ProcessAsync("test", row.Id,
            (connection, transaction, ct) => handler.HandleAsync(row.Payload, connection, transaction, ct),
            CancellationToken.None);

        await using var connection = new NpgsqlConnection(ConnectionString);
        var id = await connection.ExecuteScalarAsync<Guid>(
            "SELECT id FROM payments WHERE merchant_id = @merchantId", new { merchantId });

        return (await store.GetAsync(id, CancellationToken.None))!;
    }

    /// <summary>The payment.pending.v1 payload the dispatcher would have published.</summary>
    public string PendingEventPayload(Payments.Core.Domain.Payment payment)
        => System.Text.Json.JsonSerializer.Serialize(
            new Payments.Core.Contracts.PaymentPendingEvent(
                payment.Id, payment.MerchantId, payment.Amount.MinorUnits,
                payment.Amount.Currency, "pending", payment.CreatedAt),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    private async Task<(long Id, string Payload)> ReadOutboxForMerchantAsync(Guid merchantId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleAsync<(long, string)>(
            """
            SELECT id, payload::text FROM outbox
            WHERE payload->>'merchantId' = @merchantId AND event_type = 'payment.created.v1'
            """, new { merchantId = merchantId.ToString() });
    }

    public async Task<OutboxRow> ReadOutboxAsync(Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleAsync<OutboxRow>(
            """
            SELECT aggregate_id AS AggregateId, event_type AS EventType, payload::text AS Payload
            FROM outbox WHERE aggregate_id = @aggregateId
            """, new { aggregateId });
    }

    public sealed record OutboxRow(Guid AggregateId, string EventType, string Payload);

}
