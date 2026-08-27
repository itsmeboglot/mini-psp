namespace Payments.Core.Domain;

/// <summary>
/// The states a payment can occupy.
/// </summary>
/// <remarks>
/// Never serialised directly. Both the database column and the API contract
/// carry the text produced by <see cref="PaymentStatuses.ToWire"/>, so neither
/// depends on C# member names or on an enum naming policy.
/// </remarks>
public enum PaymentStatus
{
    Created,
    Pending,
    Authorized,
    Captured,
    Failed,
    Expired,

    /// <summary>
    /// A provider call returned no verdict, so the outcome is genuinely
    /// undetermined. Never guessed into <see cref="Failed"/>: it is resolved by
    /// querying the provider or by reconciliation against its report.
    /// </summary>
    Unknown,

    Refunded
}

/// <summary>
/// Translates between <see cref="PaymentStatus"/> and the text stored in
/// payments.status.
/// </summary>
/// <remarks>
/// The mapping is written out rather than derived from member names. The stored
/// values are constrained by a CHECK in the schema, so they are part of the
/// database contract: renaming a C# member must not silently change what is
/// written to a column that existing rows already use.
/// </remarks>
public static class PaymentStatuses
{
    public static string ToWire(PaymentStatus status) => status switch
    {
        PaymentStatus.Created => "created",
        PaymentStatus.Pending => "pending",
        PaymentStatus.Authorized => "authorized",
        PaymentStatus.Captured => "captured",
        PaymentStatus.Failed => "failed",
        PaymentStatus.Expired => "expired",
        PaymentStatus.Unknown => "unknown",
        PaymentStatus.Refunded => "refunded",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped payment status.")
    };

    public static PaymentStatus Parse(string wire) => wire switch
    {
        "created" => PaymentStatus.Created,
        "pending" => PaymentStatus.Pending,
        "authorized" => PaymentStatus.Authorized,
        "captured" => PaymentStatus.Captured,
        "failed" => PaymentStatus.Failed,
        "expired" => PaymentStatus.Expired,
        "unknown" => PaymentStatus.Unknown,
        "refunded" => PaymentStatus.Refunded,
        _ => throw new InvalidDataException($"Unrecognised payment status in storage: '{wire}'.")
    };
}
