using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Payments.Core.Domain;
using Payments.Core.Persistence;
using Payments.Core.Providers;
using Payments.Worker;

namespace Payments.Tests;

/// <summary>
/// What the platform records for each thing a provider can do.
/// </summary>
/// <remarks>
/// The provider is a stub rather than the fake service, because the point is the
/// decision made about each verdict, not the HTTP that produced it. Every other
/// part is real: a real database, real transactions, the real handler.
/// </remarks>
public sealed class ProviderOutcomeTests(PaymentsApiFixture fixture) : IClassFixture<PaymentsApiFixture>
{
    [Fact]
    public async Task An_authorised_charge_is_recorded_with_the_provider_reference()
    {
        var payment = await PendingPaymentAsync();

        await ChargeAsync(payment, new ProviderResult(ProviderVerdict.Authorized, "fp_abc", null));

        var state = await fixture.ReadPaymentAsync(payment.Id);
        Assert.Equal("authorized", state.Status);
        Assert.Equal("fp_abc", state.ProviderReference);
        Assert.Equal("stub", state.Provider);
    }

    [Fact]
    public async Task A_declined_charge_becomes_failed()
    {
        var payment = await PendingPaymentAsync();

        await ChargeAsync(payment, new ProviderResult(ProviderVerdict.Declined, "fp_def", "insufficient funds"));

        Assert.Equal("failed", (await fixture.ReadPaymentAsync(payment.Id)).Status);
    }

    /// <summary>
    /// The rule the whole platform is built around. A provider that did not answer
    /// may still have taken the money, so failed is not an available conclusion.
    /// </summary>
    [Theory]
    [InlineData("timeout")]
    [InlineData("transport")]
    [InlineData("http 500")]
    public async Task A_charge_that_went_unanswered_becomes_unknown_and_never_failed(string reason)
    {
        var payment = await PendingPaymentAsync();

        await ChargeAsync(payment, new ProviderResult(ProviderVerdict.Unknown, null, reason));

        var state = await fixture.ReadPaymentAsync(payment.Id);
        Assert.Equal("unknown", state.Status);
        Assert.NotEqual("failed", state.Status);
    }

    /// <summary>
    /// A status the connector has never seen is not a decline. A provider that
    /// adds a value means nothing to us until someone decides what it means.
    /// </summary>
    [Fact]
    public async Task An_unrecognised_status_becomes_unknown()
    {
        var payment = await PendingPaymentAsync();

        await ChargeAsync(payment, new ProviderResult(ProviderVerdict.Unknown, "fp_ghi", "unrecognised 'reviewing'"));

        Assert.Equal("unknown", (await fixture.ReadPaymentAsync(payment.Id)).Status);
    }

    /// <summary>
    /// A redelivered pending event must not charge a second time. The handler
    /// declines to act on a payment that has already left pending.
    /// </summary>
    [Fact]
    public async Task A_redelivered_event_does_not_charge_again()
    {
        var payment = await PendingPaymentAsync();
        var provider = new StubProvider(new ProviderResult(ProviderVerdict.Authorized, "fp_once", null));

        await HandleAsync(payment, provider);
        await HandleAsync(payment, provider);

        Assert.Equal(1, provider.Charges);
        Assert.Equal("authorized", (await fixture.ReadPaymentAsync(payment.Id)).Status);
    }

    private Task ChargeAsync(Payment payment, ProviderResult result)
        => HandleAsync(payment, new StubProvider(result));

    private async Task HandleAsync(Payment payment, IPaymentProvider provider)
    {
        var db = new DbConnectionFactory(fixture.ConnectionString);
        var store = new PaymentStore(db, NullLogger<PaymentStore>.Instance);

        var handler = new PaymentPendingHandler(
            store, db, provider, NullLogger<PaymentPendingHandler>.Instance);

        await handler.HandleAsync(fixture.PendingEventPayload(payment), "test-correlation", CancellationToken.None);
    }

    /// <summary>Creates a payment and moves it to pending, the way the worker does.</summary>
    private async Task<Payment> PendingPaymentAsync()
    {
        var merchantId = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new { merchantId, amountMinor = 4200L, currency = "USD" })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await fixture.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await fixture.MoveToPendingAsync(merchantId);
    }

    private sealed class StubProvider(ProviderResult charge, ProviderResult? status = null) : IPaymentProvider
    {
        public int Charges { get; private set; }

        public string Name => "stub";

        public Task<ProviderResult> ChargeAsync(ProviderCharge _, CancellationToken __)
        {
            Charges++;
            return Task.FromResult(charge);
        }

        public Task<ProviderResult> GetStatusAsync(string _, CancellationToken __)
            => Task.FromResult(status ?? new ProviderResult(ProviderVerdict.NotFound, null, "no record"));
    }
}
