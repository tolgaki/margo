"""Handle one inbound activity for a hired Margo instance. No SDK imports; the host adapts.

Order matters, and each step fails closed:

1. Get Margo's own token and look up her manager in the directory (``/me/manager``). The manager
   is who she works for; a message never decides that.
2. Compare the authenticated sender's object ID with the manager's.
3. Only for the manager, request a principal token. On-behalf-of exchanges the *sender's*
   token, so asking for it on anyone else's message would hand the guard a colleague's token
   labelled as the manager's.
4. Run the turn under the instance's state lease (``StateSync``).
5. Messages from anyone else are queued as untrusted candidate requests; any reply is decided by
   the manager's ``Replies to other people`` setting, enforced by ``ReplyGate``.
"""

import asyncio
import hashlib
import os
import re
from pathlib import Path

from .guard import ATTENDED, UNATTENDED, CallBudget, TokenUnavailable, WorkIQGuard
from .instance import InstanceBinding
from .office import OfficeQueue
from .replies import ACKNOWLEDGE, APPROVE, NONE, SETTING_LABEL, ReplyGate, load_reply_policy
from .statesync import StateSync, SyncError, container_name

TURN_CALL_BUDGET = 40
REPLIES_COMMAND = re.compile(r"^\s*/replies\s+(none|acknowledge|approve)\s*$", re.IGNORECASE)
NO_MANAGER = ("I can't find my manager in the directory, so I don't know who I work for yet. "
              "An administrator needs to set my manager before I can help.")
STATE_UNSAFE = ("I couldn't load my records safely, so I've stopped rather than work from a partial "
                "memory. Nothing was sent. Try again shortly; if it persists, an operator should check "
                "my state storage.")


class TurnTokens:
    """Tokens for one turn. A missing token raises ``TokenUnavailable``; there is no fallback."""

    def __init__(self, tokens):
        self._tokens = dict(tokens)

    def token_for(self, subject):
        value = self._tokens.get(subject)
        if not value:
            raise TokenUnavailable("%s token not available for this turn" % subject)
        return value


def _manager_from(result):
    """Extract ``(id, displayName)`` from a Work IQ fetch of ``/me/manager``."""
    if result.get("status") != "ok":
        return None
    data = result.get("data") or {}
    candidates = [data]
    for entry in data.get("results", []) if isinstance(data.get("results"), list) else []:
        if isinstance(entry, dict):
            candidates.append(entry.get("data") or {})
    if isinstance(data.get("data"), dict):
        candidates.append(data["data"])
    for candidate in candidates:
        if isinstance(candidate, dict) and isinstance(candidate.get("id"), str) and candidate["id"]:
            return candidate["id"], candidate.get("displayName") or "your manager"
    return None


def set_reply_mode(preferences, mode):
    """Write the single setting line, preserving the rest of the manager's preferences file."""
    path = Path(preferences)
    line = "- **%s:** %s" % (SETTING_LABEL, mode)
    text = path.read_text(encoding="utf-8") if path.is_file() else ""
    pattern = re.compile(r"^[ \t]*[-*][ \t]+\*\*Replies to other people:\*\*.*$", re.MULTILINE)
    text = pattern.sub(line, text) if pattern.search(text) else (text.rstrip("\n") + "\n" + line + "\n").lstrip("\n")
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    os.chmod(str(path), 0o600)


