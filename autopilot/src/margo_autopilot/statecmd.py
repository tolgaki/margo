"""The state commands desktop Margo runs as scripts, offered to the hosted engine as one tool.

Desktop procedures run, for example::

    python3 scripts/work_state.py --account ACCOUNT list --view decisions

Hosted Margo calls ``margo-state`` with ``{"script": "work_state.py", "args": ["list", "--view",
"decisions"]}``. This module runs that same script as a separate process, with no shell, against
the instance's own account and state directory, so the procedures work unchanged and the core
stays the only code that touches the database.

What it enforces (fails closed):

- Only the scripts and subcommands classified in ``COMMANDS``. A subcommand added to a script is
  unavailable until it is classified here; the parity test fails until then.
- The account and state directory come from the instance binding. The model cannot name them.
- The run mode decides which classes run: none when someone other than the manager started the
  run; reads and Margo's own bookkeeping when unattended; recording a human decision only with
  the manager present. The execution journal waits for approval cards.
- No file paths from the model. Input arrives on stdin (``--input -``) or as named files
  (``@file:NAME``) written to a private directory that exists for one call.
- Bounded before it starts: argument and input sizes, a per-turn budget, a timeout and an output
  cap. A write that times out or crashes is reported as ``unknown``, never as failed or done.
- Receipts record script, subcommand, mode, status, exit code and duration; never arguments,
  input or output.
"""

import json
import os
import re
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path

from .guard import ATTENDED, THIRD_PARTY, UNATTENDED

READ, PRIVATE, HUMAN = "read", "private", "human"
MODE_CLASSES = {ATTENDED: {READ, PRIVATE, HUMAN}, UNATTENDED: {READ, PRIVATE}, THIRD_PARTY: set()}

# Subcommand classes. READ changes nothing. PRIVATE is Margo's own bookkeeping, which the design
# allows unattended (candidates, coverage, queues, run records, passive memory). HUMAN records or
# changes something the manager decided, so it runs only while the manager is in the conversation.
COMMANDS = {
    "work_state.py": {
        READ: {"list", "show", "history", "record-id"},
        PRIVATE: {"source", "ingest", "propose", "record", "recap-check", "carry-forward"},
        HUMAN: {"item-update", "relate", "edit", "defer", "dismiss"},
    },
    "proactive_state.py": {
        READ: {"seen", "queue-list", "publication-show", "publication-list", "coverage-status", "cursor-get",
               "status"},
        PRIVATE: {"mark", "queue-add", "queue-drain", "queue-ack", "queue-release", "queue-renew",
                  "publication-record", "publication-publish", "coverage-start", "coverage-page",
                  "coverage-finish", "coverage-retire", "coverage-resume", "cursor-set", "prune"},
        HUMAN: {"publication-review"},
    },
    "memory_state.py": {
        READ: {"status", "record-id", "show", "list", "inspect", "history", "forget-preview", "usage", "graph",
               "explain", "search", "context", "policy", "policy-preview", "tombstones", "tombstones-preview",
               "capabilities", "export-preview", "dream-inspect", "dream-status", "preferences-preview"},
        PRIVATE: {"capture", "record-usage", "index", "maintain", "consolidate", "record-consolidation",
                  "propose-lesson", "trend", "dream-checkpoint", "dream-plan", "dream-finish", "dream-start"},
        HUMAN: {"put", "revise", "forget", "link", "unlink", "policy-set", "tombstones-restore",
                "preferences-import", "validate-capability", "activate-lesson", "trend-definition"},
    },
    "task_state.py": {
        READ: {"health", "record-id", "list", "show", "history", "replan-preview"},
        PRIVATE: {"create", "start", "charge", "finish", "resume", "retry", "reconcile", "recover", "sync"},
        HUMAN: {"pause", "cancel", "replan"},
    },
    # Stateless helpers without subcommands: they compute over input and touch no state.
    "capacity.py": {READ: {None}},
    "teams_feedback.py": {READ: {None}},
    "engage_parse.py": {READ: {None}},
}
STATEFUL = {"work_state.py", "proactive_state.py", "memory_state.py", "task_state.py"}

