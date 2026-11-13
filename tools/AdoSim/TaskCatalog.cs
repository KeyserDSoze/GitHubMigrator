namespace AdoSim;

// Task ids from microsoft/azure-pipelines-tasks (Tasks/*/task.json); unknown tasks get a stable synthetic id.
public static class TaskCatalog
{
    private static readonly Dictionary<string, string> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PowerShell"] = "e213ff0f-5d5c-4791-802d-52ea3e7be1f1",
        ["VSBuild"] = "71a9a2d3-a98a-4caa-96ab-affca411ecda",
        ["VSTest"] = "ef087383-ee5e-42c7-9a53-ab56c98420f9",
        ["NuGetCommand"] = "333b11bd-d341-40d9-afcf-b32d5ce6f23b",
        ["NuGetToolInstaller"] = "2c65196a-54fd-4a02-9be8-d9d1837b7c5d",
        ["CmdLine"] = "d9bafed4-0b18-4f58-968d-86655b4d2ce9",
        ["PublishBuildArtifacts"] = "2ff763a7-ce83-4e1f-bc89-0ae63477cebe",
        ["PublishTestResults"] = "0b0f01ed-7dde-43ff-9cbb-e48954daf9b1",
        ["UseDotNet"] = "b0ce7256-7898-45d3-9cb5-176b752bfea6",
        ["DotNetCoreCLI"] = "5541a522-603c-47ad-91fc-a4b1d163081b",
        ["Npm"] = "fe47e961-9fa8-4106-8639-368c022d43ad",
        ["NodeTool"] = "31c75bbb-bcdf-4706-8d7c-4da6a1959bc2",
        ["CopyFiles"] = "5bfb729a-a7c8-4a78-a7c3-8d717bb7c13c",
        ["Bash"] = "6c731c3c-3c68-459a-a5c9-bde6e6595b5b",
        ["MSBuild"] = "c6c4c611-aa2e-4a33-b606-5eaba2196824",
        ["Docker"] = "e28912f1-0114-4464-802a-a3a35437fd16",
        ["AzureCLI"] = "46e4be58-730b-4389-8a2f-ea10b3e5e815",
        ["AzureWebApp"] = "18bde28a-8172-45cb-b204-5cef1393dbb1",
        ["Cache"] = "d53ccab4-555e-4494-9d06-11db043fb4a9",
        ["PublishCodeCoverageResults"] = "2a7ebc54-c13e-490e-81a5-d7561ab7cd97",
        ["PublishPipelineArtifact"] = "ecdc45f6-832d-4ad9-b52b-ee49e94659be"
    };

    // "VSBuild@1" -> (id, "1.*")
    public static (string Id, string VersionSpec) Resolve(string task)
    {
        var parts = task.Split('@');
        var name = parts[0];
        var major = parts.Length > 1 ? parts[1] : "0";
        return (Ids.TryGetValue(name, out var id) ? id : AdoSim.Ids.Guid("task:" + name), major + ".*");
    }
}
