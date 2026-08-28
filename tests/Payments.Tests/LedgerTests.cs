using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Payments.Core.Contracts;
using Payments.Core.Domain;
using Payments.Core.Persistence;
using Payments.Worker;

namespace Payments.Tests;

/// <summary>
/// Money is moved, never created.
/// </summary>
/// <remarks>
/// Assertions are about the entries of one payment's transaction, not about
/// account balances. "fees" is a single account for the whole platform and the
/// provider account is shared too, so their balances accumulate across every test
/// in this class. Only the merchant account is per test, and that is the one
/// balance worth reading as a balance.
/// </remarks>
public sealed class LedgerTests(PaymentsApiFixture fixture) : IClassFixture<PaymentsApiFixture>
{
    private const int FeeBasisPoints = 250;

    [Fact]
    public async Task An_authorised_payment_splits_between_the_merchant_and_the_fee_account()
    {
        var (paymentId, merchantId) = await AuthorisedPaymentAsync(10_000, "USD");

        await PostAsync(paymentId, merchantId, 10_000, "USD");

        var entries = await EntriesForAsync(paymentId);

        // The provider holds the full amount for us; what we owe the merchant and
        // what we keep are claims against it.
        Assert.Equal(10_000, entries["provider:stub"]);
        Assert.Equal(-9_750, entries[$"merchant:{merchantId}"]);
        Assert.Equal(-250, entries["fees"]);
        Assert.Equal(0, entries.Values.Sum());

        // The merchant account is this test's alone, so its balance is readable.
        await using var connection = await OpenAsync();
        Assert.Equal(-9_750, await new LedgerStore(NullLogger<LedgerStore>.Instance)
            .BalanceAsync(connection, $"merchant:{merchantId}", CancellationToken.None));
    }

    /// <summary>The entries of one payment's transaction, by account.</summary>
    private async Task<Dictionary<string, long>> EntriesForAsync(Guid paymentId)
    {
        await using var connection = await OpenAsync();

        var rows = await connection.QueryAsync<(string AccountId, long AmountMinor)>(
            """
            SELECT e.account_id, e.amount_minor
            FROM ledger_entries e
            JOIN ledger_transactions t ON t.id = e.transaction_id
            WHERE t.payment_id = @paymentId
            """, new { paymentId });

        return rows.ToDictionary(row => row.AccountId, row => row.AmountMinor);
    }

    /// <summary>
    /// The invariant, across everything this class has done to the database.
    /// </summary>
    [Fact]
    public async Task Every_transaction_sums_to_zero()
    {
        var (paymentId, merchantId) = await AuthorisedPaymentAsync(7_777, "EUR");
        await PostAsync(paymentId, merchantId, 7_777, "EUR");

        await using var connection = await OpenAsync();

        var unbalanced = await connection.ExecuteScalarAsync<long>(
            """
            SELECT count(*) FROM (
                SELECT transaction_id FROM ledger_entries
                GROUP BY transaction_id HAVING sum(amount_minor) <> 0
            ) AS bad
            """);

        Assert.Equal(0, unbalanced);
    }