_EXECUTION = "the execution journal arrives with approval cards; until then Margo proposes and the manager acts"
NOT_HOSTED_COMMANDS = {
    ("work_state.py", "approve"): "approvals arrive with approval cards: a hosted approval must come from the "
                                  "manager's authenticated card, not from chat text",
    ("work_state.py", "begin"): _EXECUTION,
    ("work_state.py", "finish"): _EXECUTION,
    ("work_state.py", "reconcile"): _EXECUTION,
    ("work_state.py", "artifact-shared"): _EXECUTION,
    ("work_state.py", "import-preview"): "hosted instances start on the work ledger; there is no legacy file to import",
    ("work_state.py", "import-commitments"): "hosted instances start on the work ledger; there is no legacy file "
                                             "to import",
    ("work_state.py", "export-commitments"): "writes a Markdown file; hosted Margo reads commitments from the ledger",
    ("proactive_state.py", "import-legacy"): "there is no legacy desktop state to import",
    ("memory_state.py", "init"): "the hosting service initialises private state",
    ("memory_state.py", "migrate"): "the hosting service migrates private state",
    ("memory_state.py", "export"): "writes a file outside private state",
    ("task_state.py", "init"): "the hosting service initialises private state",
}

FORBIDDEN_OPTIONS = ("--account", "--state-dir", "--state-root", "--config-dir")
FILE_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}")
MAX_ARGS, MAX_ARG, MAX_ARGS_TOTAL = 64, 4096, 16384
MAX_INPUT, MAX_FILES, MAX_FILES_TOTAL = 256 * 1024, 8, 1024 * 1024
MAX_OUTPUT = 60000
TIMEOUT_SECONDS = 60
ENV_PASSTHROUGH = ("PATH", "HOME", "LANG", "LC_ALL", "TZ", "MARGO_ALLOW_UNSAFE_STATE_DIR")


def command_class(script, command):
    for klass, commands in COMMANDS.get(script, {}).items():
        if command in commands:
            return klass
    return None


class _Refused(Exception):
    pass


