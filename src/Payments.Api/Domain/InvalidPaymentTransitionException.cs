namespace Payments.Api.Domain;

/// <summary>
/// Thrown when a state change is not permitted by <see cref="PaymentTransitions"/>.
/// </summary>
/// <remarks>
/// An exception rather than a returned failure: request data is validated before
/// it reaches the domain, so by this point an illegal transition means the
/// platform's own logic is wrong. That is not a condition a caller should be
/// invited to handle and continue past.
/// </remarks>
public sealed class InvalidPaymentTransitionException(Guid paymentId, PaymentStatus from, PaymentStatus to)
    : InvalidOperationException($"Payment {paymentId} cannot move from {from} to {to}.")
{
    public Guid PaymentId { get; } = paymentId;

    public PaymentStatus From { get; } = from;

    public PaymentStatus To { get; } = to;
}
