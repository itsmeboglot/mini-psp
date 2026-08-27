namespace Payments.Core.Persistence;

/// <summary>
/// An event waiting to be published, written in the transaction that caused it.
/// </summary>
/// <remarks>
/// Opaque to this layer in the same way <see cref="StoredResponse"/> is: the
/// payload is serialised by whoever owns the contract, and the store only has to
/// commit it atomically with the change it describes.
/// See docs/adr/0001-transactional-outbox.md
/// </remarks>
/// <param name="AggregateId">
/// The entity the event is about. Becomes the Kafka partition key, so events for
/// one payment stay ordered relative to each other.
/// </param>
public sealed record OutboxMessage(Guid AggregateId, string EventType, string Payload);
