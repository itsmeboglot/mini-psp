using Dapper;
using Npgsql;
using Payments.Core.Domain;

namespace Payments.Core.Persistence;

/// <summary>
/// Persists payments and the idempotency keys that guard their creation.
/// </summary>
/// <remarks>
/// Deliberately ignorant of HTTP: it stores a response it is handed and replays
/// it unchanged. Deciding what a response looks like belongs to the API layer.
/// </remarks>
public sealed class PaymentStore(DbConnectionFactory db, ILogger<PaymentStore> logger)
{
    /// <summary>
    /// The unique constraint that makes creation idempotent, named explicitly in
    /// db/001_init.sql. Matching on it matters because more than one unique
    /// constraint is touched by the create transaction, and they all raise the
    /// same SQLSTATE.
    /// </summary>
    private const string IdempotencyKeyConstraint = "idempotency_keys_pkey";

    private const string InsertPayment = """
        INSERT INTO payments (id, merchant_id, status, amount_minor, currency, version, created_at, updated_at)
        VALUES (@Id, @MerchantId, @Status, @AmountMinor, @Currency, @Version, @CreatedAt, @CreatedAt);
        """;

    private const string InsertIdempotencyKey = """
        INSERT INTO idempotency_keys
            (merchant_id, idempotency_key, request_hash, payment_id, response_status, response_body)
        VALUES
            (@MerchantId, @Key, @RequestHash, @PaymentId, @ResponseStatus, @ResponseBody);
        """;

    private const string InsertOutboxMessage = """
        INSERT INTO outbox (aggregate_id, event_type, payload)
        VALUES (@AggregateId, @EventType, @Payload::jsonb);
        """;

    private const string SelectStored = """
        SELECT request_hash AS RequestHash, response_status AS StatusCode, response_body AS Body
        FROM idempotency_keys
        WHERE merchant_id = @MerchantId AND idempotency_key = @Key;
        """;

    private const string SelectPayment = """
        SELECT id AS Id, merchant_id AS MerchantId, status AS Status, amount_minor AS AmountMinor,
               currency AS Currency, version AS Version, created_at AS CreatedAt
        FROM payments
        WHERE id = @Id;
        """;

    /// <summary>
    /// Inserts <paramref name="payment"/> together with the idempotency key that
    /// claims it, or reports what an earlier request carrying that key produced.
    /// </summary>
    /// <remarks>
    /// Both rows are written in one transaction, so the unique index on
    /// (merchant_id, idempotency_key) is what makes creation idempotent. Two
    /// concurrent requests race into that insert; one commits, the other is
    /// rejected by the index. Neither can produce a second payment.
    /// See docs/adr/0003-idempotency-in-postgres.md
    /// </remarks>
    public Task<CreateOutcome> CreateAsync(
        Payment payment,
        string idempotencyKey,
        string requestHash,
        StoredResponse response,
        OutboxMessage message,
        CancellationToken ct)
        => TransientRetry.RunAsync(
            token => AttemptCreateAsync(payment, idempotencyKey, requestHash, response, message, token),
            logger,
            ct);

    private async Task<CreateOutcome> AttemptCreateAsync(
        Payment payment,
        string idempotencyKey,
        string requestHash,
        StoredResponse response,
        OutboxMessage message,
        CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(InsertPayment, new
            {
                payment.Id,
                payment.MerchantId,
                // Mapped here rather than by a Dapper type handler: Dapper treats
                // enums specially and writes the member name, which the CHECK on
                // payments.status rejects. The translation belongs to this layer
                // anyway, since the stored text is a database contract.
                Status = PaymentStatuses.ToWire(payment.Status),
                AmountMinor = payment.Amount.MinorUnits,
                Currency = payment.Amount.Currency,
                payment.Version,
                payment.CreatedAt
            }, transaction, cancellationToken: ct));

