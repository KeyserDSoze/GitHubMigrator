namespace AdoSim.Tests;

// Read-only estates shared by every test class of the same collection (built once, ~1 s each).
public sealed class OrionSim : IAsyncLifetime
{
    public SimHost Host { get; private set; } = null!;
    public async Task InitializeAsync() => Host = await SimHost.StartAsync("orion");
    public async Task DisposeAsync() => await Host.DisposeAsync();
}

public sealed class DefaultSim : IAsyncLifetime
{
    public SimHost Host { get; private set; } = null!;
    public async Task InitializeAsync() => Host = await SimHost.StartAsync("default");
    public async Task DisposeAsync() => await Host.DisposeAsync();
}

[CollectionDefinition("orion")]
public sealed class OrionCollection : ICollectionFixture<OrionSim>;

[CollectionDefinition("default")]
public sealed class DefaultCollection : ICollectionFixture<DefaultSim>;
