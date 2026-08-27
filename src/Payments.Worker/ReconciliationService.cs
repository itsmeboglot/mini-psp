using System.Text.Json;
using Microsoft.Extensions.Options;
using Payments.Core.Contracts;
using Payments.Core.Domain;
using Payments.Core.Persistence;
using Payments.Core.Providers;

namespace Payments.Worker;

public sealed class ReconciliationOptions
{
    public const string Section = "Reconciliation";

    public bool Enabled { get; set; } = true;

    public int BatchSize { get; set; } = 50;

    /// <summary>How often to look for payments whose outcome was never learned.</summary>
    public int IntervalSeconds { get; set; } = 15;

    /// <summary>
    /// How old a payment must be before it is asked about. A charge that is still
    /// in flight would otherwise be reconciled while its own answer is on the way.
    /// </summary>
    public int GraceSeconds { get; set; } = 10;

    /// <summary>How long to leave a payment alone between asking about it.</summary>
    public int RetryAfterSeconds { get; set; } = 30;

    /// <summary>
    /// How many times a provider must deny having heard of a payment before that
    /// is believed. Status APIs go briefly inconsistent after an outage, and one
    /// "never heard of it" during that window is not evidence of anything.
    /// </summary>
    public int AttemptsBeforeBelievingNotFound { get; set; } = 3;
}

/// <summary>
/// Resolves payments whose outcome the platform never learned.
/// </summary>
/// <remarks>
/// Without this, unknown is a state a payment enters and never leaves: the
/// provider timed out, nobody knows whether the money moved, and nothing asks.
/// This is the part of a payment platform that turns an unanswered question into
/// an answer, and it is the reason unknown is safe to write in the first place.
/// </remarks>
public sealed class ReconciliationService(
    IServiceScopeFactory scopes,
    IOptions<ReconciliationOptions> options,
    TimeProvider clock,
    ILogger<ReconciliationService> logger) : BackgroundService
{
    private readonly ReconciliationOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Reconciliation started, every {Interval}s", _options.IntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var resolved = await SweepAsync(stoppingToken);
                if (resolved > 0)
                {
                    logger.LogInformation("Reconciliation resolved {Count} payment(s)", resolved);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // Never let the sweep die. A stopped reconciler is silent, and its
                // symptom is payments quietly accumulating in unknown.
                logger.LogError(e, "Reconciliation sweep failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.IntervalSeconds), clock, stoppingToken);
        }

        logger.LogInformation("Reconciliation stopped");
    }

    private async Task<int> SweepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var payments = scope.ServiceProvider.GetRequiredService<PaymentStore>();
        var provider = scope.ServiceProvider.GetRequiredService<IPaymentProvider>();
        var db = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();

        var unresolved = await payments.ClaimUnresolvedAsync(
            _options.BatchSize,
            TimeSpan.FromSeconds(_options.GraceSeconds),
            TimeSpan.FromSeconds(_options.RetryAfterSeconds),
            ct);

        var resolved = 0;

        foreach (var candidate in unresolved)
        {
            if (await ResolveAsync(candidate, payments, provider, db, ct))
            {
                resolved++;
            }
        }

        return resolved;
    }

    private async Task<bool> ResolveAsync(
        UnresolvedPayment candidate,
        PaymentStore payments,
        IPaymentProvider provider,
        DbConnectionFactory db,
        CancellationToken ct)
    {
        var payment = candidate.Payment;
        var status = await provider.GetStatusAsync(payment.Id.ToString(), ct);

        var next = status.Verdict switch
        {
            ProviderVerdict.Authorized => PaymentStatus.Authorized,
            ProviderVerdict.Declined => PaymentStatus.Failed,

            // Only once the provider has denied it enough times to be believed.
            ProviderVerdict.NotFound when candidate.Attempts >= _options.AttemptsBeforeBelievingNotFound
                => PaymentStatus.Failed,

            // Still nothing conclusive. Leave it where it is and ask again later;
            // the claim already recorded that it was tried.
            _ => (PaymentStatus?)null
        };

        if (next is null)
        {
            logger.LogInformation(
                "Payment {PaymentId} still unresolved after {Attempts} attempt(s): {Verdict}",
                payment.Id, candidate.Attempts, status.Verdict);

            return false;
        }

        var transitioned = payment.TransitionTo(next.Value);

        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        if (!await payments.TryApplyTransitionAsync(
                connection, transaction, transitioned, payment.Version, provider.Name, status.Reference, ct))
        {
            // The original charge answered late, or another instance got there
            // first. Either way somebody now knows more than this sweep did.
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }

        await payments.AppendToOutboxAsync(connection, transaction, new OutboxMessage(
            AggregateId: transitioned.Id,
            EventType: PaymentResolvedEvent.EventType,
            Payload: JsonSerializer.Serialize(new PaymentResolvedEvent(
                transitioned.Id,
                transitioned.MerchantId,
                transitioned.Amount.MinorUnits,
                transitioned.Amount.Currency,
                PaymentStatuses.ToWire(transitioned.Status),
                provider.Name,
                status.Reference,
                $"reconciled after {candidate.Attempts} attempt(s): {status.Reason}",
                transitioned.CreatedAt), new JsonSerializerOptions(JsonSerializerDefaults.Web))), ct);

        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Payment {PaymentId} reconciled to {Status} on attempt {Attempts}",
            payment.Id, next, candidate.Attempts);

        return true;
    }
}
