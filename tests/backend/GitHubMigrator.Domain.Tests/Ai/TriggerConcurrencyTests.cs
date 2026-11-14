using GitHubMigrator.Business;

namespace GitHubMigrator.Domain.Tests.Ai;

// Azure's `batch` (CI) and `autoCancel` (pull requests) become ONE top-level GitHub `concurrency` setting, scoped so that only the events they are about are ever queued or cancelled; `drafts: false`
// cannot be a trigger option and is said so. The Azure defaults are the documented ones (batch false, autoCancel true, drafts true) and an option that cannot be read is never guessed.
public sealed class TriggerConcurrencyTests
{
    private static AzureTriggers Read(string yaml) => AzureTriggerReader.Read(yaml)!;
    private static string[] Codes(IEnumerable<TriggerDifference> differences) => differences.Select(d => d.Code).Order(StringComparer.Ordinal).ToArray();

    private const string Manual = "name: demo\non: workflow_dispatch\npermissions:\n  contents: read\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo hi\n";

    // ---- the reader: Azure's documented defaults -------------------------------------------------------------------------------------------------

    [Fact]
    public void An_enabled_pull_request_trigger_cancels_superseded_runs_unless_it_says_false_and_batching_is_off_unless_it_says_true()
    {
        Assert.True(Read("pr:\n  branches:\n    include: [main]\n").Pr.AutoCancel);
        Assert.True(Read("pr: [main]\n").Pr.AutoCancel);
        Assert.False(Read("pr:\n  autoCancel: false\n  branches:\n    include: [main]\n").Pr.AutoCancel);
        Assert.False(Read("pr:\n  autoCancel: FALSE\n  branches:\n    include: [main]\n").Pr.AutoCancel);
        Assert.True(Read("pr:\n  autoCancel: True\n  branches:\n    include: [main]\n").Pr.AutoCancel);

        Assert.False(Read("trigger:\n  branches:\n    include: [main]\n").Ci.Batch);
        Assert.False(Read("trigger: [main]\n").Ci.Batch);
        Assert.True(Read("trigger:\n  batch: true\n  branches:\n    include: [main]\n").Ci.Batch);
        Assert.True(Read("trigger:\n  batch: TRUE\n  branches:\n    include: [main]\n").Ci.Batch);
        Assert.False(Read("trigger:\n  batch: false\n  branches:\n    include: [main]\n").Ci.Batch);

        Assert.True(Read("pr:\n  branches:\n    include: [main]\n").Pr.Drafts); // Azure's default: drafts start a run
        Assert.False(Read("pr:\n  drafts: false\n  branches:\n    include: [main]\n").Pr.Drafts);
    }

    [Fact]
    public void An_option_that_is_not_a_plain_true_or_false_is_not_guessed_the_default_is_assumed_and_a_difference_says_so()
    {
        var variable = Read("trigger:\n  batch: $(batchRuns)\n  branches:\n    include: [main]\npr:\n  autoCancel: ${{ parameters.cancel }}\n  drafts: maybe\n  branches:\n    include: [main]\n");
        Assert.False(variable.Ci.Batch);
        Assert.True(variable.Pr.AutoCancel);
        Assert.Null(variable.Pr.Drafts); // not "false": nothing is said about drafts
        Assert.Equal(["TRIGGER_OPTION_UNREADABLE"], Codes(variable.Notes));

        var mapped = GitHubTriggerMapper.Map(variable);
        Assert.Contains("TRIGGER_OPTION_UNREADABLE", Codes(mapped.Differences));
        Assert.DoesNotContain("TRIGGER_DRAFTS_NOT_MAPPED", Codes(mapped.Differences));
        Assert.DoesNotContain("TRIGGER_BATCH_APPROXIMATED", Codes(mapped.Differences));
        Assert.Equal(new GitHubConcurrency(false, true), mapped.Concurrency);
    }

