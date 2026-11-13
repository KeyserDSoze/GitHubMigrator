using GitHubMigrator.Infrastructure.PostgreSql;
using Xunit;

namespace GitHubMigrator.Domain.Tests;

// #133: a privileged schema-bootstrap process cannot also opt into
// the limited API role; a one-shot migration must not serve HTTP.
public sealed class PostgresDatabaseStartupModeSelectorTests
{
    [Theory]
    [InlineData(false, false, PostgresDatabaseStartupMode.LegacyBootstrapAndServe)]
    [InlineData(false, true, PostgresDatabaseStartupMode.BootstrapOnly)]
    [InlineData(true, false, PostgresDatabaseStartupMode.RuntimeOnly)]
    public void Startup_mode_is_determined_before_connecting_to_postgres(
        bool runtimeOnly, bool bootstrapOnly,
        PostgresDatabaseStartupMode expected)
    {
        Assert.Equal(expected,
            PostgresDatabaseStartupModeSelector.Resolve(
                runtimeOnly, bootstrapOnly));
    }

    [Fact]
    public void Privileged_one_shot_and_restricted_runtime_cannot_overlap()
    {
        var rejected = Assert.Throws<InvalidOperationException>(() =>
            PostgresDatabaseStartupModeSelector.Resolve(
                runtimeOnly: true, bootstrapOnly: true));
        Assert.Contains("PostgreSqlStartupModeConflict",
            rejected.Message);
    }
}
