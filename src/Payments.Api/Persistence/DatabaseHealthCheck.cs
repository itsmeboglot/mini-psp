using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Payments.Api.Persistence;

/// <summary>
/// Reports the instance unhealthy when PostgreSQL cannot be reached.
/// </summary>
/// <remarks>
/// A liveness endpoint that answers "ok" regardless of the database is worse than
/// having none: an orchestrator keeps routing payment traffic to an instance that
/// cannot record a single one.
/// </remarks>
public sealed class DatabaseHealthCheck(DbConnectionFactory db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await db.OpenAsync(cancellationToken);

            await connection.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT 1;", cancellationToken: cancellationToken));

            return HealthCheckResult.Healthy();
        }
        // Cancellation is not ill health: during a graceful shutdown the host
        // cancels its probes, and reporting a failure there would be noise.
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is not reachable.", e);
        }
    }
}
