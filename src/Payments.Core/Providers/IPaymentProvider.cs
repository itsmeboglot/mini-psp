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
    Unknown
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
}
