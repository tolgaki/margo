"""One container per hired Margo: names, routes, lifecycle and plans, without any Azure SDK.

The router, the provisioner and each instance container import this module, so they agree on
how an instance is named, where it lives and what a lifecycle event means. Standard library only,
tested offline; the Azure calls live in ``router/app.py`` and ``provision/azure.py``.

An instance is one hired Margo: the agent application instance (``agenticAppId``) and her agent
user (``agenticUserId``). The manager is not part of the key. A new manager keeps the same
container, and the orchestrator already gives each manager a separate account and state.
"""

import hashlib
import json
import re
from datetime import datetime, timezone

from .instance import BindingError, _check

SCHEMA_VERSION = 1
READY, PROVISIONING, SUSPENDED, RETIRED, FAILED = "ready", "provisioning", "suspended", "retired", "failed"
STATES = {READY, PROVISIONING, SUSPENDED, RETIRED, FAILED}
ENSURE, SUSPEND, RETIRE = "ensure", "suspend", "retire"

# Agent 365 lifecycle notifications (activity name "agentLifecycle", value type below).
LIFECYCLE = {
    "agenticuseridentitycreated": ENSURE,
    "agenticuseridentityupdated": ENSURE,
    "agenticuserworkloadonboardingupdated": ENSURE,
    "agenticusermanagerupdated": ENSURE,  # same container; the new manager gets a new account
    "agenticuserundeleted": ENSURE,
    "agenticuserenabled": ENSURE,
    "agenticuserdisabled": SUSPEND,
    "agenticuserdeleted": RETIRE,
}
KEY = re.compile(r"[0-9a-f]{20}")


class RouteError(ValueError):
    """The route table or a message is malformed; nothing is changed."""


def _utc_now():
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def instance_key(agent_identity_id, agent_user_id):
    """A short, stable, opaque key for one hired instance (never an email or display name)."""
    _check("agent_identity_id", agent_identity_id)
    _check("agent_user_id", agent_user_id)
    if agent_identity_id == agent_user_id:
        raise BindingError("agent identity and agent user must differ")
    return hashlib.sha256(("%s\n%s" % (agent_identity_id, agent_user_id)).encode("utf-8")).hexdigest()[:20]


def resource_names(key):
    """Azure names for one instance: Container App (<=32), identity, Blob container (<=63)."""
    if not isinstance(key, str) or not KEY.fullmatch(key):
        raise RouteError("not an instance key")
    return {"app": "margo-" + key, "identity": "id-margo-" + key, "container": "margo-i-" + key}


def internal_url(key, environment_domain):
    """The instance's internal HTTPS address inside the Container Apps environment."""
    if not isinstance(environment_domain, str) or not re.fullmatch(r"[a-z0-9.-]{3,253}", environment_domain):
        raise RouteError("environment domain is required")
    return "https://%s.internal.%s" % (resource_names(key)["app"], environment_domain)


def activity_identity(activity):
    """``(agent_identity_id, agent_user_id)`` from a raw activity's recipient, or None."""
    recipient = activity.get("recipient") if isinstance(activity, dict) else None
    if not isinstance(recipient, dict):
        return None
    identity = recipient.get("agenticAppId")
    user = recipient.get("agenticUserId") or recipient.get("aadObjectId")
    if not isinstance(identity, str) or not isinstance(user, str):
        return None
    return identity, user


def lifecycle_message(activity):
    """A provisioning message for an Agent 365 lifecycle notification, or None for anything else."""
    if not isinstance(activity, dict) or str(activity.get("name", "")).lower() != "agentlifecycle":
        return None
    event = str(activity.get("valueType") or "").lower()
    action = LIFECYCLE.get(event)
    identity = activity_identity(activity)
    if action is None or identity is None:
        return None
    return {"action": action, "agent_identity_id": identity[0], "agent_user_id": identity[1], "event": event}


def check_message(message):
    """Validate a provisioning message from the queue; return it with its instance key."""
    if not isinstance(message, dict) or message.get("action") not in (ENSURE, SUSPEND, RETIRE):
        raise RouteError("unknown provisioning action")
    try:
        key = instance_key(message.get("agent_identity_id"), message.get("agent_user_id"))
    except BindingError as exc:
        raise RouteError(str(exc)) from exc
    return dict(message, key=key)


