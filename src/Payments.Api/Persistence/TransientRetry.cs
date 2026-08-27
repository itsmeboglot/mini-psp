using Npgsql;

namespace Payments.Api.Persistence;

/// <summary>
/// Retries a database operation that failed for a reason likely to pass.
/// </summary>
/// <remarks>
/// Hand written rather than taken from a resilience library: the policy is a
/// dozen lines and every one of them is a decision worth being able to defend.
/// Provider HTTP calls are a different problem — circuit breaking, hedging,
/// per-provider budgets — and will use Polly through IHttpClientFactory.
///
/// Retrying is only safe because the operations it wraps are idempotent. If the
/// first attempt committed and the connection dropped before the acknowledgement
/// arrived, the retry meets its own idempotency key and replays the stored
/// response instead of creating a second payment.
/// </remarks>
public static class TransientRetry
{
    private const int DefaultAttempts = 3;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(50);

    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ILogger logger,
        CancellationToken ct,
        int attempts = DefaultAttempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(ct);
            }
            catch (NpgsqlException e) when (attempt < attempts && IsRetryable(e))
            {
                var delay = BaseDelay * (1 << (attempt - 1));

                logger.LogWarning(e,
                    "Transient database failure on attempt {Attempt} of {Attempts}; retrying in {Delay}",
                    attempt, attempts, delay);

                await Task.Delay(delay, ct);
            }
        }
    }

    /// <remarks>
    /// Npgsql already classifies connection level faults and failover, so that
    /// judgement is not duplicated here. Serialisation failure and deadlock are
    /// added explicitly: both mean "the database chose to abort you so someone
    /// else could proceed", which is the textbook case for trying again.
    /// </remarks>
    private static bool IsRetryable(NpgsqlException e)
        => e.IsTransient
           || e is PostgresException
           {
               SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected
           };
}
