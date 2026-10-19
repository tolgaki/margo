"""Handle one inbound activity for a hired Margo instance. No SDK imports; the host adapts.

Order matters, and each step fails closed:

1. Get Margo's own token and look up her manager in the directory (``/me/manager``). The manager
   is who she works for; a message never decides that. A failed lookup is reported as a failure,
   never as "no manager".
2. Compare the authenticated sender's object ID with the manager's.
3. The manager is served only in their one-to-one chat. In a group chat or channel a turn would
   read their private data and answer in front of everyone, so it is refused with a fixed note.
4. Only for the manager, request a principal token. On-behalf-of exchanges the *sender's*
   token, so asking for it on anyone else's message would hand the guard a colleague's token
   labelled as the manager's.
5. Run the turn under the instance's state lease (``StateSync``). Ledger work runs off the event
   loop, on the thread that opened the connection.
6. Messages from anyone else are queued as untrusted candidate requests; any reply is decided by
   the manager's ``Replies to other people`` setting, enforced by ``ReplyGate``.

The only other text that leaves is a fixed notice (no manager, lookup failed, group refused,
turn failed). Notices name no one and carry no data, and they go only to a one-to-one Teams chat.
"""

import asyncio
import hashlib
import logging
import os
import re
from pathlib import Path

from .guard import ATTENDED, UNATTENDED, CallBudget, TokenUnavailable, WorkIQGuard, _status_codes
from .instance import BindingError, InstanceBinding
from .instances import instance_key, resource_names
from .office import OfficeQueue, OfficeReader
from .replies import ACKNOWLEDGE, APPROVE, NONE, SETTING_LABEL, ReplyGate, load_reply_policy
from .statecmd import StateCommands
from .statesync import StateSync, SyncError, container_name

logger = logging.getLogger("margo.autopilot")

TURN_CALL_BUDGET = 40
STATE_CALL_BUDGET = 40
PERSONAL = "personal"  # Teams conversationType of a one-to-one chat
REPLIES_COMMAND = re.compile(r"^\s*/replies\s+(none|acknowledge|approve)\s*$", re.IGNORECASE)
VOICE_COMMAND = re.compile(r"^\s*/voice\s*$", re.IGNORECASE)
VOICE_UNAVAILABLE = "Voice isn't set up for this deployment yet, so we can only talk here for now."
NO_MANAGER = ("I can't find my manager in the directory, so I don't know who I work for yet. "
              "An administrator needs to set my manager before I can help.")
MANAGER_UNKNOWN = ("I couldn't check who I work for just now (the directory lookup failed), so I haven't "
                   "acted on this message. Please try again shortly.")
GROUP_REFUSED = ("I only work with you in our one-to-one chat. I don't take part in group chats or "
                 "channels, so I haven't read or shared anything here. Message me directly.")
STATE_UNSAFE = ("I couldn't load my records safely, so I've stopped rather than work from a partial "
                "memory. Nothing was sent. Try again shortly; if it persists, an operator should check "
                "my state storage.")
TURN_FAILED = ("Something failed on my side before I could finish, so I stopped. Nothing was sent. "
               "Try again; if it keeps happening, an operator should check my logs.")


