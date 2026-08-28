using System.Text.Json;
using Payments.Core.Persistence;
using StackExchange.Redis;

namespace Payments.Core.Caching;

/// <summary>
/// Remembers what a completed request answered, so a retry of it need not reach
/// the database at all.
/// </summary>
/// <remarks>
/// An optimisation and nothing more, exactly as ADR 0003 says. A hit is
/// trustworthy because entries are written only after the payment has committed,
/// so the payment certainly exists. A miss means "no idea", never "not there",
/// and the request carries on to PostgreSQL where the unique index decides.
///
/// Every Redis failure is treated as a miss. Losing Redis costs latency; it must
/// not cost a payment, and it must not turn into an outage.
/// </remarks>
public sealed class IdempotencyCache(RedisConnection redis, ILogger<IdempotencyCache> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Long enough to cover the retries a client will realistically make, short
    /// enough that the cache does not become a second copy of the table.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public bool IsEnabled => redis.IsAvailable;

    public async Task<CachedResponse?> GetAsync(Guid merchantId, string idempotencyKey)
    {
        if (redis.Database is not { } database)
        {
            return null;
        }

        try
        {
            var value = await database.StringGetAsync(Key(merchantId, idempotencyKey));

            return value.HasValue ? JsonSerializer.Deserialize<CachedResponse>(value!, Json) : null;
        }
        catch (Exception e) when (e is RedisException or JsonException or TimeoutException)
        {
            logger.LogWarning(e, "Idempotency cache unavailable; falling through to the database");
            return null;
        }
    }

    public async Task SetAsync(
        Guid merchantId, string idempotencyKey, string requestHash, StoredResponse response)
    {
        if (redis.Database is not { } database)
        {
            return;
        }

        try
        {
            await database.StringSetAsync(
                Key(merchantId, idempotencyKey),
                JsonSerializer.Serialize(
                    new CachedResponse(requestHash, response.StatusCode, response.Body), Json),
                Lifetime);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            // Nothing to do about it and nothing to tell the caller: the payment
            // is already committed, and the next retry will simply miss.
            logger.LogWarning(e, "Could not cache the response for {MerchantId}", merchantId);
        }
    }

    /// <summary>
    /// Namespaced by merchant, because idempotency keys are chosen by clients and
    /// two merchants will pick the same one.
    /// </summary>
    private static string Key(Guid merchantId, string idempotencyKey)
        => $"idem:{merchantId:N}:{idempotencyKey}";
}

/// <param name="RequestHash">
/// Carried so a hit can be checked against the request that produced it. Without
/// it the cache would happily answer a different request with an old payment.
/// </param>
public sealed record CachedResponse(string RequestHash, int StatusCode, string Body);
