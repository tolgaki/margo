"""Where one inbound request goes. Standard library only; ``app.py`` performs the decision.

The router holds no Microsoft 365 token and no instance state. It validates the platform token
(in ``app.py``), then either forwards the request to the one container that serves the addressed
instance, or asks the provisioner to create it. Lifecycle notifications never reach an instance:
they become provisioning messages.
"""

import re

from ..instances import (ENSURE, PROVISIONING, READY, RETIRED, SUSPENDED, activity_identity,
                         lifecycle_message)

MAX_ACTIVITY_BYTES = 1024 * 1024
RETRY_AFTER_SECONDS = 30
VOICE_PATH = re.compile(r"^/i/([0-9a-f]{20})/voice(/[A-Za-z0-9._/-]*)?$")


def decide(activity, table):
    """Return ``{"kind": ..., "status": ...}``: lifecycle, forward, provision, wait, ignore, reject."""
    message = lifecycle_message(activity)
    if message is not None:
        return {"kind": "lifecycle", "status": 200, "message": message}
    identity = activity_identity(activity)
    if identity is None:
        return {"kind": "reject", "status": 400, "reason": "activity has no agent recipient"}
    key, entry = table.resolve(*identity)
    if key is None:
        return {"kind": "reject", "status": 400, "reason": "activity has an invalid agent recipient"}
    state = (entry or {}).get("state")
    if state == READY and entry.get("url"):
        return {"kind": "forward", "status": 200, "key": key, "url": entry["url"]}
    if state in (SUSPENDED, RETIRED):
        # Acknowledge so the platform does not retry; nothing is delivered to a stopped instance.
        return {"kind": "ignore", "status": 200, "key": key, "reason": "instance is %s" % state}
    if state == PROVISIONING:
        return {"kind": "wait", "status": 503, "key": key, "retry_after": RETRY_AFTER_SECONDS}
    return {"kind": "provision", "status": 503, "key": key, "retry_after": RETRY_AFTER_SECONDS,
            "message": {"action": ENSURE, "agent_identity_id": identity[0], "agent_user_id": identity[1],
                        "event": "first-activity" if entry is None else "retry-after-failure"}}


def voice_target(path, table):
    """``/i/{key}/voice/...`` -> the instance's ``/voice/...`` address, or a status to return."""
    match = VOICE_PATH.match(path or "")
    if not match:
        return {"status": 404}
    key, rest = match.group(1), match.group(2) or "/"
    if ".." in rest or "//" in rest:
        return {"status": 404}
    entry = table.get(key)
    if entry is None or entry.get("state") in (SUSPENDED, RETIRED):
        return {"status": 404}
    if entry.get("state") != READY or not entry.get("url"):
        return {"status": 503, "retry_after": RETRY_AFTER_SECONDS}
    return {"status": 200, "key": key, "url": entry["url"].rstrip("/") + "/voice" + rest}
