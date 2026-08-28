using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Payments.Core.Contracts;
using Payments.Core.Domain;
using Payments.Core.Observability;
using Payments.Core.Persistence;
using Payments.Core.Providers;

namespace Payments.Worker;

public sealed class SettlementOptions
{
    public const string Section = "Settlement";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often to fetch the report. A real provider publishes daily; the
    /// interval is configurable so the loop can be exercised at a sensible pace
    /// rather than waiting for tomorrow.
    /// </summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// How old an undetermined payment must be before absence from the report is
    /// taken as proof it never happened, rather than as it not having landed in
    /// the report yet.
    /// </summary>
    public int GraceSeconds { get; set; } = 30;
}

/// <summary>
/// Settles undetermined payments against the provider's own record, and reports
/// where the two disagree.
/// </summary>
/// <remarks>
/// This is what makes calling a payment failed defensible. A status endpoint
/// answers about one charge and can be behind after the outage that caused the
/// uncertainty in the first place, so counting its denials only chooses how long
/// to wait before believing something that may still be wrong. The report is the
/// provider's account of what it holds, and a charge absent from it did not
/// happen.
///
/// Discrepancies on payments that already reached an outcome are not corrected
/// here. Failed is terminal, and a system that quietly rewrites a merchant's
/// settled history is worse than one that raises its hand: they are recorded for
/// a person to look at.
/// </remarks>
public sealed class SettlementReconciler(
    PaymentStore payments,
    DbConnectionFactory db,
    IPaymentProvider provider,
    IOptions<SettlementOptions> options,
    PaymentMetrics metrics,
    TimeProvider clock,
    ILogger<SettlementReconciler> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string UndeterminedOlderThan = """
        SELECT id AS Id, merchant_id AS MerchantId, status AS Status, amount_minor AS AmountMinor,
               currency AS Currency, version AS Version, created_at AS CreatedAt
        FROM payments
        WHERE status = 'unknown' AND created_at < @Before
        ORDER BY created_at
        LIMIT 500;
        """;

    private const string ResolvedRecently = """
        SELECT id AS Id, status AS Status
        FROM payments
        WHERE status IN ('authorized', 'captured', 'failed') AND updated_at > @Since;
        """;

    private const string RecordDiscrepancy = """
        INSERT INTO settlement_discrepancies (payment_id, our_status, provider_status, provider_reference)
        VALUES (@PaymentId, @OurStatus, @ProviderStatus, @ProviderReference)
        ON CONFLICT (payment_id) DO UPDATE
            SET our_status = @OurStatus, provider_status = @ProviderStatus, observed_at = now();
        """;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var report = await provider.GetSettlementAsync(ct);
        var now = clock.GetUtcNow();

        var resolved = await SettleUndeterminedAsync(report, now.AddSeconds(-options.Value.GraceSeconds), ct);
        await ReportDisagreementsAsync(report, now.AddDays(-1), ct);

        return resolved;
    }

    /// <summary>
    /// Gives every payment whose outcome was never learned a final answer.
    /// </summary>
    private async Task<int> SettleUndeterminedAsync(
        IReadOnlyDictionary<string, ProviderResult> report, DateTimeOffset before, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        var undetermined = (await connection.QueryAsync<PaymentRow>(
            new CommandDefinition(UndeterminedOlderThan, new { Before = before }, cancellationToken: ct))).ToList();

        var resolved = 0;

        foreach (var row in undetermined)
        {
            // Absent from the provider's own account of what it holds. Not "it
            // would not say", but "it says there is nothing".
            var outcome = report.TryGetValue(row.Id.ToString(), out var line)
                ? line.Verdict switch
                {
                    ProviderVerdict.Authorized => (PaymentStatus.Authorized, line.Reference),
                    ProviderVerdict.Declined => (PaymentStatus.Failed, line.Reference),
                    _ => (PaymentStatus.Unknown, null)
                }
                : (PaymentStatus.Failed, null);

            if (outcome.Item1 == PaymentStatus.Unknown)
            {
                continue;
            }

            if (await ApplyAsync(row, outcome.Item1, outcome.Item2, ct))
            {
                metrics.PaymentReconciled(PaymentStatuses.ToWire(outcome.Item1));
                resolved++;

                logger.LogInformation(
                    "Payment {PaymentId} settled as {Status} against the {Provider} report",
                    row.Id, outcome.Item1, provider.Name);
            }
        }

        return resolved;
    }

    /// <summary>
    /// Finds payments whose recorded outcome the report contradicts.
    /// </summary>
    /// <remarks>
    /// The case worth catching: we called it failed and the provider holds a
    /// charge for it. Nothing is corrected automatically, because a terminal state
    /// a merchant has already been told about is not ours to quietly rewrite.
    /// </remarks>
    private async Task ReportDisagreementsAsync(
        IReadOnlyDictionary<string, ProviderResult> report, DateTimeOffset since, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        var settled = await connection.QueryAsync<(Guid Id, string Status)>(
            new CommandDefinition(ResolvedRecently, new { Since = since }, cancellationToken: ct));

        foreach (var (id, ourStatus) in settled)
        {
            report.TryGetValue(id.ToString(), out var line);

            var theirStatus = line?.Verdict switch
            {
                ProviderVerdict.Authorized => "authorized",
                ProviderVerdict.Declined => "declined",
                null => "absent",
                _ => "unknown"
            };

            var agrees = (ourStatus, theirStatus) switch
            {
                ("authorized" or "captured", "authorized") => true,
                ("failed", "declined" or "absent") => true,
                _ => false
            };

            if (agrees)
            {
                continue;
            }

            await connection.ExecuteAsync(new CommandDefinition(RecordDiscrepancy, new
            {
                PaymentId = id,
                OurStatus = ourStatus,
                ProviderStatus = theirStatus,
                ProviderReference = line?.Reference
            }, cancellationToken: ct));

            metrics.SettlementDiscrepancy(ourStatus, theirStatus);

            logger.LogError(
                "Settlement disagrees about payment {PaymentId}: we say {OurStatus}, {Provider} says {TheirStatus}",
                id, ourStatus, provider.Name, theirStatus);
        }
    }

    private async Task<bool> ApplyAsync(
        PaymentRow row, PaymentStatus next, string? reference, CancellationToken ct)
    {
        var payment = row.ToDomain();
        var transitioned = payment.TransitionTo(next);

        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        if (!await payments.TryApplyTransitionAsync(
                connection, transaction, transitioned, payment.Version, provider.Name, reference, ct))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }

        await payments.AppendToOutboxAsync(connection, transaction, new OutboxMessage(
            AggregateId: transitioned.Id,
            EventType: PaymentResolvedEvent.EventType,
            Payload: JsonSerializer.Serialize(new PaymentResolvedEvent(
                transitioned.Id,
                transitioned.MerchantId,
                transitioned.Amount.MinorUnits,
                transitioned.Amount.Currency,
                PaymentStatuses.ToWire(transitioned.Status),
                provider.Name,
                reference,
                "settled against the provider report",
                transitioned.CreatedAt), Json),
            CorrelationId: $"settlement-{transitioned.Id:N}"), ct);

        await transaction.CommitAsync(ct);
        return true;
    }

    private sealed record PaymentRow(
        Guid Id, Guid MerchantId, string Status, long AmountMinor, string Currency,
        int Version, DateTimeOffset CreatedAt)
    {
        public Payment ToDomain() => new(
            Id, MerchantId, PaymentStatuses.Parse(Status),
            Money.FromStorage(AmountMinor, Currency), Version, CreatedAt);
    }
}
