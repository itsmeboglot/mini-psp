using Dapper;

namespace Payments.Core.Persistence;

/// <summary>
/// Counts how many times a consumer has failed on an event, durably.
/// </summary>
/// <remarks>
/// The count has to outlive the process. Held in a variable it resets on every
/// restart, so a message that fails on each delivery and a worker that restarts
/// occasionally never reach the dead letter topic between them, and never stop
/// retrying either.
///
/// Deliberately not in the same transaction as the work. The work rolled back;
/// recording the failure alongside it would roll back too, and the count would
/// never rise. It is written on its own connection, so a failure is remembered
/// precisely because the attempt was not.
/// </remarks>
public sealed class ConsumerFailureStore(DbConnectionFactory db, ILogger<ConsumerFailureStore> logger)
{
    private const string RecordFailure = """
        INSERT INTO consumer_failures (consumer, event_id, attempts, last_error)
        VALUES (@Consumer, @EventId, 1, @Error)
        ON CONFLICT (consumer, event_id) DO UPDATE
            SET attempts   = consumer_failures.attempts + 1,
                last_error = @Error,
                updated_at = now()
        RETURNING attempts;
        """;

    private const string Forget = """
        DELETE FROM consumer_failures WHERE consumer = @Consumer AND event_id = @EventId;
        """;

    /// <returns>How many times this consumer has now failed on this event.</returns>
    public async Task<int> RecordFailureAsync(string consumer, long eventId, string error, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        var attempts = await connection.ExecuteScalarAsync<int>(new CommandDefinition(RecordFailure,
            new { Consumer = consumer, EventId = eventId, Error = Truncate(error) },
            cancellationToken: ct));

        logger.LogDebug("Consumer {Consumer} has failed {Attempts} time(s) on event {EventId}",
            consumer, attempts, eventId);

        return attempts;
    }

    /// <summary>
    /// Clears the count once the event is behind us, whether it succeeded or was
    /// dead lettered, so the table holds only what is currently failing.
    /// </summary>
    public async Task ForgetAsync(string consumer, long eventId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(Forget,
            new { Consumer = consumer, EventId = eventId }, cancellationToken: ct));
    }

    /// <summary>An exception message is not a bounded value, and this column is.</summary>
    private static string Truncate(string error)
        => error.Length <= 1000 ? error : error[..1000];
}
