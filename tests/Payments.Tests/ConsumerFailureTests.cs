using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Payments.Core.Persistence;

namespace Payments.Tests;

/// <summary>
/// Retry counts that outlive the process counting them.
/// </summary>
public sealed class ConsumerFailureTests(PaymentsApiFixture fixture) : IClassFixture<PaymentsApiFixture>
{
    private ConsumerFailureStore Store() => new(
        new DbConnectionFactory(fixture.ConnectionString), NullLogger<ConsumerFailureStore>.Instance);

    [Fact]
    public async Task Failures_accumulate_for_one_event()
    {
        var (consumer, eventId) = Unique();
        var store = Store();

        Assert.Equal(1, await store.RecordFailureAsync(consumer, eventId, "boom", CancellationToken.None));
        Assert.Equal(2, await store.RecordFailureAsync(consumer, eventId, "boom", CancellationToken.None));
        Assert.Equal(3, await store.RecordFailureAsync(consumer, eventId, "boom", CancellationToken.None));
    }

    /// <summary>
    /// The point of the table. A fresh store is a fresh process, and the count
    /// carries on from where the last one left it rather than starting again.
    /// </summary>
    [Fact]
    public async Task A_restart_does_not_hand_a_message_a_fresh_allowance()
    {
        var (consumer, eventId) = Unique();

        await Store().RecordFailureAsync(consumer, eventId, "boom", CancellationToken.None);
        await Store().RecordFailureAsync(consumer, eventId, "boom", CancellationToken.None);

        // A different instance entirely, as after a restart.
        Assert.Equal(3, await Store().RecordFailureAsync(consumer, eventId, "boom", CancellationToken.None));
    }

    [Fact]
    public async Task Two_consumers_count_the_same_event_separately()
    {
        var (_, eventId) = Unique();
        var store = Store();

        await store.RecordFailureAsync("consumer-a", eventId, "boom", CancellationToken.None);
        await store.RecordFailureAsync("consumer-a", eventId, "boom", CancellationToken.None);

        Assert.Equal(1, await store.RecordFailureAsync("consumer-b", eventId, "boom", CancellationToken.None));
    }

    [Fact]
    public async Task Forgetting_clears_the_count_and_the_row()
    {
        var (consumer, eventId) = Unique();
        var store = Store();

        await store.RecordFailureAsync(consumer, eventId, "boom", CancellationToken.None);
        await store.ForgetAsync(consumer, eventId, CancellationToken.None);

        Assert.Equal(0, await RowsAsync(consumer, eventId));

        // And a later failure starts over, because this is a different episode.
        Assert.Equal(1, await store.RecordFailureAsync(consumer, eventId, "boom", CancellationToken.None));
    }

    /// <summary>An exception message is unbounded; the column is not.</summary>
    [Fact]
    public async Task A_very_long_error_is_stored_without_complaint()
    {
        var (consumer, eventId) = Unique();

        await Store().RecordFailureAsync(consumer, eventId, new string('x', 5_000), CancellationToken.None);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        var stored = await connection.ExecuteScalarAsync<string>(
            "SELECT last_error FROM consumer_failures WHERE consumer = @consumer AND event_id = @eventId",
            new { consumer, eventId });

        Assert.Equal(1_000, stored!.Length);
    }

    private async Task<long> RowsAsync(string consumer, long eventId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM consumer_failures WHERE consumer = @consumer AND event_id = @eventId",
            new { consumer, eventId });
    }

    /// <summary>Tests in this class share a database, so they must not share rows.</summary>
    private static (string Consumer, long EventId) Unique()
        => ($"consumer-{Guid.NewGuid():N}", Random.Shared.NextInt64(1, long.MaxValue));
}
