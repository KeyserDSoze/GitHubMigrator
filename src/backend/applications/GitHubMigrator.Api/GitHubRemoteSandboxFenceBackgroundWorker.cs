using GitHubMigrator.Business;

// F-050 only observes PostgreSQL deadlines and moves expired coordination
// records into operator quarantine. It never retries or transmits Git.
public sealed class GitHubRemoteSandboxFenceBackgroundWorker(
    IServiceScopeFactory scopes, IConfiguration config,
    GitHubRemoteSandboxFenceOptions options,
    ILogger<GitHubRemoteSandboxFenceBackgroundWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || !config.GetValue("Jobs:Enabled", true))
            return;
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider
                    .GetRequiredService<IGitHubRemoteSandboxFenceStore>();
                if (await store.QuarantineNextExpiredAsync(stoppingToken))
                    continue;
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            { break; }
            catch (Exception)
            {
                logger.LogWarning("Remote sandbox coordination quarantine poll failed");
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                { break; }
            }
        }
    }
}
