using GitHubMigrator.Business;

// F-049 is explicitly OFF by default and exclusively operates on
// pre-provisioned LOCAL bare repositories. The F-047 barrier remains closed
// for all GitHub remote writes.
public sealed class GitSandboxDispatchBackgroundWorker(
    IServiceScopeFactory scopes, IConfiguration config,
    GitSandboxDispatchOptions options,
    ILogger<GitSandboxDispatchBackgroundWorker> logger) : BackgroundService
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
                var processor = scope.ServiceProvider
                    .GetRequiredService<GitSandboxDispatchProcessor>();

                // Recover uncertain results before admitting another local
                // dispatch; expired claims are inspected, NEVER sent again.
                if (await processor.InspectOneAsync(stoppingToken)) continue;
                if (await processor.ProcessOneAsync(stoppingToken)) continue;
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            { break; }
            catch (Exception)
            {
                // Raw Git output, ref names, local paths, credentials and
                // raw DB payloads must never reach application logs.
                logger.LogWarning("Local sandbox dispatch polling failed");
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                { break; }
            }
        }
    }
}
