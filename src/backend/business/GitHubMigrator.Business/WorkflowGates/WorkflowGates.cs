using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace GitHubMigrator.Business.WorkflowGates;

public sealed record WorkflowGateContext(string Yaml, ParsedWorkflow? Parsed, WorkflowPlanContext Plan, WorkflowGatePolicy Policy);

public interface IWorkflowGate
{
    string Name { get; }
    Task<GateResult> EvaluateAsync(WorkflowGateContext context, CancellationToken ct);
}

// Gate 1: is it a single, bounded, well-formed GitHub Actions workflow document at all?
public sealed class WorkflowStructureGate : IWorkflowGate
{
    public const string GateName = "structure";
    public string Name => GateName;

    public static GateResult Evaluate(string yaml, WorkflowGatePolicy policy, out ParsedWorkflow? parsed)
    {
        var findings = new List<GateFinding>();
        parsed = null;
        if (System.Text.Encoding.UTF8.GetByteCount(yaml) > policy.MaxBytes)
        {
            findings.Add(Block("WORKFLOW_TOO_LARGE", $"The workflow exceeds {policy.MaxBytes} bytes."));
            return GateResult.Passed(GateName, findings);
        }
        // bidirectional overrides and zero-width characters make code review lie ("Trojan Source")
        var suspicious = yaml.IndexOfAny(['\u202A', '\u202B', '\u202C', '\u202D', '\u202E', '\u2066', '\u2067', '\u2068', '\u2069', '\u200B', '\u200C', '\u200D', '\uFEFF']);
        if (suspicious >= 0)
            findings.Add(Block("SUSPICIOUS_UNICODE", "The workflow contains bidirectional or zero-width control characters.", LineOf(yaml, suspicious)));

        parsed = ParsedWorkflow.TryParse(yaml, out var error);
        if (parsed is null)
        {
            findings.Add(error!);
            return GateResult.Passed(GateName, findings);
        }
        if (parsed.Triggers is null) findings.Add(Block("NOT_A_WORKFLOW", "The workflow has no 'on' section.", parsed.Root.L()));
        if (!parsed.HasJobsSection) findings.Add(Block("NOT_A_WORKFLOW", "The workflow has no 'jobs' section.", parsed.Root.L()));
        else
        {
            var jobs = parsed.Jobs();
            if (jobs.Count == 0) findings.Add(Block("EMPTY_JOBS", "The workflow defines no job."));
            if (jobs.Count > policy.MaxJobs) findings.Add(Block("TOO_MANY_JOBS", $"More than {policy.MaxJobs} jobs."));
            var steps = 0;
            foreach (var job in jobs)
            {
                if (job.Uses is null && job.RunsOn is null)
                    findings.Add(Block("JOB_WITHOUT_RUNNER", $"Job '{job.Id}' has neither 'runs-on' nor 'uses'.", job.Line));
                if (job.Uses is null && job.StepNodeCount == 0)
                    findings.Add(Block("JOB_WITHOUT_STEPS", $"Job '{job.Id}' has no steps.", job.Line));
                steps += job.StepNodeCount;
                foreach (var step in job.Steps)
                    if ((step.Uses is null) == (step.Run is null))
                        findings.Add(Block("STEP_INVALID", $"Step {step.Index + 1} of job '{job.Id}' must have exactly one of 'uses' or 'run'.", step.Line));
            }
            if (steps > policy.MaxSteps) findings.Add(Block("TOO_MANY_STEPS", $"More than {policy.MaxSteps} steps."));
        }
        return GateResult.Passed(GateName, findings);
    }

    public Task<GateResult> EvaluateAsync(WorkflowGateContext context, CancellationToken ct) =>
        Task.FromResult(Evaluate(context.Yaml, context.Policy, out _));

    private static GateFinding Block(string code, string message, int? line = null) => new(GateName, code, GateSeverity.Blocker, message, line);

    internal static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }
}

