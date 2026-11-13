namespace AdoSim;

// Standalone entry point used by the container image (docker/ado-sim.Dockerfile).
// Explicit type name: the test project also references the API's global "Program".
public static class SimEntry
{
    // ADO_SIM_SEED: profile name or seed file; ADO_SIM_BULK: "projects,reposPerProject,pipelinesPerRepo[,commitsPerRepo]".
    public static SimOptions OptionsFrom(Func<string, string?> env) => new(
        SeedSpec.Load(env("ADO_SIM_SEED"), env("ADO_SIM_BULK")),
        env("ADO_SIM_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data"),
        env("ADO_SIM_PAT") ?? "sim-pat",
        env("ADO_SIM_MAX_API") ?? (IsServices(env) ? "7.1" : "7.0"),
        int.TryParse(env("ADO_SIM_REFS_PAGE"), out var page) ? page : 1000,
        env("ASPNETCORE_URLS") ?? "http://0.0.0.0:8080",
        IsServices(env) ? SimFlavors.Services : SimFlavors.Server,
        int.TryParse(env("ADO_SIM_GRAPH_PAGE"), out var graphPage) ? graphPage : 100);

    // ADO_SIM_FLAVOR: "server" (default) or "services" (stand-in for dev.azure.com + vssps).
    private static bool IsServices(Func<string, string?> env) =>
        string.Equals(env("ADO_SIM_FLAVOR"), SimFlavors.Services, StringComparison.OrdinalIgnoreCase);

    public static async Task Main()
    {
        var options = OptionsFrom(Environment.GetEnvironmentVariable);
        var app = await AdoSimApp.StartAsync(options);
        Console.WriteLine($"AdoSim ready on {string.Join(',', app.Urls)} (collection {options.Seed.Collection})");
        await app.WaitForShutdownAsync();
    }
}
