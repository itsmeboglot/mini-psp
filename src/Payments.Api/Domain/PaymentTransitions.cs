namespace Payments.Api.Domain;

/// <summary>
/// The only state changes a payment may undergo.
/// </summary>
/// <remarks>
/// The whole lifecycle is one switch, so it reads top to bottom and the compiler
/// checks every status name in it. A status the switch does not mention throws
/// rather than quietly answering "no": a forgotten case is a bug, and a bug that
/// returns a plausible answer is worse than one that stops.
/// </remarks>
public static class PaymentTransitions
{
    private static readonly PaymentStatus[] AllStatuses = Enum.GetValues<PaymentStatus>();

    public static bool IsAllowed(PaymentStatus from, PaymentStatus to) => from switch
    {
        PaymentStatus.Created =>
            to is PaymentStatus.Pending or PaymentStatus.Failed,

        PaymentStatus.Pending =>
            to is PaymentStatus.Authorized or PaymentStatus.Failed
               or PaymentStatus.Expired or PaymentStatus.Unknown,

        PaymentStatus.Authorized =>
            to is PaymentStatus.Captured or PaymentStatus.Failed,

        PaymentStatus.Captured =>
            to is PaymentStatus.Refunded,

        // Resolved by a status query or by reconciliation, which can discover any
        // real outcome the provider reached, including that the money was taken.
        // What it may never do is decide on its own that the payment failed, which
        // is why Unknown is not terminal.
        PaymentStatus.Unknown =>
            to is PaymentStatus.Authorized or PaymentStatus.Captured
               or PaymentStatus.Failed or PaymentStatus.Expired,

        PaymentStatus.Failed or PaymentStatus.Expired or PaymentStatus.Refunded =>
            false,

        _ => throw new ArgumentOutOfRangeException(
            nameof(from), from, "Payment status is missing from the transition rules.")
    };

    /// <summary>
    /// True when no transition out of <paramref name="status"/> exists.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="IsAllowed"/> rather than listed separately, so the
    /// two cannot disagree about which states are final.
    /// </remarks>
    public static bool IsTerminal(PaymentStatus status)
    {
        foreach (var to in AllStatuses)
        {
            if (IsAllowed(status, to))
            {
                return false;
            }
        }

        return true;
    }
}