class RouteTable:
    """Where each instance lives. Written only by the provisioner; read by the router."""

    def __init__(self, routes=None):
        self.routes = dict(routes or {})

    @classmethod
    def loads(cls, text):
        if not text:
            return cls()
        try:
            data = json.loads(text)
        except ValueError as exc:
            raise RouteError("route table is not JSON") from exc
        if not isinstance(data, dict) or data.get("schema_version") != SCHEMA_VERSION:
            raise RouteError("unsupported route table")
        routes = data.get("routes")
        if not isinstance(routes, dict):
            raise RouteError("route table has no routes")
        for key, entry in routes.items():
            if not KEY.fullmatch(key) or not isinstance(entry, dict) or entry.get("state") not in STATES:
                raise RouteError("malformed route entry")
        return cls(routes)

    def dumps(self):
        return json.dumps({"schema_version": SCHEMA_VERSION, "routes": self.routes}, sort_keys=True, indent=1)

    def get(self, key):
        return self.routes.get(key)

    def resolve(self, agent_identity_id, agent_user_id):
        try:
            key = instance_key(agent_identity_id, agent_user_id)
        except BindingError:
            return None, None
        return key, self.routes.get(key)

    def put(self, key, *, agent_identity_id, agent_user_id, state, url=None, error=None):
        if state not in STATES:
            raise RouteError("unknown route state")
        previous = self.routes.get(key) or {}
        entry = {"agent_identity_id": agent_identity_id, "agent_user_id": agent_user_id, "state": state,
                 "url": url if url is not None else previous.get("url"),
                 "revision": int(previous.get("revision", 0)) + 1, "updated_at": _utc_now()}
        if error:
            entry["error"] = str(error)[:500]
        if state == RETIRED:
            entry["retired_at"] = entry["updated_at"]
        self.routes[key] = entry
        return entry


def plan(message, current):
    """The ordered, idempotent steps for one provisioning message, given the current route.

    Steps: ``route_provisioning``, ``deploy``, ``route_ready``, ``route_suspended``,
    ``route_retired``, ``delete_app``, ``delete_identity``. Storage is never deleted here: a
    retired instance's state is kept until an operator deletes it after the retention period.
    """
    state = (current or {}).get("state")
    action = message["action"]
    if action == ENSURE:
        if state == READY and not message.get("redeploy"):
            return []
        return ["route_provisioning", "deploy", "route_ready"]
    if action == SUSPEND:
        return [] if state in (None, SUSPENDED, RETIRED) else ["route_suspended"]
    if state in (None, RETIRED):
        return []
    return ["route_retired", "delete_app", "delete_identity"]


REQUIRED_CONFIG = ("environmentId", "environmentDomain", "image", "storageAccountName", "registryServer",
                   "registryId", "foundryAccountName", "foundryProjectName", "foundryProjectEndpoint",
                   "modelDeployment", "storageRoleDefinitionId", "pullRoleDefinitionId", "modelRoleDefinitionId",
                   "speechRoleDefinitionId", "publicBaseUrl",
                   "tenantId", "blueprintClientId", "blueprintCredentialIdentityId", "blueprintCredentialClientId")


def deployment_parameters(key, message, config):
    """ARM parameters for ``autopilot/infra/instance.json``: one instance, from provisioner config."""
    missing = [name for name in REQUIRED_CONFIG if not config.get(name)]
    if missing:
        raise RouteError("provisioner configuration is missing: %s" % ", ".join(missing))
    names = resource_names(key)
    values = {
        "instanceKey": key,
        "agentIdentityId": message["agent_identity_id"],
        "agentUserId": message["agent_user_id"],
        "appName": names["app"],
        "identityName": names["identity"],
        "stateContainerName": names["container"],
        "minReplicas": int(config.get("minReplicas", 0)),
        "voiceLiveModel": config.get("voiceLiveModel", ""),
        "voiceName": config.get("voiceName", ""),
        "voiceEndpointId": config.get("voiceEndpointId", ""),
        "extraEnv": list(config.get("extraEnv", [])),
    }
    for name in REQUIRED_CONFIG:
        if name != "environmentDomain":
            values[name] = config[name]
    if values["minReplicas"] not in (0, 1):
        raise RouteError("minReplicas must be 0 or 1: one container serves one instance")
    return {name: {"value": value} for name, value in values.items()}
