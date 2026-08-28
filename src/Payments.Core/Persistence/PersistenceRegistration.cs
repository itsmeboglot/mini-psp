using Dapper;

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
}
