namespace AdoSim;

// Owns the lifecycle of the repositories on disk: build, reuse (same seed hash) or rebuild (changed seed).
public static class Estate
{
    public const string MarkerFile = "seed.sha256";

    // Returns true when the repositories were (re)built because the seed hash changed or no marker existed.
    public static async Task<bool> EnsureAsync(SimModel model)
    {
        Directory.CreateDirectory(model.DataDir);
        var marker = Path.Combine(model.DataDir, MarkerFile);
        var hash = model.Seed.ComputeHash();
        var current = File.Exists(marker) ? (await File.ReadAllTextAsync(marker)).Trim() : null;
        var rebuild = current != hash;
        if (rebuild) Wipe(model.DataDir);
        await BuildMissingAsync(model);
        if (rebuild) await File.WriteAllTextAsync(marker, hash + "\n");
        return rebuild;
    }

    // Back to the pristine seed: commits and refs created through the write API or git push disappear.
    public static async Task ResetAsync(SimModel model)
    {
        Wipe(model.DataDir);
        await BuildMissingAsync(model);
        await File.WriteAllTextAsync(Path.Combine(model.DataDir, MarkerFile), model.Seed.ComputeHash() + "\n");
    }

    private static async Task BuildMissingAsync(SimModel model)
    {
        var missing = model.Repos.Where(r => !Directory.Exists(r.BarePath)).ToList();
        await Parallel.ForEachAsync(missing,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) },
            async (repo, _) => await RepoBuilder.BuildAsync(model, repo));
    }

    private static void Wipe(string dataDir)
    {
        foreach (var name in new[] { "repos", "work" })
        {
            var path = Path.Combine(dataDir, name);
            if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, true);
        }
    }
}
