using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.EntityFrameworkCore;

// The ledger of what the product wrote to a destination repository (one branch, one file, one pull request per plan). The database, not only the
// service, refuses to:
//  - start a second live publication of the same plan (partial unique index: a plan whose attempt failed BEFORE any write may be tried again);
//  - change what was planned (repository, base commit, branch, path, hashes) or who asked;
//  - move a publication backwards, skip a step of the order branch -> file -> pull request, or leave Published/Failed;
//  - call a publication Published without a commit and a pull request number, or Failed without a reason;
//  - delete a publication.
// Constraints and triggers are added idempotently so they also exist on a database that EnsureCreated built from the model.
public static class WorkflowPublicationSchema
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS workflow_publications (
                id uuid PRIMARY KEY,
                proposal_id uuid NOT NULL REFERENCES workflow_proposals(id) ON DELETE RESTRICT,
                plan_sha256 varchar(64) NOT NULL,
                workflow_sha256 varchar(64) NOT NULL,
                organization varchar(100) NOT NULL,
                repository varchar(100) NOT NULL,
                base_branch varchar(200) NOT NULL,
                base_sha varchar(40) NOT NULL,
                head_branch varchar(120) NOT NULL,
                workflow_path varchar(200) NOT NULL,
                state varchar(24) NOT NULL,
                commit_sha varchar(40) NULL,
                pull_number integer NULL,
                pull_url varchar(512) NULL,
                error_code varchar(80) NULL,
                failed_at_state varchar(24) NULL,
                writes_possible boolean NOT NULL DEFAULT false,
                requested_by_id uuid NOT NULL,
                requested_by varchar(160) NOT NULL,
                started_at_utc timestamptz NOT NULL,
                updated_at_utc timestamptz NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_workflow_publications_proposal ON workflow_publications(proposal_id, started_at_utc);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_workflow_publications_live ON workflow_publications(proposal_id, plan_sha256)
                WHERE NOT (state = 'Failed' AND writes_possible = false);

            DO $wpub_constraints$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_workflow_publications_shape') THEN
                    ALTER TABLE workflow_publications ADD CONSTRAINT ck_workflow_publications_shape CHECK (
                        state IN ('Started','BranchCreating','BranchCreated','FileCommitting','FileCommitted','PullRequestOpening','PullRequestOpened','Published','Failed')
                        AND plan_sha256 ~ '^[0-9a-f]{{64}}$' AND workflow_sha256 ~ '^[0-9a-f]{{64}}$'
                        AND base_sha ~ '^[0-9a-f]{{40}}$' AND (commit_sha IS NULL OR commit_sha ~ '^[0-9a-f]{{40}}$')
                        AND workflow_path ~ '^\.github/workflows/azdo-[0-9]+-[a-z0-9-]+\.yml$'
                        AND head_branch ~ '^migrate/azdo-[0-9]+-[0-9a-f]{{8}}$'
                        AND ((state = 'Failed') = (error_code IS NOT NULL))
                        AND ((state = 'Failed') = (failed_at_state IS NOT NULL))
                        AND (state <> 'Published' OR (commit_sha IS NOT NULL AND pull_number IS NOT NULL AND pull_number > 0))
                        AND (state NOT IN ('FileCommitted','PullRequestOpening','PullRequestOpened') OR commit_sha IS NOT NULL)
                        AND (state NOT IN ('PullRequestOpened') OR pull_number IS NOT NULL)
                        AND (state <> 'Failed' OR writes_possible OR failed_at_state IN ('Started','BranchCreating')));
                END IF;
            END
            $wpub_constraints$;

            CREATE OR REPLACE FUNCTION workflow_publications_guard()
            RETURNS trigger LANGUAGE plpgsql AS $guard$
            DECLARE chain text[] := ARRAY['Started','BranchCreating','BranchCreated','FileCommitting','FileCommitted','PullRequestOpening','PullRequestOpened','Published'];
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'workflow publications are an audit record and are not deleted';
                END IF;
                IF NEW.id IS DISTINCT FROM OLD.id OR NEW.proposal_id IS DISTINCT FROM OLD.proposal_id OR NEW.plan_sha256 IS DISTINCT FROM OLD.plan_sha256
                   OR NEW.workflow_sha256 IS DISTINCT FROM OLD.workflow_sha256 OR NEW.organization IS DISTINCT FROM OLD.organization
                   OR NEW.repository IS DISTINCT FROM OLD.repository OR NEW.base_branch IS DISTINCT FROM OLD.base_branch OR NEW.base_sha IS DISTINCT FROM OLD.base_sha
                   OR NEW.head_branch IS DISTINCT FROM OLD.head_branch OR NEW.workflow_path IS DISTINCT FROM OLD.workflow_path
                   OR NEW.requested_by_id IS DISTINCT FROM OLD.requested_by_id OR NEW.requested_by IS DISTINCT FROM OLD.requested_by
                   OR NEW.started_at_utc IS DISTINCT FROM OLD.started_at_utc THEN
                    RAISE EXCEPTION 'workflow publication plan is immutable';
                END IF;
                IF OLD.state IN ('Published','Failed') THEN
                    RAISE EXCEPTION 'workflow publication is final';
                END IF;
                IF NEW.state = 'Failed' THEN
                    IF NEW.failed_at_state IS DISTINCT FROM OLD.state THEN RAISE EXCEPTION 'workflow publication failure must name the state it failed in'; END IF;
                ELSIF NEW.state IS DISTINCT FROM OLD.state THEN
                    -- one step at a time: the state is written before a write is attempted and again after it
                    IF array_position(chain, NEW.state) IS DISTINCT FROM array_position(chain, OLD.state) + 1 THEN
                        RAISE EXCEPTION 'workflow publication state transition not allowed';
                    END IF;
                END IF;
                IF (OLD.commit_sha IS NOT NULL AND NEW.commit_sha IS DISTINCT FROM OLD.commit_sha)
                   OR (OLD.pull_number IS NOT NULL AND NEW.pull_number IS DISTINCT FROM OLD.pull_number)
                   OR (OLD.pull_url IS NOT NULL AND NEW.pull_url IS DISTINCT FROM OLD.pull_url) THEN
                    RAISE EXCEPTION 'workflow publication results cannot be changed';
                END IF;
                RETURN NEW;
            END
            $guard$;
            DO $wpub_triggers$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'workflow_publications'::regclass AND tgname = 'trg_workflow_publications_guard') THEN
                    CREATE TRIGGER trg_workflow_publications_guard BEFORE UPDATE OR DELETE ON workflow_publications
                        FOR EACH ROW EXECUTE FUNCTION workflow_publications_guard();
                END IF;
            END
            $wpub_triggers$;
            """, ct);
    }
}
