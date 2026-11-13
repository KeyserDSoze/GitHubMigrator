using System.Text.RegularExpressions;

namespace GitHubMigrator.Business.WorkflowGates;

// What came out of a model, separated into the one workflow it may contain and free prose (notes) that is only
// ever shown to a human and never executed or committed.
public sealed record ExtractedWorkflow(string? Yaml, string? Notes, GateFinding? Error)
{
    public bool Ok => Yaml is not null;
}

public static class ModelOutputExtractor
{
    private static readonly Regex Fence = new(@"^[ \t]*```([A-Za-z0-9_-]*)[ \t]*\r?\n(.*?)^[ \t]*```[ \t]*$", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));
    private static readonly Regex HasJobs = new(@"^jobs:\s*$", RegexOptions.Multiline | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public static ExtractedWorkflow Extract(string? content, int maxBytes = 262_144)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Fail("NO_WORKFLOW_IN_OUTPUT", "The model returned nothing.");
        if (content.Length > maxBytes * 2L)
            return Fail("OUTPUT_TOO_LARGE", "The model output is far larger than any workflow should be.");
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            return Fail("OUTPUT_NOT_YAML", "The answer is an HTML page, not a workflow.");

        try
        {
            var blocks = Fence.Matches(content).Where(m => m.Groups[1].Value is "" or "yaml" or "yml").ToList();
            if (blocks.Count == 0)
            {
                // an opened but never closed fence means the answer was cut off
                if (Regex.IsMatch(content, @"^[ \t]*```", RegexOptions.Multiline, TimeSpan.FromMilliseconds(200)))
                    return Fail("OUTPUT_TRUNCATED", "A code block was opened and never closed: the answer was cut off.");
                return new(content.Trim() + "\n", null, null);
            }
            var workflows = blocks.Where(b => HasJobs.IsMatch(b.Groups[2].Value)).ToList();
            if (workflows.Count == 0) workflows = blocks;
            if (workflows.Count > 1)
                return Fail("MULTIPLE_WORKFLOWS_IN_OUTPUT", "The model returned more than one workflow; one conversion yields exactly one.");
            var yaml = workflows[0].Groups[2].Value.Trim('\r', '\n') + "\n";
            var notes = Fence.Replace(content, "").Trim();
            return new(yaml, notes.Length == 0 ? null : notes.Length > 4000 ? notes[..4000] : notes, null);
        }
        catch (RegexMatchTimeoutException) { return Fail("OUTPUT_TOO_LARGE", "The model output could not be analysed in bounded time."); }
    }

    private static ExtractedWorkflow Fail(string code, string message) => new(null, null, new("output", code, GateSeverity.Blocker, message));
}
