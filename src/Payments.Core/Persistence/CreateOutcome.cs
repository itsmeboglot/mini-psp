namespace Payments.Core.Persistence;

/// <summary>An HTTP response as it was stored against an idempotency key.</summary>
/// <remarks>
/// Opaque to this layer: the status code and body are produced by whoever owns
/// the API contract and are replayed back verbatim.
/// </remarks>
public sealed record StoredResponse(int StatusCode, string Body);

/// <summary>
/// What happened to a create attempt.
/// </summary>
/// <remarks>
/// A closed hierarchy rather than an enum plus a nullable payload: only
/// <see cref="Replayed"/> carries a response, and expressing that in the type
/// means no caller has to decide what a missing one implies.
/// </remarks>
public abstract record CreateOutcome
{
    private CreateOutcome() { }

    /// <summary>A new payment was inserted by this request.</summary>
    public sealed record Created : CreateOutcome;

    /// <summary>The key was already used with the same body; here is what it returned.</summary>
    public sealed record Replayed(StoredResponse Response) : CreateOutcome;

    /// <summary>
    /// Another request holds the key and has not committed, so its response does
    /// not exist yet. Distinct from <see cref="Replayed"/>: there is nothing to
    /// replay, and the caller must retry rather than be told anything about the
    /// payment.
    /// </summary>
    public sealed record InFlight : CreateOutcome;

    /// <summary>The key was already used with a different body. A client defect.</summary>
    public sealed record KeyReused : CreateOutcome;
}
