using Dapper;
using Npgsql;
using Payments.Core.Domain;

namespace Payments.Core.Persistence;

/// <param name="AccountId">
/// A natural key: "merchant:&lt;uuid&gt;", "provider:&lt;name&gt;", "fees". Derivable
/// from what is being posted, so an entry never needs a lookup to find its
/// account.
/// </param>
/// <param name="Amount">
/// Signed. Negative takes from the account, positive gives to it. The entries of
/// one transaction sum to zero.
/// </param>
public sealed record LedgerEntry(string AccountId, string AccountKind, Money Amount);

public enum PostOutcome
{
    Posted,

    /// <summary>This payment already had a transaction of this kind. Nothing changed.</summary>
    AlreadyPosted
}

/// <summary>
/// Writes movements of money into the ledger.
/// </summary>
/// <remarks>
/// Every posting happens inside a caller's transaction, alongside whatever change
/// caused it. A ledger written separately from the thing it records is a ledger
/// that can disagree with it.
/// </remarks>
public sealed class LedgerStore(ILogger<LedgerStore> logger)
{
    private const string TransactionOnceConstraint = "ledger_transactions_once";

    private const string EnsureAccount = """
        INSERT INTO ledger_accounts (id, kind, currency)
        VALUES (@Id, @Kind, @Currency)
        ON CONFLICT (id) DO NOTHING;
        """;

    private const string InsertTransaction = """
        INSERT INTO ledger_transactions (id, payment_id, kind)
        VALUES (@Id, @PaymentId, @Kind);
        """;

    private const string InsertEntry = """
        INSERT INTO ledger_entries (transaction_id, account_id, amount_minor, currency)
        VALUES (@TransactionId, @AccountId, @AmountMinor, @Currency);
        """;

    private const string SumAccount = """
        SELECT COALESCE(sum(amount_minor), 0) FROM ledger_entries WHERE account_id = @AccountId;
        """;

    /// <summary>
    /// Posts one movement of money.
    /// </summary>
    /// <remarks>
    /// The entries are not checked here for summing to zero. The database refuses
    /// to commit an unbalanced transaction, so a bug in this method produces a
    /// failed write rather than invented money, which is the right way round.
    /// </remarks>
    public async Task<PostOutcome> PostAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid paymentId,
        string kind,
        IReadOnlyList<LedgerEntry> entries,
        CancellationToken ct)
    {
        var transactionId = Guid.CreateVersion7();

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(InsertTransaction,
                new { Id = transactionId, PaymentId = paymentId, Kind = kind },
                transaction, cancellationToken: ct));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation
                                          && e.ConstraintName == TransactionOnceConstraint)
        {
            // A redelivered event. The money has already moved once, which is all
            // it was ever meant to do.
            logger.LogInformation("Payment {PaymentId} was already posted as {Kind}", paymentId, kind);
            return PostOutcome.AlreadyPosted;
        }

        foreach (var entry in entries)
        {
            await connection.ExecuteAsync(new CommandDefinition(EnsureAccount, new
            {
                Id = entry.AccountId,
                Kind = entry.AccountKind,
                Currency = entry.Amount.Currency
            }, transaction, cancellationToken: ct));

            await connection.ExecuteAsync(new CommandDefinition(InsertEntry, new
            {
                TransactionId = transactionId,
                AccountId = entry.AccountId,
                AmountMinor = entry.Amount.MinorUnits,
                Currency = entry.Amount.Currency
            }, transaction, cancellationToken: ct));
        }

        logger.LogInformation(
            "Posted {Kind} for payment {PaymentId} as {EntryCount} entries",
            kind, paymentId, entries.Count);

        return PostOutcome.Posted;
    }

    /// <summary>
    /// Derives an account's balance by summing its entries.
    /// </summary>
    /// <remarks>
    /// Not read from a stored column, because there is none. At a size where this
    /// sum is too slow the answer is periodic snapshots plus the entries since,
    /// not a running total that can drift away from its own history.
    /// </remarks>
    public async Task<long> BalanceAsync(NpgsqlConnection connection, string accountId, CancellationToken ct)
        => await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(SumAccount, new { AccountId = accountId }, cancellationToken: ct));
}
