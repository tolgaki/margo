#!/usr/bin/env python3
"""Differential judge: Margo Cloud's C# state commands versus the Python helpers.

Margo desktop runs the Python state helpers (``skills/chief-of-staff/scripts``);
Margo Cloud runs their C# ports through ``margo-state SCRIPT.py ARGS...``. Both
must be indistinguishable. This tool runs every scenario in
``cloud/contracts/state/**/*.json`` (schema: ``cloud/contracts/state/README.md``)
three times, each in a fresh private temporary root: Python, C#, then Python
again, with ``TZ=UTC LC_ALL=C.UTF-8 PYTHONHASHSEED=0``. For every step it
compares the exit status, the normalised stdout and whether stderr is empty; at
the end it compares a normalised dump of every SQLite database below the root
(schema SQL, ``PRAGMA user_version``, rows) and the file tree (paths, mode bits,
content hashes of non-database files).

Volatile values: ISO timestamps, UUIDs, long decimal numbers and hexadecimal
runs of 8+ characters are tokens. A token that differs between the two Python
runs at the same position is volatile and becomes a placeholder numbered by
first occurrence in each run (with its shape, so a format change is still
caught). A token that is identical in both Python runs is deterministic and
must match the C# output literally, so a wrong hash or a wrong normalised
timestamp is caught. When the two Python runs cannot be aligned token by token,
every token is normalised and the report says so.

Usage::

    python3 tools/state_parity.py --check            # all scenarios; exit 1 on any mismatch
    python3 tools/state_parity.py --filter store -v  # unified diff of the first mismatch
    python3 tools/state_parity.py --scenario cloud/contracts/state/core/margo_store_cli.json
    python3 tools/state_parity.py --build --check    # build margo-state first

Nothing here reaches the network; scenarios use fictional data only.
"""

import argparse
import difflib
import hashlib
import json
import os
import re
import shutil
import sqlite3
import stat
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
SCRIPTS = REPO / "skills" / "chief-of-staff" / "scripts"
PROBES = REPO / "cloud" / "contracts" / "state" / "probes"
SCENARIOS = REPO / "cloud" / "contracts" / "state"
CLI_PROJECT = REPO / "cloud" / "src" / "Margo.Cloud.StateCli" / "Margo.Cloud.StateCli.csproj"
DEFAULT_CLI = REPO / "cloud" / "src" / "Margo.Cloud.StateCli" / "bin" / "Debug" / "net10.0" / "margo-state.dll"

# Scripts that receive the instance account and state directory, as autopilot statecmd passes them.
STATEFUL = {"work_state.py", "proactive_state.py", "memory_state.py", "task_state.py"}
DEFAULT_ACCOUNT = "parity-owner@example.com"
STEP_TIMEOUT = 120
SCENARIO_KEYS = {"description", "account", "steps", "compare_db", "compare_files", "env"}
STEP_KEYS = {"script", "args", "stdin", "expect_exit", "env", "state_args"}

TOKEN = re.compile(
    r"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d{1,9})?)?(?:Z|[+-]\d{2}:\d{2}(?::\d{2}(?:\.\d{1,6})?)?)?"
    r"|(?<![0-9A-Za-z])[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?![0-9A-Za-z])"
    r"|(?<![0-9A-Za-z.])\d{9,}\.\d+(?![0-9A-Za-z])"
    r"|(?<![0-9A-Za-z])[0-9a-fA-F]{8,}(?![0-9A-Za-z])")
SQLITE_MAGIC = b"SQLite format 3\x00"
SHADOW_SUFFIXES = ("_data", "_idx", "_content", "_docsize", "_config", "_segments", "_segdir", "_stat",
                   "_rowid", "_node", "_parent")


class ScenarioError(Exception):
    """A scenario file that does not follow the documented schema."""


# ----------------------------------------------------------------------------- normalisation

def shape(token):
    """The format of a token with its digits and hex letters masked: stable across runs."""
    return re.sub(r"[0-9a-fA-F]", "9", token)


def tokens(text):
    return [match.group(0) for match in TOKEN.finditer(text)]


