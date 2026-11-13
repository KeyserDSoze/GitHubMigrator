using GitHubMigrator.Business;

public sealed class IncrementalPreparationBackgroundWorker(
    IServiceScopeFactory scopes, IConfiguration config,
    ILogger<IncrementalPreparationBackgroundWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Same flag as the durable source worker: the API can run without
        // workers, and tests claim or process preparation jobs explicitly.
        if (!config.GetValue("Jobs:Enabled", true)) return;
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var worker = scope.ServiceProvider
                    .GetRequiredService<IncrementalPreparationProcessor>();
                if (await worker.RunOnceAsync(stoppingToken)) continue;
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            { break; }
            catch (Exception ex)
            {
                // PostgreSQL/transport errors can include credentials.
                // Intentionally emit neither exception messages nor stacktraces.
                logger.LogWarning("Incremental preparation poll failed; retry scheduled ({ExceptionType})", ex.GetType().FullName);
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                { break; }
            }
        }
    }
}
