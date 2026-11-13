namespace LlmSim;

// Standalone entry point used by the container image (docker/llm-sim.Dockerfile).
// Explicit type name: test projects also reference other executables with their own entry points.
public static class LlmSimEntry
{
    public static async Task Main()
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name);
        static string[] List(string? value, string[] fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback
                : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var requireKey = (Env("LLM_SIM_REQUIRE_KEY") ?? "true").Trim().ToLowerInvariant() is not ("false" or "0" or "no" or "off");
        var options = new LlmSimOptions(
            Env("ASPNETCORE_URLS") ?? "http://0.0.0.0:8080",
            List(Env("LLM_SIM_API_KEYS"), [LlmSimOptions.DefaultApiKey]),
            requireKey,
            List(Env("LLM_SIM_MODELS"), [.. LlmSimOptions.DefaultModels]),
            Env("LLM_SIM_RECIPES"));

        var app = await LlmSimApp.StartAsync(options);
        Console.WriteLine($"LlmSim ready on {string.Join(',', app.Urls)}");
        await app.WaitForShutdownAsync();
    }
}
