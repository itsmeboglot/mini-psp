using System.Diagnostics.Metrics;

namespace Payments.Core.Observability;

/// <summary>
/// What this platform reports about itself.
/// </summary>
/// <remarks>
/// Chosen to answer the questions someone would actually be asked at three in the
/// morning: is money still moving, is anything stuck, and is the outbox draining.
/// Request rates and latencies come from the ASP.NET instrumentation and are not
/// duplicated here.
///
/// Deliberately no merchant id on any of these. A tag with unbounded values
/// multiplies every series by the number of merchants, which is how a metrics
/// backend falls over.
/// </remarks>
public sealed class PaymentMetrics : IDisposable
{
    public const string MeterName = "Payments";

    private readonly Meter _meter = new(MeterName);

    private readonly Counter<long> _created;
    private readonly Counter<long> _resolved;
    private readonly Counter<long> _published;
    private readonly Counter<long> _reconciled;
    private readonly Histogram<double> _providerDuration;
    private readonly Counter<long> _discrepancies;

    public PaymentMetrics()
    {
        _created = _meter.CreateCounter<long>(
            "payments.created", "payments", "Payments accepted.");

        _resolved = _meter.CreateCounter<long>(
            "payments.resolved", "payments", "Payments that reached an outcome, tagged with which.");

        _published = _meter.CreateCounter<long>(
            "outbox.published", "events", "Events dispatched to the broker.");

        _reconciled = _meter.CreateCounter<long>(
            "payments.reconciled", "payments", "Payments an outcome was recovered for.");

        _providerDuration = _meter.CreateHistogram<double>(
            "provider.charge.duration", "ms", "How long a provider took to answer a charge.");

        _discrepancies = _meter.CreateCounter<long>(
            "settlement.discrepancies", "payments",
            "Payments the provider's report contradicts. Should be zero, and is worth waking someone for.");
    }

    public void PaymentCreated() => _created.Add(1);

    /// <param name="status">
    /// A closed set, so the series count stays fixed however many payments there
    /// are. "unknown" rising is the one worth alerting on: it means outcomes are
    /// being lost, not that payments are failing.
    /// </param>
    public void PaymentResolved(string status, string provider)
        => _resolved.Add(1, new KeyValuePair<string, object?>("status", status),
                            new KeyValuePair<string, object?>("provider", provider));

    public void EventsPublished(int count) => _published.Add(count);

    public void PaymentReconciled(string status)
        => _reconciled.Add(1, new KeyValuePair<string, object?>("status", status));

    public void SettlementDiscrepancy(string ourStatus, string providerStatus)
        => _discrepancies.Add(1, new KeyValuePair<string, object?>("ours", ourStatus),
                                 new KeyValuePair<string, object?>("theirs", providerStatus));

    public void ProviderAnswered(string provider, string verdict, double milliseconds)
        => _providerDuration.Record(milliseconds,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("verdict", verdict));

    public void Dispose() => _meter.Dispose();
}
