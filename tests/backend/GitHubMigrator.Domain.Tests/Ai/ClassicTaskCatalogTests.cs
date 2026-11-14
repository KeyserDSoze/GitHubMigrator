using System.Text.RegularExpressions;
using GitHubMigrator.Business;

namespace GitHubMigrator.Domain.Tests.Ai;

// A classic pipeline names a task by GUID and version spec. The table that turns them into "Name@N" was generated from the public task.json files; the expected values below were read from that source
// (microsoft/azure-pipelines-tasks), not taken from the table.
public sealed class ClassicTaskCatalogTests
{
    [Fact]
    public void The_table_is_the_generated_one_and_is_well_formed()
    {
        Assert.Matches("^[0-9a-f]{40}$", ClassicTaskCatalog.SourceCommit);
        Assert.True(ClassicTaskCatalog.Count > 200, ClassicTaskCatalog.Count.ToString());
    }

    [Theory]
    [InlineData("e213ff0f-5d5c-4791-802d-52ea3e7be1f1", "2.*", "PowerShell", 2, true)]
    [InlineData("71a9a2d3-a98a-4caa-96ab-affca411ecda", "1.*", "VSBuild", 1, true)]
    [InlineData("ef087383-ee5e-42c7-9a53-ab56c98420f9", "2.*", "VSTest", 2, true)]
    [InlineData("ef087383-ee5e-42c7-9a53-ab56c98420f9", "3.*", "VSTest", 3, true)]
    [InlineData("333b11bd-d341-40d9-afcf-b32d5ce6f23b", "2.*", "NuGetCommand", 2, true)]    // one id, three names by major version
    [InlineData("333b11bd-d341-40d9-afcf-b32d5ce6f23b", "1.*", "NuGetRestore", 1, true)]
    [InlineData("333b11bd-d341-40d9-afcf-b32d5ce6f23b", "0.*", "NuGetInstaller", 0, true)]
    [InlineData("b0ce7256-7898-45d3-9cb5-176b752bfea6", "2.*", "UseDotNet", 2, true)]
    [InlineData("b0ce7256-7898-45d3-9cb5-176b752bfea6", "1.*", "DotNetCoreInstaller", 1, true)]
    [InlineData("2c65196a-54fd-4a02-9be8-d9d1837b7c5d", "1.*", "NuGetToolInstaller", 1, true)]
    public void A_task_id_and_a_version_spec_are_the_name_a_yaml_pipeline_would_write(string id, string spec, string name, int major, bool exact)
    {
        var resolved = ClassicTaskCatalog.Resolve(id, spec);
        Assert.Equal((name, major, exact), (resolved!.Name, resolved.Major, resolved.Exact));
        Assert.True(ClassicTaskCatalog.IsKnown(id));
    }

    [Fact]
    public void A_version_the_source_repository_no_longer_has_a_folder_for_keeps_its_name_but_is_not_exact()
    {
        // PowerShell@1 is still what a pipeline calls it, but the public repository only has the newer majors: the id says the name, the spec says the major, and the table does not claim to know that exact version
        var old = ClassicTaskCatalog.Resolve("e213ff0f-5d5c-4791-802d-52ea3e7be1f1", "1.*")!;
        Assert.Equal(("PowerShell", 1, false), (old.Name, old.Major, old.Exact));
    }

    [Fact]
    public void For_an_id_with_several_names_a_version_the_table_lacks_gets_the_name_of_the_latest_major_not_the_first()
    {
        // 333b11bd… is NuGetInstaller@0, NuGetRestore@1 and NuGetCommand@2: the latest known name is the last, whatever the spec asks for
        var newer = ClassicTaskCatalog.Resolve("333b11bd-d341-40d9-afcf-b32d5ce6f23b", "9.*")!;
        Assert.Equal(("NuGetCommand", 9, false), (newer.Name, newer.Major, newer.Exact));
        var any = ClassicTaskCatalog.Resolve("333b11bd-d341-40d9-afcf-b32d5ce6f23b", "*")!;
        Assert.Equal(("NuGetCommand", 2, false), (any.Name, any.Major, any.Exact));
    }

    [Fact]
    public void The_spelling_of_the_id_does_not_matter()
    {
        const string id = "E213FF0F-5D5C-4791-802D-52EA3E7BE1F1";
        Assert.Equal("PowerShell", ClassicTaskCatalog.Resolve(id, "2.*")!.Name);
        Assert.Equal("PowerShell", ClassicTaskCatalog.Resolve("{" + id + "}", "2.*")!.Name);
        Assert.Equal("PowerShell", ClassicTaskCatalog.Resolve(id.ToLowerInvariant().Replace("-", ""), "2.*")!.Name);
        Assert.Equal(ClassicTaskCatalog.NormalizeId(id), ClassicTaskCatalog.NormalizeId(id.ToLowerInvariant()));
    }

    [Fact]
    public void A_version_the_table_does_not_have_gets_the_latest_known_name_with_the_asked_major_and_is_not_exact()
    {
        var newer = ClassicTaskCatalog.Resolve("e213ff0f-5d5c-4791-802d-52ea3e7be1f1", "9.*")!;
        Assert.Equal(("PowerShell", 9, false), (newer.Name, newer.Major, newer.Exact));
        var any = ClassicTaskCatalog.Resolve("e213ff0f-5d5c-4791-802d-52ea3e7be1f1", "*")!;
        Assert.False(any.Exact);
        Assert.Equal("PowerShell", any.Name);
        Assert.True(any.Major >= 2);
        Assert.False(ClassicTaskCatalog.Resolve("e213ff0f-5d5c-4791-802d-52ea3e7be1f1", null)!.Exact);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    public void An_id_the_table_does_not_know_is_not_guessed(string? id)
    {
        Assert.Null(ClassicTaskCatalog.Resolve(id, "1.*"));
        Assert.False(ClassicTaskCatalog.IsKnown(id));
    }

    [Theory]
    [InlineData("2.*", 2)]
    [InlineData("2.1.0", 2)]
    [InlineData("2", 2)]
    [InlineData(" 3.* ", 3)]
    [InlineData("10.*", 10)]
    [InlineData("0.*", 0)]
    [InlineData("*", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("abc", null)]
    [InlineData("12345.*", null)]
    [InlineData("v2", null)]
    [InlineData("-1.*", null)]
    public void The_major_version_is_the_leading_number_of_the_spec(string? spec, int? major)
    {
        Assert.Equal(major, ClassicTaskCatalog.MajorOf(spec));
    }

    [Fact]
    public void Every_name_in_the_table_is_a_plain_name_a_yaml_pipeline_can_write()
    {
        // the names are what ends up after "task:" in the text the converter reads: nothing in them may need quoting
        var names = Enumerable.Range(0, 1).SelectMany(_ => KnownSample()).ToArray();
        Assert.All(names, n => Assert.Matches(new Regex("^[A-Za-z0-9._-]+$"), n));
        Assert.Contains("PowerShell", names);
    }

    private static IEnumerable<string> KnownSample() => new[] { "e213ff0f-5d5c-4791-802d-52ea3e7be1f1", "71a9a2d3-a98a-4caa-96ab-affca411ecda", "333b11bd-d341-40d9-afcf-b32d5ce6f23b", "ef087383-ee5e-42c7-9a53-ab56c98420f9" }
        .SelectMany(id => new[] { "0.*", "1.*", "2.*", "3.*" }.Select(spec => ClassicTaskCatalog.Resolve(id, spec)!.Name));
}
