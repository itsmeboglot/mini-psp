namespace Payments.Api.Domain;

public sealed record Payment(
    Guid Id,
    Guid MerchantId,
    string Status,
    long AmountMinor,
    string Currency,
    int Version,
    DateTimeOffset CreatedAt);
