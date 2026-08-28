using System.Reflection;
using Payments.Core.Contracts;
using Payments.Core.Persistence;

namespace Payments.Tests;

/// <summary>
/// The fingerprint decides whether two requests carrying one idempotency key mean
/// the same thing. A field it fails to cover is a money bug: two genuinely
/// different requests would share a hash, and the second would be answered with
/// the first one's response.
/// </summary>
public sealed class RequestHashTests
{
    private static CreatePaymentRequest Request() => new(
        MerchantId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        AmountMinor: 1234,
        Currency: "USD");

    /// <summary>
    /// Fails when <see cref="CreatePaymentRequest"/> gains a property, which is
    /// the moment someone has to decide whether it belongs in the fingerprint.
    /// Nothing else in the suite would notice.
    /// </summary>
    [Fact]
    public void Every_property_of_the_request_is_accounted_for()
    {
        string[] covered =
        [
            nameof(CreatePaymentRequest.MerchantId),
            nameof(CreatePaymentRequest.AmountMinor),
            nameof(CreatePaymentRequest.Currency)
        ];

        var actual = typeof(CreatePaymentRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order()
            .ToArray();

        Assert.Equal(covered.Order().ToArray(), actual);
    }

    [Fact]
    public void The_same_request_always_hashes_the_same()
        => Assert.Equal(RequestHash.Of(Request()), RequestHash.Of(Request()));

    [Fact]
    public void A_different_merchant_changes_the_hash()
        => Assert.NotEqual(
            RequestHash.Of(Request()),
            RequestHash.Of(Request() with { MerchantId = Guid.NewGuid() }));

    [Fact]
    public void A_different_amount_changes_the_hash()
        => Assert.NotEqual(
            RequestHash.Of(Request()),
            RequestHash.Of(Request() with { AmountMinor = 1235 }));

    [Fact]
    public void A_different_currency_changes_the_hash()
        => Assert.NotEqual(
            RequestHash.Of(Request()),
            RequestHash.Of(Request() with { Currency = "EUR" }));

    /// <summary>
    /// Case is normalised on the way in, so "usd" and "USD" are the same intent
    /// and must not be treated as a reused key with a different body.
    /// </summary>
    [Fact]
    public void Currency_case_does_not_change_the_hash()
        => Assert.Equal(
            RequestHash.Of(Request()),
            RequestHash.Of(Request() with { Currency = "usd" }));

    /// <summary>
    /// A field boundary that a naive concatenation would blur: 1|23 and 12|3 must
    /// not collide.
    /// </summary>
    [Fact]
    public void Field_boundaries_are_not_ambiguous()
    {
        var a = Request() with { MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111"), AmountMinor = 1 };
        var b = Request() with { MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111"), AmountMinor = 12 };

        Assert.NotEqual(RequestHash.Of(a), RequestHash.Of(b));
    }
}
