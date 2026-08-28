using System.Text.Json;
using Microsoft.Extensions.Options;
using Payments.Core.Contracts;
using Payments.Core.Domain;
using Payments.Core.Persistence;
using Payments.Core.Providers;

namespace Payments.Worker;

/// <summary>
/// Asks a provider what became of payments whose outcome was never learned, and
/// records the answers.
/// </summary>
/// <remarks>
/// Separated from the service that schedules it for the same reason the outbox
/// store is separate from its dispatcher: the decisions here are worth testing
/// without waiting on a timer.
/// </remarks>
public sealed class PaymentReconciler(
    PaymentStore payments,
    DbConnectionFactory db,
    IPaymentProvider provider,
    IOptions<ReconciliationOptions> options,
    ILogger<PaymentReconciler> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ReconciliationOptions _options = options.Value;

    /// <returns>How many payments this sweep resolved.</returns>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var unresolved = await payments.ClaimUnresolvedAsync(
            _options.BatchSize,
            TimeSpan.FromSeconds(_options.GraceSeconds),
            TimeSpan.FromSeconds(_options.RetryAfterSeconds),
            ct);

        var resolved = 0;

        foreach (var candidate in unresolved)
        {
            if (await ResolveAsync(candidate, ct))
            {
                resolved++;
            }
        }

        return resolved;
    }

    private async Task<bool> ResolveAsync(UnresolvedPayment candidate, CancellationToken ct)
    {
        var payment = candidate.Payment;
        var status = await provider.GetStatusAsync(payment.Id.ToString(), ct);

        var next = status.Verdict switch
        {
            ProviderVerdict.Authorized => PaymentStatus.Authorized,
            ProviderVerdict.Declined => PaymentStatus.Failed,

            // Believed only once the provider has denied it enough times. Status
            // APIs go briefly inconsistent after exactly the sort of outage that
            // produced this unknown, and one denial in that window proves nothing.
            ProviderVerdict.NotFound when candidate.Attempts >= _options.AttemptsBeforeBelievingNotFound
                => PaymentStatus.Failed,

            // Nothing conclusive. Leave it be and ask again; the claim already
            // recorded that it was tried.
            _ => (PaymentStatus?)null
        };

        if (next is null)
        {
            logger.LogInformation(
                "Payment {PaymentId} still unresolved after {Attempts} attempt(s): {Verdict}",
                payment.Id, candidate.Attempts, status.Verdict);

            return false;
        }

        var transitioned = payment.TransitionTo(next.Value);

        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        if (!await payments.TryApplyTransitionAsync(
                connection, transaction, transitioned, payment.Version, provider.Name, status.Reference, ct))
        {
            // The original charge answered late, or another instance got there
            // first. Either way somebody knows more than this sweep did.
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
                status.Reference,
                $"reconciled after {candidate.Attempts} attempt(s): {status.Reason}",
                transitioned.CreatedAt), Json)), ct);

        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Payment {PaymentId} reconciled to {Status} on attempt {Attempts}",
            payment.Id, next, candidate.Attempts);

        return true;
    }
}
