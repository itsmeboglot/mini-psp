using Dapper;
using Npgsql;
using Payments.Api.Domain;

namespace Payments.Api.Persistence;

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
    public async Task<CreateOutcome> CreateAsync(
        Payment payment,
        string idempotencyKey,
        string requestHash,
        StoredResponse response,
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

            await transaction.CommitAsync(ct);
            return new CreateOutcome.Created();
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation
                                         && e.ConstraintName == IdempotencyKeyConstraint)
        {
            await transaction.RollbackAsync(ct);
            return await ResolveDuplicateAsync(payment.MerchantId, idempotencyKey, requestHash, ct);
        }
    }

    public async Task<Payment?> GetAsync(Guid id, CancellationToken ct)
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
        Guid merchantId, string idempotencyKey, string requestHash, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var stored = await connection.QuerySingleOrDefaultAsync<StoredRow>(
            new CommandDefinition(SelectStored,
                new { MerchantId = merchantId, Key = idempotencyKey }, cancellationToken: ct));

        if (stored is null)
        {
            logger.LogInformation(
                "Idempotency key {Key} for merchant {MerchantId} is held by an uncommitted request",
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
