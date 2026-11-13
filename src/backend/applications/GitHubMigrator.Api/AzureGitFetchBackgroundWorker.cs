using GitHubMigrator.Business;

// Async F-037 source fetch worker. Disabled by default unless all flags
// explicitly permit *source read only*. It has no GitHub write dependencies.
public sealed class AzureGitFetchBackgroundWorker(
    IServiceScopeFactory scopes, IConfiguration settings,
    ILogger<AzureGitFetchBackgroundWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken shutdown)
    {
        if (!settings.GetValue("Jobs:Enabled", true) ||
            !settings.GetValue("GitSourceBundles:Enabled", false) ||
            !settings.GetValue("AzureGitFetch:Enabled", false) ||
            !settings.GetValue("AzureGitQueue:Enabled", false)) return;

        await Task.Yield();
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider
                    .GetRequiredService<AzureGitFetchJobProcessor>();
                if (await processor.RunOnceAsync(shutdown)) continue;
                await Task.Delay(TimeSpan.FromSeconds(2), shutdown);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            { break; }
            catch (Exception)
            {
                // Never include provider stderr, PAT, URL or exception bodies.
                logger.LogWarning("Azure Git source acquisition queue polling failed");
                try { await Task.Delay(TimeSpan.FromSeconds(3), shutdown); }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                { break; }
            }
        }
    }
}
