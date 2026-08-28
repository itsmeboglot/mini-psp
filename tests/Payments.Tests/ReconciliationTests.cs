using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Payments.Core.Domain;
using Payments.Core.Observability;
using Payments.Core.Persistence;
using Payments.Core.Providers;
using Payments.Worker;

namespace Payments.Tests;

/// <summary>
/// Getting a payment out of unknown.
/// </summary>
/// <remarks>
/// Every test here starts from a payment the platform genuinely could not resolve:
/// created through the API, moved to pending by the real handler, then left in
/// unknown by a provider that would not answer. Nothing is written straight into
/// the state under test.
///
/// Assertions are about the payment each test created, never about how many the
/// sweep resolved: a sweep is global by design, and earlier tests in this class
/// leave their own unresolved payments behind for it to find.
/// </remarks>
public sealed class ReconciliationTests(PaymentsApiFixture fixture) : IClassFixture<PaymentsApiFixture>
{
    private const int AttemptsBeforeBelievingNotFound = 3;

    [Fact]
    public async Task A_provider_that_did_charge_corrects_the_payment_to_authorized()
    {
        var payment = await UnknownPaymentAsync();

        // The case that matters most: we timed out, the provider had already taken
        // the money. Recording failed would have been a lie about someone's money.
        await SweepAsync(new ProviderResult(ProviderVerdict.Authorized, "fp_late", null));

        var state = await fixture.ReadPaymentAsync(payment.Id);
        Assert.Equal("authorized", state.Status);
        Assert.Equal("fp_late", state.ProviderReference);
    }

    [Fact]
    public async Task A_provider_that_declined_resolves_the_payment_to_failed()
    {
        var payment = await UnknownPaymentAsync();

        await SweepAsync(new ProviderResult(ProviderVerdict.Declined, "fp_no", "do not honour"));

        Assert.Equal("failed", (await fixture.ReadPaymentAsync(payment.Id)).Status);
    }

    /// <summary>
    /// One denial is not evidence. A status API is briefly inconsistent after the
    /// outage that caused the unknown in the first place.
    /// </summary>
    [Fact]
    public async Task One_denial_is_not_enough_to_call_a_payment_failed()
    {
        var payment = await UnknownPaymentAsync();

        await SweepAsync(new ProviderResult(ProviderVerdict.NotFound, null, "no record"));

        var state = await fixture.ReadPaymentAsync(payment.Id);
        Assert.Equal("unknown", state.Status);
        Assert.Equal(1, state.ReconciliationAttempts);
    }

    [Fact]
    public async Task Enough_denials_do_settle_it_as_failed()
    {
        var payment = await UnknownPaymentAsync();
        var notFound = new ProviderResult(ProviderVerdict.NotFound, null, "no record");

        for (var attempt = 1; attempt < AttemptsBeforeBelievingNotFound; attempt++)
        {
            await SweepAsync(notFound);
            Assert.Equal("unknown", (await fixture.ReadPaymentAsync(payment.Id)).Status);
        }

        await SweepAsync(notFound);

        var state = await fixture.ReadPaymentAsync(payment.Id);
        Assert.Equal("failed", state.Status);
        Assert.Equal(AttemptsBeforeBelievingNotFound, state.ReconciliationAttempts);
    }

    /// <summary>
    /// A provider that will not answer is not a provider saying no. However many
    /// times it happens, it never adds up to a verdict.
    /// </summary>
    [Fact]
    public async Task A_provider_that_cannot_be_reached_never_resolves_anything()
    {
        var payment = await UnknownPaymentAsync();
        var unreachable = new ProviderResult(ProviderVerdict.Unknown, null, "unreachable");

        for (var attempt = 0; attempt < AttemptsBeforeBelievingNotFound + 2; attempt++)
        {
            await SweepAsync(unreachable);
        }

        Assert.Equal("unknown", (await fixture.ReadPaymentAsync(payment.Id)).Status);
    }

    /// <summary>
    /// A payment whose own charge may still be in flight is left alone, so that
    /// two things are not asking about the same money at once.
    /// </summary>
    [Fact]
    public async Task A_payment_inside_the_grace_period_is_not_touched()
    {
        var payment = await UnknownPaymentAsync();

        await SweepAsync(
            new ProviderResult(ProviderVerdict.Authorized, "fp_early", null),
            graceSeconds: 3600);

        var state = await fixture.ReadPaymentAsync(payment.Id);
        Assert.Equal("unknown", state.Status);
        Assert.Equal(0, state.ReconciliationAttempts);
    }

    private async Task<int> SweepAsync(ProviderResult status, int graceSeconds = 0)
    {
        var db = new DbConnectionFactory(fixture.ConnectionString);
        var store = new PaymentStore(db, NullLogger<PaymentStore>.Instance);

        var reconciler = new PaymentReconciler(
            store,
            db,
            new StubProvider(status),
            Options.Create(new ReconciliationOptions
            {
                BatchSize = 50,
                GraceSeconds = graceSeconds,

                // Zero so consecutive sweeps in a test are not throttled; the
                // production default keeps a provider from being asked in a loop.
                RetryAfterSeconds = 0,
                AttemptsBeforeBelievingNotFound = AttemptsBeforeBelievingNotFound
            }),
            new PaymentMetrics(),
            NullLogger<PaymentReconciler>.Instance);

        return await reconciler.SweepAsync(CancellationToken.None);
    }

    /// <summary>Drives a payment to unknown the way the system actually gets there.</summary>
    private async Task<Payment> UnknownPaymentAsync()
    {
        var merchantId = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new { merchantId, amountMinor = 6100L, currency = "EUR" })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        (await fixture.CreateClient().SendAsync(request)).EnsureSuccessStatusCode();

        var pending = await fixture.MoveToPendingAsync(merchantId);

        var db = new DbConnectionFactory(fixture.ConnectionString);
        var store = new PaymentStore(db, NullLogger<PaymentStore>.Instance);

        await new PaymentPendingHandler(
                store, db,
                new StubProvider(new ProviderResult(ProviderVerdict.Unknown, null, "timeout")),
                new PaymentMetrics(),
                NullLogger<PaymentPendingHandler>.Instance)
            .HandleAsync(fixture.PendingEventPayload(pending), "test-correlation", CancellationToken.None);

        Assert.Equal("unknown", (await fixture.ReadPaymentAsync(pending.Id)).Status);
        return pending;
    }

    private sealed class StubProvider(ProviderResult result) : IPaymentProvider
    {
        public string Name => "stub";

        public Task<ProviderResult> ChargeAsync(ProviderCharge _, CancellationToken __)
            => Task.FromResult(result);

        public Task<ProviderResult> GetStatusAsync(string _, CancellationToken __)
            => Task.FromResult(result);
    }
}
