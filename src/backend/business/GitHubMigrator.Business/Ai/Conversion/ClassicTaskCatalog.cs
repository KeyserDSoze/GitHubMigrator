using System.Text.RegularExpressions;

namespace GitHubMigrator.Business;

public sealed record ClassicTaskName(string Name, int Major, bool Exact);

// What a classic (designer) pipeline calls a step's task is a GUID and a version spec ("2.*"); what a YAML pipeline, and the rest of this product, calls it is "Name@2". This maps the first to the second for
// Azure Pipelines' built-in tasks (the table is generated from the public task.json files, see ClassicTaskCatalog.Data.cs). A task that is not in it (an extension's, or one this table predates) is NOT guessed:
// the caller shows its id.
public static partial class ClassicTaskCatalog
{
    // lazy: the static fields of a partial class are initialized in no defined order across its files, and Rows is in the generated one
    private static readonly Lazy<Dictionary<string, (int Major, string Name)[]>> Index = new(() => Rows
        .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Select(r => (r.Major, r.Name)).OrderBy(r => r.Major).ToArray(), StringComparer.OrdinalIgnoreCase));

    private static Dictionary<string, (int Major, string Name)[]> ById => Index.Value;

    [GeneratedRegex(@"^\s*(\d{1,4})(?:\.|\s*$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex LeadingMajor();

    public static int Count => Rows.Length;

    // "2.*", "2.1.*" and "2.1.0" are major 2; "*", "" and anything else name no major.
    public static int? MajorOf(string? versionSpec)
    {
        if (string.IsNullOrWhiteSpace(versionSpec)) return null;
        var match = LeadingMajor().Match(versionSpec);
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    public static bool IsKnown(string? id) => NormalizeId(id) is { } normalized && ById.ContainsKey(normalized);

    // The name and major version of the task with this id for this version spec, or null when the id is not in the table. A spec whose major the table does not have (a newer one) still gets the
    // task's latest known name with the major the pipeline asked for, and is not Exact; a spec with no major gets the latest known one, also not Exact.
    public static ClassicTaskName? Resolve(string? id, string? versionSpec)
    {
        if (NormalizeId(id) is not { } normalized || !ById.TryGetValue(normalized, out var versions)) return null;
        var major = MajorOf(versionSpec);
        if (major is { } wanted && versions.FirstOrDefault(v => v.Major == wanted) is { Name: not null } exact) return new(exact.Name, wanted, true);
        var latest = versions[^1];
        return new(latest.Name, major ?? latest.Major, false);
    }

    public static string? NormalizeId(string? id) => Guid.TryParse(id, out var guid) ? guid.ToString("D") : null;
}
