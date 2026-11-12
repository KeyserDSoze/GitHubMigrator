namespace GitHubMigrator.Infrastructure.PostgreSql;

// #133: make the three startup modes mutually exclusive before any
// database connection, worker activation or HTTP listener is opened.
public enum PostgresDatabaseStartupMode
{
    LegacyBootstrapAndServe,
    BootstrapOnly,
    RuntimeOnly
}

public static class PostgresDatabaseStartupModeSelector
{
    public static PostgresDatabaseStartupMode Resolve(
        bool runtimeOnly, bool bootstrapOnly)
    {
        if (runtimeOnly && bootstrapOnly)
            throw new InvalidOperationException(
                "PostgreSqlStartupModeConflict: BootstrapOnly and " +
                "RuntimeOnly must never be enabled together.");
        return bootstrapOnly
            ? PostgresDatabaseStartupMode.BootstrapOnly
            : runtimeOnly
                ? PostgresDatabaseStartupMode.RuntimeOnly
                : PostgresDatabaseStartupMode.LegacyBootstrapAndServe;
    }
}
