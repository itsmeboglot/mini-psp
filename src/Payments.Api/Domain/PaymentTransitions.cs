using System.Collections.Frozen;

namespace Payments.Api.Domain;

/// <summary>
/// The only state changes a payment may undergo.
/// </summary>
/// <remarks>
/// Kept as data rather than scattered conditionals so that the whole lifecycle
/// is readable in one place and testable without a database. Anything absent
/// from this table is illegal by construction.
/// </remarks>
public static class PaymentTransitions
{
    private static readonly FrozenDictionary<PaymentStatus, PaymentStatus[]> Allowed =
        new Dictionary<PaymentStatus, PaymentStatus[]>
        {
            [PaymentStatus.Created] = [PaymentStatus.Pending, PaymentStatus.Failed],

            [PaymentStatus.Pending] =
            [
                PaymentStatus.Authorized,
                PaymentStatus.Failed,
                PaymentStatus.Expired,
                PaymentStatus.Unknown
            ],

            [PaymentStatus.Authorized] = [PaymentStatus.Captured, PaymentStatus.Failed],

            [PaymentStatus.Captured] = [PaymentStatus.Refunded],

            // Resolved by a status query or by reconciliation, which can discover
            // any real outcome the provider reached — including that the money was
            // taken. What it may never do is decide on its own that the payment
            // failed, which is why Unknown is not a terminal state.
            [PaymentStatus.Unknown] =
            [
                PaymentStatus.Authorized,
                PaymentStatus.Captured,
                PaymentStatus.Failed,
                PaymentStatus.Expired
            ],

            // Terminal.
            [PaymentStatus.Failed] = [],
            [PaymentStatus.Expired] = [],
            [PaymentStatus.Refunded] = []
        }.ToFrozenDictionary();

    public static bool IsAllowed(PaymentStatus from, PaymentStatus to)
        => Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static bool IsTerminal(PaymentStatus status)
        => Allowed.TryGetValue(status, out var targets) && targets.Length == 0;
}
