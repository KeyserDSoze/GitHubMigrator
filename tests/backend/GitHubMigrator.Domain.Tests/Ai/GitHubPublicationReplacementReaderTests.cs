using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitHubMigrator.Business;
using GitHubMigrator.Business.WorkflowGates;
using GitHubMigrator.Infrastructure.GitHub;

namespace GitHubMigrator.Domain.Tests.Ai;

// The read-only side of replacing a file and of reconciling an attempt: what is classified, what is believed about a blob id, and which requests are made.
public sealed class GitHubPublicationReplacementReaderTests
{
    private const string Sha = "1111111111111111111111111111111111111111";
    private const string Approved = "name: new\non:\n  workflow_dispatch:\n  push:\njobs: {}\n";
    private const string Prior = "name: old\non:\n  workflow_dispatch:\njobs: {}\n";
    private const string Head = "migrate/azdo-42-abcd1234";
    private const string Path = ".github/workflows/azdo-42-x.yml";

    private static string BlobOf(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexStringLower(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {bytes.Length}\0"), .. bytes]));
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => StubHandler.Json(status, JsonSerializer.Serialize(value));
    private static HttpResponseMessage NotFound() => StubHandler.Json(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
    private static HttpResponseMessage Repo(bool archived = false) => Json(new { archived, default_branch = "main" });
    private static HttpResponseMessage Tip(string sha = Sha) => Json(new { commit = new { sha } });
    private static HttpResponseMessage File(string text, string? sha = "auto") => Json(new
    {
        type = "file", encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).Insert(8, "\n") + "\n", sha = sha == "auto" ? BlobOf(text) : sha
    });

    private static GitHubPublicationReader Reader(StubHandler handler) => new(new HttpClient(handler));

    // ---- reading for a plan: the text a derived proposal may replace ----------------------------------------------------------------------------

    private static async Task<PublicationTarget> ReadAsync(Func<HttpResponseMessage> file, string? prior)
    {
        var handler = new StubHandler().Then(() => Repo()).Then(() => Tip()).Then(file).Then(() => NotFound()).Then(() => Json(Array.Empty<object>()));
        return await Reader(handler).ReadAsync("target-org", "orion-api", null, Head, Path, WorkflowHash.Sha256(Approved), prior, "pat", default);
    }

    [Fact]
    public async Task The_text_the_caller_named_is_a_prior_version_and_its_blob_id_is_reported_only_when_it_is_the_id_of_the_bytes_that_were_read()
    {
        var found = await ReadAsync(() => File(Prior), WorkflowHash.Sha256(Prior));
        Assert.Equal(WorkflowFileState.PriorVersion, found.WorkflowFile);
        Assert.Equal(BlobOf(Prior), found.WorkflowFileBlobSha);

        // line endings do not change which text it is, but the blob id is that of the bytes as they are
        var crlf = Prior.Replace("\n", "\r\n");
        var windows = await ReadAsync(() => File(crlf), WorkflowHash.Sha256(Prior));
        Assert.Equal((WorkflowFileState.PriorVersion, BlobOf(crlf)), (windows.WorkflowFile, windows.WorkflowFileBlobSha));

        // an id that is not the id of those bytes, or no id at all, is not a file to replace
        foreach (var lie in new string?[] { new string('0', 40), null, "", "not-a-sha" })
        {
            var unvouched = await ReadAsync(() => File(Prior, lie), WorkflowHash.Sha256(Prior));
            Assert.Equal((WorkflowFileState.Different, (string?)null), (unvouched.WorkflowFile, unvouched.WorkflowFileBlobSha));
        }
    }

    [Fact]
    public async Task Without_a_named_text_or_with_another_one_a_file_is_the_approved_text_or_somebody_elses_and_has_no_blob_id()
    {
        var unnamed = await ReadAsync(() => File(Prior), null);
        Assert.Equal((WorkflowFileState.Different, (string?)null), (unnamed.WorkflowFile, unnamed.WorkflowFileBlobSha));

        var other = await ReadAsync(() => File("name: something else\non: push\njobs: {}\n"), WorkflowHash.Sha256(Prior));
        Assert.Equal((WorkflowFileState.Different, (string?)null), (other.WorkflowFile, other.WorkflowFileBlobSha));

        // the approved text wins, and needs no blob id
        var same = await ReadAsync(() => File(Approved), WorkflowHash.Sha256(Prior));
        Assert.Equal((WorkflowFileState.Identical, (string?)null), (same.WorkflowFile, same.WorkflowFileBlobSha));

        var absent = await ReadAsync(NotFound, WorkflowHash.Sha256(Prior));
        Assert.Equal((WorkflowFileState.Absent, (string?)null), (absent.WorkflowFile, absent.WorkflowFileBlobSha));

        // something that is not a plain file is never replaceable
        foreach (var odd in new Func<HttpResponseMessage>[]
        {
            () => Json(new[] { new { type = "file" } }), () => Json(new { type = "dir" }), () => Json(new { type = "symlink", target = "x" }),
            () => Json(new { type = "file", encoding = "none", content = "", sha = BlobOf(Prior) })
        })
            Assert.Equal(WorkflowFileState.Different, (await ReadAsync(odd, WorkflowHash.Sha256(Prior))).WorkflowFile);
    }

    // ---- observing an attempt that was made ----------------------------------------------------------------------------------------------------

    private static Task<PublicationObservation> ObserveAsync(StubHandler handler, int? pullNumber = null, string? replaced = null) =>
        Reader(handler).ObserveAsync("target-org", "orion-api", "main", Head, Path, WorkflowHash.Sha256(Approved), replaced, pullNumber, "pat", default);

    private static object ListedPull(int number, string state = "open", string? mergedAt = null, string head = Head, string? headSha = "2222222222222222222222222222222222222222") =>
        new { number, state, merged_at = mergedAt, merge_commit_sha = mergedAt is null ? null : "3333333333333333333333333333333333333333", head = new { @ref = head, sha = headSha }, @base = new { @ref = "main" } };

    [Fact]
    public async Task It_looks_with_get_requests_only_at_the_repository_the_two_branches_every_pull_request_from_the_branch_and_the_file_on_each()
    {
        var handler = new StubHandler().Then(() => Repo()).Then(() => Tip()).Then(() => Json(new { @ref = "refs/heads/" + Head, @object = new { sha = "2222222222222222222222222222222222222222" } }))
            .Then(() => Json(new[] { ListedPull(7) })).Then(() => File(Approved)).Then(() => NotFound());
        var seen = await ObserveAsync(handler, 7);

        Assert.Equal((true, false, Sha, "2222222222222222222222222222222222222222"), (seen.RepositoryExists, seen.Archived, seen.BaseTipSha, seen.HeadBranchSha));
        var pull = Assert.Single(seen.Pulls);
        Assert.Equal((7, "open", false), (pull.Number, pull.State, pull.Merged));
        Assert.Equal((WorkflowFileState.Identical, WorkflowFileState.Absent), (seen.FileOnHead, seen.FileOnBase));

        Assert.All(handler.Sent, r => { Assert.Equal(HttpMethod.Get, r.Method); Assert.Equal("Bearer pat", r.Headers["Authorization"]); Assert.StartsWith("/repos/target-org/orion-api", r.Uri.AbsolutePath); });
        Assert.Equal(["/repos/target-org/orion-api", "/repos/target-org/orion-api/branches/main", $"/repos/target-org/orion-api/git/ref/heads/{Head}", "/repos/target-org/orion-api/pulls",
            $"/repos/target-org/orion-api/contents/{Path}", $"/repos/target-org/orion-api/contents/{Path}"], handler.Sent.Select(r => r.Uri.AbsolutePath.Replace("%2F", "/")));
        Assert.Equal("?state=all&head=target-org:migrate%2Fazdo-42-abcd1234&per_page=5", handler.Sent[3].Uri.Query); // every state: a merged or a closed pull request is what this is for
        Assert.Equal("?ref=2222222222222222222222222222222222222222", handler.Sent[4].Uri.Query); // the file on the branch, at the branch tip
        Assert.Equal("?ref=" + Sha, handler.Sent[5].Uri.Query);                                  // and on the base, at the base tip
    }

    [Fact]
    public async Task A_merged_pull_request_is_known_by_merged_at_in_a_list_and_by_merged_on_its_own_and_only_ours_count()
    {
        var handler = new StubHandler().Then(() => Repo()).Then(() => Tip()).Then(() => NotFound())
            .Then(() => Json(new object[]
            {
                ListedPull(9, "closed", mergedAt: "2026-11-13T12:00:00Z"),
                ListedPull(8, "closed"),                      // closed without merge
                ListedPull(7, "open", head: "some-other-branch"), // a server that ignored the filter
                ListedPull(9, "closed", mergedAt: "2026-11-13T12:00:00Z"), // a duplicate
                new { head = new { @ref = Head } },          // no number
                "junk"
            })).Then(() => NotFound());
        var seen = await ObserveAsync(handler);
        Assert.Equal([9, 8], seen.Pulls.Select(p => p.Number));
        Assert.Equal([true, false], seen.Pulls.Select(p => p.Merged));
        Assert.Equal(["closed", "closed"], seen.Pulls.Select(p => p.State));
        Assert.Equal("3333333333333333333333333333333333333333", seen.Pulls[0].MergeCommitSha);
        Assert.Equal(WorkflowFileState.Absent, seen.FileOnHead);   // no branch: no file on it, and nothing asked about it
        Assert.Equal(WorkflowFileState.Absent, seen.FileOnBase);
    }

    [Fact]
    public async Task The_number_the_ledger_recorded_is_read_directly_when_the_list_does_not_show_it_and_only_if_it_is_a_pull_request_of_this_branch()
    {
        // the list is empty (the filter may not match what was recorded): the recorded number is asked for by itself
        var shown = new StubHandler().Then(() => Repo()).Then(() => Tip()).Then(() => NotFound()).Then(() => Json(Array.Empty<object>()))
            .Then(() => Json(new { number = 7, state = "closed", merged = true, merged_at = (string?)null, head = new { @ref = Head, sha = "2222222222222222222222222222222222222222" }, @base = new { @ref = "main" } })).Then(() => NotFound());
        var seen = await ObserveAsync(shown, 7);
        Assert.Equal("/repos/target-org/orion-api/pulls/7", shown.Sent[4].Uri.AbsolutePath);
        var pull = Assert.Single(seen.Pulls);
        Assert.Equal((7, "closed", true), (pull.Number, pull.State, pull.Merged)); // `merged` on a single pull request

        // a number that belongs to another branch is not this attempt's pull request; a number that does not exist is not either
        var foreign = new StubHandler().Then(() => Repo()).Then(() => Tip()).Then(() => NotFound()).Then(() => Json(Array.Empty<object>()))
            .Then(() => Json(new { number = 7, state = "open", head = new { @ref = "another" }, @base = new { @ref = "main" } })).Then(() => NotFound());
        Assert.Empty((await ObserveAsync(foreign, 7)).Pulls);
        var gone = new StubHandler().Then(() => Repo()).Then(() => Tip()).Then(() => NotFound()).Then(() => Json(Array.Empty<object>())).Then(() => NotFound()).Then(() => NotFound());
        Assert.Empty((await ObserveAsync(gone, 7)).Pulls);

        // already in the list: not asked twice
        var listed = new StubHandler().Then(() => Repo()).Then(() => Tip()).Then(() => NotFound()).Then(() => Json(new[] { ListedPull(7) })).Then(() => NotFound());
        await ObserveAsync(listed, 7);
        Assert.DoesNotContain(listed.Sent, r => r.Uri.AbsolutePath.EndsWith("/pulls/7"));
    }

    [Fact]
    public async Task What_is_on_each_branch_is_classified_against_the_approved_text_and_the_text_that_was_replaced_and_never_returned()
    {
        async Task<(WorkflowFileState Head, WorkflowFileState Base)> Files(Func<HttpResponseMessage> onHead, Func<HttpResponseMessage> onBase, string? replaced)
        {
            var handler = new StubHandler().Then(() => Repo()).Then(() => Tip()).Then(() => Json(new { @object = new { sha = "2222222222222222222222222222222222222222" } }))
                .Then(() => Json(Array.Empty<object>())).Then(onHead).Then(onBase);
            var seen = await ObserveAsync(handler, replaced: replaced);
            return (seen.FileOnHead, seen.FileOnBase);
        }
        var prior = WorkflowHash.Sha256(Prior);
        Assert.Equal((WorkflowFileState.Identical, WorkflowFileState.PriorVersion), await Files(() => File(Approved), () => File(Prior), prior));
        Assert.Equal((WorkflowFileState.Different, WorkflowFileState.Different), await Files(() => File("name: x\njobs: {}\n"), () => File(Prior), null)); // the replaced text is named or it is somebody's
        Assert.Equal((WorkflowFileState.Different, WorkflowFileState.Identical), await Files(() => File(Prior + "# edited\n"), () => File(Approved), prior));
        Assert.Equal((WorkflowFileState.Absent, WorkflowFileState.Different), await Files(NotFound, () => Json(new { type = "dir" }), prior));
        // for a view, a file that is the replaced text counts even if its blob id was not vouched for: the id only matters when writing
        Assert.Equal((WorkflowFileState.PriorVersion, WorkflowFileState.PriorVersion), await Files(() => File(Prior, "whatever"), () => File(Prior, null), prior));
    }

    [Fact]
    public async Task A_missing_repository_stops_the_observation_and_a_missing_base_is_reported_as_missing_not_as_an_empty_file()
    {
        var none = new StubHandler().Then(() => NotFound());
        var gone = await ObserveAsync(none);
        Assert.False(gone.RepositoryExists);
        Assert.Single(none.Sent);

        var noBase = new StubHandler().Then(() => Repo(archived: true)).Then(() => NotFound()).Then(() => NotFound()).Then(() => Json(Array.Empty<object>()));
        var seen = await ObserveAsync(noBase);
        Assert.True(seen.Archived);
        Assert.Null(seen.BaseTipSha);
        Assert.Equal(4, noBase.Sent.Count); // no file is asked about on a branch that is not there
    }

    [Fact]
    public async Task Anything_but_an_answer_is_unreadable_and_not_a_finding()
    {
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError, HttpStatusCode.TooManyRequests })
            await Assert.ThrowsAsync<HttpRequestException>(() => ObserveAsync(new StubHandler().Then(() => StubHandler.Json(status, "{}"))));
        await Assert.ThrowsAsync<HttpRequestException>(() => ObserveAsync(new StubHandler().Then(() => Repo()).Then(() => StubHandler.Json(HttpStatusCode.InternalServerError, "{}"))));
        await Assert.ThrowsAsync<HttpRequestException>(() => ObserveAsync(new StubHandler().Then(() => StubHandler.Json(HttpStatusCode.OK, "not json"))));
    }
}
