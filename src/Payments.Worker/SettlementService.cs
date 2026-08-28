using Microsoft.Extensions.Options;

namespace Payments.Worker;

/// <summary>Runs <see cref="SettlementReconciler"/> on a schedule.</summary>
public sealed class SettlementService(
    IServiceScopeFactory scopes,
    IOptions<SettlementOptions> options,
    TimeProvider clock,
    ILogger<SettlementService> logger) : BackgroundService
{
    private readonly SettlementOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Settlement started, every {Interval}s", _options.IntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var settled = await scope.ServiceProvider
                    .GetRequiredService<SettlementReconciler>()
                    .RunAsync(stoppingToken);

                if (settled > 0)
                {
                    logger.LogInformation("Settlement resolved {Count} payment(s)", settled);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // A settlement run that dies quietly leaves payments undetermined
                // forever, which is the state this exists to end.
                logger.LogError(e, "Settlement run failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.IntervalSeconds), clock, stoppingToken);
        }

        logger.LogInformation("Settlement stopped");
    }
}
