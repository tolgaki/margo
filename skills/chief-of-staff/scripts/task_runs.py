"""Durable bounded task progress over the existing work, coverage and receipt journals."""

import hashlib
import hmac
import json
import secrets
import sqlite3
import uuid
from datetime import datetime, timedelta, timezone

import margo_store
import proactive_state
from margo_store import NotInitialized, StateError, canonical_json, parse_json, utc_now, validate_timestamp
from work_ledger import Ledger, digest, human


METRICS = ("tool_calls", "pages", "items", "model_calls", "output_chars")
KINDS = {"read", "local", "action"}
TERMINAL = {"succeeded", "partial", "failed", "outcome_unknown", "cancelled"}
RETRYABLE = {"timeout", "network", "throttled", "unavailable", "interrupted"}
SCHEMA = """
CREATE TABLE IF NOT EXISTS task_meta (key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS task_runs (
 id TEXT PRIMARY KEY,account TEXT NOT NULL,key_hash TEXT NOT NULL UNIQUE,
 revision INTEGER NOT NULL,state TEXT NOT NULL,plan TEXT NOT NULL,plan_hash TEXT NOT NULL,
 usage TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS task_steps (
 run_id TEXT NOT NULL REFERENCES task_runs(id),step_key TEXT NOT NULL,revision INTEGER NOT NULL,
 state TEXT NOT NULL,definition TEXT NOT NULL,attempts INTEGER NOT NULL,
 latest_attempt TEXT,result TEXT,next_retry_at TEXT,updated_at TEXT NOT NULL,
 PRIMARY KEY(run_id,step_key)
);
CREATE TABLE IF NOT EXISTS task_attempts (
 id TEXT PRIMARY KEY,run_id TEXT NOT NULL,step_key TEXT NOT NULL,number INTEGER NOT NULL,
 state TEXT NOT NULL,token_hash TEXT NOT NULL,lease_expires TEXT NOT NULL,
 reserved TEXT NOT NULL,charged TEXT NOT NULL,actual TEXT,details TEXT NOT NULL,
 result TEXT,started_at TEXT NOT NULL,finished_at TEXT,
 FOREIGN KEY(run_id,step_key) REFERENCES task_steps(run_id,step_key),
 UNIQUE(run_id,step_key,number)
);
CREATE TABLE IF NOT EXISTS task_charges (
 attempt_id TEXT NOT NULL REFERENCES task_attempts(id),event_id TEXT NOT NULL,
 cost TEXT NOT NULL,created_at TEXT NOT NULL,PRIMARY KEY(attempt_id,event_id)
);
CREATE TABLE IF NOT EXISTS task_events (
 seq INTEGER PRIMARY KEY AUTOINCREMENT,run_id TEXT NOT NULL REFERENCES task_runs(id),
 revision INTEGER NOT NULL,event TEXT NOT NULL,data TEXT NOT NULL,created_at TEXT NOT NULL
);
"""


def text(value, field, maximum=4000):
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        raise StateError(field + " must be a bounded nonempty string")
    return value


def integer(value, field, low=0, high=1000000):
    if type(value) is not int or not low <= value <= high:
        raise StateError("%s must be an integer between %d and %d" % (field, low, high))
    return value


def when(value):
    return datetime.fromisoformat(validate_timestamp(value))


def clock():
    return datetime.now(timezone.utc)


def resource_cost(value):
    if not isinstance(value, dict) or set(value) != set(METRICS):
        raise StateError("cost requires exactly " + ", ".join(METRICS))
    return {key: integer(value[key], key, high=1000000) for key in METRICS}


def zero():
    return {key: 0 for key in METRICS}


def environment(value, account, fresh=False):
    if not isinstance(value, dict) or set(value) != {"account", "host", "capabilities", "observed_at"}:
        raise StateError("binding requires account,host,capabilities,observed_at")
    if value["account"] != account:
        raise StateError("task binding belongs to another account")
    text(value["host"], "host", 200)
    capabilities = value["capabilities"]
    if not isinstance(capabilities, dict) or not capabilities or len(capabilities) > 200:
        raise StateError("binding requires a bounded capability/version map")
    for name, version in capabilities.items():
        text(name, "capability", 200)
        text(version, "capability version", 200)
    observed = when(value["observed_at"])
    age = clock() - observed
    if age < timedelta(seconds=-30) or (fresh and age > timedelta(minutes=5)):
        raise StateError("task binding must be an actual observation within five minutes")
    return dict(value, capabilities=dict(sorted(capabilities.items())),
                observed_at=validate_timestamp(value["observed_at"]))


def binding_hash(value):
    return digest({key: value[key] for key in ("account", "host", "capabilities")})


def task_identity(account, key):
    text(key, "task identity", 500)
    return "task_" + digest([account, key])[:32]