class ManagerLookupFailed(Exception):
    """The directory lookup did not succeed; that is not evidence that there is no manager."""


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
    """Return ``(id, displayName)`` from a Work IQ fetch of ``/me/manager``, or ``None`` when the
    directory answered that there is no manager (404). Any other non-ok result raises
    ``ManagerLookupFailed``: an outage, throttling or a billing denial says nothing about who
    manages Margo."""
    data = result.get("data") if isinstance(result.get("data"), dict) else {}
    if result.get("status") == "error":
        codes = _status_codes(data)
        if codes and all(code == 404 for code in codes):
            return None
    if result.get("status") != "ok":
        raise ManagerLookupFailed(result.get("status") or "no status")
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
                 recorder=None, instance=None, state_container=None, voice_url=None, per_instance_state=False):
        """``instance``: in per-instance hosting, the one ``(agent_identity_id, agent_user_id)`` this
        container serves; anything else is refused. ``state_container``: that instance's own Blob
        container, holding each manager's state under its own prefix. ``per_instance_state``: the
        Foundry-hosted Margo keeps each instance's state in that same per-instance container, so the
        instance's own container and the Foundry agent share one state (hybrid hosting).
        ``voice_url``: the voice page; ``{instance}`` is replaced by the instance key."""
        self.engine, self.transport = engine, transport
        self.state_root = Path(state_root)
        self.store_for = store_for
        self.principal_access = principal_access
        self.recorder = recorder
        self.instance, self.state_container, self.voice_url = instance, state_container, voice_url
        self.per_instance_state = per_instance_state
        self._conversations = {}
        self._initialised = set()

    def _paths(self, account):
        import margo_store  # the core's own path rules: hashed, private, never a synced folder

        _, database = margo_store.state_path(account, str(self.state_root))
        return Path(database), Path(database).parent / "preferences.md"

    async def _manager(self, event, margo_token):
        """Look the manager up on every turn: a re-org takes effect immediately, and a former
        manager stops being treated as the principal. A changed manager is a different account,
        so the previous person's ledger and memory are never opened for the new one."""
        bootstrap = InstanceBinding(event["agent_identity_id"], event["agent_user_id"], "unbound-manager")
        guard = WorkIQGuard(bootstrap, TurnTokens({"margo": margo_token}), self.transport,
                            budget=CallBudget(1), recorder=self.recorder)
        result = await asyncio.to_thread(guard.call, "fetch",
                                         {"on_data_of": "self", "entityUrls": ["/me/manager?$select=id,displayName"]},
                                         mode=UNATTENDED)
        return _manager_from(result)

    async def handle(self, event, get_token):
        """``event``: agent_identity_id, agent_user_id, sender_id, sender_name, conversation_id,
        conversation_type, activity_id, channel, text. ``get_token(kind)``: awaitable returning a
        token or None. Returns ``{"reply": str|None, "status": str}``; it does not raise."""
        try:
            margo_token = await get_token("margo")
        except Exception:
            margo_token = None
        if not margo_token:
            return {"reply": None, "status": "no_agent_token"}
        if self.instance is not None and (event.get("agent_identity_id"), event.get("agent_user_id")) != self.instance:
            return {"reply": None, "status": "wrong_instance"}  # defence in depth behind the router
        sender = event.get("sender_id")
        one_to_one = event.get("channel") != "mail" and event.get("conversation_type") == PERSONAL
        try:
            manager = await self._manager(event, margo_token)
        except BindingError:
            return {"reply": None, "status": "invalid_binding"}
        except Exception as exc:  # ManagerLookupFailed, or a guard/transport configuration error
            logger.warning("manager lookup failed: %s", exc if isinstance(exc, ManagerLookupFailed) else type(exc).__name__)
            return {"reply": MANAGER_UNKNOWN if one_to_one and sender else None, "status": "manager_lookup_failed"}
        if not manager:
            return {"reply": NO_MANAGER if one_to_one and sender else None, "status": "no_manager"}
        manager_id, manager_name = manager
        # Mail is never a conversation turn in this version, even from the manager: it is queued.
        is_manager = isinstance(sender, str) and sender == manager_id and event.get("channel") != "mail"
        if is_manager and not one_to_one:
            return {"reply": GROUP_REFUSED, "status": "group_refused"}
        try:
            # No principal chat: the activity's conversation ID is not the Graph chat ID recorded at
            # hire, so the unattended principal-chat post stays unavailable in this version.
            binding = InstanceBinding(event["agent_identity_id"], event["agent_user_id"], manager_id)
        except ValueError:
            return {"reply": None, "status": "invalid_binding"}
        try:
            return await self._run(event, get_token, binding, is_manager, manager_name, margo_token)
        except SyncError:
            return {"reply": STATE_UNSAFE if is_manager else None, "status": "state_unsafe"}
        except Exception:
            logger.exception("turn failed")
            return {"reply": TURN_FAILED if is_manager else None, "status": "error"}

    def state_sync(self, binding):
        """The leased state for one instance and manager, wherever this deployment keeps it."""
        database, preferences = self._paths(binding.account)
        container = self.state_container
        if container is None and self.per_instance_state:
            container = resource_names(instance_key(binding.agent_identity_id, binding.agent_user_id))["container"]
        if container:
            prefix = hashlib.sha256(binding.account.encode("utf-8")).hexdigest()[:40] + "/"
            return StateSync(database, self.store_for(container), account=binding.account,
                             preferences=preferences, prefix=prefix), preferences
        return StateSync(database, self.store_for(container_name(binding.account)), account=binding.account,
                         preferences=preferences), preferences

    async def _run(self, event, get_token, binding, is_manager, manager_name, margo_token):
        sync, preferences = self.state_sync(binding)
        tokens = {"margo": margo_token}
        if is_manager and self.principal_access == "obo":
            try:
                tokens["principal"] = await get_token("principal")
            except Exception:
                tokens["principal"] = None

        async def work():
            if is_manager:
                # Create the store if needed, so this turn saves (a changed setting included).
                await asyncio.to_thread(lambda: binding.open_ledger(str(self.state_root)).close())
                if binding.account not in self._initialised:
                    await asyncio.to_thread(self._state_commands(binding, preferences).initialise)
                    self._initialised.add(binding.account)
                return await self._manager_turn(event, binding, tokens, preferences)
            return await asyncio.to_thread(self._third_party_turn, event, binding, preferences, manager_name)

        outcome = await sync.run(work)
        return outcome["result"]

    def _state_commands(self, binding, preferences, budget=None):
        """The skill scripts, run for this instance's account only, inside its state lease."""
        return StateCommands(scripts_dir=Path(self.engine.repo_root) / "skills" / "chief-of-staff" / "scripts",
                             account=binding.account, state_root=self.state_root, preferences=preferences,
                             budget=budget, recorder=self.recorder)

    async def _manager_turn(self, event, binding, tokens, preferences):
        if VOICE_COMMAND.match(event.get("text") or ""):
            if not self.voice_url:
                return {"reply": VOICE_UNAVAILABLE, "status": "voice_link"}
            url = self.voice_url.replace("{instance}", instance_key(binding.agent_identity_id, binding.agent_user_id))
            return {"reply": "Talk to me here: %s" % url, "status": "voice_link"}
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
        office = OfficeReader(lambda: binding.open_ledger(str(self.state_root)))
        state = self._state_commands(binding, preferences, budget=CallBudget(STATE_CALL_BUDGET))
        result = await self.engine.run(event.get("text") or "", mode=ATTENDED, guard=guard, office=office,
                                       state=state, previous_response_id=self._conversations.get(key),
                                       preferences_path=preferences)
        if result.get("response_id"):
            self._conversations[key] = result["response_id"]
        else:
            self._conversations.pop(key, None)
        return {"reply": result.get("text") or None, "status": "stopped" if result.get("stopped") else "answered"}

    def _third_party_turn(self, event, binding, preferences, manager_name):
        """Runs in a worker thread: the ledger is opened, used and closed on this thread."""
        policy = load_reply_policy(preferences.read_text(encoding="utf-8") if preferences.is_file() else None)
        mode = policy["mode"] if policy["mode"] != APPROVE else NONE
        if event.get("channel") == "mail":
            mode = NONE  # v1 does not answer mail from other people at all
        ledger = binding.open_ledger(str(self.state_root))
        try:
            office = OfficeQueue(ledger)
            # Only an acknowledgement depends on earlier messages; otherwise skip the lookup.
            first = not office.seen_before(event.get("sender_id"), event["conversation_id"]) \
                if mode == ACKNOWLEDGE else None
            office.record(channel=event.get("channel") or "teams", external_id=event["activity_id"],
                          sender_id=event.get("sender_id"), sender_name=event.get("sender_name"),
                          text=event.get("text"), conversation_id=event["conversation_id"])
        finally:
            ledger.close()
        decision = ReplyGate(binding, mode).decide(event.get("sender_id"), event["conversation_id"], None,
                                                   manager_name=manager_name, first_in_conversation=first)
        return {"reply": decision.get("text") if decision.get("send") else None, "status": "queued"}