            await connection.ExecuteAsync(new CommandDefinition(InsertIdempotencyKey, new
            {
                payment.MerchantId,
                Key = idempotencyKey,
                RequestHash = requestHash,
                PaymentId = payment.Id,
                ResponseStatus = response.StatusCode,
                ResponseBody = response.Body
            }, transaction, cancellationToken: ct));

            // The event and the state change it describes commit together or not
            // at all. This is the whole reason the outbox exists: publishing after
            // the commit instead would leave a window where the payment is real
            // and nobody downstream has heard of it.
            await connection.ExecuteAsync(new CommandDefinition(InsertOutboxMessage, new
            {
                message.AggregateId,
                message.EventType,
                message.Payload
            }, transaction, cancellationToken: ct));

            await transaction.CommitAsync(ct);

            logger.LogInformation(
                "Created payment {PaymentId} for merchant {MerchantId}: {AmountMinor} {Currency}",
                payment.Id, payment.MerchantId, payment.Amount.MinorUnits, payment.Amount.Currency);

            return new CreateOutcome.Created();
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation
                                         && e.ConstraintName == IdempotencyKeyConstraint)
        {
            // Not ct: if the caller has already disconnected, its token is
            // cancelled, and a cancelled rollback would throw over the top of the
            // violation we are here to handle.
            await transaction.RollbackAsync(CancellationToken.None);

            // The connection is still open and no longer in a transaction, so the
            // lookup reuses it instead of taking a second one from the pool.
            return await ResolveDuplicateAsync(
                connection, payment.MerchantId, idempotencyKey, requestHash, ct);
        }
    }

    /// <summary>Fetches a payment by id.</summary>
    /// <remarks>
    /// Not scoped to a merchant, because there is no authentication yet to scope
    /// it by. That means any caller who knows or guesses an id can read any
    /// payment. It is a deliberate gap, recorded in docs/CONTEXT.md, and the fix
    /// is one signature change — GetAsync(merchantId, id) with the merchant added
    /// to the WHERE clause — the moment a caller identity exists.
    /// </remarks>
    public Task<Payment?> GetAsync(Guid id, CancellationToken ct)
        => TransientRetry.RunAsync(token => AttemptGetAsync(id, token), logger, ct);

    private async Task<Payment?> AttemptGetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<PaymentRow>(
            new CommandDefinition(SelectPayment, new { Id = id }, cancellationToken: ct));

        return row?.ToDomain();
    }

    /// <summary>
    /// Works out which kind of duplicate this was, once the index has rejected it.
    /// </summary>
    private async Task<CreateOutcome> ResolveDuplicateAsync(
        NpgsqlConnection connection,
        Guid merchantId,
        string idempotencyKey,
        string requestHash,
        CancellationToken ct)
    {
        var stored = await connection.QuerySingleOrDefaultAsync<StoredRow>(
            new CommandDefinition(SelectStored,
                new { MerchantId = merchantId, Key = idempotencyKey }, cancellationToken: ct));

        if (stored is null)
        {
            // Rare by design: a losing writer waits for the winner to commit, so
            // the row is normally visible by the time the violation surfaces.
            // Getting here suggests a retention delete or a lagging replica.
            logger.LogWarning(
                "Idempotency key {Key} for merchant {MerchantId} produced a unique violation "
                + "but no stored response was found",
                idempotencyKey, merchantId);

            return new CreateOutcome.InFlight();
        }

        return stored.RequestHash == requestHash
            ? new CreateOutcome.Replayed(new StoredResponse(stored.StatusCode, stored.Body))
            : new CreateOutcome.KeyReused();
    }

    private sealed record StoredRow(string RequestHash, int StatusCode, string Body);

    /// <summary>A payments row exactly as the database shapes it.</summary>
    private sealed record PaymentRow(
        Guid Id,
        Guid MerchantId,
        string Status,
        long AmountMinor,
        string Currency,
        int Version,
        DateTimeOffset CreatedAt)
    {
        public Payment ToDomain() => new(
            Id,
            MerchantId,
            PaymentStatuses.Parse(Status),
            Money.FromStorage(AmountMinor, Currency),
            Version,
            CreatedAt);
    }
}
