namespace Payments.Core.Observability;

/// <summary>
/// The identifier that ties everything done because of one request together.
/// </summary>
/// <remarks>
/// A payment is handled by three processes and two of them run minutes after the
/// request that caused them. Without a value carried across those hops, answering
/// "what happened to this payment" means correlating timestamps by eye across the
/// API, the dispatcher and the worker.
///
/// So it travels: accepted from the caller or minted at the edge, attached to the
/// outbox row inside the same transaction as the payment, put on the Kafka
/// message as a header, and reopened as a log scope by whatever consumes it.
/// </remarks>
public static class CorrelationId
{
    public const string Header = "X-Correlation-Id";

    public const string MessageHeader = "correlation-id";

    /// <summary>The name every log scope uses, so one query finds the whole story.</summary>
    public const string LogProperty = "CorrelationId";

    /// <summary>
    /// Keeps a caller's value if it is usable, and mints one otherwise.
    /// </summary>
    /// <remarks>
    /// A caller's value is accepted because a merchant tracing a problem on their
    /// side wants the same string on both. It is length limited and stripped of
    /// anything but plain characters: it reaches logs, and a value chosen by
    /// someone else is not to be trusted with newlines.
    /// </remarks>
    public static string Accept(string? supplied)
    {
        if (string.IsNullOrWhiteSpace(supplied))
        {
            return Guid.CreateVersion7().ToString("N");
        }

        var trimmed = supplied.Trim();

        if (trimmed.Length > 64 || !trimmed.All(IsAcceptable))
        {
            return Guid.CreateVersion7().ToString("N");
        }

        return trimmed;
    }

    private static bool IsAcceptable(char character)
        => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':' or '.';
}
