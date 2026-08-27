using Payments.Core.Messaging;
using Payments.Core.Persistence;
using Payments.Worker;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Payments")
    ?? throw new InvalidOperationException("ConnectionStrings:Payments is not configured.");

builder.Services.AddPaymentsPersistence(connectionString);
builder.Services.AddSingleton(TimeProvider.System);

// Scoped, and resolved once per message. The consumer itself is a singleton, so
// holding these directly would make them captive and hand every message the same
// database connection.
builder.Services.AddScoped<PaymentCreatedHandler>();

builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.Section));
builder.Services.Configure<ConsumerOptions>(builder.Configuration.GetSection(ConsumerOptions.Section));

builder.Services.AddHostedService<PaymentEventConsumer>();

// Migrations are the API's job. Running them from two places would mean two
// things racing to define the same schema, and the advisory lock only makes that
// safe, not sensible.
await builder.Build().RunAsync();
