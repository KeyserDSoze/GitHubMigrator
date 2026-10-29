using GitHubMigrator.Business;
using GitHubMigrator.Domain;

namespace GitHubMigrator.Domain.Tests;

public sealed class ConnectionServiceTests
{
    private readonly MemoryRepo _repo = new();
    private readonly MemorySecrets _secrets = new();
    private readonly FakeValidator _validator = new();
    private ConnectionService Service => new(new[] { _validator }, _repo, _secrets, TimeProvider.System);

    [Fact]
    public async Task Accepted_connection_is_stored_without_pat_in_metadata()
    {
        var created = await Service.CreateAsync(Draft(), CancellationToken.None);
        Assert.Equal("Verified", created.ValidationCode);
        Assert.DoesNotContain("super-secret", System.Text.Json.JsonSerializer.Serialize(created));
        Assert.Equal("super-secret", await _secrets.ReadAsync(created.SecretReference, CancellationToken.None));
        Assert.Single(await _repo.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_credentials_never_reach_storage()
    {
        _validator.Accept = false;
        var failure = await Assert.ThrowsAsync<ConnectionValidationException>(() =>
            Service.CreateAsync(Draft(), CancellationToken.None));
        Assert.Equal("Unauthorized", failure.Code);
        Assert.Empty(await _repo.ListAsync(CancellationToken.None));
        Assert.Empty(_secrets.Values);
    }

    [Theory]
    [InlineData("../localhost")]
    [InlineData("https://127.0.0.1")]
    [InlineData("org?x=bad")]
    public async Task Organization_must_be_an_identifier_not_a_url(string organization)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service.ValidateDraftAsync(
            Draft() with { Organization = organization }, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_connection_removes_credential()
    {
        var created = await Service.CreateAsync(Draft(), CancellationToken.None);
        Assert.True(await Service.DeleteAsync(created.Id, CancellationToken.None));
        Assert.Null(await _secrets.ReadAsync(created.SecretReference, CancellationToken.None));
        Assert.Empty(await _repo.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Failed_rotation_does_not_overwrite_existing_pat()
    {
        var created = await Service.CreateAsync(Draft(), CancellationToken.None);
        _validator.Accept = false;
        await Assert.ThrowsAsync<ConnectionValidationException>(() =>
            Service.RotateAsync(created.Id, "replacement", CancellationToken.None));
        Assert.Equal("super-secret", await _secrets.ReadAsync(created.SecretReference, CancellationToken.None));
    }

    [Fact]
    public async Task Verify_reports_revoked_credentials_without_returning_token()
    {
        var created = await Service.CreateAsync(Draft(), CancellationToken.None);
        _validator.Accept = false;
        var verified = await Service.VerifyAsync(created.Id, CancellationToken.None);
        Assert.Equal("Unauthorized", verified!.ValidationCode);
        Assert.DoesNotContain("super-secret", System.Text.Json.JsonSerializer.Serialize(verified));
    }

    private static ConnectionDraft Draft() => new("Source", "fake-ado", "my-org", "super-secret");

    private sealed class FakeValidator : IConnectionValidator
    {
        public string ProviderKey => "fake-ado";
        public ConnectionDirection Direction => ConnectionDirection.Source;
        public bool Accept { get; set; } = true;
        public Task<ConnectionValidation> ValidateAsync(string org, string token, CancellationToken ct) =>
            Task.FromResult(Accept
                ? new ConnectionValidation(true, "Verified", "dev", "read_only_probe")
                : new ConnectionValidation(false, "Unauthorized", null, "not_verified"));
    }

    private sealed class MemoryRepo : IConnectionRepository
    {
        private readonly Dictionary<Guid, MigrationConnection> _items = new();
        public Task<IReadOnlyList<MigrationConnection>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MigrationConnection>>(_items.Values.ToArray());
        public Task<MigrationConnection?> GetAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(_items.GetValueOrDefault(id));
        public Task SaveAsync(MigrationConnection connection, CancellationToken ct)
        {
            _items[connection.Id] = connection;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid id, CancellationToken ct)
        {
            _items.Remove(id);
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySecrets : IConnectionSecretStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public Task SaveAsync(string reference, string value, CancellationToken ct)
        {
            Values[reference] = value;
            return Task.CompletedTask;
        }
        public Task<string?> ReadAsync(string reference, CancellationToken ct) =>
            Task.FromResult(Values.GetValueOrDefault(reference));
        public Task DeleteAsync(string reference, CancellationToken ct)
        {
            Values.Remove(reference);
            return Task.CompletedTask;
        }
    }
}
