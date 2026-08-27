using System.Reflection;
using Dapper;
using Npgsql;

namespace Payments.Core.Persistence;

/// <summary>
/// Brings the database up to the schema this build expects.
/// </summary>
/// <remarks>
/// See docs/adr/0004-sql-migrations-over-ef-core.md for why the schema is
/// versioned as SQL rather than generated from a model.
/// </remarks>
public sealed class MigrationRunner(DbConnectionFactory db, ILogger<MigrationRunner> logger)
{
    /// <summary>
    /// Any constant will do; it only has to be the same in every instance. Chosen
    /// once and never changed, because changing it would let an old build and a
    /// new one migrate at the same time.
    /// </summary>
    private const long AdvisoryLockKey = 8_723_411_907_442_001;

    private const string EnsureHistoryTable = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version    text        PRIMARY KEY,
            applied_at timestamptz NOT NULL DEFAULT now()
        );
        """;

    public async Task RunAsync(CancellationToken ct)
    {
        var migrations = LoadMigrations();

        await using var connection = await db.OpenAsync(ct);

        // Serialises startup across instances. Released when the connection
        // closes, so a process that dies mid-migration does not wedge the others.
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_lock(@Key);", new { Key = AdvisoryLockKey }, cancellationToken: ct));

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(EnsureHistoryTable, cancellationToken: ct));

            var applied = (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT version FROM schema_migrations;", cancellationToken: ct))).ToHashSet();

            var pending = migrations.Where(migration => !applied.Contains(migration.Version)).ToList();

            if (pending.Count == 0)
            {
                logger.LogInformation("Schema is up to date at {Version}",
                    migrations.LastOrDefault()?.Version ?? "(empty)");
                return;
            }

            foreach (var migration in pending)
            {
                await ApplyAsync(connection, migration, ct);
            }

            logger.LogInformation("Applied {Count} migration(s), now at {Version}",
                pending.Count, pending[^1].Version);
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_unlock(@Key);", new { Key = AdvisoryLockKey }, cancellationToken: ct));
        }
    }

    private async Task ApplyAsync(NpgsqlConnection connection, Migration migration, CancellationToken ct)
    {
        logger.LogInformation("Applying migration {Version}", migration.Version);

        // The statements and the record of having run them commit together.
        // PostgreSQL rolls back DDL, so a migration that fails halfway leaves
        // neither half of itself behind.
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            migration.Sql, transaction: transaction, cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO schema_migrations (version) VALUES (@Version);",
            new { migration.Version }, transaction, cancellationToken: ct));

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Reads the migrations embedded in this assembly, ordered by file name.
    /// </summary>
    /// <remarks>
    /// Embedded rather than read from disk so that the container carries exactly
    /// the migrations the build was compiled against, with no chance of running
    /// against a directory that says something else.
    /// </remarks>
    private static List<Migration> LoadMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();

        // Derived from the assembly rather than written out: moving these files
        // to another project silently found nothing, and the only symptom was
        // every table being missing.
        var prefix = $"{assembly.GetName().Name}.Migrations.";

        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal)
                           && name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name => new Migration(
                Version: name[prefix.Length..^".sql".Length],
                Sql: Read(assembly, name)))
            .ToList();
    }

    private static string Read(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Migration {resourceName} could not be read.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record Migration(string Version, string Sql);
}
