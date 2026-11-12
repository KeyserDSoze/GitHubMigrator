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
