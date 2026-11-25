#!/usr/bin/env python3
"""Fail on any advisory in a `dotnet list package --vulnerable --include-transitive --format json` report.

Usage: python3 -I build/check-vulnerable.py <report.json>
Exit 0 clean, 1 any advisory (every severity), 2 report unusable.

dotnet exits 0 even when it lists advisories, so the parsed report is the only trustworthy signal. Any
unexpected shape exits 2 so a format change can never pass silently.
"""
import json
import sys


def _items(value, what):
    if value is None:
        return []
    if not isinstance(value, list) or not all(isinstance(i, dict) for i in value):
        raise ValueError(f"{what} is not a list of objects")
    return value


def collect(projects):
    found = []
    for proj in _items(projects, "projects"):
        for fw in _items(proj.get("frameworks"), "frameworks"):
            for kind in ("topLevelPackages", "transitivePackages"):
                for pkg in _items(fw.get(kind), kind):
                    vulns = _items(pkg.get("vulnerabilities"), "vulnerabilities")
                    # --vulnerable lists only affected packages, so an entry with no advisory means the format changed.
                    if not vulns:
                        raise ValueError(f"package {pkg.get('id')} is listed without vulnerabilities")
                    for v in vulns:
                        found.append((proj.get("path"), pkg.get("id"), pkg.get("resolvedVersion"),
                                      v.get("severity"), v.get("advisoryurl")))
    return found


def main(argv):
    if len(argv) != 2:
        print("usage: check-vulnerable.py <report.json>", file=sys.stderr)
        return 2
    path = argv[1]
    try:
        with open(path, encoding="utf-8-sig") as fh:
            data = json.load(fh)
        if not isinstance(data, dict):
            raise ValueError("top-level value is not an object")
    except Exception as exc:
        print(f"check-vulnerable: could not parse dotnet output: {exc}", file=sys.stderr)
        with open(path, encoding="utf-8", errors="replace") as fh:
            print(fh.read(), file=sys.stderr)
        return 2

    problems = data.get("problems")
    if problems:
        for p in problems:
            print(f"check-vulnerable: problem: {p.get('text') if isinstance(p, dict) else p}", file=sys.stderr)
        return 2

    # Without --include-transitive the report omits transitive advisories, which is the gap this gate exists to close.
    if "--include-transitive" not in str(data.get("parameters")):
        print("check-vulnerable: report was not produced with --include-transitive", file=sys.stderr)
        return 2

    projects = data.get("projects")
    if not projects or not isinstance(projects, list):
        print("check-vulnerable: no projects in output", file=sys.stderr)
        return 2

    try:
        found = collect(projects)
    except ValueError as exc:
        print(f"check-vulnerable: unexpected report shape: {exc}", file=sys.stderr)
        return 2

    if found:
        print("check-vulnerable: vulnerable packages found:", file=sys.stderr)
        for project, pid, ver, sev, url in found:
            print(f"  {project}: {pid} {ver} {sev} {url}", file=sys.stderr)
        return 1

    print(f"check-vulnerable: no vulnerable packages in {len(projects)} project(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
