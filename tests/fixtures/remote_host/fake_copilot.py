#!/usr/bin/env python3
"""Stand-in for the Copilot CLI binary. Test fixture only.

The scheduled wrapper looks for ``copilot`` on PATH, so tests install a one-line shim named
``copilot`` that execs this file. Every invocation appends one JSON line to ``FAKE_COPILOT_LOG``::

    {"argv": [...], "prompt": "<the -p/--prompt value or null>", "cwd": "...", "env": {"MARGO_ACCOUNT": ..., "MARGO_AUTOMATIONS": ...}}

Behaviour is driven by the environment so a test can change it between runs without rewriting
the shim:

- ``copilot --version`` (the deployment's ``copilot_check``) prints a version line and exits
  ``FAKE_COPILOT_VERSION_EXIT`` (default 0);
- any other invocation exits ``FAKE_COPILOT_EXIT`` (default 0) after sleeping
  ``FAKE_COPILOT_SLEEP`` seconds (default 0), printing the prompt's first line on stdout;
- argv containing ``login`` or ``auth`` is recorded like everything else, so a test can prove the
  harness never attempted a sign-in.
"""

import json
import os
import sys
import time


def prompt_from(argv):
    for index, item in enumerate(argv):
        if item in ("-p", "--prompt") and index + 1 < len(argv):
            return argv[index + 1]
        if item.startswith("--prompt="):
            return item[len("--prompt="):]
    return None


def record(entry):
    location = os.environ.get("FAKE_COPILOT_LOG")
    if not location:
        return
    with open(location, "a", encoding="utf-8") as stream:
        stream.write(json.dumps(entry, sort_keys=True) + "\n")


def main(argv):
    record({"argv": argv, "prompt": prompt_from(argv), "cwd": os.getcwd(),
            "env": {key: os.environ.get(key) for key in ("MARGO_ACCOUNT", "MARGO_AUTOMATIONS", "MARGO_AGENT")}})
    if argv == ["--version"]:
        print("fake copilot 0.0.0-fixture")
        return int(os.environ.get("FAKE_COPILOT_VERSION_EXIT", "0"))
    delay = float(os.environ.get("FAKE_COPILOT_SLEEP", "0") or 0)
    if delay > 0:
        time.sleep(delay)
    prompt = prompt_from(argv)
    if prompt:
        print("fake copilot ran: " + prompt.splitlines()[0][:120])
    return int(os.environ.get("FAKE_COPILOT_EXIT", "0"))


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
