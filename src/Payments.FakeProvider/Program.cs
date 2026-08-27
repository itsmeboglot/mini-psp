using System.Collections.Concurrent;

// A stand-in for a payment provider, built to misbehave.
//
// Real providers fail in ways that are hard to arrange on demand: they time out
// mid-charge, return a 500 after taking the money, and answer a retry as though
// it were a new request. This one does all of that deterministically, driven by
// the last two digits of the amount, so a test can ask for a specific disaster.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Charges already seen, by idempotency key. A provider that did not keep this is
// a provider that double charges on every retry.
var charges = new ConcurrentDictionary<string, ChargeResponse>();

app.MapPost("/charges", async (ChargeRequest request, HttpContext context, ILoggerFactory loggers) =>
{
    var log = loggers.CreateLogger("FakeProvider");
    var key = context.Request.Headers["Idempotency-Key"].ToString();

    if (string.IsNullOrWhiteSpace(key))
    {
        return Results.BadRequest(new { error = "Idempotency-Key header is required." });
    }

    // The behaviour that makes retrying a charge safe. Without it, every timeout
    // this provider causes would become a second charge.
    if (charges.TryGetValue(key, out var seen))
    {
        log.LogInformation("Replaying charge for key {Key}", key);
        return Results.Ok(seen);
    }

    var scenario = request.AmountMinor % 100;

    switch (scenario)
    {
        case 1:
            log.LogInformation("Declining charge for key {Key}", key);
            return Store(key, new ChargeResponse("declined", ProviderReference(), "insufficient funds"));

        case 2:
            // Takes far longer than any caller will wait. The caller times out
            // without ever learning the outcome, which is the whole point.
            log.LogWarning("Hanging on charge for key {Key}", key);
            await Task.Delay(TimeSpan.FromSeconds(60), context.RequestAborted);
            return Store(key, new ChargeResponse("authorized", ProviderReference(), null));

        case 3:
            // Fails after deciding. From outside this is indistinguishable from
            // failing before deciding, and the money may well have moved.
            log.LogWarning("Erroring after charging for key {Key}", key);
            Store(key, new ChargeResponse("authorized", ProviderReference(), null));
            return Results.StatusCode(StatusCodes.Status500InternalServerError);

        default:
            log.LogInformation("Authorising charge for key {Key}", key);
            return Store(key, new ChargeResponse("authorized", ProviderReference(), null));
    }

    IResult Store(string idempotencyKey, ChargeResponse response)
    {
        charges[idempotencyKey] = response;
        return Results.Ok(response);
    }
});

// What a reconciliation job asks when it does not know how a charge ended.
app.MapGet("/charges/{key}", (string key) =>
    charges.TryGetValue(key, out var charge) ? Results.Ok(charge) : Results.NotFound());

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

static string ProviderReference() => $"fp_{Guid.CreateVersion7():N}";

internal sealed record ChargeRequest(long AmountMinor, string Currency, string MerchantReference);

internal sealed record ChargeResponse(string Status, string ProviderReference, string? Reason);
