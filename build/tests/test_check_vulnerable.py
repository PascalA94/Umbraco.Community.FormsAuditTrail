import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parent / "check-vulnerable.py"
FIXTURES = HERE / "fixtures"


def run(report_path):
    return subprocess.run(
        [sys.executable, "-B", "-I", str(SCRIPT), str(report_path)],
        capture_output=True, text=True,
    )


def run_text(text, encoding="utf-8"):
    with tempfile.TemporaryDirectory() as d:
        p = Path(d) / "r.json"
        p.write_bytes(text.encode(encoding))
        return run(p)


class CheckVulnerableTests(unittest.TestCase):
    def test_clean_report_exits_0(self):
        r = run(FIXTURES / "clean.json")
        self.assertEqual(r.returncode, 0, r.stderr)

    def test_high_transitive_exits_1_and_names_package(self):
        r = run(FIXTURES / "high.json")
        self.assertEqual(r.returncode, 1)
        out = r.stdout + r.stderr
        for needle in ("System.Security.Cryptography.Pkcs", "10.0.0", "High", "GHSA-high-0001"):
            self.assertIn(needle, out)

    def test_moderate_only_exits_1_and_names_package(self):
        # R19: the repo gate fails on any severity, so Moderate and Low are not downgraded to warnings.
        r = run(FIXTURES / "moderate.json")
        self.assertEqual(r.returncode, 1)
        out = r.stdout + r.stderr
        for needle in ("Some.Direct", "Moderate", "GHSA-mod-0001"):
            self.assertIn(needle, out)

    def test_unparseable_report_exits_2(self):
        self.assertEqual(run_text("not json at all").returncode, 2)

    def test_problems_array_exits_2(self):
        r = run_text('{"version":1,"problems":[{"level":"error","text":"boom"}],"projects":[]}')
        self.assertEqual(r.returncode, 2)

    def test_no_projects_exits_2(self):
        self.assertEqual(run_text('{"version":1,"projects":[]}').returncode, 2)

    def test_report_without_include_transitive_exits_2(self):
        r = run_text('{"version":1,"parameters":"--vulnerable","projects":[{"path":"/p"}]}')
        self.assertEqual(r.returncode, 2)

    def test_listed_package_without_vulnerabilities_exits_2(self):
        r = run_text('{"parameters":"--vulnerable --include-transitive","projects":[{"path":"/p","frameworks":'
                     '[{"framework":"net10.0","topLevelPackages":[{"id":"A","resolvedVersion":"1.0.0"}]}]}]}')
        self.assertEqual(r.returncode, 2)

    def test_malformed_nested_shapes_exit_2_without_traceback(self):
        base = '{"parameters":"--vulnerable --include-transitive","projects":%s}'
        for projects in ('["x"]', '[{"path":"/p","frameworks":["x"]}]',
                         '[{"path":"/p","frameworks":[{"transitivePackages":["x"]}]}]',
                         '[{"path":"/p","frameworks":[{"transitivePackages":[{"id":"A","vulnerabilities":["x"]}]}]}]'):
            r = run_text(base % projects)
            self.assertEqual(r.returncode, 2, projects)
            self.assertNotIn("Traceback", r.stderr, projects)

    def test_utf8_bom_is_accepted(self):
        text = (FIXTURES / "clean.json").read_text(encoding="utf-8")
        self.assertEqual(run_text(text, "utf-8-sig").returncode, 0)

    def test_dotnet_exit_code_zero_with_findings_still_fails(self):
        # dotnet list exits 0 even when it lists advisories, so only the parsed report can fail the build.
        shim = HERE / "fixtures" / "dotnet-exit-0.sh"
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "v.json"
            with out.open("w") as sink:
                dotnet = subprocess.run(
                    ["bash", str(shim), str(FIXTURES / "high.json")],
                    stdout=sink, text=True,
                )
            self.assertEqual(dotnet.returncode, 0)
            self.assertEqual(run(out).returncode, 1)


if __name__ == "__main__":
    unittest.main()
