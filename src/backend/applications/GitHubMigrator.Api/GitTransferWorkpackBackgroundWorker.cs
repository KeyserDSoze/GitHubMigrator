using GitHubMigrator.Business;

// Durable evidence-only preparation worker. Deliberately no Git, PAT,
// provider or GitHub write dependency.
public sealed class GitTransferWorkpackBackgroundWorker(
    IServiceScopeFactory scopes, IConfiguration config,
    ILogger<GitTransferWorkpackBackgroundWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Jobs:Enabled", true) ||
            !config.GetValue("GitTransferWorkpacks:Enabled", true)) return;
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<GitTransferWorkpackProcessor>();
                if (await processor.RunOnceAsync(stoppingToken)) continue;
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            { break; }
            catch (Exception)
            {
                // Never log provider bodies or exception stack traces.
                logger.LogWarning("Git transfer workpack polling failed");
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
