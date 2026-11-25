#!/usr/bin/env python3
"""Summarize Margo Cloud's Work IQ guard receipts: calls per tool, outcomes, status codes and path templates.

The host logs one line per Work IQ call: ``workiq {receipt JSON}``. Receipts hold resource types, never identifiers,
addresses, search terms or results, so the summary is safe to share once you have checked it. Latency is in the
``Margo.Cloud.WorkIQ`` traces and metrics (Application Insights), not in receipts.

Usage:
    python3 tools/workiq_receipt_stats.py < host.log
"""

import collections
import json
import sys


def receipts(lines):
    for line in lines:
        index = line.find("workiq {")
        if index < 0:
            continue
        try:
            yield json.loads(line[index + len("workiq "):])
        except ValueError:
            continue


def summarize(lines):
    by_tool = collections.defaultdict(collections.Counter)
    codes = collections.Counter()
    paths = collections.Counter()
    total = 0
    for receipt in receipts(lines):
        total += 1
        by_tool[receipt.get("tool") or "?"][receipt.get("status") or "?"] += 1
        for code in receipt.get("status_codes") or []:
            codes[str(code)] += 1
        for path in receipt.get("paths") or []:
            paths[path] += 1
    return {
        "calls": total,
        "by_tool": {tool: dict(sorted(counts.items())) for tool, counts in sorted(by_tool.items())},
        "status_codes": dict(sorted(codes.items())),
        "top_paths": dict(paths.most_common(20)),
    }


def main(stdin=sys.stdin, stdout=sys.stdout):
    stdout.write(json.dumps(summarize(stdin), indent=1, sort_keys=True) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