    // ---- the mapper ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Batch_queues_push_runs_and_autocancel_cancels_pull_request_runs_and_each_only_where_its_trigger_exists()
    {
        var batch = GitHubTriggerMapper.Map(Read("trigger:\n  batch: true\n  branches:\n    include: [main]\n"));
        Assert.Equal(new GitHubConcurrency(true, false), batch.Concurrency);
        Assert.Equal(["TRIGGER_BATCH_APPROXIMATED"], Codes(batch.Differences));

        var cancel = GitHubTriggerMapper.Map(Read("trigger: none\npr:\n  branches:\n    include: [main]\n"));
        Assert.Equal(new GitHubConcurrency(false, true), cancel.Concurrency);
        Assert.Contains("TRIGGER_AUTOCANCEL_APPROXIMATED", Codes(cancel.Differences));

        var both = GitHubTriggerMapper.Map(Read("trigger:\n  batch: true\n  branches:\n    include: [main]\npr:\n  branches:\n    include: [main]\n"));
        Assert.Equal(new GitHubConcurrency(true, true), both.Concurrency);

        // nothing asked for, nothing added: no batch, autoCancel switched off, or the trigger the option belongs to is off
        foreach (var none in new[]
        {
            "trigger:\n  branches:\n    include: [main]\n",
            "trigger:\n  branches:\n    include: [main]\npr:\n  autoCancel: false\n  branches:\n    include: [main]\n",
            "trigger: none\npr: none\nschedules:\n- cron: '0 3 * * *'\n"
        })
        {
            var mapped = GitHubTriggerMapper.Map(Read(none));
            Assert.Null(mapped.Concurrency);
            Assert.DoesNotContain(mapped.Differences, d => d.Code is "TRIGGER_BATCH_APPROXIMATED" or "TRIGGER_AUTOCANCEL_APPROXIMATED");
        }
        // batch has no meaning when the pipeline has no CI trigger
        Assert.Null(GitHubTriggerMapper.Map(Read("trigger: none\nschedules:\n- cron: '0 3 * * *'\n")).Concurrency);
    }

    [Fact]
    public void The_approximations_say_what_github_does_and_drafts_are_said_not_to_be_mapped_with_the_reason()
    {
        var mapped = GitHubTriggerMapper.Map(Read("trigger:\n  batch: true\n  branches:\n    include: [main]\npr:\n  drafts: false\n  branches:\n    include: [main]\n"));
        string Message(string code) => mapped.Differences.Single(d => d.Code == code).Message;
        Assert.Contains("replaces the run that is still waiting", Message("TRIGGER_BATCH_APPROXIMATED"));
        Assert.Contains("The run in progress is not cancelled", Message("TRIGGER_BATCH_APPROXIMATED"));
        Assert.Contains("cancels the run in progress", Message("TRIGGER_AUTOCANCEL_APPROXIMATED"));
        Assert.Contains("true unless set to false", Message("TRIGGER_AUTOCANCEL_APPROXIMATED"));
        Assert.Contains("a condition on the jobs", Message("TRIGGER_DRAFTS_NOT_MAPPED"));
        Assert.Contains("github.event.pull_request.draft == false", Message("TRIGGER_DRAFTS_NOT_MAPPED"));
        Assert.Contains("does not add to an approved workflow", Message("TRIGGER_DRAFTS_NOT_MAPPED"));
        Assert.Equal(Codes(mapped.Differences).Distinct().Count(), mapped.Differences.Count);
    }

    [Fact]
    public void The_group_touches_only_the_events_that_were_asked_for_and_gives_every_other_run_a_group_of_its_own()
    {
        Assert.Equal("${{ github.workflow }}-${{ github.event_name == 'push' && github.ref || format('run-{0}', github.run_id) }}", new GitHubConcurrency(true, false).Group());
        Assert.Null(new GitHubConcurrency(true, false).CancelInProgress()); // pushes wait, they are never cancelled
        Assert.Equal("${{ github.workflow }}-${{ github.event_name == 'pull_request' && format('pr-{0}', github.event.pull_request.number) || format('run-{0}', github.run_id) }}", new GitHubConcurrency(false, true).Group());
        Assert.Equal("${{ github.event_name == 'pull_request' }}", new GitHubConcurrency(false, true).CancelInProgress());
        Assert.Equal("${{ github.workflow }}-${{ github.event_name == 'push' && github.ref || github.event_name == 'pull_request' && format('pr-{0}', github.event.pull_request.number) || format('run-{0}', github.run_id) }}",
            new GitHubConcurrency(true, true).Group());
        Assert.Equal("${{ github.event_name == 'pull_request' }}", new GitHubConcurrency(true, true).CancelInProgress()); // only pull request runs are ever cancelled
        Assert.Equal(["concurrency:", "  group: \"" + new GitHubConcurrency(true, false).Group() + "\""], new GitHubConcurrency(true, false).Lines());
        Assert.Equal(3, new GitHubConcurrency(false, true).Lines().Count);
    }

