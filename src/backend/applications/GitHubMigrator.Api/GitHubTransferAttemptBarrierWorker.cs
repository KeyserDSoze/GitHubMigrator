using GitHubMigrator.Business;

// F-047 intentionally injects only the PostgreSQL attempt store. The worker
// can cross an audit boundary, but NEVER a GitHub remote-write boundary.
public sealed class GitHubTransferAttemptBarrierWorker(
    IServiceScopeFactory scopes, IConfiguration config,
    ILogger<GitHubTransferAttemptBarrierWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Jobs:Enabled", true) ||
            !config.GetValue("GitHubTransferAttempts:Enabled", false))
            return;
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider
                    .GetRequiredService<IGitHubTransferAttemptStore>();
                // Expire abandoned human reviews first; they must not
                // monopolize the unique active target slot indefinitely.
                if (await store.ExpireNextStaleAsync(stoppingToken)) continue;
                if (await store.BlockNextAtWriteBarrierAsync(stoppingToken)
                    is not null)
                    continue;
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            { break; }
            catch (Exception)
            {
                // Never log credentials, provider bodies, refs, or DB contents.
                logger.LogWarning("Transfer preparation barrier polling failed");
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                { break; }
            }
        }
    }
}
