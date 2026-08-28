namespace Payments.Core.Domain;

/// <summary>
/// Splits a captured amount between the merchant and the platform.
/// </summary>
/// <remarks>
/// The fee is computed first and the merchant gets the remainder, rather than
/// both being computed and hoped to add up. Any rounding lands in exactly one
/// place, and the two parts sum to the original by construction rather than by
/// luck: there is no arrangement of inputs where a fraction of a minor unit goes
/// missing or is invented.
/// </remarks>
public static class Settlement
{
    /// <param name="feeBasisPoints">Hundredths of a percent. 250 is 2.5%.</param>
    public static (Money Merchant, Money Fee) Split(Money captured, int feeBasisPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(feeBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(feeBasisPoints, 10_000);

        // Integer arithmetic throughout: a percentage of an integer number of
        // minor units is not generally an integer, and doing this in floating
        // point is how a platform loses a cent per transaction.
        var fee = (long)Math.Round(
            captured.MinorUnits * (decimal)feeBasisPoints / 10_000m,
            MidpointRounding.AwayFromZero);

        var merchant = captured.MinorUnits - fee;

        return (Amount(merchant, captured.Currency), Amount(fee, captured.Currency));
    }

    private static Money Amount(long minorUnits, string currency)
        => Money.TryCreate(minorUnits, currency, out var money, out var error)
            ? money
            : throw new InvalidOperationException($"Settlement produced an invalid amount: {error}");
}
