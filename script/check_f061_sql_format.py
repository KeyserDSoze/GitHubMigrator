#!/usr/bin/env python3
"""Fail-fast guard for F-061 PostgreSQL SHA-256 CHECK bootstrap SQL.

EF Core ExecuteSqlRawAsync applies composite-format substitution even to
non-interpolated C# raw literals. The C# string {{64}} must reach PostgreSQL
as {64}. A single {64} raises FormatException in the API bootstrap instead
of producing a regex. We validate the *actual rendered SQL text* before the
much slower PostgreSQL integration suite, including the idempotent repair.
"""
from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
API = ROOT / "src/backend/applications/GitHubMigrator.Api"

# Keep this list explicit: a new F-061 schema must receive a deliberate
# review rather than silently shipping untested composite-formatted SQL.
EXPECTED = {
    "GitIncrementalLaterCheckpointMirrorLeaseSchema.cs": 10,
    "GitIncrementalLaterCheckpointMirrorVerifiedSchema.cs": 7,
    "GitIncrementalLaterCheckpointMirrorRecoveryDecisionSchema.cs": 5,
    "GitIncrementalLaterCheckpointMirrorIsolationSchema.cs": 15,
    "GitIncrementalLaterCheckpointMirrorGenerationAssessmentSchema.cs": 14,
    "GitIncrementalLaterCheckpointMirrorSecondReviewSchema.cs": 12,
    "GitIncrementalLaterCheckpointMirrorRegexConstraintRepairSchema.cs": 0,
}
REPAIR = "GitIncrementalLaterCheckpointMirrorRegexConstraintRepairSchema.cs"
RAW = re.compile(r'ExecuteSqlRawAsync\(\s*"""(.*?)"""\s*,\s*ct\s*\)', re.DOTALL)
SHA_SQL = "'^[0-9a-f]{64}$'"
SHA_CSHARP = "'^[0-9a-f]{{64}}$'"


def require(ok: bool, message: str) -> None:
    if not ok:
        raise AssertionError(message)


def check() -> None:
    discovered = {p.name for p in API.glob(
        "GitIncrementalLaterCheckpointMirror*Schema.cs")}
    require(discovered == set(EXPECTED), (
        "F-061 schema manifest changed: "
        f"new={sorted(discovered - EXPECTED.keys())}, "
        f"missing={sorted(EXPECTED.keys() - discovered)}"
    ))

    total = 0
    for filename, count in EXPECTED.items():
        source = (API / filename).read_text(encoding="utf-8")
        literals = RAW.findall(source)
        require(len(literals) == 1,
                f"{filename}: expected one EF Core raw SQL bootstrap literal")
        raw = literals[0]
        # Python format uses the same doubled-brace escape contract as
        # .NET composite formatting for this argument-free literal.
        try:
            rendered = raw.format()
        except (ValueError, IndexError, KeyError) as exc:
            raise AssertionError(
                f"{filename}: invalid EF composite format: {exc}") from exc

        if filename == REPAIR:
            require(raw.count("'{{{{64}}}}'") == 2,
                    f"{filename}: malformed persisted-regex search not escaped")
            require(raw.count("'{{64}}'") == 1,
                    f"{filename}: SQL replacement regex not escaped")
            require(rendered.count("'{{64}}'") == 2,
                    f"{filename}: PostgreSQL search must match stored {{64}}")
            require(rendered.count("'{64}'") == 1,
                    f"{filename}: PostgreSQL replacement must be {64}")
            require(
                "replace(existing.definition, '{{64}}', '{64}')" in rendered,
                f"{filename}: repair no longer upgrades malformed CHECK SQL")
        else:
            require(raw.count(SHA_CSHARP) == count,
                    f"{filename}: expected {count} C# escaped SHA regexes")
            require(rendered.count(SHA_SQL) == count,
                    f"{filename}: expected {count} PostgreSQL SHA regexes")
            require("'^[0-9a-f]{{64}}$'" not in rendered,
                    f"{filename}: malformed SHA regex would reach PostgreSQL")
            total += count

        print(f"OK {filename}: {count} SQL SHA checks")

    require(total == 63, f"F-061 bootstrap must validate 63 checks, got {total}")
    print("F-061 EF raw SQL formatting: OK (63 SHA checks + idempotent repair)")

    # EF SqlQueryRaw<T> uses composite formatting too, even for
    # argument-free raw C# SQL. Check real PG17 integration predicates
    # before they can fail at runtime with System.FormatException.
    sql_test = (ROOT / "tests/integration" /
                "ApiLaterCheckpointWorkpackHistoryEndpointTests.cs")
    snippets = re.findall(
        r'SqlQueryRaw<[^>]+>\(\s*"""(.*?)"""\s*\)',
        sql_test.read_text(encoding="utf-8"), re.DOTALL)
    require(len(snippets) >= 30,
            "F-061 integration raw-SQL coverage unexpectedly changed")
    for idx, query in enumerate(snippets):
        try:
            rendered = query.format()
        except (ValueError, KeyError, IndexError) as exc:
            raise AssertionError(
                f"SqlQueryRaw #{idx} invalid EF composite formatting: {exc}"
            ) from exc
        if "repeat('a',64) ~" in query:
            require(query.count("'^[0-9a-f]{{64}}


if __name__ == "__main__":
    check()
") == 4,
                    "Four C# SQL regex quantifiers must be doubled")
            require(rendered.count("'^[0-9a-f]{64}


if __name__ == "__main__":
    check()
") == 4,
                    "EF must emit the four valid PostgreSQL {64} quantifiers")
    print(f"F-061 SqlQueryRaw integration SQL: OK ({len(snippets)} queries)")



if __name__ == "__main__":
    check()
