"""Replies to people other than the manager: a principal-owned setting, enforced in code.

Messages from anyone except the manager are data. Whether Margo answers them is the manager's
choice, written in their private preferences file as one line::

    - **Replies to other people:** none

The hosting runtime reads that line with ``load_reply_policy`` and passes the mode to
``ReplyGate``. The model may also read the setting so it can explain itself, but the gate,
not the model, decides what leaves. Replies travel on the activity channel, which the Work IQ
guard does not cover, so this gate is their only check.

Modes:

``none`` (default)
    No reply. The message is queued for the manager.
``acknowledge``
    One fixed, content-free acknowledgement per conversation. Model text is discarded, so no
    information about the manager can leak through it.
``approve``
    Margo drafts a reply; it is sent only after the manager approves that exact text for that
    exact recipient, using the same approval journal as every other outward action.

There is no autonomous mode. Answering colleagues freely from the manager's data is a
different authority and needs its own design and review.
"""

import re

NONE, ACKNOWLEDGE, APPROVE = "none", "acknowledge", "approve"
MODES = (NONE, ACKNOWLEDGE, APPROVE)
SETTING_LABEL = "Replies to other people"
ACKNOWLEDGEMENT = "Thanks. I've passed this on to {manager}, who will reply if needed."

_SETTING = re.compile(r"^[ \t]*[-*][ \t]+\*\*Replies to other people:\*\*[ \t]*`?([A-Za-z_-]+)`?[ \t]*$",
                      re.MULTILINE)


def load_reply_policy(markdown):
    """Return ``{"mode", "issues"}``. Anything missing, unknown or conflicting means ``none``."""
    if not isinstance(markdown, str):
        return {"mode": NONE, "issues": ["preferences unavailable"]}
    values = [value.lower() for value in _SETTING.findall(markdown)]
    if not values:
        return {"mode": NONE, "issues": ["setting not present; defaulting to none"]}
    if len(set(values)) > 1:
        return {"mode": NONE, "issues": ["conflicting settings; defaulting to none"]}
    if values[0] not in MODES:
        return {"mode": NONE, "issues": ["unknown value '%s'; defaulting to none" % values[0]]}
    return {"mode": values[0], "issues": []}


class ReplyGate:
    """Decide whether one reply to a non-manager may be sent, and with what exact text."""

    def __init__(self, binding, mode, *, journal=None):
        if mode not in MODES:
            raise ValueError("unknown reply mode")
        self.binding, self.mode, self.journal = binding, mode, journal
        self._acknowledged = set()

    def decide(self, sender_id, conversation_id, proposed_text, *, manager_name,
               action_id=None, attempt_id=None, first_in_conversation=None):
        """Return ``{"send": bool, "text"?, "claim"?, "reason"?}``.

        ``first_in_conversation`` lets the host supply durable knowledge of earlier messages, so
        an acknowledgement is sent once per conversation across restarts and sandboxes.
        """
        if not isinstance(sender_id, str) or not sender_id or sender_id == self.binding.agent_user_id:
            return {"send": False, "reason": "unknown or self sender"}
        if self.binding.is_principal(sender_id):
            return {"send": True, "text": proposed_text, "reason": "conversation with the manager"}
        if self.mode == NONE:
            return {"send": False, "reason": "replies to other people are off; queued for the manager"}
        if self.mode == ACKNOWLEDGE:
            key = (sender_id, conversation_id)
            seen = key in self._acknowledged or first_in_conversation is False
            if not isinstance(conversation_id, str) or not conversation_id or seen:
                return {"send": False, "reason": "already acknowledged in this conversation"}
            if not isinstance(manager_name, str) or not manager_name.strip():
                return {"send": False, "reason": "manager display name unavailable"}
            self._acknowledged.add(key)
            return {"send": True, "text": ACKNOWLEDGEMENT.format(manager=manager_name.strip())}
        return self._approved(sender_id, proposed_text, action_id, attempt_id)

    def _approved(self, sender_id, proposed_text, action_id, attempt_id):
        if self.journal is None:
            return {"send": False, "reason": "no approval journal configured"}
        try:
            claim = self.journal.load(action_id, attempt_id, call_key="reply")
        except Exception as exc:
            return {"send": False, "reason": "needs the manager's approval of this exact reply: %s" % exc}
        if claim.get("account") != self.binding.account or claim.get("approved_by") != self.binding.principal_id:
            return {"send": False, "reason": "the approval was not given by this instance's manager"}
        if claim.get("call") != {"to": sender_id, "text": proposed_text}:
            return {"send": False, "reason": "reply differs from the approved text or recipient"}
        try:
            self.journal.mark_dispatched(claim)
        except Exception:
            return {"send": False, "reason": "could not record dispatch; nothing was sent"}
        return {"send": True, "text": proposed_text, "claim": claim}

    def settle(self, claim, state, status_code=None):
        """Record the actual outcome after the runtime sends (or fails to send) the reply."""
        return self.journal.settle(claim, state, status_code)
