using System.Security.Cryptography;
using System.Text;

namespace GitHubMigrator.Business.WorkflowGates;

public enum GateSeverity { Info, Warning, Blocker }
public enum GateStatus { Passed, Failed, NotRun }

public sealed record GateFinding(string Gate, string Code, GateSeverity Severity, string Message, int? Line = null);

public sealed record GateResult(string Gate, GateStatus Status, IReadOnlyList<GateFinding> Findings, string? Detail = null)
{
    public static GateResult Passed(string gate, IReadOnlyList<GateFinding>? findings = null) =>
        new(gate, (findings ?? []).Any(f => f.Severity == GateSeverity.Blocker) ? GateStatus.Failed : GateStatus.Passed, findings ?? []);
    public static GateResult NotRun(string gate, string detail) => new(gate, GateStatus.NotRun, [], detail);
}

// Names the migration will make available at the destination. A converted workflow may only reference these:
// an invented secret or variable would fail at run time, or worse, bind to something unrelated.
public sealed record WorkflowPlanContext(IReadOnlySet<string> AllowedSecrets, IReadOnlySet<string> AllowedVariables)
{
    public static WorkflowPlanContext Empty { get; } = new(new HashSet<string>(), new HashSet<string>());

    // Azure tasks of the source pipeline, for the accounting gate. Empty means "nothing to account for".
    public IReadOnlySet<string> SourceTasks { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static WorkflowPlanContext Of(IEnumerable<string>? secrets, IEnumerable<string>? variables) =>
        new((secrets ?? []).ToHashSet(StringComparer.Ordinal), (variables ?? []).ToHashSet(StringComparer.Ordinal));
}

public sealed record WorkflowGatePolicy
{
    public static WorkflowGatePolicy Default { get; } = new();

    public string Version { get; init; } = "2026-11-1";
    public int MaxBytes { get; init; } = 262_144;
    public int MaxJobs { get; init; } = 50;
    public int MaxSteps { get; init; } = 500;

    // Converted workflows are delivered manual-only (workflow_dispatch); the original triggers are enabled by a
    // separate, explicit approval. Branch pushes of a proposal must never start a deploy by themselves.
    public bool RequireDisabledTriggers { get; init; } = true;

    // Only meaningful when RequireDisabledTriggers is false: the automatic triggers a workflow may have. Anything else is blocked (TRIGGER_NOT_PERMITTED),
    // so lifting the manual-only rule never means "any trigger". Null: no restriction beyond the dangerous-trigger checks.
    public IReadOnlySet<string>? PermittedAutomaticTriggers { get; init; }

    // The policy a workflow whose Azure triggers were converted is judged by: the same rules, with push, pull_request and schedule allowed.
    public WorkflowGatePolicy WithAutomaticTriggers() => this with
    {
        RequireDisabledTriggers = false,
        PermittedAutomaticTriggers = new HashSet<string>(StringComparer.Ordinal) { "workflow_dispatch", "push", "pull_request", "schedule" },
        Version = Version + "+triggers"
    };

    public bool RequireShaPinning { get; init; } = true;

    // "owner/repo@sha" (lower case) of pins this product has verified; a SHA pin outside the set is a warning because
    // nothing here can tell whether that commit exists. Empty disables the check.
    public IReadOnlySet<string> VerifiedActionPins { get; init; } = ActionCatalog.Pins;
    public bool TrustedOwnersMayUseVersionTags { get; init; } = true;
    public IReadOnlySet<string> TrustedOwners { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "actions", "github" };
    public IReadOnlySet<string> AllowedActionOwners { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "actions", "github", "azure", "docker", "hashicorp", "aws-actions", "google-github-actions", "microsoft",
        "gradle", "sonarsource", "dorny", "codecov"
    };

    public bool RequireExplicitPermissions { get; init; } = true;
    public IReadOnlySet<string> BlockedWritePermissions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "contents", "actions", "administration", "secrets"
    };

    // Hosts a run step that also touches a secret may contact (suffix match with a leading "*.").
    public IReadOnlySet<string> AllowedHosts { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "api.github.com", "*.githubusercontent.com", "ghcr.io", "api.nuget.org", "*.nuget.org",
        "registry.npmjs.org", "pypi.org", "files.pythonhosted.org", "registry-1.docker.io", "*.docker.io",
        "mcr.microsoft.com", "*.azurecr.io", "*.azurewebsites.net", "*.azure.com", "management.azure.com",
        "login.microsoftonline.com", "sonarcloud.io"
    };

    // External analysers that MUST have run for a workflow to be approvable (fail closed when missing).
    public IReadOnlySet<string> RequiredExternalGates { get; init; } = new HashSet<string>(StringComparer.Ordinal) { "actionlint", "zizmor" };
    public IReadOnlySet<string> IgnoredActionlintKinds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> IgnoredZizmorIdents { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public GateSeverity ZizmorHighIs { get; init; } = GateSeverity.Blocker;
    public GateSeverity ZizmorMediumIs { get; init; } = GateSeverity.Warning;
}

public sealed record WorkflowGateReport(string WorkflowSha256, string PolicyVersion, IReadOnlyList<GateResult> Gates,
    IReadOnlyList<string> RequiredGates)
{
    public IReadOnlyList<GateFinding> Findings => Gates.SelectMany(g => g.Findings).ToArray();
    public bool HasBlockers => Findings.Any(f => f.Severity == GateSeverity.Blocker);
    public bool AllRequiredGatesRan => RequiredGates.All(r => Gates.Any(g => g.Gate == r && g.Status != GateStatus.NotRun));
    // Approvable means "a human may now review it", never "it is equivalent or safe to run".
    public bool Approvable => AllRequiredGatesRan && !HasBlockers;
    public bool Has(string code) => Findings.Any(f => f.Code == code);
}

public static class WorkflowHash
{
    public static string Sha256(string yaml) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(yaml.Replace("\r\n", "\n"))));
}
