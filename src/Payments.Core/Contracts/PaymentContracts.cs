using Payments.Core.Domain;

namespace Payments.Core.Contracts;

/// <param name="MerchantId">The merchant the payment belongs to.</param>
/// <param name="AmountMinor">
/// Amount in the currency's minor unit: 12.34 USD is 1234. Integer, never a
/// decimal or a float.
/// </param>
/// <param name="Currency">ISO 4217 alphabetic code, for example "USD".</param>
public sealed record CreatePaymentRequest(
    Guid MerchantId,
    long AmountMinor,
    string Currency);

public sealed record PaymentResponse(
    Guid Id,
    Guid MerchantId,
    string Status,
    long AmountMinor,
    string Currency,
    DateTimeOffset CreatedAt)
{
    /// <remarks>
    /// The status goes out as the same text the database stores, produced by the
    /// one mapping in <see cref="PaymentStatuses"/>. Serialising the enum
    /// directly would make the public contract depend on C# member names and on
    /// whatever enum naming policy happened to be configured.
    /// </remarks>
    public static PaymentResponse From(Payment payment) => new(
        payment.Id,
        payment.MerchantId,
        PaymentStatuses.ToWire(payment.Status),
        payment.Amount.MinorUnits,
        payment.Amount.Currency,
        payment.CreatedAt);
}