    /// <summary>
    /// The database refuses to record money that came from nowhere, whatever the
    /// application believes.
    /// </summary>
    [Fact]
    public async Task The_database_rejects_a_transaction_that_does_not_balance()
    {
        var (paymentId, merchantId) = await AuthorisedPaymentAsync(500, "USD");

        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await new LedgerStore(NullLogger<LedgerStore>.Instance).PostAsync(
                connection, transaction, paymentId, "capture",
                [
                    new LedgerEntry("provider:stub", "provider", Amount(500, "USD")),
                    // One cent short. Nothing in the application notices.
                    new LedgerEntry($"merchant:{merchantId}", "merchant", Amount(-499, "USD"))
                ],
                CancellationToken.None);

            // The trigger is deferred, so it fires here rather than on insert.
            await transaction.CommitAsync();
        });

        Assert.Contains("does not balance", error.Message);
    }

    [Fact]
    public async Task A_transaction_cannot_mix_currencies()
    {
        var (paymentId, merchantId) = await AuthorisedPaymentAsync(300, "USD");

        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await new LedgerStore(NullLogger<LedgerStore>.Instance).PostAsync(
                connection, transaction, paymentId, "capture",
                [
                    new LedgerEntry("provider:stub", "provider", Amount(300, "USD")),
                    new LedgerEntry($"merchant:{merchantId}", "merchant", Amount(-300, "EUR"))
                ],
                CancellationToken.None);

            await transaction.CommitAsync();
        });

        Assert.Contains("mixes", error.Message);
    }

    /// <summary>
    /// At-least-once delivery means this event will arrive twice. The merchant's
    /// balance must not double when it does.
    /// </summary>
    [Fact]
    public async Task Posting_the_same_capture_twice_moves_money_once()
    {
        var (paymentId, merchantId) = await AuthorisedPaymentAsync(4_000, "GBP");

        await PostAsync(paymentId, merchantId, 4_000, "GBP");
        await PostAsync(paymentId, merchantId, 4_000, "GBP");

        await using var connection = await OpenAsync();
        var ledger = new LedgerStore(NullLogger<LedgerStore>.Instance);

        Assert.Equal(-3_900, await ledger.BalanceAsync(connection, $"merchant:{merchantId}", CancellationToken.None));
    }

    [Theory]
    [InlineData(10_000, 9_750, 250)]
    [InlineData(1, 1, 0)]        // too small for a fee: the merchant keeps it all
    [InlineData(19, 19, 0)]      // 0.475 rounds to nothing
    [InlineData(20, 19, 1)]      // 0.5 rounds away from zero
    [InlineData(7_777, 7_583, 194)]
    public void A_split_always_adds_back_up_to_what_was_captured(long captured, long merchant, long fee)
    {
        var (toMerchant, toFees) = Settlement.Split(Amount(captured, "USD"), FeeBasisPoints);

        Assert.Equal(merchant, toMerchant.MinorUnits);
        Assert.Equal(fee, toFees.MinorUnits);

        // The property that matters more than any single case: nothing is lost to
        // rounding and nothing is invented by it.
        Assert.Equal(captured, toMerchant.MinorUnits + toFees.MinorUnits);
    }

    private static Money Amount(long minorUnits, string currency)
    {
        Assert.True(Money.TryCreate(minorUnits, currency, out var money, out _));
        return money;
    }

    private async Task<NpgsqlConnection> OpenAsync()
        => await new DbConnectionFactory(fixture.ConnectionString).OpenAsync(CancellationToken.None);

    /// <summary>Runs the real handler over a payment.resolved.v1 event.</summary>
    private async Task PostAsync(Guid paymentId, Guid merchantId, long amountMinor, string currency)
    {
        var payload = JsonSerializer.Serialize(new PaymentResolvedEvent(
            paymentId, merchantId, amountMinor, currency, "authorized",
            "stub", "fp_test", null, DateTimeOffset.UtcNow),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var handler = new PaymentResolvedHandler(
            new LedgerStore(NullLogger<LedgerStore>.Instance),
            Options.Create(new LedgerOptions { FeeBasisPoints = FeeBasisPoints }),
            NullLogger<PaymentResolvedHandler>.Instance);

        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await handler.HandleAsync(payload, connection, transaction, CancellationToken.None);
        await transaction.CommitAsync();
    }

    /// <summary>A payment that exists, so ledger transactions have something to reference.</summary>
    private async Task<(Guid PaymentId, Guid MerchantId)> AuthorisedPaymentAsync(long amountMinor, string currency)
    {
        var merchantId = Guid.NewGuid();
        var paymentId = Guid.CreateVersion7();

        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO payments (id, merchant_id, status, amount_minor, currency, version, created_at, updated_at)
            VALUES (@paymentId, @merchantId, 'authorized', @amountMinor, @currency, 1, now(), now());
            """,
            new { paymentId, merchantId, amountMinor, currency });

        return (paymentId, merchantId);
    }
}
