"""What desktop Margo can do that hosted Margo cannot do yet, and why. One list, used twice.

Hosted Margo is the hosted, always-on version of desktop Margo: the same persona and the same
skills, loaded unchanged. Every difference must be named here with a reason. The engine tells the
model this list, so she reports a missing capability instead of treating it as an empty result,
and ``tests/test_autopilot_parity.py`` fails when a skill uses something that is neither hosted
nor listed here.
"""

from .guard import WRITE_TOOLS
from .statecmd import NOT_HOSTED_COMMANDS

# Programs the procedures run in a desktop shell. Hosted Margo has no shell; each becomes a
# narrow tool once its credential is decided.
NOT_HOSTED_PROGRAMS = {
    "gh": "GitHub review load needs a GitHub credential for hosted Margo; not decided yet",
    "az": "the Azure CLI signs in as the desktop user; Azure DevOps work items need their own credential for "
          "hosted Margo, not decided yet",
    "curl": "raw HTTP from a shell; the procedures use it for Azure DevOps work items, which need a credential first",
    "jq": "shell JSON filtering for Azure DevOps responses; arrives with the Azure DevOps tool",
    "git": "the team decision registry needs repository access for hosted Margo; not decided yet",
}

# Scripts that are about the desktop installation, or need their own sign-in.
NOT_HOSTED_SCRIPTS = {
    "margo_doctor.py": "checks the desktop installation; hosted health comes from the service's logs",
    "margo_store.py": "the hosting service sets up the account and private state",
    "m365_files.py": "the large-file bridge has its own browser sign-in; a hosted bridge through the guard "
                     "is not built yet",
    "memory_encoder.py": "optional local embeddings are not installed in the hosted image",
}

NOT_HOSTED_WORKIQ = {tool: "outward actions arrive with approval cards; until then Margo drafts and the manager "
                           "sends" for tool in sorted(WRITE_TOOLS)}


def summary():
    """Markdown lines for the model: what is missing, and what to say instead."""
    lines = ["- Work IQ writes (%s): %s." % (", ".join("`workiq-%s`" % t for t in NOT_HOSTED_WORKIQ),
                                             next(iter(NOT_HOSTED_WORKIQ.values())))]
    for name, reason in sorted(NOT_HOSTED_PROGRAMS.items()):
        lines.append("- `%s`: %s." % (name, reason))
    for name, reason in sorted(NOT_HOSTED_SCRIPTS.items()):
        lines.append("- `%s`: %s." % (name, reason))
    for (script, command), reason in sorted(NOT_HOSTED_COMMANDS.items()):
        lines.append("- `%s %s`: %s." % (script, command, reason))
    return "\n".join(lines)
