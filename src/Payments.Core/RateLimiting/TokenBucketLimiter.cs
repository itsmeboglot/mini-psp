using StackExchange.Redis;

namespace Payments.Core.RateLimiting;

public sealed class RateLimitOptions
{
    public const string Section = "RateLimit";

    public bool Enabled { get; set; } = true;

    /// <summary>Requests a merchant may make in a burst.</summary>
    public int Capacity { get; set; } = 60;

    /// <summary>Requests per second the bucket refills at.</summary>
    public double RefillPerSecond { get; set; } = 10;
}

/// <param name="RetryAfter">How long until the next request would be allowed.</param>
public sealed record RateLimitDecision(bool Allowed, int Remaining, TimeSpan RetryAfter);

/// <summary>
/// A token bucket per merchant, held in Redis.
/// </summary>
/// <remarks>
/// The whole decision runs as one Lua script inside Redis. Reading the bucket,
/// refilling it and taking a token cannot be three round trips: between them two
/// instances would both read the same count and both decide they were allowed,
/// which is the same read-then-write race the idempotency key avoids in
/// PostgreSQL. Redis runs a script atomically, so the sequence is indivisible
/// without a lock.
///
/// A bucket rather than a fixed window because a window lets a merchant spend its
/// whole allowance in the last instant of one window and again in the first
/// instant of the next, which is twice the intended rate at exactly the wrong
/// moment.
/// </remarks>
public sealed class TokenBucketLimiter(
    IConnectionMultiplexer? redis,
    TimeProvider clock,
    ILogger<TokenBucketLimiter> logger)
{
    /// <remarks>
    /// Returns tokens as a float and lets the caller round: refilling in whole
    /// tokens only would round away a slow refill rate entirely.
    /// </remarks>
    private const string Script = """
        local key      = KEYS[1]
        local capacity = tonumber(ARGV[1])
        local refill   = tonumber(ARGV[2])
        local now      = tonumber(ARGV[3])
        local cost     = tonumber(ARGV[4])

        local bucket = redis.call('HMGET', key, 'tokens', 'updated')
        local tokens = tonumber(bucket[1])
        local updated = tonumber(bucket[2])

        if tokens == nil then
            tokens = capacity
            updated = now
        end

        -- Refill for the time that has passed, never above capacity.
        local elapsed = math.max(0, now - updated) / 1000.0
        tokens = math.min(capacity, tokens + elapsed * refill)

        local allowed = 0
        if tokens >= cost then
            tokens = tokens - cost
            allowed = 1
        end

        redis.call('HSET', key, 'tokens', tokens, 'updated', now)
        -- Expire an idle bucket rather than keeping one per merchant forever.
        redis.call('PEXPIRE', key, math.ceil((capacity / refill) * 1000) + 1000)

        local retry_after_ms = 0
        if allowed == 0 then
            retry_after_ms = math.ceil(((cost - tokens) / refill) * 1000)
        end

        return { allowed, math.floor(tokens), retry_after_ms }
        """;

    public async Task<RateLimitDecision> TryAcquireAsync(Guid merchantId, RateLimitOptions options)
    {
        if (redis is null || !options.Enabled)
        {
            return new RateLimitDecision(true, options.Capacity, TimeSpan.Zero);
        }

        try
        {
            var result = (RedisValue[])(await redis.GetDatabase().ScriptEvaluateAsync(
                Script,
                [$"ratelimit:{merchantId:N}"],
                [
                    options.Capacity,
                    options.RefillPerSecond,
                    clock.GetUtcNow().ToUnixTimeMilliseconds(),
                    1
                ]))!;

            return new RateLimitDecision(
                Allowed: (int)result[0] == 1,
                Remaining: (int)result[1],
                RetryAfter: TimeSpan.FromMilliseconds((int)result[2]));
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            // Fail open. A limiter exists to protect the platform from too much
            // traffic; refusing all traffic because the limiter is unreachable
            // turns a degraded dependency into an outage.
            logger.LogWarning(e, "Rate limiter unavailable; allowing the request");
            return new RateLimitDecision(true, options.Capacity, TimeSpan.Zero);
        }
    }
}
