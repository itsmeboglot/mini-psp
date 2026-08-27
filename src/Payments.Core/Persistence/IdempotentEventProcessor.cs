using Dapper;
using Npgsql;

namespace Payments.Core.Persistence;

public enum ProcessOutcome
{
    /// <summary>The handler ran and its work committed.</summary>
    Applied,

    /// <summary>This consumer had already handled this event. Nothing ran.</summary>
    AlreadyProcessed,

    /// <summary>The handler declined to act and nothing was recorded.</summary>
    Skipped
}

/// <summary>
/// Runs a handler at most once per consumer per event.
/// </summary>
/// <remarks>
/// The consumer side half of the outbox bargain. Delivery is at-least-once, so a
/// consumer will see the same event again after a dispatcher or a consumer dies
/// mid-flight. What makes that harmless is recording the event and doing the work
/// in one transaction: either both happened or neither did, and a redelivery
/// meets its own row in processed_events and stops.
///
/// This is the same mechanism as the idempotency key on the API side, and for the
/// same reason — a unique index, not a lock, because the record and the work have
/// to commit together.
/// </remarks>
public sealed class IdempotentEventProcessor(DbConnectionFactory db, ILogger<IdempotentEventProcessor> logger)
{
    private const string ProcessedEventsConstraint = "processed_events_pkey";

    private const string RecordProcessed = """
        INSERT INTO processed_events (consumer, event_id) VALUES (@Consumer, @EventId);
        """;

    /// <param name="handle">
    /// Returns false to decline, which rolls the whole thing back including the
    /// record of having processed it, so the event can be tried again later.
    /// </param>
    public async Task<ProcessOutcome> ProcessAsync(
        string consumer,
        long eventId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<bool>> handle,
        CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(RecordProcessed,
                new { Consumer = consumer, EventId = eventId }, transaction, cancellationToken: ct));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation
                                          && e.ConstraintName == ProcessedEventsConstraint)
        {
            await transaction.RollbackAsync(CancellationToken.None);

            logger.LogDebug("Consumer {Consumer} has already processed event {EventId}", consumer, eventId);
            return ProcessOutcome.AlreadyProcessed;
        }

        if (!await handle(connection, transaction, ct))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return ProcessOutcome.Skipped;
        }

        await transaction.CommitAsync(ct);
        return ProcessOutcome.Applied;
    }
}