class Normaliser:
    """Replaces volatile tokens by numbered placeholders, consistently across one run."""

    def __init__(self):
        self.placeholders = {}

    def placeholder(self, token):
        if token not in self.placeholders:
            self.placeholders[token] = "<V%d:%s>" % (len(self.placeholders) + 1, shape(token))
        return self.placeholders[token]

    def apply(self, text, volatile):
        """Replace the tokens whose positions are in ``volatile`` (a set of indices, or True for all)."""
        index = -1

        def replace(match):
            nonlocal index
            index += 1
            if volatile is True or index in volatile:
                return self.placeholder(match.group(0))
            return match.group(0)

        return TOKEN.sub(replace, text)


def volatile_positions(first, second):
    """Token positions whose values differ between two Python runs, or None if they cannot be aligned."""
    a, b = tokens(first), tokens(second)
    if len(a) != len(b) or TOKEN.sub("", first) != TOKEN.sub("", second):
        return None
    return {index for index, (x, y) in enumerate(zip(a, b)) if x != y}


def replace_roots(text, roots):
    for root in sorted(roots, key=len, reverse=True):
        text = text.replace(root, "<ROOT>")
    return text


def decode(data):
    return data.decode("utf-8", errors="surrogateescape")


# ----------------------------------------------------------------------------- snapshots

def is_database(path):
    try:
        with open(path, "rb") as stream:
            return stream.read(16) == SQLITE_MAGIC
    except OSError:
        return False


def render(value):
    if value is None:
        return "NULL"
    if isinstance(value, bytes):
        return "x'" + value.hex() + "'"
    if isinstance(value, float):
        return repr(value)
    if isinstance(value, int):
        return str(value)
    return json.dumps(value, ensure_ascii=False)


def dump_database(path):
    """Schema SQL, user_version and every table's rows (rowid order, or primary key for WITHOUT ROWID)."""
    lines = []
    connection = sqlite3.connect(Path(path).as_uri() + "?mode=ro", uri=True)
    try:
        lines.append("user_version=%s" % connection.execute("PRAGMA user_version").fetchone()[0])
        schema = connection.execute(
            "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name").fetchall()
        virtual = [name for kind, name, _, sql in schema
                   if kind == "table" and (sql or "").upper().startswith("CREATE VIRTUAL TABLE")]
        for kind, name, table, sql in schema:
            lines.append("schema %s %s on %s: %s" % (kind, name, table, sql))
        for kind, name, _, sql in schema:
            if kind != "table":
                continue
            if any(name.startswith(v) and name[len(v):] in SHADOW_SUFFIXES for v in virtual):
                lines.append("table %s: shadow table rows not compared" % name)
                continue
            quoted = '"' + name.replace('"', '""') + '"'
            without_rowid = "WITHOUT ROWID" in (sql or "").upper()
            if without_rowid:
                keys = [row[1] for row in sorted(
                    (row for row in connection.execute("PRAGMA table_info(%s)" % quoted) if row[5]),
                    key=lambda row: row[5])]
                order = ", ".join('"' + key.replace('"', '""') + '"' for key in keys) or "1"
                query = "SELECT * FROM %s ORDER BY %s" % (quoted, order)
            elif name in virtual:
                query = "SELECT rowid, * FROM %s ORDER BY rowid" % quoted
            else:
                query = "SELECT rowid, * FROM %s ORDER BY rowid" % quoted
            cursor = connection.execute(query)
            columns = [column[0] for column in cursor.description]
            lines.append("table %s columns %s" % (name, ", ".join(columns)))
            for row in cursor:
                lines.append("  " + " | ".join(render(value) for value in row))
    finally:
        connection.close()
    return lines


def snapshot(root):
    """Normalisable text of the file tree and of every database below root."""
    files, databases = [], []
    root = Path(root)
    for directory, dirnames, filenames in os.walk(root):
        dirnames.sort()
        relative_dir = Path(directory).relative_to(root)
        for name in sorted(dirnames) + sorted(filenames):
            path = Path(directory) / name
            relative = (relative_dir / name).as_posix()
            if relative.startswith("home/") and not relative.startswith("home/.copilot"):
                continue
            info = path.lstat()
            mode = "%04o" % stat.S_IMODE(info.st_mode)
            if stat.S_ISLNK(info.st_mode):
                files.append("link %s %s -> %s" % (relative, mode, os.readlink(path)))
            elif stat.S_ISDIR(info.st_mode):
                files.append("dir  %s %s" % (relative, mode))
            elif is_database(path):
                files.append("db   %s %s" % (relative, mode))
                databases.append("== %s" % relative)
                databases.extend(dump_database(path))
            elif re.search(r"\.sqlite3-(journal|wal|shm)$", relative):
                files.append("side %s %s" % (relative, mode))
            else:
                files.append(("file %s %s " % (relative, mode), path.read_bytes()))
    return files, databases


