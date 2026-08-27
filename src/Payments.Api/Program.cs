using Dapper;
using Payments.Api.Endpoints;
using Payments.Api.Persistence;

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

// Time is injected rather than read from DateTimeOffset.UtcNow so that expiry and
// reconciliation behaviour can be tested at chosen instants.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
app.MapPayments();

app.Run();

/// <summary>Exposed so integration tests can drive the real application.</summary>
public partial class Program;
