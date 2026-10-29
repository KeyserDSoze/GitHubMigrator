using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GitHubMigrator.Infrastructure.PostgreSql;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public bool MustChangePassword { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MigrationPlanRecord
{
    public Guid Id { get; set; }
    public Guid DiscoverySnapshotId { get; set; }
    public string TargetOrganization { get; set; } = "";
    public string PlanJson { get; set; } = "{}";
    public uint Version { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<MigrationPlanRecord> MigrationPlans => Set<MigrationPlanRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<MigrationPlanRecord>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TargetOrganization).HasMaxLength(255).IsRequired();
            entity.Property(x => x.PlanJson).HasColumnType("jsonb");
            entity.Property(x => x.Version).IsRowVersion();
        });
    }
}
