"""Pack or install a state bundle with the Python StateSync, the specification.

Run by the C# tests (python3 -B -I statesync_bundle.py REPO_ROOT ...):

    pack DATABASE PREFERENCES ACCOUNT OUT      write StateSync._pack's bundle to OUT
    install BUNDLE DATABASE PREFERENCES ACCOUNT  run StateSync._install; print the manifest, or the error

Fictional state only; touches nothing but the paths given and makes no network call.
"""

import json
import pathlib
import sys
from datetime import datetime, timezone

sys.path.insert(0, sys.argv[1] + "/autopilot/src")

from margo_autopilot import statesync  # noqa: E402

command = sys.argv[2]
if command == "pack":
    database, preferences, account, out = sys.argv[3:7]
    sync = statesync.StateSync(database, None, account=account, preferences=preferences)
    pathlib.Path(out).write_bytes(sync._pack(datetime(2026, 10, 17, 9, 30, tzinfo=timezone.utc)))
elif command == "install":
    bundle, database, preferences, account = sys.argv[3:7]
    sync = statesync.StateSync(database, None, account=account, preferences=preferences)
    try:
        print(json.dumps({"manifest": sync._install(pathlib.Path(bundle).read_bytes())}, sort_keys=True))
    except statesync.SyncError as exc:
        print(json.dumps({"sync_error": str(exc)}))
else:
    raise SystemExit("unknown command")
