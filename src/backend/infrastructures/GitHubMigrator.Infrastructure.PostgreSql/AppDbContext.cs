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
    public DbSet<ConnectionRecord> Connections => Set<ConnectionRecord>();
    public DbSet<EncryptedSecretRecord> ConnectionSecrets => Set<EncryptedSecretRecord>();

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
        builder.Entity<ConnectionRecord>(entity =>
        {
            entity.ToTable("migration_connections");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(120);
            entity.Property(x => x.ProviderKey).HasColumnName("provider_key").HasMaxLength(80);
            entity.Property(x => x.Direction).HasColumnName("direction");
            entity.Property(x => x.Organization).HasColumnName("organization").HasMaxLength(100);
            entity.Property(x => x.SecretReference).HasColumnName("secret_reference").HasMaxLength(100);
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.Property(x => x.ValidatedAtUtc).HasColumnName("validated_at_utc");
            entity.Property(x => x.ValidationCode).HasColumnName("validation_code").HasMaxLength(64);
            entity.Property(x => x.AuthenticatedAs).HasColumnName("authenticated_as").HasMaxLength(160);
            entity.Property(x => x.PermissionAssessment).HasColumnName("permission_assessment").HasMaxLength(64);
        });
        builder.Entity<EncryptedSecretRecord>(entity =>
        {
            entity.ToTable("migration_connection_secrets");
            entity.HasKey(x => x.Reference);
            entity.Property(x => x.Reference).HasColumnName("reference").HasMaxLength(100);
            entity.Property(x => x.Ciphertext).HasColumnName("ciphertext");
            entity.Property(x => x.Nonce).HasColumnName("nonce");
            entity.Property(x => x.Tag).HasColumnName("tag");
        });
    }
}
