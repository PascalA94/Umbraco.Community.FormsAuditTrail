#!/usr/bin/env python3
"""Fail unless enough tests of one category executed and every one of them passed.

Usage: python3 -I build/check-trx.py <results.trx> --category SqlServer --min 2
Exit 0 ok, 1 too few executed or any failed, 2 file unusable.

Why this exists: [SqlServerFact] skips when no server is configured, and a skipped test reports as success. The
trx is the only record of what actually executed, so a CI step reads it and fails if the SQL Server tests did not.

How a test is attributed to a category (checked against a real trx from this repo's xunit v2 adapter 4.0.1):
that adapter writes no traits into the trx at all, no TestCategory element and no property. So a test belongs to
the category when EITHER the trx carries a TestCategoryItem for it (other adapters do), OR its method name ends
with "On<Category>" (for example Upgrade_From1802Schema_OnSqlServer). SqlServerTraitConventionTests in the test
project enforces that suffix on every [SqlServerFact], so the two cannot drift. Matching on a plain substring is
deliberately avoided: SqlServerFact_InCiWithoutConnectionString_DoesNotSkip is an ordinary test about the attribute.
"""
import argparse
import sys
import xml.etree.ElementTree as ET

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
EXECUTED = {"Passed", "Failed", "Error", "Timeout", "Aborted"}


def load(path):
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as e:
        raise ValueError(f"cannot parse {path}: {e}")
    if root.tag != NS + "TestRun":
        raise ValueError("not a trx file: root element is not TestRun")
    results = root.find(NS + "Results")
    definitions = root.find(NS + "TestDefinitions")
    if results is None or definitions is None:
        raise ValueError("trx has no Results or TestDefinitions")
    return results, definitions


def categories_by_test_id(definitions):
    out = {}
    for unit in definitions.findall(NS + "UnitTest"):
        items = {i.get("TestCategory") for i in unit.iter(NS + "TestCategoryItem")}
        out[unit.get("id")] = items
    return out


def in_category(name, test_id, category, categories):
    if category in categories.get(test_id, set()):
        return True
    short = name.rsplit(".", 1)[-1]
    return short.endswith("On" + category)


def main(argv):
    parser = argparse.ArgumentParser(prog="check-trx.py")
    parser.add_argument("trx")
    parser.add_argument("--category", required=True)
    parser.add_argument("--min", required=True, type=int)
    args = parser.parse_args(argv[1:])
    # --min 0 would pass a run in which every test was skipped, which is the failure this script exists to catch.
    if args.min < 1:
        print("check-trx: --min must be at least 1", file=sys.stderr)
        return 2
    try:
        results, definitions = load(args.trx)
        categories = categories_by_test_id(definitions)
        executed, failed = [], []
        for r in results.findall(NS + "UnitTestResult"):
            name, outcome = r.get("testName") or "", r.get("outcome")
            if outcome is None:
                raise ValueError(f"result for {name} has no outcome")
            if not in_category(name, r.get("testId"), args.category, categories):
                continue
            if outcome in EXECUTED:
                executed.append(name)
                if outcome != "Passed":
                    failed.append(f"{name} ({outcome})")
    except ValueError as e:
        print(f"check-trx: {e}", file=sys.stderr)
        return 2
    if failed:
        print(f"check-trx: {len(failed)} {args.category} test(s) did not pass:", file=sys.stderr)
        for f in failed:
            print(f"  {f}", file=sys.stderr)
        return 1
    if len(executed) < args.min:
        print(f"check-trx: {len(executed)} {args.category} test(s) executed, need at least {args.min}. "
              "A skipped test does not count.", file=sys.stderr)
        return 1
    print(f"check-trx: {len(executed)} {args.category} test(s) executed and passed (minimum {args.min}).")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
