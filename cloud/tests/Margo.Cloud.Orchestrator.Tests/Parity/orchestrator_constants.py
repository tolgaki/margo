"""Print the fixed texts and limits of the Python orchestrator, office queue, reply gate and state sync as JSON.

Run by the C# tests (python3 -B -I orchestrator_constants.py REPO_ROOT): the Python modules are the specification, so
every text that can leave Margo must be identical. Reads no state and makes no network call.
"""

import json
import sys

root = sys.argv[1]
sys.path.insert(0, root + "/skills/chief-of-staff/scripts")
sys.path.insert(0, root + "/autopilot/src")

from margo_autopilot import office, orchestrator, replies, statesync  # noqa: E402

print(json.dumps({
    "TURN_CALL_BUDGET": orchestrator.TURN_CALL_BUDGET,
    "STATE_CALL_BUDGET": orchestrator.STATE_CALL_BUDGET,
    "PERSONAL": orchestrator.PERSONAL,
    "VOICE_UNAVAILABLE": orchestrator.VOICE_UNAVAILABLE,
    "NO_MANAGER": orchestrator.NO_MANAGER,
    "MANAGER_UNKNOWN": orchestrator.MANAGER_UNKNOWN,
    "GROUP_REFUSED": orchestrator.GROUP_REFUSED,
    "STATE_UNSAFE": orchestrator.STATE_UNSAFE,
    "TURN_FAILED": orchestrator.TURN_FAILED,
    "OFFICE_FAMILY": office.OFFICE_FAMILY,
    "CONVERSATION_FAMILY": office.CONVERSATION_FAMILY,
    "CONVERSATION_SCOPE": office.CONVERSATION_SCOPE,
    "OFFICE_NOTE": office.OFFICE_NOTE,
    "EXCERPT_LIMIT": office.EXCERPT_LIMIT,
    "MODES": list(replies.MODES),
    "SETTING_LABEL": replies.SETTING_LABEL,
    "ACKNOWLEDGEMENT": replies.ACKNOWLEDGEMENT,
    "BUNDLE": statesync.BUNDLE,
    "MARKER": statesync.MARKER,
}))
