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
    /// Creates an amount, or explains why the inputs are not one.
    /// </summary>
    /// <remarks>
    /// Validation returns a reason rather than throwing: these values arrive in a
    /// request body, so a bad one is a client error, not an exceptional condition.
    ///
    /// Sign is deliberately not checked here. "Greater than zero" is a rule about
    /// a payment request, not about money: a zero amount authorisation is a real
    /// operation, and a double entry ledger needs entries on both sides. Callers
    /// that require a positive amount say so themselves.
    /// </remarks>
    public static bool TryCreate(long minorUnits, string? currency, out Money money, out string? error)
    {
        money = default;

        if (!IsIsoAlphabeticCode(currency))
        {
            error = "currency must be a three letter ISO 4217 code.";
            return false;
        }

        money = new Money(minorUnits, currency!.ToUpperInvariant());
        error = null;
        return true;
    }

    /// <summary>
    /// Rebuilds an amount from a row that was validated when it was written.
    /// </summary>
    /// <remarks>
    /// Revalidated anyway: a failure here means the stored data is corrupt, and a
    /// payment platform must not carry on with an amount it cannot account for.
    /// </remarks>
    public static Money FromStorage(long minorUnits, string currency)
        => TryCreate(minorUnits, currency, out var money, out var error)
            ? money
            : throw new InvalidDataException($"Stored amount is not valid money: {error}");

    /// <remarks>
    /// A loop over the span rather than <c>All(char.IsAsciiLetter)</c>: the LINQ
    /// form allocates an enumerator on every request to inspect three characters.
    /// </remarks>
    private static bool IsIsoAlphabeticCode(string? candidate)
    {
        if (candidate is not { Length: 3 })
        {
            return false;
        }

        foreach (var character in candidate.AsSpan())
        {
            if (!char.IsAsciiLetter(character))
            {
                return false;
            }
        }

        return true;
    }
}
