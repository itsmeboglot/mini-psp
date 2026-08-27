using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Payments.Api.Tests;

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
    /// The atomicity claim, from the losing side. A duplicate create is rolled
    /// back by the unique index, and the event it had already written must go with
    /// it — otherwise consumers would hear about a payment that does not exist.
    /// </summary>
    [Fact]
    public async Task A_rolled_back_duplicate_leaves_no_event_behind()
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();

        var first = await CreateAsync(client, merchantId, 700, "EUR", key);
        var replay = await CreateAsync(client, merchantId, 700, "EUR", key);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(1, await fixture.CountPaymentsAsync(merchantId));

        // One payment, one event. The second attempt inserted a payment row and an
        // outbox row before the index rejected it, and both vanished on rollback.
        Assert.Equal(1, await fixture.CountOutboxAsync(first.Id));
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