// Gate 2: deterministic policy over what an unreviewed workflow may do. Every rule is a stable code so tests,
// the UI and the audit trail can name it. No rule needs the network or a model.
public sealed class WorkflowPolicyGate : IWorkflowGate
{
    public const string GateName = "policy";
    public string Name => GateName;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);
    private const RegexOptions O = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static readonly Regex Expression = new(@"\$\{\{(.*?)\}\}", RegexOptions.Compiled | RegexOptions.Singleline, Timeout);
    private static readonly Regex SecretRef = new(@"\bsecrets\.([A-Za-z_][A-Za-z0-9_]*)|\bsecrets\[\s*['""]([A-Za-z_][A-Za-z0-9_]*)['""]\s*\]", O, Timeout);
    private static readonly Regex VarRef = new(@"\bvars\.([A-Za-z_][A-Za-z0-9_]*)", O, Timeout);
    private static readonly Regex Url = new(@"https?://([A-Za-z0-9.-]+)", O, Timeout);
    private static readonly Regex Sha40 = new("^[0-9a-f]{40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex VersionTag = new(@"^v?\d+(\.\d+){0,2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex[] RemoteScript =
    [
        new(@"\b(?:curl|wget|Invoke-WebRequest|iwr|Invoke-RestMethod|irm)\b[^\n|;]*\|\s*(?:sudo\s+)?(?:ba|z|da|k)?sh\b", O, Timeout),
        new(@"\b(?:curl|wget|Invoke-WebRequest|iwr|Invoke-RestMethod|irm)\b[^\n|;]*\|\s*(?:sudo\s+)?(?:pwsh|powershell|iex|Invoke-Expression)\b", O, Timeout),
        new(@"\b(?:iex|Invoke-Expression)\b[^\n]*\b(?:iwr|irm|Invoke-WebRequest|Invoke-RestMethod|DownloadString)\b", O, Timeout),
        new(@"\b(?:ba|z)?sh\s+<\(\s*(?:curl|wget)", O, Timeout),
        new(@"base64\s+(?:-d|--decode)[^\n|]*\|\s*(?:ba)?sh\b", O, Timeout)
    ];

    // Contexts an attacker controls: interpolating them into a script is code injection.
    private static readonly Regex UntrustedContext = new(
        @"\bgithub\.head_ref\b|\bgithub\.event\.(?:issue|pull_request|comment|review|review_comment|discussion|head_commit|commits|pages|workflow_run|release)\b[\w.\[\]*'""-]*",
        O, Timeout);
    private static readonly Regex InputContext = new(@"\b(?:github\.event\.inputs|inputs)\.[A-Za-z_][\w-]*", O, Timeout);

    private static readonly HashSet<string> BranchLikeRefs = new(StringComparer.OrdinalIgnoreCase)
        { "main", "master", "head", "latest", "develop", "dev", "trunk", "stable", "release" };

    public Task<GateResult> EvaluateAsync(WorkflowGateContext context, CancellationToken ct)
    {
        if (context.Parsed is null)
            return Task.FromResult(GateResult.NotRun(Name, "The structure gate failed: nothing to evaluate."));
        var findings = new List<GateFinding>();
        var wf = context.Parsed; var policy = context.Policy;

        CheckTriggers(wf, policy, findings);
        CheckPermissions(wf, policy, findings);
        foreach (var job in wf.Jobs())
        {
            if (job.Uses is { } reusable) CheckUses(reusable, job.Line, policy, findings);
            CheckRunner(job, findings);
            foreach (var step in job.Steps)
            {
                if (step.Uses is { } uses) CheckStepUses(step, uses, policy, findings);
                if (step.Run is { } script) CheckRunScript(step, script, policy, findings);
                if (step.Uses is not null && step.Uses.StartsWith("actions/github-script", StringComparison.OrdinalIgnoreCase) &&
                    step.With is YamlMappingNode withMap && ParsedWorkflow.Text(withMap, "script") is { } js)
                    CheckUntrusted(js, step.Line, findings, "github-script");
            }
        }
        CheckReferences(context, findings);
        CheckLiteralSecrets(context.Yaml, findings);
        if (context.Yaml.Contains("<REDACTED:", StringComparison.Ordinal))
            findings.Add(Block("REDACTION_PLACEHOLDER_LEFT", "A redaction placeholder was kept as a value: reference a secret or variable instead."));
        return Task.FromResult(GateResult.Passed(Name, findings));
    }

    // ---- triggers ------------------------------------------------------------------------------------------

    private static void CheckTriggers(ParsedWorkflow wf, WorkflowGatePolicy policy, List<GateFinding> findings)
    {
        var triggers = wf.TriggerNames();
        foreach (var (name, line) in triggers)
        {
            if (name == "pull_request_target")
                findings.Add(Block("DANGEROUS_TRIGGER", "pull_request_target runs with secrets in the context of untrusted pull requests.", line));
            else if (name is "workflow_run" or "issue_comment" or "issues" or "discussion" or "discussion_comment" or "pull_request_review_comment")
                findings.Add(Warn("RISKY_TRIGGER", $"Trigger '{name}' is driven by content an outsider can author.", line));
        }
        var enabled = triggers.Where(t => t.Name != "workflow_dispatch").Select(t => t.Name).Distinct().ToArray();
        if (policy.RequireDisabledTriggers && enabled.Length > 0)
            findings.Add(Block("TRIGGERS_NOT_DISABLED",
                "Triggers (" + string.Join(", ", enabled) + ") would start the workflow by themselves. A proposal is delivered manual-only (workflow_dispatch); enabling the original triggers is a separate approval."));
    }

    // ---- permissions ---------------------------------------------------------------------------------------

    private static void CheckPermissions(ParsedWorkflow wf, WorkflowGatePolicy policy, List<GateFinding> findings)
    {
        var rootDeclared = wf.Permissions is not null;
        if (rootDeclared) EvaluatePermissions(wf.Permissions!, policy, findings);
        foreach (var job in wf.Jobs())
        {
            if (job.Permissions is { } permissions) EvaluatePermissions(permissions, policy, findings);
            else if (!rootDeclared && policy.RequireExplicitPermissions && job.Uses is null)
                findings.Add(Block("PERMISSIONS_UNDECLARED", $"Job '{job.Id}' relies on the repository's default token permissions: declare 'permissions' (least privilege).", job.Line));
        }
    }

    private static void EvaluatePermissions(YamlNode node, WorkflowGatePolicy policy, List<GateFinding> findings)
    {
        switch (node)
        {
            case YamlScalarNode { Value: "write-all" } scalar:
                findings.Add(Block("PERMISSIONS_TOO_BROAD", "'write-all' grants every write permission to the workflow token.", scalar.L()));
                break;
            case YamlMappingNode map:
                foreach (var pair in map.Children)
                {
                    if (pair.Key is not YamlScalarNode key || pair.Value is not YamlScalarNode value || value.Value != "write") continue;
                    var scope = key.Value ?? "";
                    if (policy.BlockedWritePermissions.Contains(scope))
                        findings.Add(Block("PERMISSION_WRITE_BLOCKED", $"'{scope}: write' is not allowed for a converted workflow.", key.L()));
                    else if (scope == "id-token")
                        findings.Add(Info("OIDC_TOKEN_REQUESTED", "id-token: write requests an OIDC token; the cloud trust policy must be reviewed.", key.L()));
                    else
                        findings.Add(Warn("PERMISSION_WRITE", $"'{scope}: write' needs a reviewer's explicit agreement.", key.L()));
                }
                break;
        }
    }

    // ---- actions -------------------------------------------------------------------------------------------

    private static void CheckStepUses(WorkflowStep step, string uses, WorkflowGatePolicy policy, List<GateFinding> findings)
    {
        CheckUses(uses, step.Line, policy, findings);
        if (uses.StartsWith("actions/checkout", StringComparison.OrdinalIgnoreCase) &&
            !(step.With is YamlMappingNode with && ParsedWorkflow.Text(with, "persist-credentials") == "false"))
            findings.Add(Warn("CHECKOUT_PERSISTS_CREDENTIALS", "actions/checkout keeps the token in the workspace: set 'persist-credentials: false' unless a later step pushes.", step.Line));
    }

    private static void CheckUses(string uses, int line, WorkflowGatePolicy policy, List<GateFinding> findings)
    {
        if (uses.StartsWith("./", StringComparison.Ordinal)) return;
        if (uses.StartsWith("docker://", StringComparison.OrdinalIgnoreCase))
        {
            if (!uses.Contains("@sha256:", StringComparison.OrdinalIgnoreCase))
                findings.Add(Block("UNPINNED_ACTION", "A docker:// action must be pinned by digest (@sha256:...).", line));
            return;
        }
        var at = uses.LastIndexOf('@');
        var path = at < 0 ? uses : uses[..at];
        var reference = at < 0 ? "" : uses[(at + 1)..];
        var owner = path.Split('/')[0];
        if (!policy.AllowedActionOwners.Contains(owner))
            findings.Add(Block("ACTION_OWNER_NOT_ALLOWED", $"Actions from '{owner}' are not on the allow-list.", line));
        if (reference.Length == 0)
            findings.Add(Block("UNPINNED_ACTION", $"'{uses}' has no ref.", line));
        else if (Sha40.IsMatch(reference))
        {
            if (policy.VerifiedActionPins.Count > 0 && !policy.VerifiedActionPins.Contains($"{path}@{reference}".ToLowerInvariant()))
                findings.Add(Warn("ACTION_PIN_NOT_VERIFIED", $"'{path}' is pinned to a commit this product has not verified: confirm that the SHA exists upstream.", line));
        }
        else if (BranchLikeRefs.Contains(reference))
            findings.Add(Block("UNPINNED_ACTION", $"'{uses}' follows a moving branch: pin a commit SHA.", line));
        else if (policy.TrustedOwnersMayUseVersionTags && policy.TrustedOwners.Contains(owner) && VersionTag.IsMatch(reference))
            findings.Add(Warn("ACTION_PINNED_BY_TAG", $"'{uses}' is pinned by a mutable tag; a commit SHA is stronger.", line));
        else
            findings.Add(policy.RequireShaPinning
                ? Block("UNPINNED_ACTION", $"'{uses}' must be pinned to a commit SHA.", line)
                : Warn("ACTION_PINNED_BY_TAG", $"'{uses}' is pinned by a mutable ref.", line));
    }

    private static void CheckRunner(WorkflowJob job, List<GateFinding> findings)
    {
        if (ParsedWorkflow.Scalars(job.RunsOn).Any(s => string.Equals(s.Value, "self-hosted", StringComparison.OrdinalIgnoreCase)))
            findings.Add(Warn("SELF_HOSTED_RUNNER", $"Job '{job.Id}' needs a self-hosted runner that may not exist at the destination.", job.Line));
    }

    // ---- scripts ------------------------------------------------------------------------------------------

    private static void CheckRunScript(WorkflowStep step, string script, WorkflowGatePolicy policy, List<GateFinding> findings)
    {
        var line = step.RunLine;
        foreach (var pattern in RemoteScript)
            if (SafeIsMatch(pattern, script))
            {
                findings.Add(Block("REMOTE_SCRIPT_EXECUTION", "A downloaded script is executed without verification (pipe to a shell).", line));
                break;
            }
        CheckUntrusted(script, line, findings, "run");
        if (SafeMatches(InputContext, script).Any())
            findings.Add(Warn("INPUT_IN_RUN_SCRIPT", "A workflow input is interpolated into a script: pass it through 'env' instead.", line));

        var scriptSecrets = SafeMatches(Expression, script).Where(m => SecretRef.IsMatch(m.Groups[1].Value)).ToList();
        if (scriptSecrets.Count > 0)
            findings.Add(Block("SECRET_IN_RUN_SCRIPT", "A secret is expanded inside a script body: map it to an environment variable.", line));

        var touchesSecret = scriptSecrets.Count > 0 || ParsedWorkflow.Scalars(step.Env).Any(s => SecretRef.IsMatch(s.Value ?? ""));
        foreach (Match url in SafeMatches(Url, script))
        {
            var host = url.Groups[1].Value;
            if (HostAllowed(host, policy)) continue;
            findings.Add(touchesSecret
                ? Block("SECRET_TO_UNLISTED_HOST", $"A step with access to a secret contacts '{host}', which is not on the host allow-list.", line)
                : Warn("UNLISTED_HOST", $"The script contacts '{host}', which is not on the host allow-list.", line));
        }
    }

    private static void CheckUntrusted(string text, int line, List<GateFinding> findings, string where)
    {
        foreach (Match expression in SafeMatches(Expression, text))
            if (SafeIsMatch(UntrustedContext, expression.Groups[1].Value))
            {
                findings.Add(Block("UNTRUSTED_CONTEXT_IN_SCRIPT", $"An attacker-controlled GitHub context is interpolated into a {where} script (injection): pass it through 'env'.", line));
                return;
            }
    }

    private static bool HostAllowed(string host, WorkflowGatePolicy policy) =>
        policy.AllowedHosts.Any(allowed => allowed.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(allowed[1..], StringComparison.OrdinalIgnoreCase) || host.Equals(allowed[2..], StringComparison.OrdinalIgnoreCase)
            : host.Equals(allowed, StringComparison.OrdinalIgnoreCase));

    // ---- references and literals ----------------------------------------------------------------------------

    private static void CheckReferences(WorkflowGateContext context, List<GateFinding> findings)
    {
        var text = context.Yaml;
        foreach (Match m in SafeMatches(Expression, text))
        {
            var line = WorkflowStructureGate.LineOf(text, m.Index);
            foreach (Match s in SafeMatches(SecretRef, m.Groups[1].Value))
            {
                var name = s.Groups[1].Success ? s.Groups[1].Value : s.Groups[2].Value;
                if (name != "GITHUB_TOKEN" && !context.Plan.AllowedSecrets.Contains(name))
                    findings.Add(Block("SECRET_NOT_IN_PLAN", $"Secret '{name}' is not part of the migration plan.", line));
            }
            foreach (Match v in SafeMatches(VarRef, m.Groups[1].Value))
                if (!context.Plan.AllowedVariables.Contains(v.Groups[1].Value))
                    findings.Add(Block("VARIABLE_NOT_IN_PLAN", $"Variable '{v.Groups[1].Value}' is not part of the migration plan.", line));
        }
        if (Regex.IsMatch(text, @"^\s*secrets:\s*inherit\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase, Timeout))
            findings.Add(Warn("SECRETS_INHERIT", "'secrets: inherit' passes every secret to the called workflow."));
    }

    private static void CheckLiteralSecrets(string yaml, List<GateFinding> findings)
    {
        foreach (var match in SecretPatterns.Scan(yaml))
            findings.Add(Block("LITERAL_SECRET", $"A literal credential ({match.Kind}) is written in the workflow: use a secret reference.", match.Line));
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static bool SafeIsMatch(Regex regex, string input)
    {
        try { return regex.IsMatch(input); } catch (RegexMatchTimeoutException) { return true; }
    }

    private static List<Match> SafeMatches(Regex regex, string input)
    {
        try { return regex.Matches(input).ToList(); } catch (RegexMatchTimeoutException) { return []; }
    }

    private static GateFinding Block(string code, string message, int? line = null) => new(GateName, code, GateSeverity.Blocker, message, line);
    private static GateFinding Warn(string code, string message, int? line = null) => new(GateName, code, GateSeverity.Warning, message, line);
    private static GateFinding Info(string code, string message, int? line = null) => new(GateName, code, GateSeverity.Info, message, line);
}
