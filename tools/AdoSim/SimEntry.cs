namespace AdoSim;

// Standalone entry point used by the container image (docker/ado-sim.Dockerfile).
// Explicit type name: the test project also references the API's global "Program".
public static class SimEntry
{
    public static async Task Main()
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name);
        var options = new SimOptions(
            SeedSpec.Load(Env("ADO_SIM_SEED")),
            Env("ADO_SIM_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data"),
            Env("ADO_SIM_PAT") ?? "sim-pat",
            Env("ADO_SIM_MAX_API") ?? "7.0",
            int.TryParse(Env("ADO_SIM_REFS_PAGE"), out var page) ? page : 1000,
            Env("ASPNETCORE_URLS") ?? "http://0.0.0.0:8080");

        var app = await AdoSimApp.StartAsync(options);
        Console.WriteLine($"AdoSim ready on {string.Join(',', app.Urls)} (collection {options.Seed.Collection})");
        await app.WaitForShutdownAsync();
    }
}
