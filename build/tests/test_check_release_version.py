import re
import shutil
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "build" / "check-release-version.sh"


def prop(name):
    return re.search(rf"<{name}>(.*?)</{name}>", (ROOT / "Directory.Build.props").read_text(encoding="utf-8")).group(1)


def run(tag):
    return subprocess.run(["bash", str(SCRIPT), tag], cwd=ROOT, capture_output=True, text=True)


@unittest.skipUnless(shutil.which("node"), "check-release-version.sh reads the client version files with node")
class CheckReleaseVersionTests(unittest.TestCase):
    def test_tag_matching_directory_build_props_passes(self):
        suffix = prop("PackageVersionSuffix")
        tag = f"release/{prop('PackageMinor')}.{prop('PackagePatch')}" + (f"-{suffix}" if suffix else "")
        result = run(tag)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_tag_that_disagrees_with_directory_build_props_fails(self):
        result = run("release/999.999")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("tag wants '999.999'", result.stderr)

    def test_old_per_line_tag_shapes_are_rejected(self):
        for tag in ("v18.1.2", "release/18.1.2", "release/1", "release/1.2-", "release/x.y"):
            with self.subTest(tag=tag):
                result = run(tag)
                self.assertNotEqual(result.returncode, 0)


if __name__ == "__main__":
    unittest.main()
