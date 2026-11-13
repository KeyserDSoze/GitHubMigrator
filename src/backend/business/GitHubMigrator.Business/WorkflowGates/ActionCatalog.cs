namespace GitHubMigrator.Business.WorkflowGates;

public sealed record VerifiedAction(string Repository, string Tag, string Sha, string Purpose)
{
    public string Reference => $"{Repository}@{Sha}";
}

// Commits that were resolved from the upstream repositories (git ls-remote on the tag, peeled when annotated) on
// VerifiedOn and whose input names were read from the action.yml at that commit. A model cannot know a commit SHA: handed
// this list it can pin real ones, and the policy gate warns about every other pin (ACTION_PIN_NOT_VERIFIED) because a
// made-up SHA fails only when the workflow runs. It is a snapshot: it neither follows new releases nor checks advisories.
public static class ActionCatalog
{
    public static readonly DateOnly VerifiedOn = new(2026, 11, 13);

    public static readonly IReadOnlyList<VerifiedAction> All =
    [
        new("actions/checkout", "v7.0.1", "3d3c42e5aac5ba805825da76410c181273ba90b1", "check out the repository (use persist-credentials: false)"),
        new("actions/setup-dotnet", "v6.1.0", "0f4a354dd5547a28c70b7853dbd95d5b67886e73", "install a .NET SDK (dotnet-version)"),
        new("actions/setup-node", "v7.1.0", "949feb2413d6458794dcd2491c4babbbce0c15c1", "install Node.js (node-version, registry-url)"),
        new("actions/setup-python", "v7.1.0", "6d4fd22b20465ccc0f7354476588e8df7c9ca0e0", "install Python (python-version)"),
        new("actions/cache", "v6.1.0", "55cc8345863c7cc4c66a329aec7e433d2d1c52a9", "cache directories (path, key, restore-keys)"),
        new("actions/upload-artifact", "v7.0.2", "cf430e030ddbb5b0abf93d22962f4752f3646cd9", "publish files as an artifact (name, path)"),
        new("actions/download-artifact", "v8.0.2", "9000827ccba6bdab643e8b6fd33ac0654aef8333", "download an artifact (name, path)"),
        new("azure/login", "v3.1.0", "a641126d1b8aa4d1fa005f4f92df94a3a4c4c906", "log in to Azure with OIDC (client-id, tenant-id, subscription-id; job needs id-token: write)"),
        new("azure/webapps-deploy", "v3.0.8", "02a81bead70021f5284939794bcec79c271ab383", "deploy to an Azure Web App (app-name, package)"),
        new("azure/aks-set-context", "v5.0.1", "883bdc8d8d1d0fd87e66cc4ba6f0b618f9eb5281", "select an AKS cluster for kubectl (resource-group, cluster-name)"),
        new("hashicorp/setup-terraform", "v4.0.1", "dfe3c3f87815947d99a8997f908cb6525fc44e9e", "install Terraform (terraform_version, terraform_wrapper)"),
        new("docker/login-action", "v4.8.0", "8aa5831fb8d48d8f76594c7ddde61ae58019f550", "log in to a container registry (registry, username, password)"),
        new("docker/build-push-action", "v7.4.0", "c3c9e263c25d99ce0380d002d59b67737d91b0dc", "build and push an image (context, file, push, tags)"),
        new("microsoft/setup-msbuild", "v3.0.0", "30375c66a4eea26614e0d39710365f22f8b0af57", "put msbuild on PATH on Windows runners"),
        new("sonarsource/sonarqube-quality-gate-action", "v1.2.1", "7a5fffe8e523c40e0c740b6bc2712ab503e52efa", "wait for the SonarQube quality gate (pollingTimeoutSec, scanMetadataReportFile)")
    ];

    // lower-case "owner/repo@sha" for membership tests
    public static IReadOnlySet<string> Pins { get; } = All.Select(a => a.Reference.ToLowerInvariant()).ToHashSet();
}
