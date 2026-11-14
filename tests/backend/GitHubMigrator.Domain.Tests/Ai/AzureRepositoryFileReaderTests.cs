using System.Net;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Infrastructure.AzureDevOps;

namespace GitHubMigrator.Domain.Tests.Ai;

// One file of one repository of the connection's own organization, by GET, with the connection's credential and nothing else: what a template read sends and how it answers.
public sealed class AzureRepositoryFileReaderTests
{
    private static HttpResponseMessage Item(string? content, HttpStatusCode status = HttpStatusCode.OK) =>
        StubHandler.Json(status, JsonSerializer.Serialize<object>(content is null ? new { path = "/x", isFolder = true } : new { path = "/x", content }));

    private static AzureDevOpsWorkloadReader Reader(StubHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task It_asks_for_one_file_by_path_in_the_project_and_repository_it_was_given_on_the_default_branch_with_the_connections_credential()
    {
        var handler = new StubHandler().Then(() => Item("steps:\n- script: echo\n"));
        var file = await ((IRepositoryFileReader)Reader(handler)).ReadRepositoryFileAsync("contoso", "Orion Platform", "orion templates", "/steps/a b.yml", null, null, "ado-pat", default);

        Assert.Equal(("steps:\n- script: echo\n", false), (file.Content, file.TooLarge));
        var request = Assert.Single(handler.Sent);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("dev.azure.com", request.Uri.Host);
        Assert.Equal("/contoso/Orion%20Platform/_apis/git/repositories/orion%20templates/items", request.Uri.AbsolutePath);
        Assert.Contains("path=%2Fsteps%2Fa%20b.yml", request.Uri.Query);
        Assert.Contains("includeContent=true", request.Uri.Query);
        Assert.DoesNotContain("versionDescriptor", request.Uri.Query); // the default branch
        Assert.Equal("Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":ado-pat")), request.Headers["Authorization"]);
    }

    [Theory]
    [InlineData("branch", "main", "versionDescriptor.version=main&versionDescriptor.versionType=branch")]
    [InlineData("tag", "v1.2", "versionDescriptor.version=v1.2&versionDescriptor.versionType=tag")]
    [InlineData("branch", "release/2026", "versionDescriptor.version=release%2F2026&versionDescriptor.versionType=branch")]
    public async Task A_branch_or_a_tag_is_asked_for_as_a_version_descriptor(string type, string version, string expected)
    {
        var handler = new StubHandler().Then(() => Item("a: 1\n"));
        await ((IRepositoryFileReader)Reader(handler)).ReadRepositoryFileAsync("contoso", "p", "r", "/a.yml", type, version, "t", default);
        Assert.Contains(expected, handler.Sent.Single().Uri.Query);
    }

    [Theory]
    [InlineData("commit", "abc")]
    [InlineData("branch", null)]
    [InlineData(null, "main")]
    [InlineData("branch&x=1", "main")]
    public async Task Anything_but_a_branch_or_a_tag_with_a_version_is_refused_before_a_request_is_made(string? type, string? version)
    {
        var handler = new StubHandler();
        await Assert.ThrowsAsync<HttpRequestException>(() => ((IRepositoryFileReader)Reader(handler)).ReadRepositoryFileAsync("contoso", "p", "r", "/a.yml", type, version, "t", default));
        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task A_folder_has_no_content_a_refusal_is_an_exception_and_a_large_file_is_said_to_be_too_large()
    {
        var folder = new StubHandler().Then(() => Item(null));
        Assert.Null((await ((IRepositoryFileReader)Reader(folder)).ReadRepositoryFileAsync("contoso", "p", "r", "/steps", null, null, "t", default)).Content);

        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.InternalServerError, HttpStatusCode.Found })
        {
            var refused = new StubHandler().Then(() => StubHandler.Json(status, "{}"));
            await Assert.ThrowsAsync<HttpRequestException>(() => ((IRepositoryFileReader)Reader(refused)).ReadRepositoryFileAsync("contoso", "p", "r", "/a.yml", null, null, "t", default));
        }

        var large = new StubHandler().Then(() => Item(new string('x', AzureDevOpsWorkloadReader.MaxPipelineYamlChars + 1)));
        var read = await ((IRepositoryFileReader)Reader(large)).ReadRepositoryFileAsync("contoso", "p", "r", "/a.yml", null, null, "t", default);
        Assert.Equal((null, true), (read.Content, read.TooLarge));
    }
}
