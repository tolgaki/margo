"""Identity binding for one hired autopilot instance, and its private-state seam.

An instance serves exactly one principal (its manager). Its private state reuses the existing
account-isolated store: the account key combines the instance's agent identity and the
principal's directory object ID, so two instances, or one instance re-bound to another
person, can never open each other's ledger or memory.
"""

import re
from dataclasses import dataclass

_ID = re.compile(r"[^\s\x00-\x1f\x7f{}/?#]{1,256}")


class BindingError(ValueError):
    """The instance binding is incomplete or ambiguous; refuse to run."""


def _check(name, value):
    if not isinstance(value, str) or not _ID.fullmatch(value):
        raise BindingError("%s must be an explicit opaque identifier" % name)
    return value


@dataclass(frozen=True)
class InstanceBinding:
    """Recorded at hire time from authenticated platform data, never from message content."""

    agent_identity_id: str
    agent_user_id: str
    principal_id: str
    principal_chat_id: str

    def __post_init__(self):
        for name in ("agent_identity_id", "agent_user_id", "principal_id", "principal_chat_id"):
            _check(name, getattr(self, name))
        if self.principal_id == self.agent_user_id:
            raise BindingError("the principal cannot be the agent's own user account")

    @property
    def account(self):
        """Private-state account key; deliberately not an email address or display name."""
        return "autopilot:%s:%s" % (self.agent_identity_id, self.principal_id)

    def is_principal(self, sender_id):
        """Authority comes from the authenticated activity's object ID, never a name or header."""
        return isinstance(sender_id, str) and sender_id == self.principal_id

    def open_ledger(self, state_root):
        """Open this instance's work ledger through the existing public API."""
        import work_ledger  # resolved from skills/chief-of-staff/scripts by the host

        return work_ledger.Ledger(account=self.account, state_root=state_root)
