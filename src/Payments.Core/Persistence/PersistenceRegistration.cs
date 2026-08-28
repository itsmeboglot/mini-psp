using Dapper;
using Payments.Core.Caching;
using Payments.Core.RateLimiting;
using StackExchange.Redis;

namespace Payments.Core.Persistence;

public static class PersistenceRegistration
{
    /// <summary>
    /// Registers everything needed to read and write payments, and configures
    /// Dapper for this process.
    /// </summary>
    /// <remarks>
    /// The Dapper configuration lives here rather than in each host's startup
    /// because its handler registry is process wide and easy to forget. It was
    /// forgotten: the worker read the same rows the API read fine and failed on
    /// every one, because nothing had told its copy of Dapper how a timestamptz
    /// becomes a DateTimeOffset. A host that registers persistence now cannot
    /// miss it.
    ///
    /// AddTypeHandler replaces rather than accumulates, so calling this more than
    /// once in a process is harmless.
    /// </remarks>
    public static IServiceCollection AddPaymentsPersistence(
        this IServiceCollection services, string connectionString)
    {
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());

        services.AddSingleton(_ => new DbConnectionFactory(connectionString));
        services.AddSingleton<MigrationRunner>();

        services.AddScoped<PaymentStore>();
        services.AddScoped<OutboxStore>();
        services.AddScoped<IdempotentEventProcessor>();
        services.AddScoped<LedgerStore>();

        return services;
    }

    /// <summary>
    /// Adds the Redis backed cache and rate limiter, or working versions of both
    /// that do nothing when no connection string is configured.
    /// </summary>
    /// <remarks>
    /// Registered even without Redis on purpose. ADR 0003 claims the cache is
    /// only an optimisation, and the honest test of that claim is that the
    /// platform runs correctly with Redis absent rather than refusing to start.
    /// </remarks>
    public static IServiceCollection AddRedis(this IServiceCollection services, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddSingleton<IConnectionMultiplexer?>(_ => null);
        }
        else
        {
            services.AddSingleton<IConnectionMultiplexer?>(provider =>
            {
                var configuration = ConfigurationOptions.Parse(connectionString);

                // Rather than blocking startup until Redis answers: it is optional,
                // and an optional dependency that can stop the process is not.
                configuration.AbortOnConnectFail = false;

                return ConnectionMultiplexer.Connect(configuration);
            });
        }

        services.AddSingleton<IdempotencyCache>();
        services.AddSingleton<TokenBucketLimiter>();

        return services;
    }
}
