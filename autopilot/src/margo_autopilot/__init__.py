"""Offline building blocks for a hosted Margo autopilot (design stage, not deployed).

Standard library only. Nothing here authenticates, provisions or calls a network service;
token providers and MCP transports are supplied by a hosting runtime that does not yet exist.
"""

from .guard import (  # noqa: F401
    ATTENDED,
    MARGO,
    PRINCIPAL,
    SELF,
    THIRD_PARTY,
    UNATTENDED,
    WORKIQ_MCP_URL,
    WORKIQ_RESOURCE,
    CallBudget,
    GuardConfigError,
    TokenUnavailable,
    TransportError,
    TransportTimeout,
    WorkIQGuard,
    model_tools,
)
from .instance import InstanceBinding  # noqa: F401
from .replies import ReplyGate, load_reply_policy  # noqa: F401
