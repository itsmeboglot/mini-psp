using Dapper;
using Payments.Api.Endpoints;
using Payments.Core.Messaging;
using Payments.Api.Outbox;
using Payments.Core.Persistence;

// Dapper's handler registry is process wide, so it is configured once here.
SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Payments")
    ?? throw new InvalidOperationException("ConnectionStrings:Payments is not configured.");

// Registered through a factory rather than as a ready-made instance: the
// container only disposes what it creates, and this owns an Npgsql data source
// that holds a connection pool.
builder.Services.AddSingleton(_ => new DbConnectionFactory(connectionString));
builder.Services.AddScoped<PaymentStore>();
builder.Services.AddSingleton<MigrationRunner>();

// Time is injected rather than read from DateTimeOffset.UtcNow so that expiry and
// reconciliation behaviour can be tested at chosen instants.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.Section));
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection(OutboxOptions.Section));
builder.Services.AddScoped<OutboxStore>();
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
