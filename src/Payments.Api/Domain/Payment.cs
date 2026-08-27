namespace Payments.Api.Domain;

public sealed record Payment(
    Guid Id,
    Guid MerchantId,
    PaymentStatus Status,
    long AmountMinor,
    string Currency,
    int Version,
    DateTimeOffset CreatedAt)
{
    /// <summary>Starts a new payment in <see cref="PaymentStatus.Created"/>.</summary>
    /// <remarks>
    /// Identity is minted here rather than by the database: the caller needs the
    /// id to build a response before the row is committed, and a version 7 UUID
    /// is time ordered, so inserts stay at the right edge of the primary key
    /// index instead of scattering across it.
    /// </remarks>
    public static Payment Create(Guid merchantId, Money amount) => new(
        Id: Guid.CreateVersion7(),
        MerchantId: merchantId,
        Status: PaymentStatus.Created,
        AmountMinor: amount.MinorUnits,
        Currency: amount.Currency,
        Version: 1,
        CreatedAt: DateTimeOffset.UtcNow);

    /// <summary>
    /// Returns this payment in <paramref name="next"/>, or throws if the move is
    /// not in the transition table.
    /// </summary>
    /// <exception cref="InvalidPaymentTransitionException">The move is illegal.</exception>
    public Payment TransitionTo(PaymentStatus next)
        => PaymentTransitions.IsAllowed(Status, next)
            ? this with { Status = next, Version = Version + 1 }
            : throw new InvalidPaymentTransitionException(Id, Status, next);
}
