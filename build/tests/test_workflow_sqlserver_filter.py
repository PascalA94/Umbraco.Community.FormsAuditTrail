import re
import unittest
from pathlib import Path

WORKFLOWS = Path(__file__).resolve().parents[2] / ".github" / "workflows"

# In CI the SQL Server tests fail rather than skip when FORMSAUDIT_SQLSERVER is unset (P6), so a workflow that runs the
# whole suite without a server would fail on every run. Stdlib only: no YAML parser, so steps are found by their
# "- name:" or "- uses:" lines, which every workflow here uses.
STEP_START = re.compile(r"^\s*-\s+(name|uses|run):")


def steps(text):
    current = []
    for line in text.splitlines():
        if STEP_START.match(line) and current:
            yield "\n".join(current)
            current = []
        current.append(line)
    if current:
        yield "\n".join(current)


class WorkflowSqlServerFilterTests(unittest.TestCase):
    def test_every_dotnet_test_step_filters_or_provides_a_server(self):
        files = sorted(WORKFLOWS.glob("*.yml"))
        self.assertTrue(files, "no workflows found")
        offenders = []
        for path in files:
            for step in steps(path.read_text(encoding="utf-8")):
                if "dotnet test" not in step:
                    continue
                if "Category!=SqlServer" in step or "Category=SqlServer" in step or "FORMSAUDIT_SQLSERVER" in step:
                    continue
                offenders.append(path.name)
        self.assertEqual(offenders, [], "dotnet test without a SqlServer category filter or FORMSAUDIT_SQLSERVER")

    def test_guard_detects_an_unfiltered_step(self):
        text = "steps:\n  - name: Test\n    run: dotnet test x.slnx -c Release\n  - name: Other\n    run: echo\n"
        flagged = [s for s in steps(text) if "dotnet test" in s and "Category" not in s]
        self.assertEqual(len(flagged), 1)


if __name__ == "__main__":
    unittest.main()
