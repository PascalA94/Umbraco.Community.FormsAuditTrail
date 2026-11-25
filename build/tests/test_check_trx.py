import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parent / "check-trx.py"
FIXTURES = HERE / "fixtures"


def run(trx_path, minimum="2", category="SqlServer"):
    return subprocess.run(
        [sys.executable, "-B", "-I", str(SCRIPT), str(trx_path), "--category", category, "--min", minimum],
        capture_output=True, text=True,
    )


def run_text(text):
    with tempfile.TemporaryDirectory() as d:
        p = Path(d) / "r.trx"
        p.write_text(text, encoding="utf-8")
        return run(p)


class CheckTrxTests(unittest.TestCase):
    # The fixtures mirror a real xunit v2 trx: no TestCategory data, category tests identified by name suffix.
    def test_enough_passed_exits_0(self):
        r = run(FIXTURES / "trx-pass.trx")
        self.assertEqual(r.returncode, 0, r.stderr)

    def test_skipped_tests_do_not_count_and_exit_1(self):
        r = run(FIXTURES / "trx-skipped.trx")
        self.assertEqual(r.returncode, 1)
        self.assertIn("0 SqlServer test(s) executed", r.stderr)

    def test_failed_test_exits_1_and_names_it(self):
        r = run(FIXTURES / "trx-failed.trx")
        self.assertEqual(r.returncode, 1)
        self.assertIn("Model_CascadesChildRows_OnSqlServer", r.stderr)

    def test_below_minimum_exits_1(self):
        r = run(FIXTURES / "trx-one.trx")
        self.assertEqual(r.returncode, 1)
        self.assertIn("need at least 2", r.stderr)

    def test_same_file_passes_with_lower_minimum(self):
        self.assertEqual(run(FIXTURES / "trx-one.trx", minimum="1").returncode, 0)

    def test_ordinary_test_mentioning_the_category_is_not_counted(self):
        # trx-one has SqlServerFact_InCiWithoutConnectionString_DoesNotSkip, which is a plain [Fact].
        self.assertEqual(run(FIXTURES / "trx-one.trx", minimum="2").returncode, 1)

    def test_testcategory_element_is_honoured(self):
        text = (
            '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'
            '<UnitTestResult testId="t1" testName="A.B.Anything" outcome="Passed" /></Results>'
            '<TestDefinitions><UnitTest name="A.B.Anything" id="t1"><TestCategory>'
            '<TestCategoryItem TestCategory="SqlServer" /></TestCategory></UnitTest></TestDefinitions></TestRun>'
        )
        self.assertEqual(run_text(text).returncode, 1)  # one executed, run_text asks for two
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "r.trx"
            p.write_text(text, encoding="utf-8")
            self.assertEqual(run(p, minimum="1").returncode, 0)

    def test_min_below_one_exits_2(self):
        for value in ("0", "-1"):
            r = run(FIXTURES / "trx-skipped.trx", minimum=value)
            self.assertEqual(r.returncode, 2, value)
            self.assertIn("--min must be at least 1", r.stderr)

    def test_unparseable_file_exits_2(self):
        self.assertEqual(run_text("not xml").returncode, 2)

    def test_wrong_root_exits_2(self):
        self.assertEqual(run_text("<Other/>").returncode, 2)

    def test_missing_file_exits_2(self):
        self.assertEqual(run(HERE / "fixtures" / "nope.trx").returncode, 2)


if __name__ == "__main__":
    unittest.main()
