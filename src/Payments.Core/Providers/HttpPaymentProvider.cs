using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Payments.Core.Providers;

/// <summary>
/// Charges through a provider that speaks HTTP.
/// </summary>
/// <remarks>
/// The rule this class exists to enforce: anything short of a clear answer is
/// <see cref="ProviderVerdict.Unknown"/>, never a failure. A timeout, a dropped
/// connection or a 500 all mean the same thing from here, which is that the
/// provider may have taken the money and may not have. Recording that as failed
/// would tell a merchant their customer was not charged while the money is on its
/// way, and no amount of retrying afterwards can undo that claim.
/// </remarks>
public sealed class HttpPaymentProvider(
    string name,
    HttpClient client,
    ILogger<HttpPaymentProvider> logger) : IPaymentProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name { get; } = name;

    public async Task<ProviderResult> ChargeAsync(ProviderCharge charge, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/charges")
        {
            Content = JsonContent.Create(new
            {
                charge.AmountMinor,
                charge.Currency,
                MerchantReference = charge.PaymentId.ToString()
            }, options: Json)
        };

        // The same key on every attempt at this payment. It is what makes a
        // second call safe when the first one told us nothing.
        request.Headers.TryAddWithoutValidation("Idempotency-Key", charge.IdempotencyKey);

        try
        {
            using var response = await client.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.OK)
            {
                var body = await response.Content.ReadFromJsonAsync<ChargeResponse>(Json, ct);
                return Interpret(charge, body);
            }

            logger.LogWarning(
                "Provider {Provider} answered {Status} for payment {PaymentId}",
                Name, (int)response.StatusCode, charge.PaymentId);

            return new ProviderResult(ProviderVerdict.Unknown, null, $"http {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The timeout fired rather than the caller giving up. The request was
            // sent; whether it was acted on is not knowable from here.
            logger.LogWarning("Provider {Provider} timed out on payment {PaymentId}", Name, charge.PaymentId);
            return new ProviderResult(ProviderVerdict.Unknown, null, "timeout");
        }
        catch (HttpRequestException e)
        {
            logger.LogWarning(e, "Provider {Provider} unreachable for payment {PaymentId}", Name, charge.PaymentId);
            return new ProviderResult(ProviderVerdict.Unknown, null, "transport");
        }
    }

    private ProviderResult Interpret(ProviderCharge charge, ChargeResponse? body)
    {
        if (body is null)
        {
            logger.LogError("Provider {Provider} returned an empty body for {PaymentId}", Name, charge.PaymentId);
            return new ProviderResult(ProviderVerdict.Unknown, null, "empty response");
        }

        return body.Status switch
        {
            "authorized" => new ProviderResult(ProviderVerdict.Authorized, body.ProviderReference, null),
            "declined" => new ProviderResult(ProviderVerdict.Declined, body.ProviderReference, body.Reason),

            // A status this connector does not recognise is not a decline. A new
            // value the provider added is unknown until someone teaches us what it
            // means.
            _ => new ProviderResult(ProviderVerdict.Unknown, body.ProviderReference, $"unrecognised '{body.Status}'")
        };
    }

    private sealed record ChargeResponse(string Status, string ProviderReference, string? Reason);
}