# ----------------------------------------------------------------------------- running

def load_scenario(path):
    try:
        data = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        raise ScenarioError("cannot read scenario: %s" % exc) from exc
    if not isinstance(data, dict) or set(data) - SCENARIO_KEYS:
        raise ScenarioError("unknown scenario keys: %s" % sorted(set(data) - SCENARIO_KEYS))
    if not isinstance(data.get("description"), str) or not data["description"].strip():
        raise ScenarioError("description is required")
    steps = data.get("steps")
    if not isinstance(steps, list) or not steps:
        raise ScenarioError("steps must be a non-empty list")
    for index, step in enumerate(steps):
        if not isinstance(step, dict) or set(step) - STEP_KEYS or not isinstance(step.get("script"), str):
            raise ScenarioError("step %d: needs script and only %s" % (index, sorted(STEP_KEYS)))
        if not isinstance(step.get("args", []), list) or not all(isinstance(a, str) for a in step.get("args", [])):
            raise ScenarioError("step %d: args must be a list of strings" % index)
        if "expect_exit" in step and type(step["expect_exit"]) is not int:
            raise ScenarioError("step %d: expect_exit must be an integer" % index)
        if "stdin" in step and not isinstance(step["stdin"], (str, dict, list)):
            raise ScenarioError("step %d: stdin must be a string or JSON value" % index)
        for env in (step.get("env", {}), data.get("env", {})):
            if not isinstance(env, dict) or not all(isinstance(k, str) and (v is None or isinstance(v, str))
                                                    for k, v in env.items()):
                raise ScenarioError("env must map names to strings (or null to unset)")
    return data


def python_script(name):
    for directory in (SCRIPTS, PROBES):
        if (directory / name).is_file():
            return directory / name
    raise ScenarioError("no Python script named %s in %s or %s" % (name, SCRIPTS, PROBES))


def base_environment(root):
    env = {
        "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
        "HOME": str(root / "home"),
        "TZ": "UTC",
        "LC_ALL": "C.UTF-8",
        "LANG": "C.UTF-8",
        "PYTHONHASHSEED": "0",
        "PYTHONDONTWRITEBYTECODE": "1",
        "PYTHONIOENCODING": "utf-8",
        "MARGO_ALLOW_UNSAFE_STATE_DIR": "1",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_NOLOGO": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
    }
    for name in ("DOTNET_ROOT", "SYSTEMROOT"):
        if name in os.environ:
            env[name] = os.environ[name]
    return env


def substitute(value, root, account):
    return (value.replace("{root}", str(root)).replace("{state_dir}", str(root / "state"))
            .replace("{account}", account))


def run_scenario_once(scenario, implementation, cli):
    """Run all steps in a fresh private root; return per-step results and the final snapshot."""
    root = Path(tempfile.mkdtemp(prefix="margo-parity-"))
    try:
        (root / "home").mkdir(mode=0o700)
        account = scenario.get("account", DEFAULT_ACCOUNT)
        results = []
        for step in scenario["steps"]:
            script = step["script"]
            args = [substitute(arg, root, account) for arg in step.get("args", [])]
            if step.get("state_args", script in STATEFUL):
                args = ["--account", account, "--state-dir", str(root / "state")] + args
            if implementation == "python":
                argv = [sys.executable, "-s", str(python_script(script))] + args
            elif cli.suffix == ".dll":
                argv = ["dotnet", str(cli), script] + args
            else:
                argv = [str(cli), script] + args
            env = base_environment(root)
            for source in (scenario.get("env", {}), step.get("env", {})):
                for name, value in source.items():
                    if value is None:
                        env.pop(name, None)
                    else:
                        env[name] = substitute(value, root, account)
            stdin = step.get("stdin")
            if stdin is None:
                data = b""
            elif isinstance(stdin, str):
                data = substitute(stdin, root, account).encode("utf-8")
            else:
                data = json.dumps(stdin, ensure_ascii=False).encode("utf-8")
            try:
                completed = subprocess.run(argv, input=data, capture_output=True, timeout=STEP_TIMEOUT,
                                           env=env, cwd=str(root))
                results.append((completed.returncode, completed.stdout, completed.stderr))
            except subprocess.TimeoutExpired:
                results.append((None, b"", b"timed out after %ds" % STEP_TIMEOUT))
        files, databases = snapshot(root)
        roots = {str(root), os.path.realpath(root)}
        return results, files, databases, roots
    finally:
        shutil.rmtree(root, ignore_errors=True)


