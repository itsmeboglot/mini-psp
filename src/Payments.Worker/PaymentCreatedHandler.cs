using System.Text.Json;
using Npgsql;
using Payments.Core.Contracts;
using Payments.Core.Domain;
using Payments.Core.Persistence;

namespace Payments.Worker;

/// <summary>
/// Takes a newly created payment and hands it on for processing.
/// </summary>
/// <remarks>
/// Today that means moving it from created to pending, which is the state a
/// payment sits in while a provider decides. When the connectors exist this is
/// where the provider call goes, between reading the payment and writing the new
/// state; the transaction shape does not change.
/// </remarks>
public sealed class PaymentCreatedHandler(PaymentStore payments, ILogger<PaymentCreatedHandler> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> HandleAsync(
        string payload,
        string? correlationId,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        var @event = JsonSerializer.Deserialize<PaymentCreatedEvent>(payload, Json)
            ?? throw new InvalidDataException("payment.created.v1 payload was empty.");

        var payment = await payments.GetAsync(connection, transaction, @event.PaymentId, ct);

        if (payment is null)
        {
            // Cannot happen while the event and the payment commit together, and
            // worth shouting about if it ever does: it would mean the outbox and
            // the payments table disagree.
            logger.LogError("Event names payment {PaymentId}, which does not exist", @event.PaymentId);
            return false;
        }

        if (payment.Status != PaymentStatus.Created)
        {
            // A redelivery that arrived after something else already moved this
            // payment on. Not an error, and not work to redo.
            logger.LogInformation(
                "Payment {PaymentId} is already {Status}; nothing to do", payment.Id, payment.Status);
            return false;
        }

        var pending = payment.TransitionTo(PaymentStatus.Pending);

        if (!await payments.TryApplyTransitionAsync(connection, transaction, pending, payment.Version, ct: ct))
        {
            // Someone moved it between the read and the write. Decline, so the
            // event is not recorded as processed and can be delivered again.
            logger.LogWarning("Payment {PaymentId} changed underneath this handler", payment.Id);
            return false;
        }

        await payments.AppendToOutboxAsync(connection, transaction, new OutboxMessage(
            AggregateId: pending.Id,
            EventType: PaymentPendingEvent.EventType,
            CorrelationId: correlationId,
            Payload: JsonSerializer.Serialize(new PaymentPendingEvent(
                pending.Id,
                pending.MerchantId,
                pending.Amount.MinorUnits,
                pending.Amount.Currency,
                PaymentStatuses.ToWire(pending.Status),
                pending.CreatedAt), Json)), ct);

        logger.LogInformation("Payment {PaymentId} moved to {Status}", pending.Id, pending.Status);
        return true;
    }
}
