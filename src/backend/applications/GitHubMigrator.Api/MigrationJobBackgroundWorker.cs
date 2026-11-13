using GitHubMigrator.Business;

public sealed class MigrationJobBackgroundWorker(
    IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<MigrationJobBackgroundWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Split deployment can disable the API's in-process worker and run a
        // separate dedicated worker host later. The PostgreSQL queue is shared.
        if (!configuration.GetValue("Jobs:Enabled", true))
        {
            logger.LogInformation("Migration job worker disabled by configuration");
            return;
        }

        await Task.Yield(); // never block ASP.NET startup on background scheduling
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<MigrationJobProcessor>();
                if (await processor.RunOnceAsync(stoppingToken))
                    continue; // drain queued work without artificial per-job sleeps

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // held lease will expire and be recoverable by another worker
            }
            catch (Exception ex)
            {
                // Provider exceptions can contain tokens or request paths. Never log
                // raw exception text or stack traces for this generic work loop.
                logger.LogWarning("Migration job worker failed to poll or process a job; retry scheduled ({ExceptionType})", ex.GetType().FullName);
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
