using KasaSika.Application.Options;
using KasaSika.Application.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KasaSika.Infrastructure.Background;

/// <summary>
/// Keeps asking the provider about transfers whose outcome is not yet known (submitted, pending, unknown,
/// interrupted mid-submission). This is what resolves a transfer when the callback never arrives and the
/// user has closed the app. Each transaction gets its own scope, so one bad row cannot stop the batch.
/// </summary>
public sealed class ReconciliationWorker(IServiceScopeFactory scopes, IOptions<ReconciliationOptions> options, ILogger<ReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.IntervalSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                IReadOnlyList<Guid> ids;
                using (var scope = scopes.CreateScope())
                    ids = await scope.ServiceProvider.GetRequiredService<ReconciliationService>().GetCandidateIdsAsync(stoppingToken);

                foreach (var id in ids)
                {
                    try
                    {
                        using var scope = scopes.CreateScope();
                        if (await scope.ServiceProvider.GetRequiredService<ReconciliationService>().ReconcileAsync(id, stoppingToken))
                            logger.LogInformation("Reconciler updated transaction {TransactionId}", id);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Reconciliation of {TransactionId} failed; will retry next cycle", id);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Reconciliation cycle failed");
            }
        }
    }
}
