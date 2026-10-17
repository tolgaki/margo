"""Execution claims for guarded writes, backed by the existing approval journal.

The guard never decides that a write is approved. It asks this journal whether a specific
execution attempt, already started by ``Ledger.begin`` after an exact human approval, is still
live, and records the actual outcome through ``Ledger.finish``.

Hosted convention: an action executed by the autopilot carries the exact outward call in its
approved snapshot, so the human approved the call itself, not a description of it::

    "target":  {..., "on_data_of": "principal"},
    "payload": {..., "workiq": {"tool": "do_action", "arguments": {"actionUrl": "..."}}}

An approved reply to someone other than the manager uses ``"payload": {"reply": {"to": ID,
"text": "..."}}`` instead. The approval must be recorded with the manager's directory object
ID as its actor; the hosting runtime takes that ID from the authenticated card submission.
"""

from datetime import datetime, timezone

DISPATCHED = "workiq_dispatched"


class ClaimError(ValueError):
    """The claim is missing, settled, mismatched or otherwise unusable."""


def _now():
    return datetime.now(timezone.utc).isoformat(timespec="microseconds")


class LedgerClaimJournal:
    """Read claims with ``Ledger.show`` and settle them with ``Ledger.finish``."""

    def __init__(self, ledger):
        self.ledger = ledger

    def load(self, action_id, attempt_id, call_key="workiq"):
        if not isinstance(action_id, str) or not isinstance(attempt_id, str):
            raise ClaimError("action_id and attempt_id are required for a guarded write")
        try:
            action = self.ledger.show(action_id)
        except Exception as exc:  # StateError from the ledger; never leak internals as success
            raise ClaimError("unknown action") from exc
        if action.get("type") != "action" or action.get("state") != "executing":
            raise ClaimError("action has no live execution claim")
        attempt = next((row for row in action.get("executions", []) if row.get("id") == attempt_id), None)
        if not attempt or attempt.get("state") != "executing" or attempt.get("revision") != action["revision"]:
            raise ClaimError("execution attempt is not live for the current revision")
        target, payload = action.get("target"), action.get("payload")
        if not isinstance(target, dict) or not isinstance(payload, dict) or not isinstance(payload.get(call_key), dict):
            raise ClaimError("approved action does not carry an exact %s call" % call_key)
        if self._dispatched(action_id, attempt_id):
            raise ClaimError("attempt was already dispatched; reconcile it, never resend")
        return {
            "account": self.ledger.account,
            "action_id": action_id,
            "attempt_id": attempt_id,
            "revision": action["revision"],
            "action_hash": action["action_hash"],
            "on_data_of": target.get("on_data_of"),
            "call_key": call_key,
            "call": payload[call_key],
            "approved_by": self._approver(action_id, action["revision"], action["action_hash"]),
        }

    def _approver(self, action_id, revision, action_hash):
        """The actor recorded on the approval that authorised this exact revision and hash."""
        actor = None
        for event in self.ledger.history(action_id)["events"]:
            evidence = event.get("data", {}).get("evidence", {}) if event.get("event") == "approved" else {}
            if evidence.get("revision") == revision and evidence.get("action_hash") == action_hash:
                actor = evidence.get("actor")
        return actor

    def _dispatched(self, action_id, attempt_id):
        return any(event.get("event") == DISPATCHED and event.get("data", {}).get("attempt_id") == attempt_id
                   for event in self.ledger.history(action_id)["events"])

    def mark_dispatched(self, claim):
        """Durably consume the claim before the provider call, across processes and restarts.

        A crash between dispatch and settlement leaves the attempt ``executing`` with this
        event recorded; it must then be reconciled, and no guard will send it again.
        """
        with self.ledger.transaction():
            action = self.ledger.show(claim["action_id"])
            attempt = next((row for row in action.get("executions", []) if row.get("id") == claim["attempt_id"]), None)
            if (action.get("state") != "executing" or not attempt or attempt.get("state") != "executing"
                    or action.get("action_hash") != claim["action_hash"]):
                raise ClaimError("execution claim is no longer live")
            if self._dispatched(claim["action_id"], claim["attempt_id"]):
                raise ClaimError("attempt was already dispatched; reconcile it, never resend")
            self.ledger.event(claim["action_id"], DISPATCHED,
                              {"attempt_id": claim["attempt_id"], "dispatched_at": _now()})

    def settle(self, claim, state, status_code=None):
        """Record the actual outcome once. Unknown stays unknown; nothing is retried."""
        receipt = None
        if state != "outcome_unknown":
            receipt = {"kind": "tool_result", "reference": "workiq:" + claim["attempt_id"],
                       "outcome": state, "recorded_at": _now()}
            if status_code is not None:
                receipt["status_code"] = status_code
            if state == "failed":
                receipt["definitive_no_effect"] = True
        return self.ledger.finish(claim["attempt_id"], state, receipt)
