"""Fictional remote-host deployment files for tests. GUIDs are generated at runtime, never typed.

Import via sys.path (plain helper module, not a package):

    sys.path.insert(0, str(ROOT / "tests/fixtures/remote_host"))
    from deployment_fixture import make_deployment, MANAGER_OID, MARGO_OID

``make_deployment(tmpdir, **overrides)`` writes a valid ``deployment.json`` and returns its path.
Section overrides (``control={"teams_enabled": False}``) are merged into that section; any other
override replaces the key. ``cli_logins`` defaults to the current login so peer-credential tests
can run as the manager.
"""

import json
import os
import uuid
from pathlib import Path

try:
    import pwd
except ImportError:  # Windows: no passwd database, so no resolvable CLI login.
    pwd = None

MANAGER_PRINCIPAL = "dana@example.com"
MARGO_PRINCIPAL = "margo@example.com"
MANAGER_OID = str(uuid.uuid5(uuid.NAMESPACE_DNS, "manager.example.com"))
MARGO_OID = str(uuid.uuid5(uuid.NAMESPACE_DNS, "margo.example.com"))
OUTSIDER_OID = str(uuid.uuid5(uuid.NAMESPACE_DNS, "outsider.example.org"))
CHAT_ID = "19:" + uuid.uuid5(uuid.NAMESPACE_DNS, "chat.example.com").hex + "@unq.gbl.spaces"


def current_login():
    if pwd is None or not hasattr(os, "getuid"):
        return None
    try:
        return pwd.getpwuid(os.getuid()).pw_name
    except KeyError:
        return None


def deployment_dict(**overrides):
    """The contract's schema v1 with fictional values; see make_deployment for override rules."""
    login = current_login()
    data = {
        "schema_version": 1,
        "profile": "remote-host",
        "margo": {"principal": MARGO_PRINCIPAL, "object_id": MARGO_OID},
        "manager": {"principal": MANAGER_PRINCIPAL, "object_id": MANAGER_OID, "display_name": "Dana"},
        "control": {
            "cli_logins": [login] if login else [],
            "teams_chat_id": CHAT_ID,
            "teams_enabled": True,
            "email_enabled": True,
            "require_authentication_results": True,
            "max_replies_per_hour": 12,
            "approval_ttl_hours": 24,
        },
        "workiq": {
            "command": ["python3", "fake_workiq.py"],
            "env": {},
            "server_name": "workiq-gate",
            "read_tools": ["fetch", "retrieve", "ask", "call_function", "get_schema", "search_paths", "fetch_blob"],
            "write_tools": ["do_action", "create_entity", "update_entity", "delete_entity"],
            "argument_keys": {"path": ["path", "url", "endpoint", "resource", "entityPath", "entity_path"],
                              "method": ["method", "httpMethod", "verb"],
                              "body": ["body", "payload", "data", "entity", "properties"],
                              "action": ["action", "actionName", "operation"]},
            "call_templates": {
                "teams_message": {"tool": "create_entity", "arguments": {
                    "path": "/chats/{chat_id}/messages", "method": "POST",
                    "body": {"body": {"contentType": "text", "content": "{text}"}}}},
                "email_message": {"tool": "do_action", "arguments": {
                    "path": "/me/sendMail", "method": "POST",
                    "body": {"message": {"subject": "{subject}", "body": {"contentType": "text", "content": "{text}"},
                                         "toRecipients": [{"emailAddress": {"address": "{address}"}}]}}}},
            },
        },
        "harness": {"poll_seconds": 180, "copilot_command": ["copilot"], "copilot_check": ["copilot", "--version"],
                    "max_backoff_seconds": 900, "startup_verb": "startup",
                    "health_path": "/var/lib/margo/health.json", "automations_dir": None, "scheduled_wrapper": None},
        "gate": {"socket": "/run/margo/gate.sock", "audit_log": "/var/lib/margo/gate-audit.jsonl",
                 "margo_root_paths": ["/me/", "/users/" + MARGO_OID + "/", "/users/" + MARGO_PRINCIPAL + "/"],
                 "manager_root_paths": ["/users/" + MANAGER_OID + "/", "/users/" + MANAGER_PRINCIPAL + "/"]},
    }
    for key, value in overrides.items():
        if isinstance(value, dict) and isinstance(data.get(key), dict):
            data[key] = dict(data[key], **value)
        else:
            data[key] = value
    return data


def make_deployment(tmpdir, **overrides):
    """Write deployment.json under tmpdir (or at the ``path`` override) and return its path as a string."""
    target = overrides.pop("path", None)
    path = Path(target) if target else Path(tmpdir) / "deployment.json"
    path.write_text(json.dumps(deployment_dict(**overrides), indent=2) + "\n", encoding="utf-8")
    return str(path)