def text_of_files(files, roots):
    """The file listing with text contents inline (so volatile tokens inside files are normalised too)
    and a SHA-256 for binary contents."""
    lines = []
    for entry in files:
        if isinstance(entry, tuple):
            prefix, content = entry
            try:
                body = content.decode("utf-8")
            except UnicodeDecodeError:
                lines.append(prefix + "binary sha256:" + hashlib.sha256(content).hexdigest())
                continue
            lines.append(prefix + "text %d lines" % len(body.splitlines()))
            lines.extend("  | " + line for line in body.split("\n"))
        else:
            lines.append(entry)
    return replace_roots("\n".join(lines), roots)


class Outcome:
    def __init__(self, name):
        self.name = name
        self.failures = []
        self.diff = None
        self.notes = []

    def fail(self, reason, expected=None, actual=None):
        self.failures.append(reason)
        if self.diff is None and expected is not None and actual is not None:
            self.diff = "".join(difflib.unified_diff(
                expected.splitlines(True), actual.splitlines(True), "python", "csharp"))


def judge(name, scenario, cli):
    """Run Python, C#, Python and compare; returns an Outcome."""
    outcome = Outcome(name)
    first = run_scenario_once(scenario, "python", cli)
    csharp = run_scenario_once(scenario, "csharp", cli)
    second = run_scenario_once(scenario, "python", cli)
    norm_first, norm_second, norm_csharp = Normaliser(), Normaliser(), Normaliser()

    def texts(run, normaliser, volatiles):
        results, files, databases, roots = run
        steps = []
        for index, (code, out, err) in enumerate(results):
            text = replace_roots(decode(out), roots)
            steps.append((code, normaliser.apply(text, volatiles[index]), bool(err.strip()) if err else False,
                          replace_roots(decode(err), roots)))
        db_text = normaliser.apply(replace_roots("\n".join(databases), roots), volatiles[-2])
        file_text = normaliser.apply(text_of_files(files, roots), volatiles[-1])
        return steps, db_text, file_text

    # Token alignment between the two Python runs, per step output, DB dump and file listing.
    raw_first = [replace_roots(decode(out), first[3]) for _, out, _ in first[0]]
    raw_second = [replace_roots(decode(out), second[3]) for _, out, _ in second[0]]
    raw_first += [replace_roots("\n".join(first[2]), first[3]), text_of_files(first[1], first[3])]
    raw_second += [replace_roots("\n".join(second[2]), second[3]), text_of_files(second[1], second[3])]
    volatiles = []
    for a, b in zip(raw_first, raw_second):
        positions = volatile_positions(a, b)
        if positions is None:
            outcome.notes.append("python runs not token-aligned; all tokens normalised")
            positions = True
        volatiles.append(positions)

    steps_first, db_first, files_first = texts(first, norm_first, volatiles)
    steps_second, db_second, files_second = texts(second, norm_second, volatiles)
    steps_csharp, db_csharp, files_csharp = texts(csharp, norm_csharp, volatiles)

    for index, step in enumerate(scenario["steps"]):
        label = "step %d (%s %s)" % (index + 1, step["script"], " ".join(step.get("args", []))[:60])
        (code_a, out_a, err_a, raw_err_a), (code_b, out_b, err_b, _) = steps_first[index], steps_second[index]
        code_c, out_c, err_c, raw_err_c = steps_csharp[index]
        if "expect_exit" in step and code_a != step["expect_exit"]:
            outcome.fail("%s: python exit %s, scenario expects %s" % (label, code_a, step["expect_exit"]),
                         "exit=%s\n" % step["expect_exit"], "exit=%s\n%s" % (code_a, raw_err_a))
        if (code_a, out_a, err_a) != (code_b, out_b, err_b):
            outcome.fail("%s: the two python runs disagree outside volatile tokens" % label, out_a, out_b)
        if code_c != code_a:
            outcome.fail("%s: exit %s, python %s" % (label, code_c, code_a),
                         "exit=%s\n%s" % (code_a, raw_err_a), "exit=%s\n%s" % (code_c, raw_err_c))
        if out_c != out_a:
            outcome.fail("%s: stdout differs" % label, out_a, out_c)
        if err_c != err_a:
            outcome.fail("%s: stderr is %s, python's is %s" % (label, "non-empty" if err_c else "empty",
                                                               "non-empty" if err_a else "empty"),
                         raw_err_a, raw_err_c)
    if scenario.get("compare_db", True):
        if db_first != db_second:
            outcome.fail("databases: the two python runs disagree", db_first, db_second)
        if db_csharp != db_first:
            outcome.fail("databases differ", db_first, db_csharp)
    if scenario.get("compare_files", True):
        if files_first != files_second:
            outcome.fail("files: the two python runs disagree", files_first, files_second)
        if files_csharp != files_first:
            outcome.fail("file tree differs", files_first, files_csharp)
    return outcome


