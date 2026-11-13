using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using GitHubMigrator.Business.WorkflowGates;

namespace GitHubMigrator.Infrastructure.WorkflowTools;

// Runs a static analyser (actionlint, zizmor) on ONE untrusted workflow file, nothing else:
// a private temp directory holds only that file (so no repository config can mute the tool), the environment is
// empty, the network is not needed (zizmor --offline), output is bounded and the process is killed at the deadline.
// zizmor runs with --no-ignores (a "# zizmor: ignore" comment written by a model must not mute it), --no-config and
// the strictest persona: for generated workflows a false positive costs a review, a false negative costs security.
public sealed class ProcessWorkflowToolRunner : IWorkflowToolRunner
{
    private const int MaxOutputBytes = 2 * 1024 * 1024;
    private readonly string _executable;
    private readonly Func<string, string[]> _arguments;
    private readonly TimeSpan _timeout;

    private ProcessWorkflowToolRunner(string tool, string executable, Func<string, string[]> arguments, TimeSpan timeout)
    {
        Tool = tool; _executable = executable; _arguments = arguments; _timeout = timeout;
    }

    public string Tool { get; }

    public static ProcessWorkflowToolRunner Actionlint(string executable, TimeSpan? timeout = null) =>
        new("actionlint", executable, file => ["-format", "{{json .}}", "-no-color", file], timeout ?? TimeSpan.FromSeconds(30));

    public static ProcessWorkflowToolRunner Zizmor(string executable, TimeSpan? timeout = null) =>
        new("zizmor", executable, file => ["--offline", "--no-progress", "--no-config", "--no-ignores", "--persona", "auditor", "--format", "json", file], timeout ?? TimeSpan.FromSeconds(60));

    // A configured path wins; otherwise the tool is looked up on PATH. Null means "not installed": the gate then
    // reports NotRun and the workflow cannot be approved.
    public static string? Locate(string tool, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)) return File.Exists(configuredPath) ? configuredPath : null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, tool);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public async Task<ToolRun> RunAsync(string workflowYaml, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetTempPath(), "workflow-gate-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, ".github", "workflows", "workflow.yml");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, workflowYaml, new UTF8Encoding(false), ct);
            var info = new ProcessStartInfo(_executable)
            {
                WorkingDirectory = directory, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false
            };
            foreach (var argument in _arguments(file)) info.ArgumentList.Add(argument);
            info.Environment.Clear();
            info.Environment["PATH"] = "/usr/bin:/bin";
            info.Environment["HOME"] = directory;
            info.Environment["NO_COLOR"] = "1";

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_timeout);
            var overflowed = 0;
            void Overflow() { Interlocked.Exchange(ref overflowed, 1); deadline.Cancel(); }

            using var process = new Process { StartInfo = info };
            try { process.Start(); }
            catch (Win32Exception) { return new(false, "", Tool + " could not be started."); }
            process.StandardInput.Close();

            Task<string?>? stdout = null, stderr = null;
            try
            {
                stdout = ReadBoundedAsync(process.StandardOutput, Overflow, deadline.Token);
                stderr = ReadBoundedAsync(process.StandardError, Overflow, deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                var output = await stdout;
                _ = await stderr;
                if (output is null) return new(false, "", Tool + " produced more output than allowed.");
                return new(true, output, null, process.ExitCode);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new(false, "", Volatile.Read(ref overflowed) == 1
                    ? Tool + " produced more output than allowed."
                    : Tool + " exceeded its time limit.");
            }
            finally
            {
                if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
                await DrainAsync(stdout);
                await DrainAsync(stderr);
            }
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    // After the kill the readers end on their own; a grandchild that kept a pipe open must not hold the gate hostage.
    private static async Task DrainAsync(Task? reader)
    {
        if (reader is null) return;
        try { await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(2))); } catch (OperationCanceledException) { }
        if (reader.IsFaulted) _ = reader.Exception;
    }

    private static async Task<string?> ReadBoundedAsync(StreamReader reader, Action onOverflow, CancellationToken ct)
    {
        var buffer = new char[8192];
        var text = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            text.Append(buffer, 0, read);
            if (text.Length > MaxOutputBytes)
            {
                onOverflow();
                return null;
            }
        }
        return text.ToString();
    }
}
