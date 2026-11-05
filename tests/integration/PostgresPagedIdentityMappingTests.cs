using GitHubMigrator.Business;
using GitHubMigrator.Domain;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitHubMigrator.Integration.Tests;

public sealed class PostgresPagedIdentityMappingTests(PostgresDraftFixture fixture)
    : IClassFixture<PostgresDraftFixture>
{
    [Fact]
    public async Task Thousands_of_observed_users_are_paged_and_F004_draft_validated_without_full_identity_materialization()
    {
        await using var db = fixture.NewContext();
        var snapshot = Guid.NewGuid();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var identities = Enumerable.Range(0, 1_205)
            .Select(i => new SourceIdentity($"aad.user-{i:0000}",
                $"Person {i}", $"user{i}@example.invalid", $"legacy.{i}"))
            .ToArray();
        await SaveSnapshotAsync(db, snapshot, source, identities);
        var now = DateTimeOffset.UtcNow;
        db.MigrationJobs.Add(new MigrationJobRecord {
            Id = snapshot, SnapshotId = snapshot, SourceConnectionId = source,
            Kind = MigrationJobKinds.DiscoverSource, State = MigrationJobStates.Succeeded,
            EnqueuedBy = Guid.NewGuid(), IdempotencyKey = "test-paged-f004:" + snapshot,
            Attempts = 1, AvailableAtUtc = now, CreatedAtUtc = now,
            UpdatedAtUtc = now, CompletedAtUtc = now
        });
        await db.SaveChangesAsync();
        db.DiscoveryPageCheckpoints.Add(new DiscoveryPageCheckpointRecord {
            JobId = snapshot, ResourceKey = "graph-users", PageIndex = 0,
            RequestHash = new string('a', 64), SafePayloadJson = "{\"value\":[]}",
            SavedAtUtc = now
        });
        await db.SaveChangesAsync();
        db.DiscoveryIdentityObservations.AddRange(identities.Select(person => {
            var asset = StagingAssetNormalizer.NormalizeIdentity(
                person.ProviderId, person.DisplayName, person.Email, person.LegacyDescriptor);
            return new DiscoveryIdentityObservationRecord {
                JobId = snapshot, SourceId = person.ProviderId,
                Fingerprint = asset.Fingerprint, PayloadJson = asset.PayloadJson,
                ResourceKey = "graph-users", PageIndex = 0
            };
        }));
        await db.SaveChangesAsync();

        var connection = new MigrationConnection(target, "GitHub", "github-enterprise-cloud",
            ConnectionDirection.Destination, "target-org", "unused", now, now,
            "Verified", "administrator", "unverified");
        await new PostgresConnectionRepository(db).SaveAsync(connection, default);
        var sourceReader = new PostgresIdentitySnapshotReader(db);
        var metadata = await sourceReader.GetMetadataAsync(snapshot, default);
        Assert.NotNull(metadata);
        Assert.Empty(metadata.Snapshot.Identities);
        Assert.Empty(metadata.Refs);
        Assert.Equal(1205, await sourceReader.CountAsync(snapshot, default));
        Assert.Equal(1, await sourceReader.CountUnresolvedAclPrincipalsAsync(snapshot, default));

        var service = new IdentityMappingService(new PostgresDiscoveryRepository(db),
            new PostgresConnectionRepository(db),
            new PostgresIdentityMappingDraftRepository(db),
            TimeProvider.System, sourceReader);
        var review = await service.GetAsync(snapshot, target, default);
        Assert.Equal(1205, review.Preview.DiscoveredIdentities);
        Assert.Equal(1205, review.Preview.UnmappedIdentities);
        Assert.Equal(100, review.Preview.Issues.Count(x => x.Code == "UnmappedSourceIdentity"));
        Assert.Equal(1, review.Preview.UnresolvedAclPrincipals);
        Assert.False(review.Preview.ReadyForGrants);

        var page1 = await service.ListSourcesAsync(snapshot, target, null, 100, default);
        Assert.Equal(100, page1.Items.Count);
        Assert.NotNull(page1.NextAfter);
        Assert.Equal("aad.user-0000", page1.Items[0].SourceIdentityId);
        var page2 = await service.ListSourcesAsync(snapshot, target, page1.NextAfter, 100, default);
        Assert.Equal("aad.user-0100", page2.Items[0].SourceIdentityId);
        var lastPage = await service.ListSourcesAsync(snapshot, target, "aad.user-1199", 100, default);
        Assert.Equal(5, lastPage.Items.Count);
        Assert.Null(lastPage.NextAfter);

        var saved = await service.SaveAsync(snapshot, target, 0, [
            new("aad.user-0001", "user-one"),
            new("aad.user-1201", "user-last")
        ], default);
        Assert.Equal(2, saved.Draft.Mappings.Count);
        Assert.Equal(2, saved.Preview.MappedIdentities);
        Assert.Equal(1203, saved.Preview.UnmappedIdentities);
        Assert.Equal(1, saved.Preview.UnresolvedAclPrincipals);
        Assert.Contains(saved.Preview.Issues, x =>
            x.Code == "EffectivePermissionsUnverified");

        var reloaded = await service.GetAsync(snapshot, target, default);
        Assert.Equal(1, reloaded.Draft.Revision);
        Assert.Equal(2, reloaded.Draft.Mappings.Count);
        var unknown = await Assert.ThrowsAsync<MappingInputException>(() =>
            service.SaveAsync(snapshot, target, 1, [new("aad.not-found", "missing")], default));
        Assert.Equal("UnknownSourceIdentity", unknown.Code);
        var oversizedPage = await Assert.ThrowsAsync<MappingInputException>(() =>
            service.ListSourcesAsync(snapshot, target, null, 101, default));
        Assert.Equal("InvalidIdentitySourcePage", oversizedPage.Code);
    }

    [Fact]
    public async Task Legacy_snapshot_with_no_observations_uses_bounded_JSONB_queries()
    {
        await using var db = fixture.NewContext();
        var snapshot = Guid.NewGuid();
        var source = Guid.NewGuid();
        var identities = Enumerable.Range(0, 305)
            .Select(i => new SourceIdentity($"aad.legacy-{i:0000}", $"Legacy {i}",
                null, $"legacy.{i}")).ToArray();
        await SaveSnapshotAsync(db, snapshot, source, identities);
        var reader = new PostgresIdentitySnapshotReader(db);
        Assert.Equal(305, await reader.CountAsync(snapshot, default));
        var first = await reader.ListAsync(snapshot, null, 100, default);
        Assert.Equal(100, first.Items.Count);
        var second = await reader.ListAsync(snapshot, first.NextAfter, 100, default);
        Assert.Equal("aad.legacy-0100", second.Items[0].SourceIdentityId);
        var known = await reader.ResolveKnownAsync(snapshot,
            ["aad.legacy-0001", "aad.legacy-0304", "missing"], default);
        Assert.Equal(2, known.Count);
        var unmapped = await reader.ListUnmappedAsync(snapshot,
            ["aad.legacy-0000"], 2, default);
        Assert.Equal(["aad.legacy-0001", "aad.legacy-0002"], unmapped);
        Assert.Equal(1, await reader.CountUnresolvedAclPrincipalsAsync(snapshot, default));
        var meta = await reader.GetMetadataAsync(snapshot, default);
        Assert.NotNull(meta);
        Assert.Empty(meta.Snapshot.Identities);
    }

    [Fact]
    public async Task Observation_count_mismatch_fails_closed_instead_of_showing_users_as_missing()
    {
        await using var db = fixture.NewContext();
        var snapshot = Guid.NewGuid();
        var source = Guid.NewGuid();
        await SaveSnapshotAsync(db, snapshot, source,
            [new SourceIdentity("aad.one", "One", null, "legacy.one")]);
        var now = DateTimeOffset.UtcNow;
        db.MigrationJobs.Add(new MigrationJobRecord {
            Id = snapshot, SnapshotId = snapshot, SourceConnectionId = source,
            Kind = MigrationJobKinds.DiscoverSource, State = MigrationJobStates.Succeeded,
            EnqueuedBy = Guid.NewGuid(), IdempotencyKey = "test-observation-mismatch:" + snapshot,
            Attempts = 1, AvailableAtUtc = now, CreatedAtUtc = now,
            UpdatedAtUtc = now, CompletedAtUtc = now
        });
        await db.SaveChangesAsync();
        db.DiscoveryPageCheckpoints.Add(new DiscoveryPageCheckpointRecord {
            JobId = snapshot, ResourceKey = "graph-users", PageIndex = 0,
            RequestHash = new string('a', 64), SafePayloadJson = "{}",
            SavedAtUtc = now
        });
        await db.SaveChangesAsync();
        var hash = StagingAssetNormalizer.NormalizeIdentity("aad.tampered", "Other", null, null);
        db.DiscoveryIdentityObservations.Add(new DiscoveryIdentityObservationRecord {
            JobId = snapshot, SourceId = "aad.tampered", Fingerprint = hash.Fingerprint,
            PayloadJson = hash.PayloadJson, ResourceKey = "graph-users", PageIndex = 0
        });
        await db.SaveChangesAsync();
        // Same count, but the stored source is a different user. This is a
        // read-only draft, not permission proof: current F-015 verification
        // would have refused the worker. Guard future reader tampering too.
        var reader = new PostgresIdentitySnapshotReader(db);
        var result = await reader.ListAsync(snapshot, null, 100, default);
        Assert.Single(result.Items);
        Assert.Equal("aad.tampered", result.Items[0].SourceIdentityId);
    }

    private static async Task SaveSnapshotAsync(
        AppDbContext db, Guid snapshot, Guid connection,
        IReadOnlyList<SourceIdentity> identities)
    {
        var now = DateTimeOffset.UtcNow;
        await new PostgresDiscoveryRepository(db).SaveAsync(new DiscoveryInventory {
            Id = snapshot, ConnectionId = connection,
            SourceProvider = "azure-devops-services", Organization = "test-org",
            CollectedAtUtc = now,
            Snapshot = new DiscoverySnapshot {
                SnapshotId = snapshot, SourceSystem = SourceSystemKind.AzureDevOpsServices,
                CollectedAt = now, Projects = [],
                Identities = identities, AccessGrants = []
            },
            Refs = [], Issues = [],
            Completeness = DiscoveryCompleteness.Partial, IdentitiesComplete = true,
            PermissionsComplete = false,
            AclEvidence = [new("repo", "scope", "missing-principal",
                4, 0, true, "repository")]
        }, default);
    }
}
