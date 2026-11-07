"""Answer generated cases with the Python orchestrator helpers, the specification, as JSON.

Run by the C# tests (python3 -B -I orchestrator_differential.py REPO_ROOT SCRATCH < cases.json). Each case names a
function and its inputs; the answer is the function's result, or the exception's class name. Fictional values only;
reads and writes nothing outside SCRATCH and makes no network call.
"""

import json
import os
import sys

root, scratch = sys.argv[1], sys.argv[2]
sys.path.insert(0, root + "/skills/chief-of-staff/scripts")
sys.path.insert(0, root + "/autopilot/src")

from margo_autopilot import office, orchestrator, replies  # noqa: E402
from margo_autopilot.instance import InstanceBinding  # noqa: E402

BINDING = InstanceBinding("agent-identity-1", "margo-user-1", "principal-1")


def answer(case):
    kind = case["kind"]
    if kind == "policy":
        return replies.load_reply_policy(case["text"])
    if kind == "commands":
        text = case["text"]
        replies_match = orchestrator.REPLIES_COMMAND.match(text or "")
        return {"voice": bool(orchestrator.VOICE_COMMAND.match(text or "")),
                "mode": replies_match.group(1).lower() if replies_match else None}
    if kind == "set_reply_mode":
        path = os.path.join(scratch, "case-%d" % case["index"], "preferences.md")
        if case["text"] is not None:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as stream:
                stream.write(case["text"].encode("utf-8", "surrogatepass"))
        orchestrator.set_reply_mode(path, case["mode"])
        with open(path, "rb") as stream:
            return stream.read().decode("utf-8")
    if kind == "manager":
        found = orchestrator._manager_from(case["result"])
        return list(found) if found else None
    if kind == "excerpt":
        text = case["text"]
        return {"excerpt": (text or "").strip()[:office.EXCERPT_LIMIT] or "(no text)",
                "key": office._conversation_key(case["sender"], case["conversation"])}
    if kind == "gate":
        gate = replies.ReplyGate(BINDING, case["mode"])
        decisions = []
        for step in case["steps"]:
            decision = gate.decide(step["sender"], step["conversation"], step["proposed"],
                                   manager_name=step["manager"], first_in_conversation=step["first"])
            decisions.append(decision)
        return decisions
    raise ValueError("unknown case kind")


answers = []
for case in json.loads(sys.stdin.buffer.read().decode("utf-8", "surrogatepass")):
    try:
        answers.append({"ok": answer(case)})
    except Exception as exc:  # the class is the observable behaviour; messages differ between runtimes
        answers.append({"error": type(exc).__name__})
sys.stdout.buffer.write(json.dumps(answers).encode("ascii"))
