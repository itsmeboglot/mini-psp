using Dapper;
using Payments.Api.Endpoints;
using Payments.Api.Persistence;

SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Payments")
    ?? throw new InvalidOperationException("ConnectionStrings:Payments is not configured.");

builder.Services.AddSingleton(new DbConnectionFactory(connectionString));
builder.Services.AddScoped<PaymentStore>();
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
