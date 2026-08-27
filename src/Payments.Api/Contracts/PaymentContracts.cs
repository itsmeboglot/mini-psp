using System.Text.Json.Serialization;
using Payments.Api.Domain;

namespace Payments.Api.Contracts;

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
    public static PaymentResponse From(Payment payment) => new(
        payment.Id,
        payment.MerchantId,
        payment.Status,
        payment.AmountMinor,
        payment.Currency,
        payment.CreatedAt);
}

[JsonSerializable(typeof(PaymentResponse))]
internal sealed partial class PaymentJsonContext : JsonSerializerContext;
