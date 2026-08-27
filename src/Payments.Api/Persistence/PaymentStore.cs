using System.Text.Json;
using Dapper;
using Npgsql;
using Payments.Api.Contracts;
using Payments.Api.Domain;

namespace Payments.Api.Persistence;

public enum CreateOutcomeKind
{
    /// <summary>A new payment was created by this request.</summary>
    Created,

    /// <summary>The key was seen before with the same body; the stored response is returned.</summary>
    Replayed,

    /// <summary>The key was seen before with a different body. A client defect.</summary>
    KeyReused
}

public sealed record StoredResponse(int StatusCode, string Body);

public sealed record CreateOutcome(CreateOutcomeKind Kind, StoredResponse? Response);

public sealed class PaymentStore(DbConnectionFactory db, ILogger<PaymentStore> logger)
{
    /// <summary>Matches what ASP.NET Core uses, so a replayed body is byte-identical to a fresh one.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string InsertPayment = """
        INSERT INTO payments (id, merchant_id, status, amount_minor, currency, version, created_at, updated_at)
        VALUES (@Id, @MerchantId, @Status, @AmountMinor, @Currency, @Version, @CreatedAt, @CreatedAt);
        """;

    private const string InsertIdempotencyKey = """
        INSERT INTO idempotency_keys
            (merchant_id, idempotency_key, request_hash, payment_id, response_status, response_body)
        VALUES
            (@MerchantId, @Key, @RequestHash, @PaymentId, @ResponseStatus, @ResponseBody::jsonb);
        """;

    private const string SelectStored = """
        SELECT request_hash AS RequestHash, response_status::int AS StatusCode, response_body::text AS Body
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
    /// Creates a payment, or returns what a previous request carrying the same
    /// idempotency key produced.
    /// </summary>
    /// <remarks>
    /// The payment row and the idempotency row are written in one transaction,
    /// so the unique index on (merchant_id, idempotency_key) is what makes the
    /// operation idempotent. Two concurrent requests race into that insert; one
    /// commits, the other is rejected by the index and replays the stored
    /// response. Neither can produce a second payment.
    /// See docs/adr/0003-idempotency-in-postgres.md
    /// </remarks>
    public async Task<CreateOutcome> CreateAsync(
        Guid merchantId,
        string idempotencyKey,
        string requestHash,
        Money amount,
        CancellationToken ct)
    {
        var payment = new Payment(
            // Version 7 UUIDs are time ordered, which keeps inserts at the right
            // edge of the primary key index instead of scattering them.
            Id: Guid.CreateVersion7(),
            MerchantId: merchantId,
            Status: PaymentStatus.Created,
            AmountMinor: amount.MinorUnits,
            Currency: amount.Currency,
            Version: 1,
            CreatedAt: DateTimeOffset.UtcNow);

        var body = JsonSerializer.Serialize(PaymentResponse.From(payment), JsonOptions);

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            await conn.ExecuteAsync(new CommandDefinition(InsertPayment, payment, tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(InsertIdempotencyKey, new
            {
                MerchantId = merchantId,
                Key = idempotencyKey,
                RequestHash = requestHash,
                PaymentId = payment.Id,
                ResponseStatus = (short)StatusCodes.Status201Created,
                ResponseBody = body
            }, tx, cancellationToken: ct));

            await tx.CommitAsync(ct);
            return new CreateOutcome(CreateOutcomeKind.Created, new StoredResponse(StatusCodes.Status201Created, body));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await tx.RollbackAsync(ct);

            var stored = await ReadStoredAsync(merchantId, idempotencyKey, ct);
            if (stored is null)
            {
                // The winning transaction has not committed yet, so its row is
                // not visible. Surfacing a conflict is correct: the client
                // retries and gets the stored response once that commit lands.
                logger.LogInformation(
                    "Idempotency key {Key} for merchant {MerchantId} is in flight on another request",
                    idempotencyKey, merchantId);
                return new CreateOutcome(CreateOutcomeKind.Replayed, null);
            }

            return stored.Value.RequestHash == requestHash
                ? new CreateOutcome(CreateOutcomeKind.Replayed, new StoredResponse(stored.Value.StatusCode, stored.Value.Body))
                : new CreateOutcome(CreateOutcomeKind.KeyReused, null);
        }
    }

    public async Task<Payment?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Payment>(
            new CommandDefinition(SelectPayment, new { Id = id }, cancellationToken: ct));
    }

    private async Task<(string RequestHash, int StatusCode, string Body)?> ReadStoredAsync(
        Guid merchantId, string key, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<StoredRow>(
            new CommandDefinition(SelectStored, new { MerchantId = merchantId, Key = key }, cancellationToken: ct));

        return row is null ? null : (row.RequestHash, row.StatusCode, row.Body);
    }

    private sealed record StoredRow(string RequestHash, int StatusCode, string Body);
}
