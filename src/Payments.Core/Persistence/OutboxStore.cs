using Dapper;

namespace Payments.Core.Persistence;

/// <summary>One event waiting to be published.</summary>
public sealed record OutboxRecord(
    long Id, Guid AggregateId, string EventType, string Payload, int Attempts, string? CorrelationId);

/// <summary>The reason a batch stopped.</summary>
public enum DispatchOutcome
{
    /// <summary>Nothing was waiting.</summary>
    Idle,

    /// <summary>Everything claimed was published.</summary>
    Dispatched,

    /// <summary>The broker could not be reached. Nothing was counted against any row.</summary>
    BrokerUnavailable
}

public sealed record DispatchResult(DispatchOutcome Outcome, int Published, int Claimed);

/// <summary>
/// Drains the outbox: claims a batch, hands each record to a publisher, and marks
/// what went out.
/// </summary>
/// <remarks>
/// The claim is held in a transaction for as long as publishing takes. That is a
/// deliberate trade: a database transaction spanning a network call is normally
/// something to avoid, but the row locks are exactly what stop a second
/// dispatcher republishing the same events. Batches are small and the publish
/// timeout is short to keep the window bounded.
///
/// SKIP LOCKED alone would let several dispatchers interleave, which loses
/// ordering per payment: one instance could take a payment's second event while
/// another still holds its first, and publish it first. So the claim is
/// partitioned by a hash of the aggregate instead. Every event about a payment
/// falls to exactly one instance, which publishes them in id order, and the
/// instances never contend for the same rows.
/// </remarks>
public sealed class OutboxStore(DbConnectionFactory db, ILogger<OutboxStore> logger)
{
    private const string ClaimPending = """
        SELECT id AS Id, aggregate_id AS AggregateId, event_type AS EventType,
               payload::text AS Payload, attempts AS Attempts,
               correlation_id AS CorrelationId
        FROM outbox
        WHERE published_at IS NULL AND dead_at IS NULL
          -- One instance owns an aggregate entirely. hashtext is cast to bigint
          -- first because it can return int.MinValue, which abs() cannot negate.
          AND mod(abs(hashtext(aggregate_id::text)::bigint), @PartitionCount) = @PartitionIndex
        ORDER BY id
        FOR UPDATE SKIP LOCKED
        LIMIT @BatchSize;
        """;

    private const string MarkPublished = """
        UPDATE outbox SET published_at = now() WHERE id = ANY(@Ids);
        """;

    private const string RecordFailure = """
        UPDATE outbox
        SET attempts   = attempts + 1,
            last_error = @Error,
            dead_at    = CASE WHEN attempts + 1 >= @MaxAttempts THEN now() ELSE NULL END
        WHERE id = @Id;
        """;

    public async Task<DispatchResult> DispatchBatchAsync(
        Func<OutboxRecord, CancellationToken, Task> publish,
        Func<Exception, bool> isBrokerUnavailable,
        int batchSize,
        int maxAttempts,
        CancellationToken ct,
        int partitionIndex = 0,
        int partitionCount = 1)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var claimed = (await connection.QueryAsync<OutboxRecord>(
            new CommandDefinition(ClaimPending, new
            {
                BatchSize = batchSize,
                PartitionIndex = partitionIndex,
                PartitionCount = partitionCount
            }, transaction, cancellationToken: ct)))
            .AsList();

        if (claimed.Count == 0)
        {
            await transaction.CommitAsync(ct);
            return new DispatchResult(DispatchOutcome.Idle, 0, 0);
        }

        var published = new List<long>(claimed.Count);

        foreach (var record in claimed)
        {
            try
            {
                await publish(record, ct);
                published.Add(record.Id);
            }
            catch (Exception e) when (isBrokerUnavailable(e))
            {
                // An outage is not a poisonous message. Counting attempts here
                // would march every waiting event towards dead in the minutes a
                // broker takes to come back, so the whole batch is abandoned
                // untouched and tried again on the next tick.
                await transaction.RollbackAsync(CancellationToken.None);

                logger.LogWarning(e,
                    "Broker unavailable while dispatching; {Claimed} events left untouched", claimed.Count);

                return new DispatchResult(DispatchOutcome.BrokerUnavailable, 0, claimed.Count);
            }
            catch (Exception e)
            {
                // This record is the problem, not the broker. Count it, and set it
                // aside once it has had enough chances, so it stops holding up
                // everything behind it.
                await connection.ExecuteAsync(new CommandDefinition(RecordFailure,
                    new { record.Id, Error = e.Message, MaxAttempts = maxAttempts },
                    transaction, cancellationToken: ct));

                logger.LogError(e,
                    "Failed to publish outbox event {OutboxId} ({EventType}), attempt {Attempt} of {MaxAttempts}",
                    record.Id, record.EventType, record.Attempts + 1, maxAttempts);
            }
        }

        if (published.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(MarkPublished,
                new { Ids = published.ToArray() }, transaction, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
        return new DispatchResult(DispatchOutcome.Dispatched, published.Count, claimed.Count);
    }
}
