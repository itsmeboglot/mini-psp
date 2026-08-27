namespace Payments.Api.Messaging;

public sealed class KafkaOptions
{
    public const string Section = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";

    /// <summary>
    /// One topic for every payment event. Consumers filter by event type rather
    /// than by subscribing to a topic per type, which keeps ordering per payment
    /// intact: two events about one payment cannot land in different topics and
    /// arrive out of order.
    /// </summary>
    public string Topic { get; set; } = "payments.events";

    /// <summary>How long a single publish may take before it counts as failed.</summary>
    public int PublishTimeoutSeconds { get; set; } = 5;
}
