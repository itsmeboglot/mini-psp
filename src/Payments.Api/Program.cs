using Payments.Api.Endpoints;
using Payments.Core.RateLimiting;
using Payments.Core.Messaging;
using Payments.Api.Outbox;
using Payments.Core.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Payments")
    ?? throw new InvalidOperationException("ConnectionStrings:Payments is not configured.");

builder.Services.AddPaymentsPersistence(connectionString);

// Optional by design: the cache is an optimisation and the limiter fails open, so
// the platform is correct without Redis, only slower and less protected.
builder.Services.AddRedis(builder.Configuration.GetConnectionString("Redis"));
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection(RateLimitOptions.Section));

// Time is injected rather than read from DateTimeOffset.UtcNow so that expiry and
// reconciliation behaviour can be tested at chosen instants.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.Section));
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection(OutboxOptions.Section));
builder.Services.AddSingleton<EventPublisher>();
if (builder.Configuration.GetValue($"{OutboxOptions.Section}:{nameof(OutboxOptions.Enabled)}", true))
{
    builder.Services.AddHostedService<OutboxDispatcher>();
}
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgres");
builder.Services.AddOpenApi();

var app = builder.Build();

// Before anything serves a request or dispatches an event against a schema that
// might be older than this build.
await app.Services.GetRequiredService<MigrationRunner>().RunAsync(CancellationToken.None);

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health").ExcludeFromDescription();
app.MapPayments();

app.Run();

/// <summary>Exposed so integration tests can drive the real application.</summary>
public partial class Program;
