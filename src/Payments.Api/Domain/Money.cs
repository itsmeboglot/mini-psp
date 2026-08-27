namespace Payments.Api.Domain;

/// <summary>
/// An amount as an integer count of the currency's minor unit, never a floating
/// point type. See docs/adr/0002-money-as-minor-units.md
/// </summary>
public readonly record struct Money
{
    private Money(long minorUnits, string currency)
    {
        MinorUnits = minorUnits;
        Currency = currency;
    }

    public long MinorUnits { get; }

    /// <summary>ISO 4217 alphabetic code, upper case.</summary>
    public string Currency { get; }

    /// <summary>
    /// Creates an amount, or explains why the inputs are not one. Validation
    /// returns a reason rather than throwing: these values come from a request
    /// body, so a bad one is a client error, not an exceptional condition.
    /// </summary>
    public static bool TryCreate(long minorUnits, string? currency, out Money money, out string? error)
    {
        money = default;

        if (minorUnits <= 0)
        {
            error = "amountMinor must be greater than zero.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3
            || !currency.All(char.IsAsciiLetter))
        {
            error = "currency must be a three letter ISO 4217 code.";
            return false;
        }

        money = new Money(minorUnits, currency.ToUpperInvariant());
        error = null;
        return true;
    }
}
