using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Payments.Core.Contracts;
using Payments.Core.Domain;
using Payments.Api.Observability;
using Payments.Core.Caching;
using Payments.Core.Observability;
using Payments.Core.Persistence;
using Payments.Core.RateLimiting;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Payments.Api.Endpoints;

public static class PaymentEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const int MaxIdempotencyKeyLength = 255;

    public static IEndpointRouteBuilder MapPayments(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/payments").WithTags("Payments");

        // "" maps the group prefix exactly; "/" would publish /v1/payments/.
        group.MapPost("", CreateAsync)
            .WithName("CreatePayment")
            .WithSummary("Creates a payment. Requires an Idempotency-Key header.")
            // Declared by hand because the created and replayed responses are
            // written as pre-serialised content, which carries no type metadata.
            .Produces<PaymentResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetPayment")
            .WithSummary("Fetches a payment by id.");

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreatePaymentRequest request,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        PaymentStore store,
        IdempotencyCache cache,
        TokenBucketLimiter limiter,
        IOptions<RateLimitOptions> rateLimit,
        IOptions<JsonOptions> jsonOptions,
        PaymentMetrics metrics,
        HttpContext context,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (Validate(request, idempotencyKey, out var amount) is { } failure)
        {
            return failure;
        }

        var allowance = await limiter.TryAcquireAsync(request.MerchantId, rateLimit.Value);

        if (!allowance.Allowed)
        {
            return Results.Problem(
                detail: "Too many requests. Retry after the interval in the Retry-After header.",
                title: "Rate limit exceeded",
                statusCode: StatusCodes.Status429TooManyRequests,
                extensions: new Dictionary<string, object?>
                {
                    ["retryAfterSeconds"] = Math.Ceiling(allowance.RetryAfter.TotalSeconds)
                });
        }

        var requestHash = RequestHash.Of(request);

        // Cheapest path first: a retry of a request already answered need not open
        // a transaction only to have the unique index reject it. A hit is
        // trustworthy because entries are written after the payment commits; a
        // miss says nothing, and the request carries on to the database.
        if (await cache.GetAsync(request.MerchantId, idempotencyKey!) is { } cached
            && cached.RequestHash == requestHash)
        {
            return Results.Content(cached.Body, "application/json", statusCode: cached.StatusCode);
        }

        var payment = Payment.Create(request.MerchantId, amount, clock);

        // Rendered here, not in the store: the API owns its representation, and
        // using the framework's own options means a replayed body is byte for
        // byte what a fresh one would have been.
        var response = new StoredResponse(
            StatusCodes.Status201Created,
            JsonSerializer.Serialize(PaymentResponse.From(payment), jsonOptions.Value.SerializerOptions));

        var message = new OutboxMessage(
            AggregateId: payment.Id,
            EventType: PaymentCreatedEvent.EventType,
            CorrelationId: context.CorrelationId(),
            Payload: JsonSerializer.Serialize(
                new PaymentCreatedEvent(
                    payment.Id,
                    payment.MerchantId,
                    payment.Amount.MinorUnits,
                    payment.Amount.Currency,
                    PaymentStatuses.ToWire(payment.Status),
                    payment.CreatedAt),
                jsonOptions.Value.SerializerOptions));

        var outcome = await store.CreateAsync(
            payment, idempotencyKey!, requestHash, response, message, ct);

        if (outcome is CreateOutcome.Created)
        {
            // Only now, and only after the commit. Caching before it would let a
            // rolled back payment be replayed as though it existed.
            await cache.SetAsync(request.MerchantId, idempotencyKey!, requestHash, response);
            metrics.PaymentCreated();
        }

        return outcome switch
        {
            CreateOutcome.Created => Json(response),

            CreateOutcome.Replayed replayed => Json(replayed.Response),

            CreateOutcome.InFlight => Problem(StatusCodes.Status409Conflict, "Request in flight",
                "Another request with this idempotency key is still being processed. Retry shortly."),

            CreateOutcome.KeyReused => Problem(StatusCodes.Status422UnprocessableEntity, "Idempotency key reused",
                "This idempotency key was already used with a different request body."),

            _ => throw new UnreachableException($"Unhandled outcome {outcome.GetType().Name}.")
        };
    }

    private static async Task<Results<Ok<PaymentResponse>, NotFound>> GetAsync(
        Guid id, PaymentStore store, CancellationToken ct)
    {
        var payment = await store.GetAsync(id, ct);

        return payment is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(PaymentResponse.From(payment));
    }

    /// <summary>Returns a problem response if the request cannot be accepted, otherwise null.</summary>
    private static IResult? Validate(CreatePaymentRequest request, string? idempotencyKey, out Money amount)
    {
        amount = default;

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Problem(StatusCodes.Status400BadRequest, "Missing idempotency key",
                $"An {IdempotencyHeader} header is required so that a retried request cannot charge twice.");
        }

        if (idempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid idempotency key",
                $"{IdempotencyHeader} must be at most {MaxIdempotencyKeyLength} characters.");
        }

        if (request.MerchantId == Guid.Empty)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", "merchantId is required.");
        }

        // Money itself permits any sign, because a ledger needs both and a zero
        // amount authorisation is a real operation. Requiring a positive amount is
        // a rule about creating a payment, so it lives with the request.
        if (request.AmountMinor <= 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request",
                "amountMinor must be greater than zero.");
        }

        return Money.TryCreate(request.AmountMinor, request.Currency, out amount, out var error)
            ? null
            : Problem(StatusCodes.Status400BadRequest, "Invalid request", error!);
    }

    private static IResult Json(StoredResponse response)
        => Results.Content(response.Body, "application/json", statusCode: response.StatusCode);

    private static IResult Problem(int status, string title, string detail)
        => Results.Problem(detail: detail, title: title, statusCode: status);
}
