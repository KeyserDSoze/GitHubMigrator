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
