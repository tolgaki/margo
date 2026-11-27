"""Prints the subcommands each named desktop state script's argparse parser accepts, as JSON.

The desktop helpers in skills/chief-of-staff/scripts/ stay the specification of the state commands, so ParityTests
reads their parsers live: a subcommand added to the desktop must be classified for Margo Cloud.

    python3 -B -I subcommands.py REPO_ROOT SCRIPT.py ...
"""

import argparse
import importlib
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(sys.argv[1]).resolve() / "skills" / "chief-of-staff" / "scripts"))


def subcommands(script):
    parser = importlib.import_module(script[:-3]).parser()
    actions = [action for action in parser._actions if isinstance(action, argparse._SubParsersAction)]
    return sorted(actions[0].choices) if actions else []


sys.stdout.write(json.dumps({script: subcommands(script) for script in sorted(sys.argv[2:])}, ensure_ascii=True))
