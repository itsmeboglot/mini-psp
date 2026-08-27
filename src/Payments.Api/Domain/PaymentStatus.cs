namespace Payments.Api.Domain;

/// <summary>
/// Payment states. The values match the CHECK constraint on payments.status, so
/// a typo here fails at the database rather than silently storing garbage.
/// </summary>
public static class PaymentStatus
{
    public const string Created = "created";
    public const string Pending = "pending";
    public const string Authorized = "authorized";
    public const string Captured = "captured";
    public const string Failed = "failed";
    public const string Expired = "expired";

    /// <summary>
    /// The provider did not return a verdict, so the outcome is genuinely
    /// undetermined. Never guessed into <see cref="Failed"/>; resolved by
    /// querying the provider or by reconciliation.
    /// </summary>
    public const string Unknown = "unknown";

    public const string Refunded = "refunded";
}
