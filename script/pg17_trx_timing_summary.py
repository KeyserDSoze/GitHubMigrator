#!/usr/bin/env python3
"""#132: deterministic, redacted per-method timing summary of a full PG17 TRX.

Never copy test parameter values, stdout, error messages, paths, tokens,
Git OIDs, connection strings or raw XML into the derived JSON or CI logs.
The full TRX remains separately available as the original CI artifact.
"""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from decimal import Decimal, InvalidOperation
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

_SYMBOL = re.compile(r"^[A-Za-z_][A-Za-z0-9_]{0,127}$")
# Never print or persist an arbitrary TRX outcome (which can contain secrets).
_SAFE_OUTCOMES = frozenset({
    "Passed", "Failed", "NotExecuted", "Inconclusive", "Timeout", "Aborted",
    "Error", "Warning", "NotRunnable", "Pending", "InProgress",
    "Disconnected", "Completed",
})
_MAX_DURATION_SECONDS = Decimal(7 * 24 * 60 * 60)
_TIME = re.compile(r"^(\d+):([0-5]\d):([0-5]\d)(?:\.(\d{1,9}))?$")
# #132: profile ONLY the known expensive physical F-097 theory. Never echo
# xUnit's free-text arguments (Git refs, tokens, paths, reviewer evidence).
_F097_NAME = "F097c4c0c_authenticated_http_review_of_real_later_checkpoint_is_immutable"
_F097_METHOD = (
    "PostgresGitHubSourceCheckpointGitRefLedgerCaptureTests." + _F097_NAME
)
# The actual .NET 10 VSTest TRX exporter includes the full namespace and
# class in UnitTestResult.testName. Earlier synthetic fixtures only used
# the short method name, leaving profiles [] for a real 504-case green run.
# Accept exactly these two immutable names, never an arbitrary suffix.
_F097_RESULT_PREFIXES = (
    _F097_NAME + "(",
    "GitHubMigrator.Integration.Tests." + _F097_METHOD + "(",
)
_F097_OID = re.compile(r"(?:^|,\s*)oidLength:\s*(40|64)(?=,|$)")
_F097_PHASE = re.compile(r"(?:^|,\s*)postMovePhase:\s*([0-5])(?=,|$)")


def _tag(local_tag: str) -> str:
    return local_tag.rsplit("}", 1)[-1]


def _method_name(unit: ET.Element) -> str:
    method = next((node for node in unit.iter() if _tag(node.tag) == "TestMethod"), None)
    if method is None:
        return "unknown_test"
    namespace_class = method.attrib.get("className", "")
    class_name = namespace_class.rsplit(".", 1)[-1]
    method_name = method.attrib.get("name", "")
    # Names may include parameter payloads in some TRX exporters.
    method_name = method_name.split("(", 1)[0]
    if not _SYMBOL.fullmatch(class_name) or not _SYMBOL.fullmatch(method_name):
        return "unknown_test"
    return f"{class_name}.{method_name}"


def _f097_profile(test_name: str, method: str) -> tuple[str, int] | None:
    """Return only allowlisted SHA/phase labels; never raw TRX parameters."""
    if (method != _F097_METHOD or len(test_name) > 1024
            or not test_name.endswith(")")):
        return None
    prefix = next(
        (item for item in _F097_RESULT_PREFIXES
         if test_name.startswith(item)),
        None,
    )
    if prefix is None:
        return None
    arguments = test_name[len(prefix):-1]
    oid_values = _F097_OID.findall(arguments)
    phase_values = _F097_PHASE.findall(arguments)
    if len(oid_values) != 1 or len(phase_values) != 1:
        return None
    return ("SHA-1" if oid_values[0] == "40" else "SHA-256",
            int(phase_values[0]))


def _seconds(raw: str) -> Decimal:
    found = _TIME.fullmatch(raw)
    if found is None:
        raise ValueError("Unrecognized TRX duration format")
    hours, minutes, seconds, fraction = found.groups()
    # Ignore malformed/unbounded values instead of manufacturing false timings
    # or overflowing numeric conversion of attacker-controlled XML fields.
    if len(hours) > 3:
        raise ValueError("TRX duration exceeds safety limit")
    try:
        measured = (
            Decimal(hours) * 3600
            + Decimal(minutes) * 60
            + Decimal(seconds)
            + Decimal("0." + (fraction or "0"))
        )
    except InvalidOperation as error:
        raise ValueError("Invalid TRX duration") from error
    if measured > _MAX_DURATION_SECONDS:
        raise ValueError("TRX duration exceeds safety limit")
    return measured


