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
    outcomes: Counter[str] = Counter()
    total = 0
    for result in root.iter():
        if _tag(result.tag) != "UnitTestResult":
            continue
        total += 1
        outcome = result.attrib.get("outcome", "")
        outcomes[outcome if outcome in _SAFE_OUTCOMES else "Other"] += 1
        method = definitions.get(result.attrib.get("testId", ""), "unknown_test")
        # A missing duration is not a zero-second test. Refuse to report
        # incomplete measurements as valid, especially after 4/4 success.
        groups[method].append(_seconds(result.attrib.get("duration", "")))
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
    return {
        "schema_version": 1,
        "test_cases": total,
        "outcomes": dict(sorted(outcomes.items())),
        "measured_case_seconds": float(
            sum((sum(values, Decimal(0)) for values in groups.values()), Decimal(0))
        ),
        "methods": len(groups),
        "slowest_methods": slowest[:top],
        "note": "Sum of measured test-case durations, not workflow wall time",
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("trx", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--top", type=int, default=15)
    parser.add_argument("--allow-missing", action="store_true")
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
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
