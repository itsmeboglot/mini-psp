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
        INSERT INTO outbox (aggregate_id, event_type, payload, correlation_id)
        VALUES (@AggregateId, @EventType, @Payload::jsonb, @CorrelationId);
        """;

    private const string SelectStored = """
        SELECT request_hash AS RequestHash, response_status AS StatusCode, response_body AS Body
        FROM idempotency_keys
        WHERE merchant_id = @MerchantId AND idempotency_key = @Key;
        """;

    private const string ApplyTransition = """
        UPDATE payments
        SET status = @Status,
            version = @Version,
            updated_at = now(),
            -- COALESCE so a transition that knows nothing about a provider does
            -- not erase what an earlier one recorded.
            provider = COALESCE(@Provider, provider),
            provider_payment_id = COALESCE(@ProviderReference, provider_payment_id)
        WHERE id = @Id AND version = @ExpectedVersion;
        """;

    /// <remarks>
    /// Claims and stamps in one statement. The stamp is the claim: a provider
    /// call cannot happen inside a transaction, so no lock can be held across it,
    /// and another instance is kept off the row by seeing it was tried recently.
    /// SKIP LOCKED keeps two instances from claiming the same rows in the instant
    /// before either has committed its stamp.
    /// </remarks>
    private const string ClaimUnresolved = """
        WITH claimed AS (
            SELECT id
            FROM payments
            WHERE status = 'unknown'
              AND created_at < now() - make_interval(secs => @GraceSeconds)
              AND (last_reconciled_at IS NULL
                   OR last_reconciled_at < now() - make_interval(secs => @RetrySeconds))
            ORDER BY last_reconciled_at NULLS FIRST, created_at
            FOR UPDATE SKIP LOCKED
            LIMIT @BatchSize
        )
        UPDATE payments p
        SET last_reconciled_at = now(),
            reconciliation_attempts = p.reconciliation_attempts + 1
        FROM claimed c
        WHERE p.id = c.id
        RETURNING p.id AS Id, p.merchant_id AS MerchantId, p.status AS Status,
                  p.amount_minor AS AmountMinor, p.currency AS Currency,
                  p.version AS Version, p.created_at AS CreatedAt,
                  p.reconciliation_attempts AS Attempts;
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
                message.Payload,
                message.CorrelationId
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
    /// Takes a batch of payments whose outcome was never learned, marking them as
    /// being looked into.
    /// </summary>
    public async Task<IReadOnlyList<UnresolvedPayment>> ClaimUnresolvedAsync(
        int batchSize, TimeSpan grace, TimeSpan retryAfter, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        var rows = await connection.QueryAsync<UnresolvedRow>(new CommandDefinition(ClaimUnresolved, new
        {
            BatchSize = batchSize,
            GraceSeconds = grace.TotalSeconds,
            RetrySeconds = retryAfter.TotalSeconds
        }, cancellationToken: ct));

        return rows.Select(row => new UnresolvedPayment(row.ToDomain(), row.Attempts)).ToList();
    }

    /// <summary>
    /// Reads a payment inside a caller's transaction.
    /// </summary>
    public async Task<Payment?> GetAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<PaymentRow>(
            new CommandDefinition(SelectPayment, new { Id = id }, transaction, cancellationToken: ct));

        return row?.ToDomain();
    }

    /// <summary>
    /// Writes a transitioned payment back, refusing if someone else moved it first.
    /// </summary>
    /// <remarks>
    /// Optimistic concurrency: the update asserts the version it read, so two
    /// writers racing to move the same payment cannot both win. The loser is told
    /// so rather than silently overwriting a state it never saw. No row was
    /// locked while the caller was deciding, which is the point — locks held
    /// across domain logic are how a payment platform deadlocks itself.
    /// </remarks>
    public async Task<bool> TryApplyTransitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Payment transitioned,
        int expectedVersion,
        string? provider = null,
        string? providerReference = null,
        CancellationToken ct = default)
    {
        var updated = await connection.ExecuteAsync(new CommandDefinition(ApplyTransition, new
        {
            transitioned.Id,
            Status = PaymentStatuses.ToWire(transitioned.Status),
            transitioned.Version,
            ExpectedVersion = expectedVersion,
            Provider = provider,
            ProviderReference = providerReference
        }, transaction, cancellationToken: ct));

        return updated == 1;
    }

    /// <summary>
    /// Queues an event inside a caller's transaction, so it commits with whatever
    /// change produced it.
    /// </summary>
    public Task AppendToOutboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, OutboxMessage message, CancellationToken ct)
        => connection.ExecuteAsync(new CommandDefinition(InsertOutboxMessage, new
        {
            message.AggregateId,
            message.EventType,
            message.Payload,
            message.CorrelationId
        }, transaction, cancellationToken: ct));

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

    private sealed record UnresolvedRow(
        Guid Id, Guid MerchantId, string Status, long AmountMinor, string Currency,
        int Version, DateTimeOffset CreatedAt, int Attempts)
    {
        public Payment ToDomain() => new(
            Id, MerchantId, PaymentStatuses.Parse(Status),
            Money.FromStorage(AmountMinor, Currency), Version, CreatedAt);
    }

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
