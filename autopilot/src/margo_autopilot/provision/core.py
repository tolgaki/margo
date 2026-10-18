"""The provisioner's decisions, without Azure: what to do with one queue message, step by step.

Each step is idempotent, so a message that fails part-way is safe to retry from the start. The
route table is the only record the provisioner writes; storage is never deleted by it.
"""

import json

from ..instances import (FAILED, PROVISIONING, READY, RETIRED, SUSPENDED, RouteError, RouteTable, check_message,
                         deployment_parameters, internal_url, plan, resource_names)

MAX_ATTEMPTS = 5
ROUTE_STATES = {"route_provisioning": PROVISIONING, "route_ready": READY, "route_suspended": SUSPENDED,
                "route_retired": RETIRED}


class Provisioner:
    """``cloud`` performs Azure calls: ``deploy(name, template, parameters)``, ``delete_app(name)``,
    ``delete_identity(name)``, ``load_routes() -> (text, etag)``, ``save_routes(text, etag)``.
    ``save_routes`` must fail if the etag no longer matches, so concurrent writers never lose an
    update."""

    def __init__(self, *, cloud, config, template, log=None):
        self.cloud, self.config, self.template = cloud, config, template
        self._log = log or (lambda *_: None)

    def handle(self, raw, dequeue_count=1):
        """Process one message. Returns ``"done"``, ``"retry"`` or ``"dropped"``; never raises."""
        try:
            message = check_message(json.loads(raw) if isinstance(raw, str) else raw)
        except (ValueError, RouteError):
            self._log("dropped malformed provisioning message")
            return "dropped"
        key = message["key"]
        steps = plan(message, self._route(key))
        self._log("instance %s: %s -> %s" % (key, message["action"], steps or "nothing to do"))
        try:
            for step in steps:
                self._step(step, key, message)
            return "done"
        except Exception as exc:  # the message reappears; after MAX_ATTEMPTS the route stays failed
            self._log("instance %s: step failed: %s" % (key, type(exc).__name__))
            try:
                self._set_route(key, message, FAILED, error=type(exc).__name__)
            except Exception:
                self._log("instance %s: could not record the failure" % key)
            return "dropped" if dequeue_count >= MAX_ATTEMPTS else "retry"

    def _route(self, key):
        text, _ = self.cloud.load_routes()
        return RouteTable.loads(text).get(key)

    def _step(self, step, key, message):
        names = resource_names(key)
        if step in ROUTE_STATES:
            self._set_route(key, message, ROUTE_STATES[step])
        elif step == "deploy":
            parameters = deployment_parameters(key, message, self.config)
            self.cloud.deploy("margo-instance-" + key, self.template, parameters)
        elif step == "delete_app":
            self.cloud.delete_app(names["app"])
        elif step == "delete_identity":
            self.cloud.delete_identity(names["identity"])
        else:
            raise RouteError("unknown step %s" % step)

    def _set_route(self, key, message, state, error=None):
        url = internal_url(key, self.config["environmentDomain"])
        for _ in range(5):  # optimistic concurrency: re-read and re-apply on a conflicting write
            text, etag = self.cloud.load_routes()
            table = RouteTable.loads(text)
            table.put(key, agent_identity_id=message["agent_identity_id"], agent_user_id=message["agent_user_id"],
                      state=state, url=url, error=error)
            if self.cloud.save_routes(table.dumps(), etag):
                return
        raise RouteError("route table kept changing; try again")
