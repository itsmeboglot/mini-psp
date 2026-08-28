using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Payments.Core.Persistence;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Payments.Tests;

/// <summary>
/// The application with a real Redis behind it, for the two things Redis does:
/// remembering answers and refusing traffic.
/// </summary>
/// <remarks>
/// Separate from <see cref="PaymentsApiFixture"/>, which deliberately runs
/// without Redis. Both are worth having: one proves the platform still works when
/// the optional dependency is absent, the other that it uses it when present.
/// </remarks>
public sealed class RedisFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("minipsp").WithUsername("minipsp").WithPassword("minipsp")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public string RedisConnectionString => _redis.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        await new MigrationRunner(
                new DbConnectionFactory(ConnectionString),
                NullLogger<MigrationRunner>.Instance)
            .RunAsync(CancellationToken.None);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Payments", ConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", RedisConnectionString);
        builder.UseSetting("Outbox:Enabled", "false");

        // Small enough that a handful of requests in a test can exhaust it, and
        // refilling slowly enough that they do not refill mid-test.
        builder.UseSetting("RateLimit:Capacity", "3");
        builder.UseSetting("RateLimit:RefillPerSecond", "0.5");
    }
}