class TaskStore:
    def __init__(self, account=None, state_root=None, read_only=False):
        self.account = margo_store.resolve_account(account)
        self.read_only = read_only
        self.conn = None
        try:
            if read_only:
                self.conn = margo_store.connect(self.account, state_root, read_only=True)
                self.ledger = Ledger.from_connection(self.conn, self.account)
            else:
                self.ledger = Ledger(self.account, state_root)
                self.conn = self.ledger.conn
            self._initialize()
        except Exception:
            if self.conn is not None:
                self.conn.close()
            raise

    def close(self):
        self.conn.close()

    def transaction(self):
        return margo_store.transaction(self.conn, read_only=self.read_only)

    def _initialize(self):
        expected = {"task_meta", "task_runs", "task_steps", "task_attempts", "task_charges", "task_events"}
        with self.transaction():
            tables = {row[0] for row in self.conn.execute(
                "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'task_%'")}
            marker = self.conn.execute("SELECT value FROM margo_meta WHERE key='task_schema_version'").fetchone()
            if not tables and marker is None and self.read_only:
                raise NotInitialized("Task state is not initialized; run task_state.py init explicitly.")
            if tables or marker is not None:
                if tables != expected or marker is None or marker[0] != "1":
                    raise StateError("incomplete or incompatible task schema; explicit recovery required")
                local = self.conn.execute("SELECT value FROM task_meta WHERE key='schema_version'").fetchone()
                if local is None or local[0] != "1":
                    raise StateError("task schema marker mismatch")
                reference = sqlite3.connect(":memory:")
                try:
                    reference.executescript(SCHEMA)
                    for table in sorted(expected):
                        for pragma in ("table_info", "foreign_key_list"):
                            actual = [tuple(row) for row in self.conn.execute("PRAGMA %s(%s)" % (pragma, table))]
                            wanted = [tuple(row) for row in reference.execute("PRAGMA %s(%s)" % (pragma, table))]
                            if actual != wanted:
                                raise StateError("task schema contract mismatch: " + table)
                finally:
                    reference.close()
            if not self.read_only:
                for statement in SCHEMA.split(";"):
                    if statement.strip():
                        self.conn.execute(statement)
                self.conn.execute("INSERT OR IGNORE INTO task_meta VALUES('schema_version','1')")
                self.conn.execute("INSERT OR IGNORE INTO margo_meta VALUES('task_schema_version','1')")
                proactive_state.initialize(self.conn)

    def _run(self, run_id):
        row = self.conn.execute("SELECT * FROM task_runs WHERE id=? AND account=?", (run_id, self.account)).fetchone()
        if row is None:
            raise StateError("unknown task run")
        return row

    def _step(self, run_id, key):
        self._run(run_id)
        row = self.conn.execute("SELECT * FROM task_steps WHERE run_id=? AND step_key=?", (run_id, key)).fetchone()
        if row is None:
            raise StateError("unknown task step")
        return row

    def _attempt(self, attempt_id):
        row = self.conn.execute(
            "SELECT a.* FROM task_attempts a JOIN task_runs r ON r.id=a.run_id WHERE a.id=? AND r.account=?",
            (attempt_id, self.account)).fetchone()
        if row is None:
            raise StateError("unknown task attempt")
        return row

    def _event(self, run_id, event, data):
        self.conn.execute("UPDATE task_runs SET revision=revision+1,updated_at=? WHERE id=?",
                          (utc_now(), run_id))
        revision = self._run(run_id)["revision"]
        self.conn.execute("INSERT INTO task_events(run_id,revision,event,data,created_at) VALUES(?,?,?,?,?)",
                          (run_id, revision, event, canonical_json(data), utc_now()))
        run = self._run(run_id)
        plan, usage = parse_json(run["plan"]), parse_json(run["usage"])
        keys = {step["key"] for step in plan["steps"]}
        states = {row["step_key"]: row["state"] for row in self.conn.execute(
            "SELECT step_key,state FROM task_steps WHERE run_id=?", (run_id,))}
        if (run["state"] == "active" and all(states.get(key) == "succeeded" for key in keys)
                and all(usage[metric] <= plan["limits"][metric] for metric in METRICS)):
            self.conn.execute("UPDATE task_runs SET state='completed' WHERE id=?", (run_id,))

    def _plan(self, value):
        required = {"goal", "routine", "request_ref", "mode", "environment", "window", "limits", "steps"}
        if not isinstance(value, dict) or set(value) != required:
            raise StateError("task plan requires exactly " + ", ".join(sorted(required)))
        text(value["goal"], "goal")
        text(value["routine"], "routine", 100)
        request = text(value["request_ref"], "request_ref", 1000)
        if not request.startswith(("conversation:", "host-interaction:", "automation:")):
            raise StateError("task request must reference a user interaction or automation, not observed source instructions")
        if value["mode"] not in {"foreground", "unattended"}:
            raise StateError("task mode must be foreground or unattended")
        if value["mode"] == "unattended" and not request.startswith("automation:"):
            raise StateError("unattended task requires its automation request reference")
        env = environment(value["environment"], self.account)
        window = value["window"]
        if not isinstance(window, dict) or set(window) != {"start", "end"} or when(window["start"]) >= when(window["end"]):
            raise StateError("task window requires explicit ordered start/end timestamps")
        if when(window["end"]) - when(window["start"]) > timedelta(days=366):
            raise StateError("task source window exceeds one year")
        limits = value["limits"]
        required_limits = set(METRICS) | {"max_steps", "max_attempts_per_step", "max_parallel", "deadline_at", "lease_seconds"}
        if not isinstance(limits, dict) or set(limits) != required_limits:
            raise StateError("task limits must explicitly bound resources, attempts, parallelism and time")
        limits = dict(limits, **resource_cost({key: limits[key] for key in METRICS}))
        for key, maximum in (("max_steps", 100), ("max_attempts_per_step", 10), ("max_parallel", 10), ("lease_seconds", 900)):
            integer(limits[key], key, 1, maximum)
        limits["deadline_at"] = validate_timestamp(limits["deadline_at"])
        if when(limits["deadline_at"]) <= clock() or when(limits["deadline_at"]) > clock() + timedelta(days=7):
            raise StateError("task deadline must be future and within seven days")
        steps = value["steps"]
        if not isinstance(steps, list) or not 1 <= len(steps) <= limits["max_steps"]:
            raise StateError("task needs a nonempty step list within max_steps")
        normalized, keys = [], set()
        for step in steps:
            if not isinstance(step, dict):
                raise StateError("step must be an object")
            basic = {"key", "title", "kind", "capability", "depends_on", "allow_partial", "cost"}
            if not basic <= set(step) or set(step) - basic - {"source", "action_id", "action_revision", "action_hash"}:
                raise StateError("step has missing or unsupported fields")
            key = text(step["key"], "step key", 100)
            if key in keys:
                raise StateError("duplicate step key")
            keys.add(key)
            text(step["title"], "step title", 300)
            if step["kind"] not in KINDS or step["capability"] not in env["capabilities"]:
                raise StateError("step kind/capability is not available in the declared binding")
            dependencies = step["depends_on"]
            if (not isinstance(dependencies, list) or not all(isinstance(item, str) for item in dependencies)
                    or len(dependencies) != len(set(dependencies))):
                raise StateError("step dependencies must be distinct keys")
            if type(step["allow_partial"]) is not bool or (step["allow_partial"] and step["kind"] != "local"):
                raise StateError("only local preparation may explicitly use partial prerequisites")
            item = dict(step, depends_on=list(dependencies), cost=resource_cost(step["cost"]))
            if step["kind"] in {"read", "action"} and item["cost"]["tool_calls"] < 1:
                raise StateError("a remote read/action must reserve at least one tool call")
            if any(item["cost"][metric] > limits[metric] for metric in METRICS):
                raise StateError("step reservation exceeds the run resource limit")
            if step["kind"] == "read":
                source = step.get("source")
                if (not isinstance(source, dict) or not {"family", "scope", "kind", "query_version"} <= set(source)
                        or set(source) - {"family", "scope", "kind", "query_version", "reauthenticated", "reactivate"}):
                    raise StateError("read step requires a bounded source collection contract")
                text(source["family"], "source family", 100)
                text(source["query_version"], "source query version", 200)
                if not isinstance(source["scope"], dict) or not source["scope"]:
                    raise StateError("source scope must explicitly identify the collection")
                if source["kind"] not in {"enumeration", "snapshot", "search"}:
                    raise StateError("unsupported source collection kind")
                for flag in ("reauthenticated", "reactivate"):
                    if type(source.get(flag, False)) is not bool or (value["mode"] == "unattended" and source.get(flag)):
                        raise StateError("source reauthentication/reactivation is an explicit foreground operation")
                if any(name in step for name in ("action_id", "action_revision", "action_hash")):
                    raise StateError("read steps cannot hide an action reference")
            elif "source" in step:
                raise StateError("only read steps use a source collection contract")
            if step["kind"] == "action":
                if value["mode"] != "foreground":
                    raise StateError("unattended tasks may prepare proposals, never execute actions")
                action = self.ledger.show(step.get("action_id"))
                if (action.get("type") != "action" or action["revision"] != step.get("action_revision")
                        or action["action_hash"] != step.get("action_hash")):
                    raise StateError("action step must bind an exact current action revision/hash")
            elif any(name in step for name in ("action_id", "action_revision", "action_hash")):
                raise StateError("only action steps may bind external execution")
            normalized.append(item)
        by_key = {step["key"]: step for step in normalized}
        visiting, visited = set(), set()

        def visit(key):
            if key not in by_key or key in visiting:
                raise StateError("task dependencies are missing or cyclic")
            if key in visited:
                return
            visiting.add(key)
            for dependency in by_key[key]["depends_on"]:
                visit(dependency)
            visiting.remove(key)
            visited.add(key)

        for key in keys:
            visit(key)
        result = dict(value, environment=env, limits=limits, steps=normalized,
                      window={key: validate_timestamp(window[key]) for key in ("start", "end")})
        if len(canonical_json(result)) > 100000:
            raise StateError("task plan exceeds its storage budget")
        return result

    def create(self, key, plan):
        text(key, "task identity", 500)
        with self.transaction():
            plan = self._plan(plan)
            key_hash = digest([self.account, key])
            run_id = task_identity(self.account, key)
            previous = self.conn.execute("SELECT * FROM task_runs WHERE id=? AND account=?", (run_id, self.account)).fetchone()
            encoded = canonical_json(plan)
            if previous:
                if previous["plan"] != encoded:
                    raise StateError("task identity " + run_id + " already has a different plan; inspect or explicitly replan it")
                return self.show(run_id)
            stamp = utc_now()
            self.conn.execute("INSERT INTO task_runs VALUES(?,?,?,1,'planned',?,?,?,?,?)",
                              (run_id, self.account, key_hash, encoded, digest(plan), canonical_json(zero()), stamp, stamp))
            for step in plan["steps"]:
                self.conn.execute("INSERT INTO task_steps VALUES(?,?,1,'pending',?,0,NULL,NULL,NULL,?)",
                                  (run_id, step["key"], canonical_json(step), stamp))
            self._event(run_id, "created", {"request_ref": plan["request_ref"], "mode": plan["mode"]})
            return self.show(run_id)

    def _binding(self, plan, supplied):
        actual = environment(supplied, self.account, fresh=True)
        if binding_hash(actual) != binding_hash(plan["environment"]):
            raise StateError("task capability/host binding changed; pause and review a new plan")

    def _prerequisites(self, run_id, definition):
        reasons = []
        for key in definition["depends_on"]:
            step = self._step(run_id, key)
            accepted = {"succeeded"}
            if definition["allow_partial"]:
                accepted |= {"partial", "failed"}
            if step["state"] not in accepted:
                reasons.append("prerequisite %s is %s" % (key, step["state"]))
            result = parse_json(step["result"]) if step["result"] else {}
            if result.get("source_refs") and self.ledger.refs_stale(result["source_refs"]):
                reasons.append("prerequisite %s has changed source evidence; add a fresh read in a reviewed plan" % key)
        return reasons

    def _budget_reasons(self, run, reservation=None):
        plan, usage = parse_json(run["plan"]), parse_json(run["usage"])
        reasons = []
        if when(plan["limits"]["deadline_at"]) <= clock():
            reasons.append("task deadline expired")
        for metric in METRICS:
            if usage[metric] + (reservation or {}).get(metric, 0) > plan["limits"][metric]:
                reasons.append(metric + " budget exhausted")
        return reasons

    def _token(self, attempt, token):
        text(token, "claim token", 200)
        if not hmac.compare_digest(hashlib.sha256(token.encode()).hexdigest(), attempt["token_hash"]):
            raise StateError("attempt claim token does not match")

    def start(self, run_id, step_key, revision, plan_hash, binding, preflight=None):
        with self.transaction():
            result = self._start(run_id, step_key, revision, plan_hash, binding, preflight)
        if result.get("preflight_blocked"):
            raise StateError(result["preflight_blocked"])
        return result

    def _start(self, run_id, step_key, revision, plan_hash, binding, preflight=None):
        with self.transaction():
            run, step = self._run(run_id), self._step(run_id, step_key)
            plan, definition = parse_json(run["plan"]), parse_json(step["definition"])
            if run["state"] not in {"planned", "active"} or run["plan_hash"] != plan_hash:
                raise StateError("task is paused/cancelled/completed or its plan changed")
            if type(revision) is not int or step["revision"] != revision or step["state"] != "pending":
                raise StateError("step changed or is not pending")
            self._binding(plan, binding)
            if definition["capability"] not in binding["capabilities"]:
                raise StateError("step capability is unavailable")
            reasons = self._prerequisites(run_id, definition) + self._budget_reasons(run, definition["cost"])
            if reasons:
                raise StateError("; ".join(reasons))
            if step["attempts"] >= plan["limits"]["max_attempts_per_step"]:
                raise StateError("step attempt budget exhausted")
            if step["next_retry_at"] and when(step["next_retry_at"]) > clock():
                raise StateError("provider/backoff retry time has not arrived")
            running = self.conn.execute("SELECT count(*) FROM task_steps WHERE run_id=? AND state='running'", (run_id,)).fetchone()[0]
            if running >= plan["limits"]["max_parallel"]:
                raise StateError("parallel-step limit reached; recover expired claims before retrying")
            attempt_id, token = "task_attempt_" + uuid.uuid4().hex, secrets.token_hex(24)
            expiry = min(clock() + timedelta(seconds=plan["limits"]["lease_seconds"]), when(plan["limits"]["deadline_at"])).isoformat()
            details, execution, source_contract = {}, None, None
            if definition["kind"] == "action":
                action = self.ledger.show(definition["action_id"])
                if action["revision"] != definition["action_revision"] or action["action_hash"] != definition["action_hash"]:
                    raise StateError("linked action changed; replan and review the exact new action")
                try:
                    execution = self.ledger.begin(definition["action_id"], definition["action_revision"], preflight)
                except StateError as exc:
                    # Preserve the ledger's intentional approval invalidation before surfacing failure.
                    if action["state"] == "approved" and self.ledger.show(action["id"])["state"] == "stale":
                        self._event(run_id, "preflight_blocked", {"step_key": step_key, "reason": str(exc)})
                        return {"preflight_blocked": str(exc)}
                    raise
                details["action_attempt_id"] = execution["attempt_id"]
            elif preflight is not None:
                raise StateError("only action steps accept an execution preflight")
            if definition["kind"] == "read":
                source_contract = dict(definition["source"], window=plan["window"], run_id=attempt_id,
                                       capability=definition["capability"])
                previous = self._attempt(step["latest_attempt"]) if step["latest_attempt"] else None
                if previous:
                    old_details = parse_json(previous["details"])
                    old_id = old_details.get("coverage_attempt_id")
                    old = self.conn.execute("SELECT * FROM proactive_attempts WHERE id=?", (old_id,)).fetchone()
                    if (old and old["status"] in {"partial", "failed"} and old["continuation"]
                            and not old["pagination_complete"] and old["error_class"] not in {
                                "expired_token", "access_denied", "authentication_required"}):
                        source_contract["resume_attempt"] = old_id
                        details["base_pages"], details["base_items"] = old["pages"], old["observations"]
                collection = proactive_state.coverage_start(self.conn, source_contract)
                details["coverage_attempt_id"] = collection["attempt_id"]
                details["source_contract"] = source_contract
            usage = parse_json(run["usage"])
            for metric in METRICS:
                usage[metric] += definition["cost"][metric]
            charged = zero()
            if definition["kind"] == "action":
                charged["tool_calls"] = 1
            stamp = utc_now()
            self.conn.execute("INSERT INTO task_attempts VALUES(?,?,?,?,?,?,?,?,?,?,?,NULL,?,NULL)",
                              (attempt_id, run_id, step_key, step["attempts"] + 1, "running",
                               hashlib.sha256(token.encode()).hexdigest(), expiry, canonical_json(definition["cost"]),
                               canonical_json(charged), None, canonical_json(details), stamp))
            self.conn.execute("UPDATE task_steps SET revision=revision+1,state='running',attempts=attempts+1,"
                              "latest_attempt=?,result=NULL,next_retry_at=NULL,updated_at=? WHERE run_id=? AND step_key=?",
                              (attempt_id, stamp, run_id, step_key))
            self.conn.execute("UPDATE task_runs SET state='active',usage=? WHERE id=?", (canonical_json(usage), run_id))
            self._event(run_id, "step_started", {"step_key": step_key, "attempt_id": attempt_id, "kind": definition["kind"]})
            result = {"run_id": run_id, "step_key": step_key, "attempt_id": attempt_id, "token": token,
                      "lease_expires": expiry, "source_contract": source_contract,
                      "coverage_attempt_id": details.get("coverage_attempt_id"), "execution": execution}
            return result

    def charge(self, attempt_id, token, event_id, cost):
        value = resource_cost(cost)
        if not any(value.values()):
            raise StateError("a charge must account for actual planned work, not an empty execution grant")
        text(event_id, "charge event identity", 200)
        with self.transaction():
            attempt = self._attempt(attempt_id)
            self._token(attempt, token)
            previous = self.conn.execute("SELECT cost FROM task_charges WHERE attempt_id=? AND event_id=?",
                                         (attempt_id, event_id)).fetchone()
            if previous:
                if previous["cost"] != canonical_json(value):
                    raise StateError("charge replay changed its cost")
                return {"execute": False, "replayed": True, "event_id": event_id}
            run, step = self._run(attempt["run_id"]), self._step(attempt["run_id"], attempt["step_key"])
            definition = parse_json(step["definition"])
            if definition["kind"] == "action":
                raise StateError("the exact action call was already claimed; additional action grants are forbidden")
            if definition["kind"] == "read" and value["tool_calls"] < 1:
                raise StateError("a read charge must include its tool call")
            if (run["state"] != "active" or attempt["state"] != "running"
                    or step["latest_attempt"] != attempt_id or when(attempt["lease_expires"]) <= clock()):
                raise StateError("claim is paused, cancelled, expired or no longer owns the step")
            reasons = self._budget_reasons(run)
            if reasons:
                raise StateError("; ".join(reasons))
            charged, reserved = parse_json(attempt["charged"]), parse_json(attempt["reserved"])
            for metric in METRICS:
                charged[metric] += value[metric]
                if charged[metric] > reserved[metric]:
                    raise StateError("attempt " + metric + " reservation exhausted")
            self.conn.execute("INSERT INTO task_charges VALUES(?,?,?,?)", (attempt_id, event_id, canonical_json(value), utc_now()))
            self.conn.execute("UPDATE task_attempts SET charged=? WHERE id=?", (canonical_json(charged), attempt_id))
            self._event(run["id"], "budget_charged", {"attempt_id": attempt_id, "event_id": event_id, "cost": value})
            return {"execute": True, "replayed": False, "event_id": event_id, "charged": charged}

    def _result(self, attempt, definition, outcome, result):
        if not isinstance(result, dict) or len(canonical_json(result)) > 16000:
            raise StateError("step result must be a bounded receipt object")
        if definition["kind"] == "action":
            expected = {
                "account": self.account, "action_id": definition["action_id"],
                "attempt_id": parse_json(attempt["details"])["action_attempt_id"],
                "revision": definition["action_revision"], "action_revision": definition["action_revision"],
                "action_hash": definition["action_hash"],
            }
            for key, value in expected.items():
                if key in result and (result[key] != value or (
                        key in {"revision", "action_revision"} and type(result[key]) is not int)):
                    raise StateError("action receipt contradicts its claimed " + key)
            if outcome != "outcome_unknown":
                self.ledger.validate_receipt(result, outcome)
            return dict(result)
        allowed = {"kind", "reference", "summary", "coverage", "coverage_attempt_id", "source_refs",
                   "work_ids", "publication_id", "error_class", "retry_after_seconds", "definitive_no_effect"}
        if set(result) - allowed:
            raise StateError("unsupported step result fields")
        if result.get("kind") != ("tool_result" if definition["kind"] == "read" else "local_result"):
            raise StateError("step receipt kind does not match the operation")
        text(result.get("reference"), "result reference", 1000)
        if result.get("summary") is not None:
            text(result["summary"], "result summary", 4000)
        value = dict(result, source_refs=self.ledger.source_refs(result.get("source_refs", []), require_current=False))
        ids = result.get("work_ids", [])
        if not isinstance(ids, list) or len(ids) > 100:
            raise StateError("work_ids must be bounded canonical references")
        for identity in ids:
            self.ledger.show(identity)
        if result.get("publication_id"):
            publication = self.conn.execute(
                "SELECT status,receipt FROM proactive_publications WHERE id=?", (result["publication_id"],)).fetchone()
            if not publication or publication["status"] not in {"available", "published", "reviewed"} or not publication["receipt"]:
                raise StateError("output availability requires its actual publication receipt")
        if definition["kind"] == "read":
            expected = parse_json(attempt["details"])["coverage_attempt_id"]
            if result.get("coverage_attempt_id") != expected:
                raise StateError("read result must name this claim's coverage attempt")
            coverage = self.conn.execute("SELECT * FROM proactive_attempts WHERE id=?", (expected,)).fetchone()
            if not coverage or coverage["run_id"] != attempt["id"] or coverage["status"] == "running":
                raise StateError("finish the exact source coverage attempt before settling the step")
            if result.get("coverage") != coverage["status"]:
                raise StateError("reported coverage differs from the durable source result")
            if outcome == "succeeded" and coverage["status"] != "complete":
                raise StateError("incomplete source coverage cannot establish successful collection")
            interrupted = result.get("error_class") == "interrupted" and result["reference"].startswith("task-recovery:")
            if result.get("error_class") and result["error_class"] != coverage["error_class"] and not interrupted:
                raise StateError("reported source error differs from durable coverage")
            if coverage["error_class"]:
                value["error_class"] = coverage["error_class"]
        if value.get("retry_after_seconds") is not None:
            integer(value["retry_after_seconds"], "retry_after_seconds", high=86400)
        if outcome == "failed" and definition["kind"] == "local" and value.get("definitive_no_effect") is not True:
            raise StateError("local failure needs proof of no effect; otherwise record unknown/partial")
        return value

    def _settle(self, attempt, outcome, result, actual=None):
        step, run = self._step(attempt["run_id"], attempt["step_key"]), self._run(attempt["run_id"])
        definition = parse_json(step["definition"])
        if step["latest_attempt"] != attempt["id"]:
            raise StateError("attempt no longer owns this step; it cannot settle a newer attempt")
        result = self._result(attempt, definition, outcome, result)
        if attempt["state"] in {"succeeded", "failed"}:
            if attempt["state"] == outcome and attempt["result"] == canonical_json(result):
                return dict(self.show(run["id"]), replayed=True)
            raise StateError("settled attempt is immutable")
        if attempt["state"] not in {"running", "partial", "outcome_unknown"}:
            raise StateError("attempt cannot be settled from its current state")
        if attempt["state"] == "partial" and outcome in {"failed", "outcome_unknown"}:
            raise StateError("known partial effects cannot become no effect or unknown")
        reserved, charged = parse_json(attempt["reserved"]), parse_json(attempt["charged"])
        observed = resource_cost(actual) if actual is not None else dict(reserved)
        for metric in ("tool_calls", "model_calls"):
            if observed[metric] < charged[metric]:
                raise StateError("actual cost cannot erase charged calls")
        if definition["kind"] == "read":
            details = parse_json(attempt["details"])
            coverage = self.conn.execute("SELECT * FROM proactive_attempts WHERE id=?",
                                         (details["coverage_attempt_id"],)).fetchone()
            for metric, field, baseline in (("pages", "pages", "base_pages"), ("items", "observations", "base_items")):
                known = coverage[field] - details.get(baseline, 0)
                if known < 0 or (actual is not None and (
                        observed[metric] < known or (coverage["status"] == "complete" and observed[metric] != known))):
                    raise StateError("actual source cost cannot contradict durable coverage")
                observed[metric] = known if coverage["status"] == "complete" else max(observed[metric], known)
        if definition["kind"] == "local":
            observed["output_chars"] = max(observed["output_chars"], len(result.get("summary", "")))
        if definition["kind"] in {"read", "local"} and outcome in {"succeeded", "partial"}:
            if definition["kind"] == "read" and charged["tool_calls"] == 0:
                raise StateError("record the bounded tool charge before claiming a completed read")
        if definition["kind"] == "action":
            execution_id = parse_json(attempt["details"])["action_attempt_id"]
            stored = self.conn.execute("SELECT state,receipt FROM work_executions WHERE id=?", (execution_id,)).fetchone()
            if not stored:
                raise StateError("linked action execution is missing")
            if stored["state"] in {"succeeded", "failed"}:
                if stored["state"] != outcome or parse_json(stored["receipt"]) != result:
                    raise StateError("result differs from the settled canonical execution")
            else:
                self.ledger.finish(execution_id, outcome, result,
                                   reconcile=stored["state"] in {"partial", "outcome_unknown"})
        usage = parse_json(run["usage"])
        previous_cost = parse_json(attempt["actual"]) if attempt["actual"] else reserved
        for metric in METRICS:
            usage[metric] += observed[metric] - previous_cost[metric]
        next_retry = None
        if definition["kind"] == "read" and outcome in {"failed", "partial"}:
            delay = max(min(2 ** attempt["number"], 300), result.get("retry_after_seconds", 0))
            details = parse_json(attempt["details"])
            coverage = self.conn.execute("SELECT result,finished_at FROM proactive_attempts WHERE id=?",
                                         (details["coverage_attempt_id"],)).fetchone()
            next_retry = clock() + timedelta(seconds=delay)
            provider = parse_json(coverage["result"])
            if provider.get("retry_after_seconds") is not None:
                next_retry = max(next_retry, when(coverage["finished_at"]) + timedelta(seconds=provider["retry_after_seconds"]))
            if provider.get("next_retry_at"):
                next_retry = max(next_retry, when(provider["next_retry_at"]))
            next_retry = next_retry.isoformat()
        stamp = utc_now()
        self.conn.execute("UPDATE task_attempts SET state=?,actual=?,result=?,finished_at=? WHERE id=?",
                          (outcome, canonical_json(observed), canonical_json(result), stamp, attempt["id"]))
        self.conn.execute("UPDATE task_steps SET revision=revision+1,state=?,result=?,next_retry_at=?,updated_at=? "
                          "WHERE run_id=? AND step_key=?",
                          (outcome, canonical_json(result), next_retry, stamp, run["id"], step["step_key"]))
        self.conn.execute("UPDATE task_runs SET usage=? WHERE id=?", (canonical_json(usage), run["id"]))
        self._event(run["id"], "step_finished", {"step_key": step["step_key"], "attempt_id": attempt["id"], "outcome": outcome})
        return self.show(run["id"])

    def finish(self, attempt_id, token, outcome, result, actual=None):
        if outcome not in {"succeeded", "partial", "failed", "outcome_unknown"}:
            raise StateError("unsupported task outcome")
        with self.transaction():
            attempt = self._attempt(attempt_id)
            self._token(attempt, token)
            return self._settle(attempt, outcome, result, actual)

    def pause(self, run_id, reason):
        text(reason, "pause reason", 1000)
        with self.transaction():
            run = self._run(run_id)
            if run["state"] in {"completed", "cancelled"}:
                raise StateError("a completed/cancelled task cannot be paused")
            if run["state"] != "paused":
                self.conn.execute("UPDATE task_runs SET state='paused' WHERE id=?", (run_id,))
                self._event(run_id, "paused", {"reason": reason})
            return self.show(run_id)

    def cancel(self, run_id, reason):
        text(reason, "cancellation reason", 1000)
        with self.transaction():
            run = self._run(run_id)
            if run["state"] in {"cancelled", "completed"}:
                return self.show(run_id)
            for step in self.conn.execute("SELECT * FROM task_steps WHERE run_id=?", (run_id,)).fetchall():
                definition = parse_json(step["definition"])
                if definition["kind"] == "action" and step["state"] not in {"running", "succeeded", "partial", "outcome_unknown"}:
                    action = self.ledger.show(definition["action_id"])
                    if action["revision"] == definition["action_revision"] and action["action_hash"] == definition["action_hash"]:
                        self.ledger.invalidate_approval(action["id"], action["revision"], action["action_hash"], reason)
                if step["state"] == "pending":
                    self.conn.execute("UPDATE task_steps SET revision=revision+1,state='cancelled',updated_at=? "
                                      "WHERE run_id=? AND step_key=?", (utc_now(), run_id, step["step_key"]))
            self.conn.execute("UPDATE task_runs SET state='cancelled' WHERE id=?", (run_id,))
            self._event(run_id, "cancelled", {"reason": reason, "undo_performed": False})
            return self.show(run_id)

    def resume(self, run_id, revision, binding):
        with self.transaction():
            run = self._run(run_id)
            if type(revision) is not int or run["revision"] != revision:
                raise StateError("task revision changed")
            if run["state"] != "paused":
                raise StateError("only paused tasks resume; cancelled/completed tasks need a deliberate new run")
            self._binding(parse_json(run["plan"]), binding)
            reasons = self._budget_reasons(run)
            if reasons:
                raise StateError("; ".join(reasons) + "; review a revised plan before resuming")
            self.conn.execute("UPDATE task_runs SET state='active' WHERE id=?", (run_id,))
            self._event(run_id, "resumed", {"binding": binding_hash(binding)})
            return self.show(run_id)

    def retry(self, run_id, step_key, revision, binding):
        with self.transaction():
            run, step = self._run(run_id), self._step(run_id, step_key)
            plan, definition = parse_json(run["plan"]), parse_json(step["definition"])
            if type(revision) is not int or step["revision"] != revision:
                raise StateError("step revision changed")
            if run["state"] not in {"active", "planned"} or definition["kind"] != "read":
                raise StateError("only read steps in active tasks may retry")
            if step["state"] not in {"failed", "partial"} or step["attempts"] >= plan["limits"]["max_attempts_per_step"]:
                raise StateError("step cannot retry or its attempt budget is exhausted")
            result = parse_json(step["result"])
            attempt = self._attempt(step["latest_attempt"])
            coverage_id = parse_json(attempt["details"])["coverage_attempt_id"]
            coverage = self.conn.execute("SELECT * FROM proactive_attempts WHERE id=?", (coverage_id,)).fetchone()
            provider_result = parse_json(coverage["result"]) if coverage["result"] else {}
            unfinished_page = (step["state"] == "partial" and coverage["status"] == "partial"
                               and coverage["continuation"] and not coverage["pagination_complete"]
                               and "error_class" not in provider_result)
            if result.get("error_class") not in RETRYABLE and not unfinished_page:
                raise StateError("failure needs explicit capability/auth/source revalidation, not automatic retry")
            self._binding(plan, binding)
            if step["next_retry_at"] and when(step["next_retry_at"]) > clock():
                raise StateError("provider/backoff retry time has not arrived")
            if self._budget_reasons(run, definition["cost"]):
                raise StateError("task budget/deadline does not permit another attempt")
            self.conn.execute("UPDATE task_steps SET revision=revision+1,state='pending',updated_at=? "
                              "WHERE run_id=? AND step_key=?", (utc_now(), run_id, step_key))
            self._event(run_id, "read_retry_ready", {"step_key": step_key})
            return self.show(run_id)

    def recover(self, run_id):
        with self.transaction():
            self._run(run_id)
            self._sync(run_id)
            for attempt in self.conn.execute(
                    "SELECT * FROM task_attempts WHERE run_id=? AND state='running'", (run_id,)).fetchall():
                if when(attempt["lease_expires"]) > clock():
                    continue
                step = self._step(run_id, attempt["step_key"])
                definition, details = parse_json(step["definition"]), parse_json(attempt["details"])
                if definition["kind"] == "read":
                    coverage = self.conn.execute("SELECT status FROM proactive_attempts WHERE id=?",
                                                 (details["coverage_attempt_id"],)).fetchone()
                    if coverage["status"] == "running":
                        proactive_state.coverage_finish(self.conn, details["coverage_attempt_id"],
                                                         {"status": "failed", "error_class": "timeout"})
                    status = self.conn.execute("SELECT status FROM proactive_attempts WHERE id=?",
                                               (details["coverage_attempt_id"],)).fetchone()[0]
                    result = {"kind": "tool_result", "reference": "task-recovery:" + attempt["id"],
                              "coverage": status, "coverage_attempt_id": details["coverage_attempt_id"],
                              "error_class": "interrupted",
                              "summary": "Read interrupted; no complete task result was recorded."}
                    self._settle(attempt, "partial" if status == "complete" else "failed", result)
                elif definition["kind"] == "action":
                    self._settle(attempt, "outcome_unknown", {
                        "kind": "tool_result", "reference": "task-recovery:" + attempt["id"],
                        "outcome": "outcome_unknown", "recorded_at": utc_now()})
                else:
                    self._settle(attempt, "outcome_unknown", {
                        "kind": "local_result", "reference": "task-recovery:" + attempt["id"],
                        "summary": "Local preparation was interrupted; inspect its canonical records before retrying."})
            return self.show(run_id)

    def _sync(self, run_id):
        for step in self.conn.execute("SELECT * FROM task_steps WHERE run_id=?", (run_id,)).fetchall():
            definition = parse_json(step["definition"])
            if definition["kind"] != "action" or not step["latest_attempt"] or step["state"] in {"succeeded", "failed", "cancelled"}:
                continue
            attempt = self._attempt(step["latest_attempt"])
            execution_id = parse_json(attempt["details"])["action_attempt_id"]
            execution = self.conn.execute("SELECT state,receipt FROM work_executions WHERE id=?", (execution_id,)).fetchone()
            if execution and execution["state"] in {"succeeded", "failed", "partial"} and execution["receipt"]:
                result = parse_json(execution["receipt"])
                if attempt["state"] != execution["state"] or attempt["result"] != canonical_json(result):
                    self._settle(attempt, execution["state"], result)

    def sync(self, run_id):
        with self.transaction():
            self._run(run_id)
            self._sync(run_id)
            return self.show(run_id)

    def reconcile(self, run_id, step_key, revision, outcome, result):
        if outcome not in {"succeeded", "failed", "partial"}:
            raise StateError("reconciliation requires a known result")
        with self.transaction():
            step = self._step(run_id, step_key)
            if (type(revision) is not int or step["revision"] != revision
                    or step["state"] not in {"partial", "outcome_unknown"} or not step["latest_attempt"]):
                raise StateError("reconciliation requires the current unsettled step")
            return self._settle(self._attempt(step["latest_attempt"]), outcome, result)

    def replan_preview(self, run_id, plan):
        run = self._run(run_id)
        if run["state"] != "paused":
            raise StateError("pause the task before revising its plan")
        plan = self._plan(plan)
        old = parse_json(run["plan"])
        if (plan["goal"], plan["routine"], plan["request_ref"], plan["mode"]) != (
                old["goal"], old["routine"], old["request_ref"], old["mode"]):
            raise StateError("a different goal/routine/request/mode needs a new task")
        if plan["window"] != old["window"] and any(
                row["attempts"] and parse_json(row["definition"])["kind"] == "read"
                for row in self.conn.execute("SELECT attempts,definition FROM task_steps WHERE run_id=?", (run_id,))):
            raise StateError("an attempted source window is immutable; create a new task for a different interval")
        replacement = {step["key"]: step for step in plan["steps"]}
        for step in self.conn.execute("SELECT * FROM task_steps WHERE run_id=?", (run_id,)):
            if step["state"] in {"running", "outcome_unknown"} or (
                    step["state"] == "partial" and parse_json(step["definition"])["kind"] != "read"):
                raise StateError("reconcile in-flight or uncertain effects before replanning")
            if step["state"] == "succeeded" and replacement.get(step["step_key"]) != parse_json(step["definition"]):
                raise StateError("completed steps and their results are immutable; add a new step for a fresh read")
            if (step["attempts"] and step["step_key"] in replacement
                    and replacement[step["step_key"]] != parse_json(step["definition"])):
                raise StateError("attempted step definitions are immutable; use a new step key")
        usage = parse_json(run["usage"])
        if any(plan["limits"][metric] < usage[metric] for metric in METRICS):
            raise StateError("revised limits cannot erase already spent/reserved work")
        value = {"account": self.account, "run_id": run_id, "expected_revision": run["revision"],
                 "old_plan_hash": run["plan_hash"], "plan": plan}
        return dict(value, subject_id="task-replan:" + digest(value), revision=1,
                    external_approval_granted=False)

    def replan(self, run_id, plan, evidence):
        with self.transaction():
            preview = self.replan_preview(run_id, plan)
            human(evidence, preview["subject_id"], 1, "replan")
            old = parse_json(self._run(run_id)["plan"])
            changed_binding = binding_hash(old["environment"]) != binding_hash(preview["plan"]["environment"])
            existing = {row["step_key"]: row for row in self.conn.execute("SELECT * FROM task_steps WHERE run_id=?", (run_id,))}
            replacement = {step["key"]: step for step in preview["plan"]["steps"]}
            for key, row in existing.items():
                definition = parse_json(row["definition"])
                if definition["kind"] == "action" and row["state"] != "succeeded" and (
                        changed_binding or replacement.get(key) != definition):
                    action = self.ledger.show(definition["action_id"])
                    if action["revision"] == definition["action_revision"] and action["action_hash"] == definition["action_hash"]:
                        self.ledger.invalidate_approval(action["id"], action["revision"], action["action_hash"], "Task plan changed")
                if key not in replacement:
                    # Keep history and attempts; removed steps remain explicitly cancelled.
                    self.conn.execute("UPDATE task_steps SET state='cancelled',revision=revision+1,updated_at=? "
                                      "WHERE run_id=? AND step_key=?", (utc_now(), run_id, key))
            for key, definition in replacement.items():
                row = existing.get(key)
                if row is None:
                    self.conn.execute("INSERT INTO task_steps VALUES(?,?,1,'pending',?,0,NULL,NULL,NULL,?)",
                                      (run_id, key, canonical_json(definition), utc_now()))
                elif row["state"] != "succeeded":
                    if row["attempts"] and canonical_json(definition) != row["definition"]:
                        raise StateError("attempted step definitions are immutable; use a new step key")
                    self.conn.execute("UPDATE task_steps SET definition=?,state='pending',revision=revision+1,updated_at=? "
                                      "WHERE run_id=? AND step_key=?",
                                      (canonical_json(definition), utc_now(), run_id, key))
            encoded = canonical_json(preview["plan"])
            self.conn.execute("UPDATE task_runs SET plan=?,plan_hash=? WHERE id=?",
                              (encoded, digest(preview["plan"]), run_id))
            self._event(run_id, "replanned", {"subject_id": preview["subject_id"], "binding_changed": changed_binding})
            return self.show(run_id)

    def show(self, run_id):
        with margo_store.transaction(self.conn, read_only=True):
            run = self._run(run_id)
            plan, usage = parse_json(run["plan"]), parse_json(run["usage"])
            active_keys = {step["key"] for step in plan["steps"]}
            steps, ready, expired, unknown = [], [], [], []
            blocked = self._budget_reasons(run)
            running_count = self.conn.execute(
                "SELECT count(*) FROM task_steps WHERE run_id=? AND state='running'", (run_id,)).fetchone()[0]
            for row in self.conn.execute("SELECT * FROM task_steps WHERE run_id=? ORDER BY rowid", (run_id,)):
                definition = parse_json(row["definition"])
                result = parse_json(row["result"]) if row["result"] else None
                step = {"key": row["step_key"], "revision": row["revision"], "state": row["state"],
                        "definition": definition, "attempts": row["attempts"], "latest_attempt": row["latest_attempt"],
                        "result": result, "next_retry_at": row["next_retry_at"], "in_current_plan": row["step_key"] in active_keys}
                reasons = self._prerequisites(run_id, definition) if row["step_key"] in active_keys else []
                if row["state"] == "pending":
                    reasons.extend(self._budget_reasons(run, definition["cost"]))
                    if run["state"] not in {"planned", "active"}:
                        reasons.append("task is " + run["state"])
                    if row["attempts"] >= plan["limits"]["max_attempts_per_step"]:
                        reasons.append("step attempt budget exhausted")
                    if row["next_retry_at"] and when(row["next_retry_at"]) > clock():
                        reasons.append("provider/backoff retry time has not arrived")
                    if running_count >= plan["limits"]["max_parallel"]:
                        reasons.append("parallel-step limit reached")
                if result and result.get("source_refs"):
                    step["source_evidence_changed"] = self.ledger.refs_stale(result["source_refs"])
                if row["state"] == "running" and row["latest_attempt"]:
                    attempt = self._attempt(row["latest_attempt"])
                    step["lease_expires"] = attempt["lease_expires"]
                    if when(attempt["lease_expires"]) <= clock():
                        expired.append(row["step_key"])
                if definition["kind"] == "action":
                    action = self.ledger.show(definition["action_id"])
                    step["action_state"] = action["state"]
                    if row["latest_attempt"]:
                        owned = self._attempt(row["latest_attempt"])
                        execution_id = parse_json(owned["details"]).get("action_attempt_id")
                        execution = self.conn.execute(
                            "SELECT state FROM work_executions WHERE id=? AND action_id=? AND revision=?",
                            (execution_id, definition["action_id"], definition["action_revision"])).fetchone()
                        if execution is None:
                            unknown.append(row["step_key"])
                            reasons.append("owned execution record is unavailable")
                        else:
                            step["owned_action_state"] = execution["state"]
                            if execution["state"] in {"executing", "partial", "outcome_unknown"}:
                                unknown.append(row["step_key"])
                            if execution["state"] in {"succeeded", "failed"} and row["state"] != execution["state"]:
                                reasons.append("canonical execution result is available; synchronize task progress")
                    if row["state"] == "pending" and action["state"] != "approved":
                        reasons.append("linked action already has an execution; inspect its canonical result"
                                       if action["state"] in {"executing", "succeeded", "partial", "outcome_unknown"}
                                       else "exact action approval required")
                    if row["state"] == "pending" and action["state"] == "approved":
                        if not any(approval["invalidated_at"] is None
                                   and approval["revision"] == definition["action_revision"]
                                   and approval["action_hash"] == definition["action_hash"]
                                   and when(approval["expires_at"]) > clock() for approval in action["approvals"]):
                            reasons.append("exact action approval expired or is missing")
                        if action["stale"]:
                            reasons.append("action source evidence changed")
                    if (action["revision"] != definition["action_revision"]
                            or action["action_hash"] != definition["action_hash"]):
                        reasons.append("linked action revision changed")
                if row["state"] == "outcome_unknown":
                    unknown.append(row["step_key"])
                step["blocked_reasons"] = reasons
                if (row["state"] == "pending" and run["state"] in {"planned", "active"}
                        and not reasons and not blocked and row["step_key"] in active_keys):
                    ready.append(row["step_key"])
                steps.append(step)
            current = [step for step in steps if step["in_current_plan"]]
            states = {step["state"] for step in current}
            if run["state"] in {"paused", "cancelled"}:
                status = run["state"]
            elif unknown:
                status = "outcome_unknown"
            elif expired:
                status = "interrupted"
            elif "running" in states:
                status = "running"
            elif states == {"succeeded"}:
                status = "partial" if any(usage[metric] > plan["limits"][metric] for metric in METRICS) else "succeeded"
            elif states <= TERMINAL:
                status = "partial" if states & {"succeeded", "partial"} else "failed"
            elif blocked:
                status = "blocked"
            elif ready:
                status = "planned" if run["state"] == "planned" else "ready"
            elif any("exact action approval required" in step["blocked_reasons"] for step in current):
                status = "waiting_approval"
            else:
                status = "blocked"
            return {"id": run_id, "account": self.account, "revision": run["revision"], "state": run["state"],
                    "status": status, "plan_hash": run["plan_hash"], "plan": plan, "steps": steps,
                    "usage": usage, "remaining": {metric: max(0, plan["limits"][metric] - usage[metric]) for metric in METRICS},
                    "ready_steps": ready, "expired_claims": expired, "unresolved_effects": sorted(set(unknown)),
                    "blocked_reasons": blocked, "created_at": run["created_at"], "updated_at": run["updated_at"],
                    "limits_enforcement": "Tracked claims and reported results only; not a sandbox over other host tools.",
                    "token_usage": None, "model_cost": None, "approval_granted": False}

    def list(self, limit=50, after=None):
        integer(limit, "list limit", 1, 100)
        if after is not None:
            text(after, "list cursor", 100)
        rows = self.conn.execute("SELECT id FROM task_runs WHERE account=? AND id>? ORDER BY id LIMIT ?",
                                 (self.account, after or "", limit + 1)).fetchall()
        runs = []
        for row in rows[:limit]:
            item = self.show(row["id"])
            counts = {}
            for step in item["steps"]:
                if step["in_current_plan"]:
                    counts[step["state"]] = counts.get(step["state"], 0) + 1
            runs.append({key: item[key] for key in (
                "id", "account", "revision", "state", "status", "plan_hash", "usage", "remaining",
                "expired_claims", "unresolved_effects", "blocked_reasons", "created_at", "updated_at")})
            runs[-1].update(goal=item["plan"]["goal"], routine=item["plan"]["routine"],
                            mode=item["plan"]["mode"], step_counts=counts)
        return {"account": self.account, "runs": runs,
                "next_cursor": rows[limit - 1]["id"] if len(rows) > limit else None}

    def health(self):
        with margo_store.transaction(self.conn, read_only=True):
            counts = {row["state"]: row["count"] for row in self.conn.execute(
                "SELECT state,count(*) AS count FROM task_runs WHERE account=? GROUP BY state", (self.account,))}
            expired = self.conn.execute(
                "SELECT count(*) FROM task_attempts a JOIN task_runs r ON r.id=a.run_id "
                "JOIN task_steps s ON s.run_id=a.run_id AND s.step_key=a.step_key AND s.latest_attempt=a.id "
                "WHERE r.account=? AND a.state='running' AND a.lease_expires<=?",
                (self.account, utc_now())).fetchone()[0]
            unsettled = self.conn.execute(
                "SELECT a.state,s.definition FROM task_attempts a JOIN task_runs r ON r.id=a.run_id "
                "JOIN task_steps s ON s.run_id=a.run_id AND s.step_key=a.step_key AND s.latest_attempt=a.id "
                "WHERE r.account=? AND a.state IN ('partial','outcome_unknown')", (self.account,)).fetchall()
            unknown = sum(row["state"] == "outcome_unknown" or parse_json(row["definition"])["kind"] != "read"
                          for row in unsettled)
            return {"account": self.account, "status": "attention-needed" if expired or unknown else "available",
                    "runs_by_state": counts, "expired_claims": expired, "unreconciled_attempts": unknown,
                    "action": "Inspect task_state.py list; recover expired claims and reconcile uncertain results."
                    if expired or unknown else None,
                    "note": "These are tracked-task journal states, not proof of current provider state."}

    def history(self, run_id, limit=100):
        self._run(run_id)
        integer(limit, "history limit", 1, 500)
        return {"run_id": run_id, "events": [dict(row, data=parse_json(row["data"])) for row in self.conn.execute(
            "SELECT revision,event,data,created_at FROM task_events WHERE run_id=? ORDER BY seq DESC LIMIT ?", (run_id, limit))]}
