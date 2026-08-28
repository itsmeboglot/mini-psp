using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Payments.Core.Domain;
using Payments.Core.Observability;
using Payments.Core.Persistence;
using Payments.Core.Providers;
using Payments.Worker;

namespace Payments.Tests;

/// <summary>
/// Settling undetermined payments against the provider's own record.
/// </summary>
/// <remarks>
/// The hole this closes: a status endpoint saying "never heard of it" is an
/// opinion that can be wrong while the provider catches up, and no number of
/// those opinions becomes proof. A settlement report is the provider's account of
/// what it holds, so absence from it is evidence.
/// </remarks>
public sealed class SettlementTests(PaymentsApiFixture fixture) : IClassFixture<PaymentsApiFixture>
{
    [Fact]
    public async Task A_payment_the_report_holds_is_settled_as_authorized()
    {
        var payment = await UnknownPaymentAsync();

        var settled = await RunAsync(report => report[payment.Id.ToString()] =
            new ProviderResult(ProviderVerdict.Authorized, "fp_report", null));

        Assert.True(settled >= 1);

        var state = await fixture.ReadPaymentAsync(payment.Id);
        Assert.Equal("authorized", state.Status);
        Assert.Equal("fp_report", state.ProviderReference);
    }

    /// <summary>
    /// Absence from the report is the only thing that makes failed defensible.
    /// </summary>
    [Fact]
    public async Task A_payment_the_report_does_not_hold_is_settled_as_failed()
    {
        var payment = await UnknownPaymentAsync();

        await RunAsync(_ => { });

        Assert.Equal("failed", (await fixture.ReadPaymentAsync(payment.Id)).Status);
    }

    [Fact]
    public async Task A_payment_the_report_declined_is_settled_as_failed()
    {
        var payment = await UnknownPaymentAsync();

        await RunAsync(report => report[payment.Id.ToString()] =
            new ProviderResult(ProviderVerdict.Declined, "fp_no", "do not honour"));

        Assert.Equal("failed", (await fixture.ReadPaymentAsync(payment.Id)).Status);
    }

    /// <summary>
    /// A payment whose own charge may still be in flight is not declared missing
    /// because it has not reached the report yet.
    /// </summary>
    [Fact]
    public async Task A_payment_inside_the_grace_period_is_left_undetermined()
    {
        var payment = await UnknownPaymentAsync();

        await RunAsync(_ => { }, graceSeconds: 3600);

        Assert.Equal("unknown", (await fixture.ReadPaymentAsync(payment.Id)).Status);
    }

    /// <summary>
    /// The case worth waking someone for: we told a merchant their payment
    /// failed, and the provider is holding a charge for it.
    /// </summary>
    [Fact]
    public async Task A_report_that_contradicts_a_settled_payment_is_recorded_and_not_corrected()
    {
        var payment = await UnknownPaymentAsync();

        // Settled as failed, because the report did not hold it.
        await RunAsync(_ => { });
        Assert.Equal("failed", (await fixture.ReadPaymentAsync(payment.Id)).Status);

        // The next report disagrees.
        await RunAsync(report => report[payment.Id.ToString()] =
            new ProviderResult(ProviderVerdict.Authorized, "fp_late", null));

        var discrepancy = await DiscrepancyAsync(payment.Id);
        Assert.NotNull(discrepancy);
        Assert.Equal("failed", discrepancy.OurStatus);
        Assert.Equal("authorized", discrepancy.ProviderStatus);

        // Left alone on purpose. Failed is terminal, the merchant has been told,
        // and rewriting that quietly is worse than raising a hand.
        Assert.Equal("failed", (await fixture.ReadPaymentAsync(payment.Id)).Status);
    }

    private async Task<int> RunAsync(
        Action<Dictionary<string, ProviderResult>> buildReport, int graceSeconds = 0)
    {
        var report = new Dictionary<string, ProviderResult>();
        buildReport(report);

        var db = new DbConnectionFactory(fixture.ConnectionString);

        var reconciler = new SettlementReconciler(
            new PaymentStore(db, NullLogger<PaymentStore>.Instance),
            db,
            new ReportingProvider(report),
            Options.Create(new SettlementOptions { GraceSeconds = graceSeconds }),
            new PaymentMetrics(),
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            NullLogger<SettlementReconciler>.Instance);

        return await reconciler.RunAsync(CancellationToken.None);
    }

    private async Task<Discrepancy?> DiscrepancyAsync(Guid paymentId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        return await connection.QuerySingleOrDefaultAsync<Discrepancy>(
            """
            SELECT our_status AS OurStatus, provider_status AS ProviderStatus
            FROM settlement_discrepancies WHERE payment_id = @paymentId
            """, new { paymentId });
    }

    private sealed record Discrepancy(string OurStatus, string ProviderStatus);

    /// <summary>Drives a payment to unknown the way the system actually gets there.</summary>
    private async Task<Payment> UnknownPaymentAsync()
    {
        var merchantId = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new { merchantId, amountMinor = 8_800L, currency = "USD" })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        (await fixture.CreateClient().SendAsync(request)).EnsureSuccessStatusCode();

        var pending = await fixture.MoveToPendingAsync(merchantId);
        var db = new DbConnectionFactory(fixture.ConnectionString);

        await new PaymentPendingHandler(
                new PaymentStore(db, NullLogger<PaymentStore>.Instance), db,
                new ReportingProvider([], new ProviderResult(ProviderVerdict.Unknown, null, "timeout")),
                new PaymentMetrics(),
                NullLogger<PaymentPendingHandler>.Instance)
            .HandleAsync(fixture.PendingEventPayload(pending), "settlement-test", CancellationToken.None);

        Assert.Equal("unknown", (await fixture.ReadPaymentAsync(pending.Id)).Status);
        return pending;
    }

    private sealed class ReportingProvider(
        Dictionary<string, ProviderResult> report, ProviderResult? charge = null) : IPaymentProvider
    {
        public string Name => "stub";

        public Task<ProviderResult> ChargeAsync(ProviderCharge _, CancellationToken __)
            => Task.FromResult(charge ?? new ProviderResult(ProviderVerdict.Unknown, null, "timeout"));

        public Task<ProviderResult> GetStatusAsync(string _, CancellationToken __)
            => Task.FromResult(new ProviderResult(ProviderVerdict.Unknown, null, "unreachable"));

        public Task<IReadOnlyDictionary<string, ProviderResult>> GetSettlementAsync(CancellationToken _)
            => Task.FromResult<IReadOnlyDictionary<string, ProviderResult>>(report);
    }
}