    // ---- the editor ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_concurrency_setting_is_added_right_after_the_on_entry_and_every_other_line_is_returned_byte_for_byte()
    {
        var derived = WorkflowTriggerEditor.Derive(Manual, Read("trigger:\n  batch: true\n  branches:\n    include: [main]\n"));
        Assert.True(derived.Ok, derived.Error);
        var after = derived.Yaml!.Split('\n');
        var before = Manual.Split('\n');

        Assert.Equal(before.Take(1), after.Take(1));
        Assert.Equal(before.SkipWhile(l => !l.StartsWith("permissions:")), after.SkipWhile(l => !l.StartsWith("permissions:")));
        Assert.Equal(["on:", "  workflow_dispatch:", "  push:", "    branches:", "      - 'main'", "concurrency:",
            "  group: \"${{ github.workflow }}-${{ github.event_name == 'push' && github.ref || format('run-{0}', github.run_id) }}\""],
            after.Skip(1).TakeWhile(l => !l.StartsWith("permissions:")));
        Assert.Equal(1, after.Count(l => l.StartsWith("concurrency:")));
        Assert.Equal(derived.Yaml, WorkflowTriggerEditor.Derive(Manual, Read("trigger:\n  batch: true\n  branches:\n    include: [main]\n")).Yaml); // deterministic
        Assert.Equal(new GitHubConcurrency(true, false), derived.Triggers!.Concurrency);
        Assert.Contains("concurrency: push runs wait for the run in progress (per ref)", derived.Triggers.Summary());
    }

    [Fact]
    public void When_a_person_chooses_not_to_translate_them_batch_and_autocancel_are_only_named_and_nothing_is_added()
    {
        var azure = Read("trigger:\n  batch: true\n  branches:\n    include: [main]\npr:\n  branches:\n    include: [main]\n");
        var mapped = GitHubTriggerMapper.Map(azure, translateConcurrency: false);
        Assert.Null(mapped.Concurrency);
        Assert.Equal(["TRIGGER_AUTOCANCEL_NOT_MAPPED", "TRIGGER_BATCH_NOT_MAPPED", "TRIGGER_PR_AZURE_REPOS_IGNORED"], Codes(mapped.Differences));
        Assert.Contains("It was not translated, as asked", mapped.Differences.Single(d => d.Code == "TRIGGER_BATCH_NOT_MAPPED").Message);

        var derived = WorkflowTriggerEditor.Derive(Manual, azure, translateConcurrency: false);
        Assert.True(derived.Ok, derived.Error);
        Assert.DoesNotContain("concurrency", derived.Yaml!);
        Assert.DoesNotContain(derived.Triggers!.Summary(), l => l.StartsWith("concurrency"));
        Assert.Contains("concurrency:", WorkflowTriggerEditor.Derive(Manual, azure).Yaml!); // and the default is to translate

        // autoCancel false and no batch: nothing to name either way
        var quiet = GitHubTriggerMapper.Map(Read("trigger:\n  branches:\n    include: [main]\npr:\n  autoCancel: false\n  branches:\n    include: [main]\n"), translateConcurrency: false);
        Assert.DoesNotContain(quiet.Differences, d => d.Code is "TRIGGER_BATCH_NOT_MAPPED" or "TRIGGER_AUTOCANCEL_NOT_MAPPED");
    }

    [Fact]
    public void Nothing_is_added_when_nothing_asks_for_it()
    {
        var derived = WorkflowTriggerEditor.Derive(Manual, Read("trigger:\n  branches:\n    include: [main]\n"));
        Assert.True(derived.Ok, derived.Error);
        Assert.DoesNotContain("concurrency", derived.Yaml!);
        Assert.Null(derived.Triggers!.Concurrency);
    }

    [Fact]
    public void A_workflow_that_already_has_a_concurrency_setting_keeps_it_and_the_difference_says_the_azure_options_were_not_added()
    {
        var own = Manual.Replace("permissions:", "concurrency:\n  group: mine\n  cancel-in-progress: false\npermissions:");
        var derived = WorkflowTriggerEditor.Derive(own, Read("trigger:\n  batch: true\n  branches:\n    include: [main]\npr:\n  branches:\n    include: [main]\n"));
        Assert.True(derived.Ok, derived.Error);

        Assert.Equal(1, derived.Yaml!.Split('\n').Count(l => l.StartsWith("concurrency:")));
        Assert.Contains("concurrency:\n  group: mine\n  cancel-in-progress: false\npermissions:", derived.Yaml);
        Assert.Null(derived.Triggers!.Concurrency);
        Assert.DoesNotContain(derived.Triggers.Summary(), l => l.StartsWith("concurrency"));
        Assert.Contains("TRIGGER_CONCURRENCY_EXISTS", Codes(derived.Differences));
        Assert.DoesNotContain(derived.Differences, d => d.Code is "TRIGGER_BATCH_APPROXIMATED" or "TRIGGER_AUTOCANCEL_APPROXIMATED");
        Assert.Contains("were not added", derived.Differences.Single(d => d.Code == "TRIGGER_CONCURRENCY_EXISTS").Message);
        // the rest of the mapping is unaffected
        Assert.Contains("  push:", derived.Yaml.Split('\n'));
        Assert.Contains("  pull_request:", derived.Yaml.Split('\n'));
    }
}