def summarize_trx(xml_path: Path, top: int = 15) -> dict:
    if top < 1 or top > 100:
        raise ValueError("Top must be from 1 to 100")
    root = ET.parse(xml_path).getroot()
    definitions = {
        unit.attrib.get("id", ""): _method_name(unit)
        for unit in root.iter()
        if _tag(unit.tag) == "UnitTest"
    }
    groups: dict[str, list[Decimal]] = defaultdict(list)
    profiles: dict[tuple[str, int], list[tuple[Decimal, str]]] = defaultdict(list)
    outcomes: Counter[str] = Counter()
    total = 0
    f097_review_cases = 0
    for result in root.iter():
        if _tag(result.tag) != "UnitTestResult":
            continue
        total += 1
        outcome = result.attrib.get("outcome", "")
        safe_outcome = outcome if outcome in _SAFE_OUTCOMES else "Other"
        outcomes[safe_outcome] += 1
        method = definitions.get(result.attrib.get("testId", ""), "unknown_test")
        if method == _F097_METHOD:
            f097_review_cases += 1
        # A missing duration is not a zero-second test. Refuse to report
        # incomplete measurements as valid, especially after 4/4 success.
        duration = _seconds(result.attrib.get("duration", ""))
        groups[method].append(duration)
        profile = _f097_profile(result.attrib.get("testName", ""), method)
        if profile is not None:
            profiles[profile].append((duration, safe_outcome))
    if total == 0:
        raise ValueError("TRX has no UnitTestResult entries")
    slowest = sorted(
        (
            {"method": name, "cases": len(durations),
             "total_seconds": float(sum(durations, Decimal(0))),
             "max_seconds": float(max(durations))}
            for name, durations in groups.items()
        ),
        key=lambda item: (-item["total_seconds"], item["method"]),
    )
    # At most 2 SHA formats x 6 phases; data and labels are strictly
    # bounded. No individual raw theory parameter or test name is copied.
    f097_profiles = sorted(
        (
            {
                "algorithm": algorithm, "post_move_phase": phase,
                "cases": len(items),
                "total_seconds": float(sum(
                    (seconds for seconds, _ in items), Decimal(0))),
                "max_seconds": float(max(seconds for seconds, _ in items)),
                "outcomes": dict(sorted(Counter(
                    status for _, status in items).items())),
            }
            for (algorithm, phase), items in profiles.items()
        ),
        key=lambda item: (
            -item["total_seconds"], item["algorithm"], item["post_move_phase"]),
    )
    return {
        "schema_version": 2,
        "test_cases": total,
        "outcomes": dict(sorted(outcomes.items())),
        "measured_case_seconds": float(
            sum((sum(values, Decimal(0)) for values in groups.values()), Decimal(0))
        ),
        "methods": len(groups),
        "slowest_methods": slowest[:top],
        "f097_phase_profiles": f097_profiles,
        "f097_review_cases": f097_review_cases,
        "f097_profiled_cases": sum(
            len(items) for items in profiles.values()
        ),
        "note": "Sum of measured test-case durations, not workflow wall time",
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trx", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--top", type=int, default=15)
    parser.add_argument("--allow-missing", action="store_true")
    # Success-only full CI gate: require the original 23 physical cases,
    # both Git object formats and all six post-move phases per format.
    # Reject silent partial profiles when the TRX exporter evolves.
    parser.add_argument("--require-f097-physical-coverage", action="store_true")
    args = parser.parse_args(argv)
    if not args.trx.is_file():
        if args.allow_missing:
            print("Full PostgreSQL17 TRX was not emitted; no timing report produced.")
            return 0
        print("Full PostgreSQL17 TRX is missing.", file=sys.stderr)
        return 1
    try:
        report = summarize_trx(args.trx, args.top)
    except (ET.ParseError, OSError, ValueError) as error:
        # Never print source text or potentially sensitive XML diagnostics.
        print(f"TRX timing analysis failed ({type(error).__name__}).", file=sys.stderr)
        return 1
    if args.require_f097_physical_coverage:
        # This extra check is only enabled for the completed successful,
        # unfiltered PG17 CI step. A failed/early-aborted run can still
        # emit its partial timing report for triage.
        actual = {
            (entry["algorithm"], entry["post_move_phase"])
            for entry in report["f097_phase_profiles"]
        }
        expected = {
            (algorithm, phase)
            for algorithm in ("SHA-1", "SHA-256")
            for phase in range(6)
        }
        if (report["f097_review_cases"] < 23
                or report["f097_profiled_cases"] != report["f097_review_cases"]
                or not expected.issubset(actual)):
            # Fixed wording only: do not echo untrusted names,
            # parameters, source paths, OIDs or test arguments.
            print("PostgreSQL17 F-097 physical test coverage/profile incomplete.",
                  file=sys.stderr)
            return 1
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(report, sort_keys=True, indent=2) + "\n", encoding="utf-8"
    )
    print(
        f"PostgreSQL17: {report['test_cases']} cases, "
        f"{report['methods']} methods, {report['outcomes']}."
    )
    for item in report["slowest_methods"]:
        print(
            f"{item['method']}: {item['cases']} case(s), "
            f"sum={item['total_seconds']:.2f}s max={item['max_seconds']:.2f}s"
        )
    for item in report["f097_phase_profiles"]:
        print(
            f"F-097 {item['algorithm']} phase={item['post_move_phase']}: "
            f"{item['cases']} case(s), "
            f"sum={item['total_seconds']:.2f}s "
            f"max={item['max_seconds']:.2f}s "
            f"outcomes={item['outcomes']}"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
