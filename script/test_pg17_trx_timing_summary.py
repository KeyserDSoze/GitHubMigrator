"""#132: verify duration aggregation and intentionally redacted output."""

import contextlib
import io
from pathlib import Path
import tempfile
import unittest

from pg17_trx_timing_summary import main, summarize_trx


# Parameterized TRX names may include arbitrary, private fixture values.
FIXTURE = """<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
 <TestDefinitions>
  <UnitTest id="id1" name="Theory(a_secret_token)">
   <TestMethod className="GitTests" name="Theory(a_secret_token)" />
  </UnitTest>
  <UnitTest id="id2" name="Fast(private_oid)">
   <TestMethod className="ApiTests" name="Fast(private_oid)" />
  </UnitTest>
 </TestDefinitions>
 <Results>
  <UnitTestResult testId="id1" testName="Theory(secret_connection_string)"
     outcome="Passed" duration="00:00:05.125" />
  <UnitTestResult testId="id1" testName="Theory(a_private_git_oid)"
     outcome="Failed" duration="00:00:04.875" />
  <UnitTestResult testId="id2" testName="Fast(local_storage_root)"
     outcome="Passed" duration="00:00:00.250" />
 </Results>
</TestRun>
"""


class TrxTimingSummaryTests(unittest.TestCase):
    def test_groups_parameterized_cases_and_removes_private_values(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "suite.trx"
            path.write_text(FIXTURE, encoding="utf-8")
            data = summarize_trx(path, top=2)
            self.assertEqual(data["test_cases"], 3)
            self.assertEqual(data["methods"], 2)
            self.assertEqual(data["outcomes"], {"Failed": 1, "Passed": 2})
            self.assertEqual(data["measured_case_seconds"], 10.25)
            self.assertEqual(data["slowest_methods"][0], {
                "method": "GitTests.Theory", "cases": 2,
                "total_seconds": 10.0, "max_seconds": 5.125
            })
            self.assertEqual(data["slowest_methods"][1]["method"], "ApiTests.Fast")
            output = Path(directory) / "result.json"
            stdout = io.StringIO()
            with contextlib.redirect_stdout(stdout):
                self.assertEqual(main([str(path), "--output", str(output)]), 0)
            combined = output.read_text(encoding="utf-8") + stdout.getvalue()
            for private in ("secret", "token", "private", "oid", "connection",
                            "root", str(path)):
                self.assertNotIn(private, combined)

    def test_f097_phase_profiles_are_bounded_and_never_leak_parameters(self):
        # Only the known F-097 class+method is profiled. The raw name
        # contains sensitive test arguments and MUST never be published.
        theory = ("F097c4c0c_authenticated_http_review_of_real_"
                  "later_checkpoint_is_immutable")
        xml = f"""<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
 <TestDefinitions>
  <UnitTest id="f097">
   <TestMethod
     className="GitHubMigrator.Integration.Tests.PostgresGitHubSourceCheckpointGitRefLedgerCaptureTests"
     name="{theory}(oidLength: 40, postMovePhase: 1)" />
  </UnitTest>
  <UnitTest id="untrusted">
   <TestMethod className="OtherTests" name="Other(oidLength: 64)" />
  </UnitTest>
 </TestDefinitions>
 <Results>
  <UnitTestResult testId="f097" outcome="Passed" duration="00:00:03"
    testName='{theory}(oidLength: 40, decision: "secret_source_token", postMovePhase: 1)' />
  <UnitTestResult testId="f097" outcome="Failed" duration="00:00:04"
    testName='{theory}(oidLength: 64, decision: "private_oid", postMovePhase: 2)' />
  <UnitTestResult testId="f097" outcome="Passed" duration="00:00:05"
    testName='{theory}(oidLength: 64, decision: "private_oid", postMovePhase: 2)' />
  <UnitTestResult testId="f097" outcome="Passed" duration="00:00:06"
    testName='{theory}(oidLength: 40, postMovePhase: 99)' />
  <UnitTestResult testId="untrusted" outcome="Passed" duration="00:00:07"
    testName='Other(oidLength: 64, postMovePhase: 2)' />
  <UnitTestResult testId="f097" outcome="Passed" duration="00:00:08"
    testName='{theory}(oidLength: 40, oidLength: 64, postMovePhase: 1)' />
 </Results>
</TestRun>
"""
        with tempfile.TemporaryDirectory() as directory:
            input_path = Path(directory) / "source.trx"
            output = Path(directory) / "timings.json"
            input_path.write_text(xml, encoding="utf-8")
            report = summarize_trx(input_path)
            self.assertEqual(report["schema_version"], 2)
            self.assertEqual(report["test_cases"], 6)
            self.assertEqual(report["measured_case_seconds"], 33.0)
            self.assertEqual(report["f097_phase_profiles"], [
                {"algorithm": "SHA-256", "post_move_phase": 2, "cases": 2,
                 "total_seconds": 9.0, "max_seconds": 5.0,
                 "outcomes": {"Failed": 1, "Passed": 1}},
                {"algorithm": "SHA-1", "post_move_phase": 1, "cases": 1,
                 "total_seconds": 3.0, "max_seconds": 3.0,
                 "outcomes": {"Passed": 1}},
            ])
            stdout = io.StringIO()
            with contextlib.redirect_stdout(stdout):
                self.assertEqual(main([str(input_path), "--output",
                                       str(output)]), 0)
            published = output.read_text(encoding="utf-8") + stdout.getvalue()
            for secret in ("secret", "token", "private", "oid", "source.trx",
                           'decision:', '99', str(input_path)):
                self.assertNotIn(secret, published)
            self.assertIn("SHA-256 phase=2", published)

    def test_actual_vstest_fully_qualified_f097_results_are_profiled(self):
        # A real 504-case PostgreSQL17 TRX from CI 43277987423 uses the
        # full namespace/class in UnitTestResult.testName. Do not
        # silently emit an empty profile just because the synthetic
        # fixtures use short theory names.
        theory = ("F097c4c0c_authenticated_http_review_of_real_"
                  "later_checkpoint_is_immutable")
        klass = ("GitHubMigrator.Integration.Tests."
                 "PostgresGitHubSourceCheckpointGitRefLedgerCaptureTests")
        xml = f"""<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
 <TestDefinitions>
  <UnitTest id="physical">
   <TestMethod className="{klass}"
      name="{theory}(oidLength: 40, postMovePhase: 1)" />
  </UnitTest>
 </TestDefinitions>
 <Results>
  <UnitTestResult testId="physical" outcome="Passed"
    duration="00:05:04.3904791"
    testName='{klass}.{theory}(oidLength: 40, decision: "secret_token", verifyPhysical: True, postMovePhase: 1)' />
  <UnitTestResult testId="physical" outcome="Passed"
    duration="00:05:13.9447392"
    testName='{klass}.{theory}(oidLength: 64, decision: "private_path", verifyPhysical: True, postMovePhase: 1)' />
  <UnitTestResult testId="physical" outcome="Passed"
    duration="00:00:07"
    testName='Other.Namespace.{klass}.{theory}(oidLength: 64, postMovePhase: 4)' />
 </Results>
</TestRun>
"""
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "source.trx"
            out = Path(directory) / "safe.json"
            path.write_text(xml, encoding="utf-8")
            data = summarize_trx(path)
            self.assertEqual(data["f097_phase_profiles"], [
                {"algorithm": "SHA-256", "post_move_phase": 1, "cases": 1,
                 "total_seconds": 313.9447392,
                 "max_seconds": 313.9447392, "outcomes": {"Passed": 1}},
                {"algorithm": "SHA-1", "post_move_phase": 1, "cases": 1,
                 "total_seconds": 304.3904791,
                 "max_seconds": 304.3904791, "outcomes": {"Passed": 1}},
            ])
            stdout = io.StringIO()
            with contextlib.redirect_stdout(stdout):
                self.assertEqual(main([str(path), "--output", str(out)]), 0)
            published = out.read_text(encoding="utf-8") + stdout.getvalue()
            for private in ("secret_token", "private_path",
                            "Other.Namespace", str(path)):
                self.assertNotIn(private, published)
            self.assertIn("SHA-1 phase=1", published)
            self.assertIn("SHA-256 phase=1", published)

    def test_full_pg17_f097_coverage_gate_enforces_real_theory_matrix(self):
        # Both Git formats, every independent post-move phase, all 23
        # physical cases and complete name parsing are requirements of
        # the existing full green PG17 gate. Keep this synthetic,
        # credential-free test independent from the DB/Git fixtures.
        method = ("F097c4c0c_authenticated_http_review_of_real_"
                  "later_checkpoint_is_immutable")
        klass = ("GitHubMigrator.Integration.Tests."
                 "PostgresGitHubSourceCheckpointGitRefLedgerCaptureTests")
        rows = [
            (40 if index % 2 == 0 else 64, (index // 2) % 6)
            for index in range(24)
        ]
        def make_trx(cases):
            entries = "".join(
                f'<UnitTestResult testId="physical" outcome="Passed" '
                f'duration="00:00:01" testName="{klass}.{method}'
                f'(oidLength: {algorithm}, postMovePhase: {phase})" />'
                for algorithm, phase in cases
            )
            return (f'<TestRun><TestDefinitions><UnitTest id="physical">'
                    f'<TestMethod className="{klass}" name="{method}" />'
                    f'</UnitTest></TestDefinitions><Results>{entries}'
                    f'</Results></TestRun>')

        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source.trx"
            output = Path(directory) / "safe-summary.json"
            gate = [str(source), "--output", str(output),
                    "--require-f097-physical-coverage"]
            source.write_text(make_trx(rows), encoding="utf-8")
            self.assertEqual(main(gate), 0)
            report = summarize_trx(source)
            self.assertEqual(report["f097_review_cases"], 24)
            self.assertEqual(report["f097_profiled_cases"], 24)
            self.assertEqual(len(report["f097_phase_profiles"]), 12)
            output.unlink()

            # Removing one whole phase from the baseline matrix is not
            # acceptable even when at least 23 cases still execute.
            missing_phase = [
                (64, 4) if case == (64, 5) else case
                for case in rows
            ]
            self.assertEqual(len(missing_phase), 24)
            source.write_text(make_trx(missing_phase), encoding="utf-8")
            with contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(main(gate), 1)
            self.assertFalse(output.exists())

            # A superficially healthy 23-case run is also incomplete if
            # the exporter renames even ONE case without updating the
            # strict profile parser. The previous silent [] regression
            # must be a hard CI failure, never an empty green report.
            damaged = make_trx(rows)
            damaged = damaged.replace(
                f"{klass}.{method}(oidLength: 40, postMovePhase: 0)",
                f"Other.Namespace.{klass}.{method}(oidLength: 40, postMovePhase: 0)",
                1,
            )
            source.write_text(damaged, encoding="utf-8")
            err = io.StringIO()
            with contextlib.redirect_stderr(err):
                self.assertEqual(main(gate), 1)
            self.assertIn("profile incomplete", err.getvalue())
            self.assertNotIn("Other.Namespace", err.getvalue())
            self.assertFalse(output.exists())

            # A true full-run filter accidentally omitting the F-097
            # method altogether must fail rather than report success.
            source.write_text(FIXTURE, encoding="utf-8")
            with contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(main(gate), 1)
            self.assertFalse(output.exists())

    def test_missing_allowed_only_when_explicit(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "missing.trx"
            output = Path(directory) / "summary.json"
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(main([str(path), "--output", str(output),
                                       "--allow-missing"]), 0)
            with contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(main([str(path), "--output", str(output)]), 1)
            self.assertFalse(output.exists())

    def test_bad_xml_and_bad_duration_fail_without_echoing_source(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad.trx"
            output = Path(directory) / "summary.json"
            for xml in ("<TestRun>secret_token", FIXTURE.replace(
                "00:00:05.125", "password=password")):
                path.write_text(xml, encoding="utf-8")
                err = io.StringIO()
                with contextlib.redirect_stderr(err):
                    self.assertEqual(main([str(path), "--output", str(output)]), 1)
                self.assertNotIn("password", err.getvalue())
                self.assertNotIn("secret", err.getvalue())
                self.assertFalse(output.exists())


    def test_untrusted_outcome_is_never_copied_into_json_or_ci_stdout(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "forged.trx"
            path.write_text(FIXTURE.replace(
                'outcome="Passed"', 'outcome="password=secret_token"', 1
            ), encoding="utf-8")
            data = summarize_trx(path)
            self.assertEqual(data["outcomes"], {
                "Failed": 1, "Other": 1, "Passed": 1
            })
            output = Path(directory) / "safe-summary.json"
            stdout = io.StringIO()
            with contextlib.redirect_stdout(stdout):
                self.assertEqual(main([str(path), "--output", str(output)]), 0)
            published = output.read_text(encoding="utf-8") + stdout.getvalue()
            self.assertNotIn("secret_token", published)
            self.assertNotIn("password=", published)

    def test_missing_or_unbounded_duration_fails_not_zero_fills(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad-duration.trx"
            output = Path(directory) / "safe-summary.json"
            for altered in (
                FIXTURE.replace(' duration="00:00:05.125"', "", 1),
                FIXTURE.replace('00:00:05.125', '999999999999:00:00', 1),
                FIXTURE.replace('00:00:05.125', '169:00:00', 1),
            ):
                path.write_text(altered, encoding="utf-8")
                with self.assertRaises(ValueError):
                    summarize_trx(path)
                stderr = io.StringIO()
                with contextlib.redirect_stderr(stderr):
                    self.assertEqual(main([str(path), "--output", str(output)]), 1)
                self.assertFalse(output.exists())
                self.assertNotIn("bad-duration.trx", stderr.getvalue())

    def test_rejects_zero_cases_and_invalid_top(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "empty.trx"
            path.write_text("<TestRun />", encoding="utf-8")
            with self.assertRaises(ValueError):
                summarize_trx(path)
            path.write_text(FIXTURE, encoding="utf-8")
            with self.assertRaises(ValueError):
                summarize_trx(path, top=0)


if __name__ == "__main__":
    unittest.main()
