using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Payments.Core.Contracts;
using Payments.Core.Domain;
using Payments.Core.Persistence;

namespace Payments.Worker;

public sealed class LedgerOptions
{
    public const string Section = "Ledger";

    /// <summary>Hundredths of a percent taken from each captured payment. 250 is 2.5%.</summary>
    public int FeeBasisPoints { get; set; } = 250;
}

/// <summary>
/// Records the money movement behind an authorised payment.
/// </summary>
/// <remarks>
/// Driven by an event rather than called from the two places that authorise a
/// payment. Both the provider handler and reconciliation can reach authorized,
/// and posting from each would be the same rule written twice, in two
/// transactions, with two chances to drift.
/// </remarks>
public sealed class PaymentResolvedHandler(
    LedgerStore ledger,
    IOptions<LedgerOptions> options,
    ILogger<PaymentResolvedHandler> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> HandleAsync(
        string payload,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        var @event = JsonSerializer.Deserialize<PaymentResolvedEvent>(payload, Json)
            ?? throw new InvalidDataException("payment.resolved.v1 payload was empty.");

        if (@event.Status != PaymentStatuses.ToWire(PaymentStatus.Authorized))
        {
            // Declined, expired and unknown move no money, so there is nothing to
            // record. Only an authorised payment has a movement to describe.
            logger.LogDebug("Payment {PaymentId} resolved as {Status}; nothing to post",
                @event.PaymentId, @event.Status);

            return true;
        }

        if (!Money.TryCreate(@event.AmountMinor, @event.Currency, out var captured, out var error))
        {
            throw new InvalidDataException($"payment.resolved.v1 carried an invalid amount: {error}");
        }

        var (merchant, fee) = Settlement.Split(captured, options.Value.FeeBasisPoints);

        // The provider is holding the full amount on our behalf, so that account
        // gains it. What we owe the merchant and what we keep as fees are both
        // claims against it, so they take from it. The three sum to zero because
        // nothing was created, only moved.
        var entries = new List<LedgerEntry>
        {
            new($"provider:{@event.Provider}", "provider", captured),
            new($"merchant:{@event.MerchantId}", "merchant", Negate(merchant)),
            new("fees", "fees", Negate(fee))
        };

        // A zero fee would be an entry that moves nothing, which the schema
        // refuses. Small amounts and a low rate can produce exactly that.
        entries.RemoveAll(entry => entry.Amount.MinorUnits == 0);

        var outcome = await ledger.PostAsync(
            connection, transaction, @event.PaymentId, "capture", entries, ct);

        logger.LogInformation(
            "Payment {PaymentId}: {Outcome} {Captured} {Currency}, merchant {Merchant}, fee {Fee}",
            @event.PaymentId, outcome, captured.MinorUnits, captured.Currency,
            merchant.MinorUnits, fee.MinorUnits);

        return true;
    }

    private static Money Negate(Money amount)
        => Money.TryCreate(-amount.MinorUnits, amount.Currency, out var negated, out _)
            ? negated
            : throw new InvalidOperationException("Money refused a sign it is supposed to allow.");
}