class Orchestrator:
    def __init__(self, *, engine, transport, state_root, store_for, principal_access="obo",
                 recorder=None):
        self.engine, self.transport = engine, transport
        self.state_root = Path(state_root)
        self.store_for = store_for
        self.principal_access = principal_access
        self.recorder = recorder
        self._conversations = {}

    def _paths(self, account):
        import margo_store  # the core's own path rules: hashed, private, never a synced folder

        _, database = margo_store.state_path(account, str(self.state_root))
        return Path(database), Path(database).parent / "preferences.md"

    async def _manager(self, event, margo_token):
        """Look the manager up on every turn: a re-org takes effect immediately, and a former
        manager stops being treated as the principal. A changed manager is a different account,
        so the previous person's ledger and memory are never opened for the new one."""
        bootstrap = InstanceBinding(event["agent_identity_id"], event["agent_user_id"],
                                    "unbound-manager", "unbound-chat")
        guard = WorkIQGuard(bootstrap, TurnTokens({"margo": margo_token}), self.transport,
                            budget=CallBudget(1), recorder=self.recorder)
        result = await asyncio.to_thread(guard.call, "fetch",
                                         {"on_data_of": "self", "entityUrls": ["/me/manager?$select=id,displayName"]},
                                         mode=UNATTENDED)
        return _manager_from(result)

    async def handle(self, event, get_token):
        """``event``: agent_identity_id, agent_user_id, sender_id, sender_name, conversation_id,
        activity_id, channel, text. ``get_token(kind)``: awaitable returning a token or None.
        Returns ``{"reply": str|None, "status": str}``."""
        try:
            margo_token = await get_token("margo")
        except Exception:
            margo_token = None
        if not margo_token:
            return {"reply": None, "status": "no_agent_token"}
        manager = await self._manager(event, margo_token)
        sender = event.get("sender_id")
        if not manager:
            return {"reply": NO_MANAGER if sender else None, "status": "no_manager"}
        manager_id, manager_name = manager
        # Mail is never a conversation turn in this version, even from the manager: it is queued.
        is_manager = isinstance(sender, str) and sender == manager_id and event.get("channel") != "mail"
        chat_id = event["conversation_id"] if is_manager else "unbound-chat"
        try:
            binding = InstanceBinding(event["agent_identity_id"], event["agent_user_id"], manager_id, chat_id)
        except ValueError:
            return {"reply": None, "status": "invalid_binding"}
        database, preferences = self._paths(binding.account)
        sync = StateSync(database, self.store_for(container_name(binding.account)),
                         account=binding.account, preferences=preferences)
        tokens = {"margo": margo_token}
        if is_manager and self.principal_access == "obo":
            try:
                tokens["principal"] = await get_token("principal")
            except Exception:
                tokens["principal"] = None

        async def work():
            ledger = binding.open_ledger(str(self.state_root))
            try:
                if is_manager:
                    return await self._manager_turn(event, binding, ledger, tokens, preferences)
                return self._third_party_turn(event, binding, ledger, preferences, manager_name)
            finally:
                ledger.close()

        try:
            outcome = await sync.run(work)
        except SyncError:
            return {"reply": STATE_UNSAFE if is_manager else None, "status": "state_unsafe"}
        return outcome["result"]

    async def _manager_turn(self, event, binding, ledger, tokens, preferences):
        command = REPLIES_COMMAND.match(event.get("text") or "")
        if command:
            mode = command.group(1).lower()
            set_reply_mode(preferences, mode)
            note = {NONE: "I won't reply to anyone else; their messages wait for you.",
                    ACKNOWLEDGE: "I'll send one short 'passed this on' note per conversation, nothing more.",
                    APPROVE: ("Noted, but approval cards aren't in this version yet, so until they are I'll "
                              "treat this as 'none'.")}[mode]
            return {"reply": note, "status": "setting_changed"}
        guard = WorkIQGuard(binding, TurnTokens(tokens), self.transport, budget=CallBudget(TURN_CALL_BUDGET),
                            principal_access=self.principal_access, recorder=self.recorder)
        key = hashlib.sha256((binding.account + "|" + event["conversation_id"]).encode("utf-8")).hexdigest()
        result = await self.engine.run(event.get("text") or "", mode=ATTENDED, guard=guard,
                                       office=OfficeQueue(ledger), previous_response_id=self._conversations.get(key),
                                       preferences_path=preferences)
        if result.get("response_id"):
            self._conversations[key] = result["response_id"]
        else:
            self._conversations.pop(key, None)
        return {"reply": result.get("text") or None, "status": "stopped" if result.get("stopped") else "answered"}

    def _third_party_turn(self, event, binding, ledger, preferences, manager_name):
        office = OfficeQueue(ledger)
        first = not office.seen_before(event.get("sender_id"), event["conversation_id"])
        office.record(channel=event.get("channel") or "teams", external_id=event["activity_id"],
                      sender_id=event.get("sender_id"), sender_name=event.get("sender_name"),
                      text=event.get("text"), conversation_id=event["conversation_id"])
        policy = load_reply_policy(preferences.read_text(encoding="utf-8") if preferences.is_file() else None)
        mode = policy["mode"] if policy["mode"] != APPROVE else NONE
        if event.get("channel") == "mail":
            mode = NONE  # v1 does not answer mail from other people at all
        decision = ReplyGate(binding, mode).decide(event.get("sender_id"), event["conversation_id"], None,
                                                   manager_name=manager_name, first_in_conversation=first)
        return {"reply": decision.get("text") if decision.get("send") else None, "status": "queued"}
