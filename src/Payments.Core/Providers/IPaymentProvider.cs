namespace Payments.Core.Providers;

/// <summary>What a provider was asked to do.</summary>
/// <param name="IdempotencyKey">
/// Sent to the provider so that a request it may already have seen is not
/// charged again. Derived from the payment, so every attempt at the same payment
/// carries the same key.
/// </param>
public sealed record ProviderCharge(
    Guid PaymentId,
    long AmountMinor,
    string Currency,
    string IdempotencyKey);

public enum ProviderVerdict
{
    /// <summary>The provider approved the charge.</summary>
    Authorized,

    /// <summary>The provider refused it, and said so.</summary>
    Declined,

    /// <summary>
    /// The provider returned no verdict. It is not a failure: the money may or
    /// may not have moved, and the only honest answer is that we do not know.
    /// </summary>
    Unknown,

    /// <summary>
    /// The provider has no record of this charge at all. Different from
    /// <see cref="Unknown"/>, which means it would not say: this means it says
    /// there is nothing to say. Only trustworthy once the provider has had time
    /// to become consistent, which is why it takes several of these before a
    /// payment is called failed.
    /// </summary>
    NotFound
}

/// <param name="Reference">
/// The provider's own id for the charge, when it gave one. Needed later to ask
/// the provider what happened and to match its settlement report.
/// </param>
public sealed record ProviderResult(ProviderVerdict Verdict, string? Reference, string? Reason);

/// <summary>A payment provider this platform can charge through.</summary>
public interface IPaymentProvider
{
    /// <summary>Identifies the provider in stored payments and in routing.</summary>
    string Name { get; }

    Task<ProviderResult> ChargeAsync(ProviderCharge charge, CancellationToken ct);

    /// <summary>
    /// Asks the provider what became of a charge, by the key it was sent under.
    /// </summary>
    /// <remarks>
    /// The way out of <see cref="ProviderVerdict.Unknown"/>. A platform without
    /// this has no answer for a payment whose outcome it never learned, beyond
    /// guessing.
    /// </remarks>
    Task<ProviderResult> GetStatusAsync(string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Everything the provider holds, keyed by the idempotency key each charge
    /// was sent under.
    /// </summary>
    /// <remarks>
    /// The authority. A status endpoint answers about one charge and can be wrong
    /// while it catches up after an outage; a settlement report is the provider's
    /// own account of what it has, and a charge absent from it is a charge that
    /// did not happen. That difference is what makes it safe to call a payment
    /// failed, which no number of status queries ever quite does.
    /// </remarks>
    Task<IReadOnlyDictionary<string, ProviderResult>> GetSettlementAsync(CancellationToken ct);
}
