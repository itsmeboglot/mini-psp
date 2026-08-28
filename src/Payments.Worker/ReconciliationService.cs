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
/// Runs <see cref="PaymentReconciler"/> on a schedule.
/// </summary>
/// <remarks>
/// Without it, unknown is a state a payment enters and never leaves: the provider
/// timed out, nobody knows whether the money moved, and nothing asks. This is the
/// part of a payment platform that turns an unanswered question into an answer,
/// and it is what makes unknown safe to write in the first place.
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
                using var scope = scopes.CreateScope();
                var resolved = await scope.ServiceProvider
                    .GetRequiredService<PaymentReconciler>()
                    .SweepAsync(stoppingToken);

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
}