def discover(arguments):
    if arguments.scenario:
        paths = [Path(path).resolve() for path in arguments.scenario]
    else:
        paths = sorted(SCENARIOS.rglob("*.json"))
    selected = []
    for path in paths:
        try:
            name = path.relative_to(SCENARIOS).as_posix()
        except ValueError:
            name = str(path)
        if arguments.filter and arguments.filter not in name:
            continue
        selected.append((name, path))
    return selected


def build_cli():
    completed = subprocess.run(["dotnet", "build", str(CLI_PROJECT), "-nologo", "-v", "q"],
                               capture_output=True, text=True)
    if completed.returncode != 0:
        sys.stderr.write(completed.stdout[-4000:] + completed.stderr[-4000:])
        raise SystemExit("state_parity: building margo-state failed")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true",
                        help="run every selected scenario; exit 1 on any mismatch or when nothing ran")
    parser.add_argument("--filter", help="only scenarios whose path contains this substring")
    parser.add_argument("--scenario", action="append", help="run this scenario file (repeatable)")
    parser.add_argument("--verbose", "-v", action="store_true", help="unified diff of the first mismatch per scenario")
    parser.add_argument("--cli", help="margo-state executable or .dll (default: the Debug build)")
    parser.add_argument("--build", action="store_true", help="dotnet build the CLI first")
    arguments = parser.parse_args(argv)
    if arguments.build:
        build_cli()
    cli = Path(arguments.cli).resolve() if arguments.cli else DEFAULT_CLI
    if not cli.exists():
        print("state_parity: C# CLI not found at %s; build it or pass --build/--cli" % cli, file=sys.stderr)
        return 2
    if cli.suffix == ".dll" and shutil.which("dotnet") is None:
        print("state_parity: %s needs the dotnet host on PATH" % cli, file=sys.stderr)
        return 2
    selected = discover(arguments)
    if not selected:
        print("state_parity: no scenarios selected", file=sys.stderr)
        return 1 if arguments.check else 0
    failed = 0
    for name, path in selected:
        try:
            scenario = load_scenario(path)
            outcome = judge(name, scenario, cli)
        except ScenarioError as exc:
            failed += 1
            print("FAIL %s: invalid scenario: %s" % (name, exc))
            continue
        notes = (" (" + "; ".join(sorted(set(outcome.notes))) + ")") if outcome.notes else ""
        if outcome.failures:
            failed += 1
            print("FAIL %s: %s%s" % (name, outcome.failures[0], notes))
            for reason in outcome.failures[1:]:
                print("     %s" % reason)
            if arguments.verbose and outcome.diff:
                print(outcome.diff.rstrip("\n"))
        else:
            print("PASS %s%s" % (name, notes))
    print("%d scenario(s): %d passed, %d failed" % (len(selected), len(selected) - failed, failed))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
