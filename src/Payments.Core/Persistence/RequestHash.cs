using System.Security.Cryptography;
using System.Text;
using Payments.Core.Contracts;

namespace Payments.Core.Persistence;

/// <summary>
/// Fingerprints the meaningful content of a create request so that the same
/// idempotency key sent with different intent can be detected and rejected.
/// </summary>
/// <remarks>
/// Hashing the parsed fields in a fixed order sidesteps JSON canonicalisation:
/// whitespace and property order cannot change the result, and neither can a
/// field the API does not read. A platform that must detect any body change,
/// including unknown fields, would hash the canonicalised raw body instead.
/// </remarks>
public static class RequestHash
{
    public static string Of(CreatePaymentRequest request)
    {
        var canonical = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{request.MerchantId:N}|{request.AmountMinor}|{request.Currency.ToUpperInvariant()}");

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
