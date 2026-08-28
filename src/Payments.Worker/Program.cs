using Microsoft.Extensions.Http.Resilience;
using Payments.Core.Messaging;
using Payments.Core.Providers;
using Polly;
using Payments.Core.Persistence;
using Payments.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Same format as the API, and scopes included, so the correlation id a message
// carries is a searchable field on every line the worker writes about it.
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);

var connectionString = builder.Configuration.GetConnectionString("Payments")
    ?? throw new InvalidOperationException("ConnectionStrings:Payments is not configured.");

builder.Services.AddPaymentsPersistence(connectionString);
builder.Services.AddSingleton(TimeProvider.System);

// Scoped, and resolved once per message. The consumer itself is a singleton, so
// holding these directly would make them captive and hand every message the same
// database connection.
builder.Services.AddScoped<PaymentCreatedHandler>();
builder.Services.AddScoped<PaymentPendingHandler>();
builder.Services.AddScoped<PaymentReconciler>();
builder.Services.AddScoped<PaymentResolvedHandler>();
builder.Services.Configure<LedgerOptions>(builder.Configuration.GetSection(LedgerOptions.Section));

var providerName = builder.Configuration.GetValue("Provider:Name", "fake");

builder.Services.AddHttpClient("provider", client =>
    {
        client.BaseAddress = new Uri(builder.Configuration.GetValue("Provider:BaseUrl", "http://localhost:8081")!);

        // Shorter than the provider's worst case on purpose. Waiting longer does
        // not produce an answer, it only holds a worker thread while one payment
        // sits undecided.
        client.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Provider:TimeoutSeconds", 5));
    })
    .AddResilienceHandler("provider", pipeline =>
    {
        // No retry. A charge that timed out may already have moved money, and
        // retrying it is only safe where the provider honours an idempotency key.
        // Some do and some quietly do not, so the platform does not assume it:
        // an unanswered charge becomes unknown and reconciliation settles it.
        pipeline.AddCircuitBreaker(new Polly.CircuitBreaker.CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            // Once a provider is clearly down, queuing every payment behind a
            // five second timeout each helps nobody. Failing immediately puts
            // them in unknown sooner, where reconciliation can find them.
            FailureRatio = 0.5,
            MinimumThroughput = 10,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(15)
        });
    });

builder.Services.AddScoped<IPaymentProvider>(services => new HttpPaymentProvider(
    providerName,
    services.GetRequiredService<IHttpClientFactory>().CreateClient("provider"),
    services.GetRequiredService<ILogger<HttpPaymentProvider>>()));

builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.Section));
builder.Services.Configure<ConsumerOptions>(builder.Configuration.GetSection(ConsumerOptions.Section));

builder.Services.Configure<ReconciliationOptions>(
    builder.Configuration.GetSection(ReconciliationOptions.Section));

builder.Services.AddHostedService<PaymentEventConsumer>();

if (builder.Configuration.GetValue($"{ReconciliationOptions.Section}:Enabled", true))
{
    builder.Services.AddHostedService<ReconciliationService>();
}

// Migrations are the API's job. Running them from two places would mean two
// things racing to define the same schema, and the advisory lock only makes that
// safe, not sensible.
await builder.Build().RunAsync();
