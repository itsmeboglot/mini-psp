using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Payments.Tests;

/// <summary>
/// What Redis does, and what it must not be trusted to do.
/// </summary>
public sealed class RedisTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_cached_replay_is_byte_identical_to_the_first_answer()
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();

        var first = await PostAsync(client, merchantId, 5_000, "USD", key);
        var replay = await PostAsync(client, merchantId, 5_000, "USD", key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, await CountPaymentsAsync(merchantId));
    }

    /// <summary>
    /// The cache must not answer a request it never saw. Same key, different body,
    /// and the reply is a refusal rather than someone else's payment.
    /// </summary>
    [Fact]
    public async Task A_cached_entry_is_not_replayed_for_a_different_request()
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();

        await PostAsync(client, merchantId, 5_000, "USD", key);
        var different = await PostAsync(client, merchantId, 9_999, "USD", key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, different.StatusCode);
        Assert.Equal(1, await CountPaymentsAsync(merchantId));
    }

    /// <summary>
    /// A cache hit must never be the reason a payment exists. Emptying Redis
    /// entirely changes the latency of a replay and nothing else about it.
    /// </summary>
    [Fact]
    public async Task Losing_the_cache_changes_nothing_about_correctness()
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();

        var first = await PostAsync(client, merchantId, 3_300, "EUR", key);
        var body = await first.Content.ReadAsStringAsync();

        await FlushCacheAsync();

        var replay = await PostAsync(client, merchantId, 3_300, "EUR", key);

        // Served from PostgreSQL this time, and the same answer.
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, await CountPaymentsAsync(merchantId));
    }

    [Fact]
    public async Task A_merchant_that_exceeds_its_burst_is_refused()
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();

        // Capacity is three in this fixture.
        for (var request = 0; request < 3; request++)
        {
            var allowed = await PostAsync(client, merchantId, 100, "USD", Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        }

        var refused = await PostAsync(client, merchantId, 100, "USD", Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(3, await CountPaymentsAsync(merchantId));
    }

    /// <summary>
    /// The bucket is per merchant, so one noisy merchant cannot spend another's
    /// allowance.
    /// </summary>
    [Fact]
    public async Task One_merchant_hitting_the_limit_does_not_affect_another()
    {
        var client = fixture.CreateClient();
        var noisy = Guid.NewGuid();
        var quiet = Guid.NewGuid();

        for (var request = 0; request < 4; request++)
        {
            await PostAsync(client, noisy, 100, "USD", Guid.NewGuid().ToString());
        }

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await PostAsync(client, noisy, 100, "USD", Guid.NewGuid().ToString())).StatusCode);

        Assert.Equal(HttpStatusCode.Created,
            (await PostAsync(client, quiet, 100, "USD", Guid.NewGuid().ToString())).StatusCode);
    }

    /// <summary>
    /// The refusal says when to come back, rather than leaving a client to guess.
    /// </summary>
    [Fact]
    public async Task A_refusal_says_how_long_to_wait()
    {
        var client = fixture.CreateClient();
        var merchantId = Guid.NewGuid();

        for (var request = 0; request < 4; request++)
        {
            await PostAsync(client, merchantId, 100, "USD", Guid.NewGuid().ToString());
        }

        var refused = await PostAsync(client, merchantId, 100, "USD", Guid.NewGuid().ToString());
        var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());

        Assert.True(problem.RootElement.GetProperty("retryAfterSeconds").GetDouble() > 0);
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

    private async Task<long> CountPaymentsAsync(Guid merchantId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM payments WHERE merchant_id = @merchantId", new { merchantId });
    }

    private async Task FlushCacheAsync()
    {
        var redis = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(
            $"{fixture.RedisConnectionString},allowAdmin=true");

        await using (redis)
        {
            foreach (var endpoint in redis.GetEndPoints())
            {
                await redis.GetServer(endpoint).FlushDatabaseAsync();
            }
        }
    }
}
