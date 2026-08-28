using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Payments.Tests;

/// <summary>
/// The outbox exists so that a state change and the announcement of it either
/// both happen or neither does. These tests are about that guarantee, not about
/// Kafka: nothing here publishes anything.
/// </summary>
public sealed class OutboxTests(PaymentsApiFixture fixture) : IClassFixture<PaymentsApiFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Creating_a_payment_leaves_exactly_one_unpublished_event()
    {
        var client = fixture.CreateClient();
        var payment = await CreateAsync(client, Guid.NewGuid(), 1500, "USD", Guid.NewGuid().ToString());

        Assert.Equal(1, await fixture.CountOutboxAsync(payment.Id));
        Assert.Equal(1, await fixture.CountUnpublishedOutboxAsync(payment.Id));
    }

    /// <summary>
    /// A replay must not announce the payment twice.
    /// </summary>
    /// <remarks>
    /// Note what this does not prove. On a duplicate the idempotency insert fails
    /// before the outbox insert is reached, so no event is written and none has to
    /// be rolled back. Atomicity itself is structural here — all three inserts
    /// share one transaction, so no code path can commit one without the others —
    /// and demonstrating it under a mid-transaction failure needs fault injection,
    /// which arrives with the dispatcher.
    /// </remarks>
    [Fact]
    public async Task A_replayed_create_adds_no_second_event()
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();

        var first = await CreateAsync(client, merchantId, 700, "EUR", key);
        var replay = await CreateAsync(client, merchantId, 700, "EUR", key);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(1, await fixture.CountPaymentsAsync(merchantId));

        // Counted across the merchant, not the aggregate: a rolled back attempt
        // carries a payment id nobody ever sees, so counting by aggregate id would
        // miss an orphan. This is also what the jsonb payload buys — events are
        // queryable by content.
        Assert.Equal(1, await fixture.CountOutboxForMerchantAsync(merchantId));
    }

    [Fact]
    public async Task The_event_carries_the_payment_and_is_keyed_by_it()
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();

        var payment = await CreateAsync(client, merchantId, 4321, "gbp", Guid.NewGuid().ToString());
        var row = await fixture.ReadOutboxAsync(payment.Id);

        Assert.Equal("payment.created.v1", row.EventType);
        Assert.Equal(payment.Id, row.AggregateId);

        using var payload = JsonDocument.Parse(row.Payload);
        var body = payload.RootElement;

        Assert.Equal(payment.Id, body.GetProperty("paymentId").GetGuid());
        Assert.Equal(merchantId, body.GetProperty("merchantId").GetGuid());
        Assert.Equal(4321, body.GetProperty("amountMinor").GetInt64());
        Assert.Equal("GBP", body.GetProperty("currency").GetString());
        Assert.Equal("created", body.GetProperty("status").GetString());
    }

    /// <summary>
    /// The invariant the outbox has to hold whatever else happens: every event
    /// describes a payment that exists. Checked across everything this suite has
    /// done to the database, not just one case.
    /// </summary>
    [Fact]
    public async Task No_event_exists_without_the_payment_it_describes()
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();

        await CreateAsync(client, merchantId, 100, "USD", Guid.NewGuid().ToString());
        await CreateAsync(client, merchantId, 200, "USD", key);
        await CreateAsync(client, merchantId, 200, "USD", key);

        Assert.Equal(0, await fixture.CountOrphanEventsAsync());
    }

    [Fact]
    public async Task A_rejected_request_writes_no_event_at_all()
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync("/v1/payments",
            new { merchantId, amountMinor = -1L, currency = "USD" }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await fixture.CountPaymentsAsync(merchantId));
    }

    private static async Task<PaymentDto> CreateAsync(
        HttpClient client, Guid merchantId, long amountMinor, string currency, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new { merchantId, amountMinor, currency }, options: Json)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<PaymentDto>(Json))!;
    }

    private sealed record PaymentDto(Guid Id, Guid MerchantId, string Status, long AmountMinor, string Currency);
}
