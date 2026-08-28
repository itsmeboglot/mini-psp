using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Payments.Tests;

public sealed class IdempotencyTests(PaymentsApiFixture fixture) : IClassFixture<PaymentsApiFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Creating_a_payment_returns_201_and_the_payment_can_be_fetched()
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();

        var response = await PostAsync(client, merchantId, 1234, "usd", key: Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await ReadPaymentAsync(response);
        Assert.Equal(merchantId, created.MerchantId);
        Assert.Equal(1234, created.AmountMinor);
        Assert.Equal("USD", created.Currency);   // normalised on the way in
        Assert.Equal("created", created.Status);

        var fetched = await client.GetFromJsonAsync<PaymentDto>($"/v1/payments/{created.Id}", Json);
        Assert.Equal(created.Id, fetched!.Id);
    }

    [Fact]
    public async Task Replaying_the_same_key_returns_the_first_payment_and_creates_nothing()
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();

        var first = await ReadPaymentAsync(await PostAsync(client, merchantId, 5000, "EUR", key));
        var second = await ReadPaymentAsync(await PostAsync(client, merchantId, 5000, "EUR", key));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await fixture.CountPaymentsAsync(merchantId));
    }

    [Fact]
    public async Task Twenty_concurrent_requests_with_one_key_create_exactly_one_payment()
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => PostAsync(client, merchantId, 999, "USD", key)));

        // A request that loses the race either replays the stored response or,
        // if the winner has not committed yet, is told to retry. Nothing else is
        // acceptable, and in particular no second payment may appear.
        Assert.All(responses, r => Assert.Contains(
            r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }));

        var ids = new HashSet<Guid>();
        foreach (var response in responses.Where(r => r.StatusCode == HttpStatusCode.Created))
        {
            ids.Add((await ReadPaymentAsync(response)).Id);
        }

        Assert.Single(ids);
        Assert.Equal(1, await fixture.CountPaymentsAsync(merchantId));
    }

    /// <summary>
    /// Guards the reason response_body is text and not jsonb: jsonb reparses and
    /// reorders keys, so a replay would return the same object with a different
    /// byte sequence. A client that signs or hashes the body would see a mismatch.
    /// </summary>
    [Fact]
    public async Task A_replayed_response_is_byte_identical_to_the_first()
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();

        var first = await PostAsync(client, merchantId, 4242, "GBP", key);
        var replay = await PostAsync(client, merchantId, 4242, "GBP", key);

        var firstBody = await first.Content.ReadAsStringAsync();
        var replayBody = await replay.Content.ReadAsStringAsync();

        Assert.Equal(firstBody, replayBody);
        Assert.Equal(first.StatusCode, replay.StatusCode);
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_body_is_rejected()
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();

        await PostAsync(client, merchantId, 100, "USD", key);
        var response = await PostAsync(client, merchantId, 999, "USD", key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(1, await fixture.CountPaymentsAsync(merchantId));
    }

    [Fact]
    public async Task A_request_without_an_idempotency_key_is_rejected()
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync("/v1/payments",
            new { merchantId, amountMinor = 100L, currency = "USD" }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await fixture.CountPaymentsAsync(merchantId));
    }

    [Theory]
    [InlineData(0, "USD")]
    [InlineData(-1, "USD")]
    [InlineData(100, "US")]
    [InlineData(100, "US1")]
    public async Task Invalid_amounts_and_currencies_are_rejected(long amountMinor, string currency)
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();

        var response = await PostAsync(client, merchantId, amountMinor, currency, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await fixture.CountPaymentsAsync(merchantId));
    }

    [Fact]
    public async Task Fetching_an_unknown_payment_returns_404()
    {
        var client = fixture.CreateClient();

        var response = await client.GetAsync($"/v1/payments/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client, Guid merchantId, long amountMinor, string currency, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(new { merchantId, amountMinor, currency }, options: Json)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static async Task<PaymentDto> ReadPaymentAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<PaymentDto>(Json))!;

    private sealed record PaymentDto(
        Guid Id, Guid MerchantId, string Status, long AmountMinor, string Currency, DateTimeOffset CreatedAt);
}
