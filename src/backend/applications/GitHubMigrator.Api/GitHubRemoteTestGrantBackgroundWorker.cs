using GitHubMigrator.Business;

// No git runner, GitHub App token provider or transport service is injected.
// The only scheduled job quarantines expired no-write test intentions.
public sealed class GitHubRemoteTestGrantBackgroundWorker(
    IServiceScopeFactory scopes, IConfiguration config,
    GitHubRemoteTestGrantOptions options,
    ILogger<GitHubRemoteTestGrantBackgroundWorker> logger)
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
                    .GetRequiredService<IGitHubRemoteTestGrantStore>();
                if (await store.QuarantineNextExpiredAsync(stoppingToken))
                    continue;
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                logger.LogWarning("Remote test grant quarantine poll failed");
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
