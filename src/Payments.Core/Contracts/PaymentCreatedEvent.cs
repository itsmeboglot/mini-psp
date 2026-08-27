namespace Payments.Core.Contracts;

/// <summary>
/// Announces that a payment now exists. Published to other services once the
/// transaction that created it has committed.
/// </summary>
/// <remarks>
/// This is an integration contract, not an internal one: other services and
/// other teams deserialise it, so it changes only in ways they survive. The
/// version is part of the event type rather than a field, so a consumer
/// subscribes to a shape rather than to a name and a runtime check.
/// </remarks>
public sealed record PaymentCreatedEvent(
    Guid PaymentId,
    Guid MerchantId,
    long AmountMinor,
    string Currency,
    string Status,
    DateTimeOffset OccurredAt)
{
    public const string EventType = "payment.created.v1";
}

/// <summary>
/// Announces that a payment has been handed on for processing.
/// </summary>
/// <remarks>
/// Shares the shape of <see cref="PaymentCreatedEvent"/> because consumers of
/// either want the same facts. They stay separate types so that adding a field
/// one of them needs does not silently change the other.
/// </remarks>
public sealed record PaymentPendingEvent(
    Guid PaymentId,
    Guid MerchantId,
    long AmountMinor,
    string Currency,
    string Status,
    DateTimeOffset OccurredAt)
{
    public const string EventType = "payment.pending.v1";
}
