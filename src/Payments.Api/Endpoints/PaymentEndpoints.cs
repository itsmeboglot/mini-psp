using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Payments.Api.Contracts;
using Payments.Api.Domain;
using Payments.Api.Persistence;

namespace Payments.Api.Endpoints;

public static class PaymentEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapPayments(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/payments").WithTags("Payments");

        group.MapPost("/", CreateAsync)
            .WithName("CreatePayment")
            .WithSummary("Creates a payment. Requires an Idempotency-Key header.");

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetPayment")
            .WithSummary("Fetches a payment by id.");

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreatePaymentRequest request,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        PaymentStore store,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Problem(StatusCodes.Status400BadRequest, "Missing idempotency key",
                $"A {IdempotencyHeader} header is required so that a retried request cannot charge twice.");
        }

        if (idempotencyKey.Length > 255)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid idempotency key",
                $"{IdempotencyHeader} must be at most 255 characters.");
        }

        if (request.MerchantId == Guid.Empty)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", "merchantId is required.");
        }

        if (!Money.TryCreate(request.AmountMinor, request.Currency, out var amount, out var error))
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", error!);
        }

        var outcome = await store.CreateAsync(
            request.MerchantId, idempotencyKey, RequestHash.Of(request), amount, ct);

        return outcome switch
        {
            { Kind: CreateOutcomeKind.Created, Response: { } r } =>
                Results.Content(r.Body, "application/json", statusCode: r.StatusCode),

            { Kind: CreateOutcomeKind.Replayed, Response: { } r } =>
                Results.Content(r.Body, "application/json", statusCode: r.StatusCode),

            // The key is held by a request that has not committed yet.
            { Kind: CreateOutcomeKind.Replayed, Response: null } =>
                Problem(StatusCodes.Status409Conflict, "Request in flight",
                    "Another request with this idempotency key is still being processed. Retry shortly."),

            { Kind: CreateOutcomeKind.KeyReused } =>
                Problem(StatusCodes.Status422UnprocessableEntity, "Idempotency key reused",
                    "This idempotency key was already used with a different request body."),

            _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
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

    private static IResult Problem(int status, string title, string detail)
        => Results.Problem(detail: detail, title: title, statusCode: status);
}
