using System.Text.Json;
using Payments.Core.Contracts;
using Payments.Core.Domain;
using Payments.Core.Persistence;
using Payments.Core.Providers;

namespace Payments.Worker;

/// <summary>
/// Charges a pending payment through a provider and records what came back.
/// </summary>
/// <remarks>
/// The provider call deliberately sits outside any transaction. Holding one open
/// across a call to a third party means holding row locks for as long as they
/// take to answer, and they are exactly the party most likely to take a long
/// time. The payment is already recorded as pending before this runs, so a crash
/// mid-call leaves a payment that says "a charge may be in flight", which
/// reconciliation can resolve. Nothing is lost; only certainty is.
/// </remarks>
public sealed class PaymentPendingHandler(
    PaymentStore payments,
    DbConnectionFactory db,
    IPaymentProvider provider,
    ILogger<PaymentPendingHandler> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task HandleAsync(string payload, string? correlationId, CancellationToken ct)
    {
        var @event = JsonSerializer.Deserialize<PaymentPendingEvent>(payload, Json)
            ?? throw new InvalidDataException("payment.pending.v1 payload was empty.");

        var payment = await payments.GetAsync(@event.PaymentId, ct);

        if (payment is null || payment.Status != PaymentStatus.Pending)
        {
            // Already resolved, by a redelivery of this event or by
            // reconciliation. Charging again would be the mistake.
            logger.LogInformation(
                "Payment {PaymentId} is {Status}, not awaiting a provider",
                @event.PaymentId, payment?.Status.ToString() ?? "missing");
            return;
        }

        var result = await provider.ChargeAsync(new ProviderCharge(
            PaymentId: payment.Id,
            AmountMinor: payment.Amount.MinorUnits,
            Currency: payment.Amount.Currency,

            // Derived from the payment, so every attempt at this payment carries
            // the same key and the provider can recognise a repeat.
            IdempotencyKey: payment.Id.ToString()), ct);

        var next = result.Verdict switch
        {
            ProviderVerdict.Authorized => PaymentStatus.Authorized,
            ProviderVerdict.Declined => PaymentStatus.Failed,

            // Never Failed. We do not know, and saying we do would be a lie about
            // someone's money.
            _ => PaymentStatus.Unknown
        };

        await RecordAsync(payment, next, result, correlationId, ct);

        logger.LogInformation(
            "Payment {PaymentId} is {Status} after {Provider} said {Verdict}{Reason}",
            payment.Id, next, provider.Name, result.Verdict,
            result.Reason is null ? "" : $" ({result.Reason})");
    }

    private async Task RecordAsync(
        Payment payment, PaymentStatus next, ProviderResult result, string? correlationId, CancellationToken ct)
    {
        var transitioned = payment.TransitionTo(next);

        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        if (!await payments.TryApplyTransitionAsync(
                connection, transaction, transitioned, payment.Version, provider.Name, result.Reference, ct))
        {
            // Something else resolved this payment while the provider was
            // deciding, most likely reconciliation. Its answer is at least as good
            // as ours, so leave it alone.
            await transaction.RollbackAsync(CancellationToken.None);
            logger.LogWarning("Payment {PaymentId} was resolved by someone else", payment.Id);
            return;
        }

        await payments.AppendToOutboxAsync(connection, transaction, new OutboxMessage(
            AggregateId: transitioned.Id,
            EventType: PaymentResolvedEvent.EventType,
            CorrelationId: correlationId,
            Payload: JsonSerializer.Serialize(new PaymentResolvedEvent(
                transitioned.Id,
                transitioned.MerchantId,
                transitioned.Amount.MinorUnits,
                transitioned.Amount.Currency,
                PaymentStatuses.ToWire(transitioned.Status),
                provider.Name,
                result.Reference,
                result.Reason,
                transitioned.CreatedAt), Json)), ct);

        await transaction.CommitAsync(ct);
    }
}
