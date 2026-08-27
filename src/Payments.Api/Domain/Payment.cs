namespace Payments.Api.Domain;

public sealed record Payment(
    Guid Id,
    Guid MerchantId,
    PaymentStatus Status,
    Money Amount,
    int Version,
    DateTimeOffset CreatedAt)
{
    /// <summary>Starts a new payment in <see cref="PaymentStatus.Created"/>.</summary>
    /// <remarks>
    /// Identity is minted here rather than by the database: the caller needs the
    /// id to build a response before the row is committed, and a version 7 UUID
    /// is time ordered, so inserts stay at the right edge of the primary key
    /// index instead of scattering across it.
    ///
    /// The clock is a parameter because time drives real behaviour here, not just
    /// a display field: authorisation deadlines, expiry sweeps and reconciliation
    /// windows all read it. One clock feeds both the timestamp and the id, so the
    /// two can never disagree.
    /// </remarks>
    public static Payment Create(Guid merchantId, Money amount, TimeProvider clock)
    {
        var now = clock.GetUtcNow();

        return new Payment(
            Id: Guid.CreateVersion7(now),
            MerchantId: merchantId,
            Status: PaymentStatus.Created,
            Amount: amount,
            Version: 1,
            CreatedAt: now);
    }

    /// <summary>
    /// Returns this payment in <paramref name="next"/>, or throws if the move is
    /// not in the transition rules.
    /// </summary>
    /// <exception cref="InvalidPaymentTransitionException">The move is illegal.</exception>
    public Payment TransitionTo(PaymentStatus next)
        => PaymentTransitions.IsAllowed(Status, next)
            ? this with { Status = next, Version = Version + 1 }
            : throw new InvalidPaymentTransitionException(Id, Status, next);
}