class StateCommands:
    """Run classified state commands for one instance. Not thread-safe across calls of one turn."""

    def __init__(self, *, scripts_dir, account, state_root, preferences=None, budget=None, recorder=None,
                 timeout=TIMEOUT_SECONDS, python=None):
        self.scripts_dir = Path(scripts_dir)
        self.account, self.state_root = account, str(state_root)
        self.preferences = Path(preferences) if preferences else None
        self.budget, self.timeout = budget, timeout
        self.python = python or sys.executable
        self._recorder = recorder

    # -- public -------------------------------------------------------------------------

    def initialise(self):
        """Host-managed and idempotent: the schemas the skills expect before their first command."""
        for script in ("memory_state.py", "task_state.py"):
            outcome = self._execute(script, ["init"], None, {}, READ, mode="host")
            if outcome["status"] != "ok":
                raise RuntimeError("could not initialise %s: %s" % (script, outcome.get("reason")))

    def run(self, arguments, *, mode):
        """Return ``{"status", "script", "command", ...}``. Never raises; refusals are results."""
        script = arguments.get("script") if isinstance(arguments, dict) else None
        command = None
        try:
            if not isinstance(arguments, dict):
                raise _Refused("arguments must be an object")
            script, args = self._script_and_args(arguments)
            command = args[0] if None not in COMMANDS[script].get(READ, ()) else None
            klass = self._authorise(script, command, mode)
            files = self._files(arguments.get("files"))
            args = self._resolve(args, files)
            stdin = self._stdin(arguments.get("input"))
        except _Unavailable as exc:
            return self._finish(script, command, mode, "unavailable", reason=str(exc))
        except _Refused as exc:
            return self._finish(script, command, mode, "refused", reason=str(exc))
        if self.budget is not None and not self.budget.charge():
            return self._finish(script, command, mode, "budget_exhausted", reason="state command budget exhausted")
        return self._execute(script, args, stdin, files, klass, mode=mode)

    # -- policy -------------------------------------------------------------------------

    def _script_and_args(self, arguments):
        script = arguments.get("script")
        if isinstance(script, str) and script.startswith("scripts/"):
            script = script[len("scripts/"):]
        if isinstance(script, str) and not script.endswith(".py"):
            script += ".py"
        if script not in COMMANDS:
            from .parity import NOT_HOSTED_SCRIPTS

            if script in NOT_HOSTED_SCRIPTS:
                raise _Unavailable("%s is not available hosted: %s" % (script, NOT_HOSTED_SCRIPTS[script]))
            raise _Refused("unknown script")
        args = arguments.get("args", [])
        if not isinstance(args, list) or len(args) > MAX_ARGS or not all(isinstance(a, str) for a in args):
            raise _Refused("args must be a list of up to %d strings" % MAX_ARGS)
        if any(len(a) > MAX_ARG for a in args) or sum(len(a) for a in args) > MAX_ARGS_TOTAL:
            raise _Refused("arguments are too long")
        if None not in COMMANDS[script].get(READ, ()) and (not args or args[0].startswith("-")):
            raise _Refused("args must start with the subcommand, e.g. [\"list\", \"--view\", \"decisions\"]")
        for value in args:
            if any(value == option or value.startswith(option + "=") for option in FORBIDDEN_OPTIONS):
                raise _Refused("the service supplies the account and state directory; omit %s" % value.split("=")[0])
        return script, args

    @staticmethod
    def _authorise(script, command, mode):
        if (script, command) in NOT_HOSTED_COMMANDS:
            raise _Unavailable("%s %s is not available hosted: %s" % (script, command,
                                                                      NOT_HOSTED_COMMANDS[(script, command)]))
        klass = command_class(script, command)
        if klass is None:
            raise _Refused("unknown or unclassified subcommand")
        if mode not in MODE_CLASSES:
            raise _Refused("unknown run mode")
        if klass not in MODE_CLASSES[mode]:
            if mode == THIRD_PARTY:
                raise _Refused("a run started by someone other than the manager cannot use Margo's state")
            raise _Refused("this records a decision by the manager, so it needs the manager in the conversation")
        return klass

    @staticmethod
    def _files(files):
        if files is None:
            return {}
        if not isinstance(files, dict) or len(files) > MAX_FILES:
            raise _Refused("files must be an object of up to %d named files" % MAX_FILES)
        total = 0
        for name, content in files.items():
            if not isinstance(name, str) or not FILE_NAME.fullmatch(name) or not isinstance(content, str):
                raise _Refused("file names are letters, digits, '.', '_' or '-'; contents are text")
            total += len(content.encode("utf-8"))
        if total > MAX_FILES_TOTAL:
            raise _Refused("files are too large")
        return files

    def _resolve(self, args, files):
        """Replace ``@file:NAME`` and ``@preferences``; refuse anything that looks like a path."""
        resolved = []
        for value in args:
            option, value = (value.split("=", 1)[0] + "=", value.split("=", 1)[1]) \
                if value.startswith("--") and "=" in value else ("", value)
            if value.startswith("@file:"):
                name = value[len("@file:"):]
                if name not in files:
                    raise _Refused("no file named %r was passed in files" % name)
                resolved.append(("file", name, option))
            elif value == "@preferences":
                if self.preferences is None or not self.preferences.is_file():
                    raise _Unavailable("the manager's preferences are not set up yet")
                resolved.append(option + str(self.preferences))
            elif value.startswith(("/", "~", "./", "..", "\\")) or "/../" in value or "\x00" in value:
                raise _Refused("file paths are not available; pass content in files and use @file:NAME")
            else:
                resolved.append(option + value)
        return resolved

    @staticmethod
    def _stdin(value):
        if value is None:
            return None
        text = value if isinstance(value, str) else json.dumps(value, ensure_ascii=False)
        data = text.encode("utf-8")
        if len(data) > MAX_INPUT:
            raise _Refused("input is too large")
        return data

    # -- execution ----------------------------------------------------------------------

    def _environment(self):
        env = {key: os.environ[key] for key in ENV_PASSTHROUGH if key in os.environ}
        env.update({"PYTHONDONTWRITEBYTECODE": "1", "PYTHONIOENCODING": "utf-8"})
        return env

    def _execute(self, script, args, stdin, files, klass, *, mode):
        command = args[0] if script in STATEFUL and args else None
        started = time.monotonic()
        with tempfile.TemporaryDirectory(prefix="margo-state-") as scratch:
            final = []
            for value in args:
                if isinstance(value, tuple):
                    _, name, option = value
                    path = Path(scratch) / name
                    path.write_text(files[name], encoding="utf-8")
                    os.chmod(str(path), 0o600)
                    final.append(option + str(path))
                else:
                    final.append(value)
            argv = [self.python, "-s", str(self.scripts_dir / script)]
            if script in STATEFUL:
                argv += ["--account", self.account, "--state-dir", self.state_root]
            argv += final
            try:
                completed = subprocess.run(argv, input=stdin if stdin is not None else b"", capture_output=True,
                                           timeout=self.timeout, env=self._environment(), cwd=scratch)
            except subprocess.TimeoutExpired:
                status = "error" if klass == READ else "unknown"
                return self._finish(script, command, mode, status, started=started,
                                    reason="timed out after %ds%s" % (self.timeout, "" if klass == READ else
                                                                     "; the change may or may not have been saved"))
            except OSError as exc:  # nothing ran
                return self._finish(script, command, mode, "error", started=started,
                                    reason="could not start the command: %s" % type(exc).__name__)
        return self._outcome(script, command, mode, klass, completed, started)

    def _outcome(self, script, command, mode, klass, completed, started):
        out = completed.stdout.decode("utf-8", errors="replace")
        err = completed.stderr.decode("utf-8", errors="replace")
        truncated = len(out) > MAX_OUTPUT
        parsed = None
        if not truncated:
            try:
                parsed = json.loads(out) if out.strip() else None
            except ValueError:
                parsed = None
        reported = parsed if isinstance(parsed, dict) else None
        if completed.returncode == 0 and not (reported and "error" in reported):
            payload = {"result": parsed} if parsed is not None else {"text": out[:MAX_OUTPUT]}
            if truncated:
                payload["note"] = "Output was too large; only the beginning is shown. Narrow the command."
            return self._finish(script, command, mode, "partial" if truncated else "ok", started=started,
                                exit_code=completed.returncode, **payload)
        reason = (reported or {}).get("error") or _first_error(err) or "the command failed"
        # The scripts exit 2 after rolling back a refused change. Any other failure of a write
        # may have happened after a commit, so its effect is unknown.
        status = "error" if klass == READ or completed.returncode in (0, 2) else "unknown"
        return self._finish(script, command, mode, status, started=started, exit_code=completed.returncode,
                            reason=str(reason)[:2000])

    def _finish(self, script, command, mode, status, *, started=None, exit_code=None, reason=None, **payload):
        receipt = {"at": datetime.now(timezone.utc).isoformat(timespec="microseconds"), "tool": "margo-state",
                   "script": script if isinstance(script, str) else None,
                   "command": command if isinstance(command, str) else None,
                   "mode": mode if isinstance(mode, str) else None, "status": status}
        if exit_code is not None:
            receipt["exit_code"] = exit_code
        if started is not None:
            receipt["ms"] = int((time.monotonic() - started) * 1000)
        if self._recorder is not None:
            self._recorder(dict(receipt))
        response = {"status": status, "script": receipt["script"], "command": receipt["command"]}
        if reason:
            response["reason"] = reason
        response.update(payload)
        return response


class _Unavailable(_Refused):
    """Classified as not hosted yet; reported as unavailable, not as a refusal or an empty result."""


def _first_error(stderr):
    for line in stderr.strip().splitlines()[::-1]:
        line = line.strip()
        if not line:
            continue
        try:
            value = json.loads(line)
        except ValueError:
            return line[:500]
        if isinstance(value, dict) and value.get("error"):
            return value["error"]
        return line[:500]
    return None
