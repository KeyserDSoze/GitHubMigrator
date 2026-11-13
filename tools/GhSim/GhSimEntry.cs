namespace GhSim;

// Standalone entry point used by the container image (docker/gh-sim.Dockerfile).
// Explicit type name: test projects also reference other executables with their own entry points.
public static class GhSimEntry
{
    public static async Task Main()
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name);
        var options = new GhSimOptions(
            GhSeed.Load(Env("GH_SIM_SEED")),
            Env("GH_SIM_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data"),
            Env("ASPNETCORE_URLS") ?? "http://0.0.0.0:8080",
            MaxPushBytes: long.TryParse(Env("GH_SIM_MAX_PUSH_BYTES"), out var max) ? max : 2L * 1024 * 1024 * 1024);

        var app = await GhSimApp.StartAsync(options);
        Console.WriteLine($"GhSim ready on {string.Join(',', app.Urls)}");
        await app.WaitForShutdownAsync();
    }
}
