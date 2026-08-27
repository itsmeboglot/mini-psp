using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Payments.Api.Tests;

/// <summary>
/// Runs the real application against a real PostgreSQL in a container. The
/// behaviour under test — a unique index rejecting a concurrent insert — cannot
/// be observed against a fake or an in-memory provider, so there is no version
/// of these tests worth writing without a database.
/// </summary>
public sealed class PaymentsApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("minipsp")
        .WithUsername("minipsp")
        .WithPassword("minipsp")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(await File.ReadAllTextAsync(FindSchemaFile()));
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.UseSetting("ConnectionStrings:Payments", ConnectionString);

    public async Task<long> CountPaymentsAsync(Guid merchantId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM payments WHERE merchant_id = @merchantId", new { merchantId });
    }

    public async Task<long> CountOutboxAsync(Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM outbox WHERE aggregate_id = @aggregateId", new { aggregateId });
    }

    public async Task<long> CountUnpublishedOutboxAsync(Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM outbox WHERE aggregate_id = @aggregateId AND published_at IS NULL",
            new { aggregateId });
    }

    public async Task<OutboxRow> ReadOutboxAsync(Guid aggregateId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleAsync<OutboxRow>(
            """
            SELECT aggregate_id AS AggregateId, event_type AS EventType, payload::text AS Payload
            FROM outbox WHERE aggregate_id = @aggregateId
            """, new { aggregateId });
    }

    public sealed record OutboxRow(Guid AggregateId, string EventType, string Payload);

    /// <summary>Walks up from the test binaries to the repository root.</summary>
    private static string FindSchemaFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "db", "001_init.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("db/001_init.sql was not found above the test output directory.");
    }
}
