import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import vm from "node:vm";
import {
    createTaskBackend, resolveTaskScript, validateTaskInput, validateTaskResult,
} from "./task-backend.mjs";
import { BackendError } from "./backend.mjs";
import { startServer, taskReviewPrompt } from "./server.mjs";

const HASH = "b".repeat(64);

function sampleRun(overrides = {}) {
    const base = {
        id: "task_abc123def456", account: "synthetic-account", revision: 3, state: "active", status: "ready",
        plan_hash: HASH,
        plan: {
            goal: "Prepare synthetic quarterly notes", routine: "meeting-prep", request_ref: "conversation:synthetic-1",
            mode: "foreground",
            environment: { account: "synthetic-account", host: "synthetic-host", capabilities: { cli: "1" }, observed_at: "2026-01-01T00:00:00Z" },
            window: { start: "2025-12-01T00:00:00Z", end: "2026-01-01T00:00:00Z" },
            limits: {
                max_steps: 5, max_attempts_per_step: 3, max_parallel: 1, tool_calls: 10, pages: 10,
                items: 50, model_calls: 5, output_chars: 20000, deadline_at: "2026-02-01T00:00:00Z", lease_seconds: 300,
            },
            steps: [{
                key: "collect-notes", title: "Collect notes", kind: "read", capability: "mail.search",
                depends_on: [], allow_partial: false,
                cost: { tool_calls: 1, pages: 1, items: 5, model_calls: 0, output_chars: 0 },
                source: { family: "mail", scope: "inbox", kind: "search", query_version: 1 },
            }],
        },
        steps: [{
            key: "collect-notes", revision: 1, state: "pending",
            definition: { kind: "read", capability: "mail.search" },
            attempts: 0, latest_attempt: null, result: null, next_retry_at: null,
            in_current_plan: true, blocked_reasons: [],
        }],
        usage: { tool_calls: 0, pages: 0, items: 0, model_calls: 0, output_chars: 0 },
        remaining: { tool_calls: 10, pages: 10, items: 50, model_calls: 5, output_chars: 20000 },
        ready_steps: ["collect-notes"], expired_claims: [], unresolved_effects: [], blocked_reasons: [],
        created_at: "2026-01-01T00:00:00Z", updated_at: "2026-01-01T00:00:00Z",
        limits_enforcement: "Tracked claims and reported results only; not a sandbox over other host tools.",
        token_usage: null, model_cost: null, approval_granted: false,
    };
    return { ...base, ...overrides };
}

function listRow(run) {
    return {
        id: run.id, account: run.account, revision: run.revision, state: run.state, status: run.status,
        plan_hash: run.plan_hash, goal: run.plan.goal, routine: run.plan.routine, mode: run.plan.mode,
        step_counts: { pending: 1 }, usage: run.usage, remaining: run.remaining,
        expired_claims: run.expired_claims, unresolved_effects: run.unresolved_effects,
        blocked_reasons: run.blocked_reasons, created_at: run.created_at, updated_at: run.updated_at,
    };
}

// ---------------------------------------------------------------------------
// Backend/CLI adapter
// ---------------------------------------------------------------------------

test("task script resolves adjacent to the resolved work_state.py core", async () => {
    const script = await resolveTaskScript(async () => "/project/skills/chief-of-staff/scripts/work_state.py");
    assert.equal(script, "/project/skills/chief-of-staff/scripts/task_state.py");
});

test("task command argv is fixed, shell-free and bounded for every read", async () => {
    const calls = [];
    const backend = createTaskBackend({
        resolveScript: async () => "/project/skills/chief-of-staff/scripts/work_state.py",
        execute: async (...args) => {
            calls.push(args);
            const [, argv] = args;
            if (argv.includes("list")) return { stdout: JSON.stringify({ account: "a", runs: [], next_cursor: null }) };
            if (argv.includes("show")) return { stdout: JSON.stringify(sampleRun()) };
            if (argv.includes("history")) return { stdout: JSON.stringify({ run_id: "task_abc123def456", events: [] }) };
            return { stdout: JSON.stringify({ account: "a", status: "available", runs_by_state: {}, expired_claims: 0, unreconciled_attempts: 0, action: null, note: "note" }) };
        },
    });
    await backend.run("list", {});
    assert.deepEqual(calls.at(-1)[1], ["-B", "/project/skills/chief-of-staff/scripts/task_state.py", "list", "--limit", "20"]);
    assert.equal(calls.at(-1)[2].shell, false);
    assert.equal(calls.at(-1)[2].timeout, 10_000);
    assert.equal(calls.at(-1)[2].windowsHide, true);
    await backend.run("list", { limit: 5, after: "task_prior000000000000000000000" });
    assert.deepEqual(calls.at(-1)[1].slice(2), ["list", "--limit", "5", "--after", "task_prior000000000000000000000"]);
    await backend.run("show", { id: "task_abc123def456" });
    assert.deepEqual(calls.at(-1)[1].slice(2), ["show", "task_abc123def456"]);
    await backend.run("history", { id: "task_abc123def456" });
    assert.deepEqual(calls.at(-1)[1].slice(2), ["history", "task_abc123def456", "--limit", "20"]);
    await backend.run("history", { id: "task_abc123def456", limit: 5 });
    assert.deepEqual(calls.at(-1)[1].slice(2), ["history", "task_abc123def456", "--limit", "5"]);
    await backend.run("health", {});
    assert.deepEqual(calls.at(-1)[1].slice(2), ["health"]);
    assert.ok(calls.every((call) => call[0] === (process.platform === "win32" ? "python" : "python3")));
    for (const call of calls) assert.ok(!call[1].some((token) => typeof token === "string" && token.includes("$(")));
});

test("invalid fields, IDs, cursors and limits are rejected before any command executes; no mutation command exists", () => {
    for (const [operation, input] of [
        ["list", { db: "/somewhere" }],
        ["list", { limit: 0 }],
        ["list", { limit: 51 }],
        ["list", { after: "../etc/passwd" }],
        ["show", { id: "../file" }],
        ["show", {}],
        ["show", { id: "ok", extra: 1 }],
        ["history", { id: "ok", limit: 0 }],
        ["history", { id: "ok", limit: 101 }],
        ["health", { id: "ok" }],
    ]) {
        assert.throws(() => validateTaskInput(operation, input), { code: "invalid_input" });
    }
    for (const operation of ["pause", "cancel", "resume", "replan", "recover", "reconcile", "start", "charge", "finish", "create", "init", "approve", "execute"]) {
        assert.throws(() => validateTaskInput(operation, {}), { code: "invalid_input" });
    }
});

test("not-initialized state is surfaced without creating any state, and unknown runs are not_found", async () => {
    const backend = (execute) => createTaskBackend({ resolveScript: async () => "/code/work_state.py", execute });
    await assert.rejects(backend(async () => {
        throw Object.assign(new Error("private stderr"), { stderr: JSON.stringify({ error: "task state is not initialized", command: "list", code: "not_initialized" }) });
    }).run("list"), { code: "not_initialized", status: 503 });
    await assert.rejects(backend(async () => {
        throw Object.assign(new Error("private stderr"), { stderr: JSON.stringify({ error: "unknown task run", command: "show" }) });
    }).run("show", { id: "task_missing00000000000000000000" }), { code: "not_found", status: 404 });
    await assert.rejects(backend(async () => {
        throw Object.assign(new Error(), { code: "ENOENT" });
    }).run("list"), { code: "setup_needed" });
    await assert.rejects(backend(async () => {
        throw Object.assign(new Error(), { killed: true });
    }).run("list"), { code: "backend_timeout", status: 504 });
    await assert.rejects(backend(async () => ({ stdout: "not JSON" })).run("list"), { code: "invalid_backend_response" });
    await assert.rejects(backend(async () => {
        throw Object.assign(new Error("private detail that must not leak"), { stderr: "not JSON either" });
    }).run("list"), (error) => !error.message.includes("private detail"));
});

test("responses missing required contract fields, or leaking a token, are rejected", () => {
    const run = sampleRun();
    assert.doesNotThrow(() => validateTaskResult("show", run, { id: run.id }));
    assert.throws(() => validateTaskResult("show", { ...run, token_usage: 0 }, { id: run.id }), { code: "invalid_backend_response" });
    assert.throws(() => validateTaskResult("show", { ...run, approval_granted: true }, { id: run.id }), { code: "invalid_backend_response" });
    assert.throws(() => validateTaskResult("show", { ...run, steps: [{ ...run.steps[0], token: "secret" }] }, { id: run.id }), { code: "invalid_backend_response" });
    assert.throws(() => validateTaskResult("show", { ...run, steps: [{ ...run.steps[0], claim_token: "secret" }] }, { id: run.id }), { code: "invalid_backend_response" });
    assert.throws(() => validateTaskResult("show", { ...run, token: "secret" }, { id: run.id }), { code: "invalid_backend_response" });
    assert.throws(() => validateTaskResult("show", { ...run, steps: [{ ...run.steps[0], result: { access_token: "secret" } }] }, { id: run.id }), { code: "invalid_backend_response" });
    assert.throws(() => validateTaskResult("show", { ...run, usage: { ...run.usage, items: -1 } }, { id: run.id }), { code: "invalid_backend_response" });
    assert.doesNotThrow(() => validateTaskResult("show",
        { ...run, steps: [{ ...run.steps[0], action_state: "approved", owned_action_state: "executing" }] }, { id: run.id }));
    assert.throws(() => validateTaskResult("list", { account: "a", runs: [run], next_cursor: null }, {}), { code: "invalid_backend_response" });
    assert.doesNotThrow(() => validateTaskResult("list", { account: "a", runs: [listRow(run)], next_cursor: null }, {}));
    assert.throws(() => validateTaskResult("history", { run_id: run.id, events: [{ revision: 1, event: "x", created_at: "t", token: "secret" }] }, { id: run.id }), { code: "invalid_backend_response" });
    assert.doesNotThrow(() => validateTaskResult("history", { run_id: run.id, events: [{ revision: 1, event: "created", data: {}, created_at: "t" }] }, { id: run.id }));
    assert.throws(() => validateTaskResult("health", { account: "a" }, {}), { code: "invalid_backend_response" });
    assert.doesNotThrow(() => validateTaskResult("health", { account: "a", status: "available", runs_by_state: {}, expired_claims: 0, unreconciled_attempts: 0 }, {}));
});

// ---------------------------------------------------------------------------
// HTTP surface
// ---------------------------------------------------------------------------

function resultFor(data, operation, input = {}) {
    if (operation === "show") return data.run;
    if (operation === "list") return { account: data.run.account, runs: [listRow(data.run)], next_cursor: null };
    if (operation === "history") return { run_id: data.run.id, events: data.events };
    if (operation === "health") return data.health;
    throw new BackendError("invalid_input", "unsupported in fixture", 400);
}

async function serverFixture(t, options = {}) {
    const data = {
        run: sampleRun(),
        events: [{ revision: 1, event: "created", data: { goal: sampleRun().plan.goal }, created_at: "2026-01-01T00:00:00Z" }],
        health: { account: "synthetic-account", status: "available", runs_by_state: { active: 1 }, expired_claims: 0, unreconciled_attempts: 0, action: null, note: "Tracked-task journal states, not proof of current provider state." },
    };
    const calls = [];
    const sent = [];
    const panel = await startServer({
        backend: { run: async () => ({ items: [] }) },
        taskBackend: {
            run: async (operation, input) => {
                calls.push({ operation, input });
                return options.run ? options.run(operation, input, data) : resultFor(data, operation, input);
            },
        },
        sendReview: async (value) => {
            if (options.sendReview) await options.sendReview(value);
            sent.push(value);
        },
    });
    t.after(() => panel.close());
    const base = new URL(panel.url);
    const token = new URLSearchParams(base.hash.slice(1)).get("token");
    const headers = { Origin: base.origin, Authorization: "Bearer " + token, "Content-Type": "application/json" };
    const request = (operation, input = {}, overrides = {}) => fetch(new URL("/api/task/" + operation, base), {
        method: "POST", headers, body: JSON.stringify(input), ...overrides,
    });
    return { data, calls, sent, request, panel, base, token };
}

test("HTTP serves the tasks static surface without leaking the token, and shares access controls", async (t) => {
    const { base, token, request } = await serverFixture(t);
    const headers = { Origin: base.origin, Authorization: "Bearer " + token, "Content-Type": "application/json" };
    const page = await fetch(new URL("/tasks", base));
    const html = await page.text();
    assert.ok(!html.includes(token));
    assert.match(html, /--cp-bg:/);
    assert.doesNotMatch(html, /__THEME__|__NONCE__/);
    assert.equal(page.headers.get("cache-control"), "no-store");
    assert.ok(page.headers.get("content-security-policy").includes("default-src 'none'"));
    const script = await (await fetch(new URL("/tasks.js", base))).text();
    assert.match(script, /api\/task\//);
    assert.equal((await request("list", {}, { headers: { Authorization: "" } })).status, 403);
    assert.equal((await request("list", {}, { headers: { Origin: "https://example.com" } })).status, 403);
    assert.equal((await fetch(new URL("/api/task/list?x=1", base), { method: "POST", headers, body: "{}" })).status, 400);
    assert.equal((await request("pause", { id: "task_x" })).status, 404);
    assert.equal((await request("execute", {})).status, 404);
    assert.equal((await fetch(new URL("/api/task/list", base), { method: "GET", headers })).status, 405);
});

test("list/show/history/health return only the documented read contracts", async (t) => {
    const { data, request, calls } = await serverFixture(t);
    const list = await (await request("list", {})).json();
    assert.deepEqual(list.runs, [listRow(data.run)]);
    assert.equal(list.next_cursor, null);
    const show = await (await request("show", { id: data.run.id })).json();
    assert.equal(show.id, data.run.id);
    assert.equal(show.token_usage, null);
    assert.equal(show.approval_granted, false);
    const history = await (await request("history", { id: data.run.id, limit: 5 })).json();
    assert.equal(history.events.length, 1);
    const health = await (await request("health", {})).json();
    assert.equal(health.status, "available");
    assert.ok(calls.every((call) => ["list", "show", "history", "health"].includes(call.operation)));
    assert.equal((await request("list", { limit: 51 })).status, 400);
    assert.equal((await request("show", { id: "../x" })).status, 400);
});

test("missing initialization is surfaced as setup guidance, never as an empty list", async (t) => {
    const { request } = await serverFixture(t, {
        run: async () => { throw new BackendError("not_initialized", "Task tracking is not initialized for this account.", 503); },
    });
    const response = await request("list", {});
    assert.equal(response.status, 503);
    const payload = await response.json();
    assert.equal(payload.error.code, "not_initialized");
});

test("review requires an exact current revision/plan hash, a valid intent, and never runs from a registered read action", async (t) => {
    const { data, request, sent, calls } = await serverFixture(t);
    const exact = { id: data.run.id, revision: data.run.revision, plan_hash: data.run.plan_hash };
    for (const input of [null, [], "review", { ...exact, intent: "approve" }, { ...exact, intent: "execute" },
        { ...exact }, { ...exact, intent: "cancel", extra: 1 }, { ...exact, intent: "cancel", revision: 0 },
        { ...exact, intent: "cancel", plan_hash: "z".repeat(64) }, { ...exact, intent: "cancel", id: "../x" },
        { id: data.run.id, intent: "cancel" }]) {
        assert.equal((await request("review", input)).status, 400);
    }
    assert.equal(sent.length, 0);
    assert.equal((await request("review", { ...exact, revision: 999, intent: "cancel" })).status, 409);
    assert.equal((await request("review", { ...exact, plan_hash: "a".repeat(64), intent: "cancel" })).status, 409);
    assert.equal(sent.length, 0);
    const response = await request("review", { ...exact, intent: "cancel" });
    assert.equal(response.status, 200);
    const payload = await response.json();
    assert.equal(payload.approved, false);
    assert.match(payload.message, /NOT approval/);
    assert.match(payload.message, /NOT the operation/);
    assert.equal(sent.length, 1);
    assert.equal(sent[0].agentMode, "interactive");
    assert.match(sent[0].prompt, /NOT approval/);
    assert.match(sent[0].prompt, /NOT the requested operation/);
    assert.match(sent[0].prompt, /never unsends, undoes or erases/);
    assert.deepEqual(JSON.parse(sent[0].prompt.split("\n").at(-1)),
        { account: data.run.account, id: data.run.id, revision: data.run.revision, plan_hash: data.run.plan_hash, intent: "cancel" });
    assert.ok(calls.filter((call) => call.operation === "show").length >= 1);
    assert.ok(!taskReviewPrompt(data.run, "cancel").includes("ignore previous instructions"));
});

test("review rejects concurrent submissions, then rechecks the current revision on retry", async (t) => {
    let release;
    let entered;
    const started = new Promise((resolve) => { entered = resolve; });
    const gate = new Promise((resolve) => { release = resolve; });
    const { data, request, sent, calls } = await serverFixture(t, {
        run: async (operation, input, value) => {
            if (operation === "show") { entered(); await gate; }
            return resultFor(value, operation, input);
        },
    });
    t.after(() => release());
    const exact = { id: data.run.id, revision: data.run.revision, plan_hash: data.run.plan_hash, intent: "reconcile" };
    const first = request("review", exact);
    await started;
    assert.equal((await request("review", exact)).status, 409);
    assert.equal(calls.filter((call) => call.operation === "show").length, 1);
    data.run.revision = data.run.revision + 1;
    release();
    assert.equal((await first).status, 409);
    assert.equal(sent.length, 0);
    assert.equal((await request("review", { ...exact, revision: data.run.revision, plan_hash: "c".repeat(64) })).status, 409);
    assert.equal(sent.length, 0);
    assert.equal((await request("review", { ...exact, revision: data.run.revision })).status, 200);
    assert.equal(sent.length, 1);
});

test("review transport failure is not silently treated as approval", async (t) => {
    const { data, request } = await serverFixture(t, { sendReview: async () => { throw new Error("private transport detail"); } });
    const response = await request("review", { id: data.run.id, revision: data.run.revision, plan_hash: data.run.plan_hash, intent: "pause" });
    assert.equal(response.status, 503);
    const payload = await response.json();
    assert.equal(payload.error.code, "review_unavailable");
    assert.ok(!payload.error.message.includes("private transport detail"));
});

// ---------------------------------------------------------------------------
// Static analysis and agent-facing action safety
// ---------------------------------------------------------------------------

test("agent-facing task actions are read-only; review is reachable only through the browser panel", async () => {
    const source = await readFile(new URL("./extension.mjs", import.meta.url), "utf8");
    const start = source.indexOf('id: "margo-task-progress"');
    assert.ok(start > -1);
    const section = source.slice(start, source.indexOf("open: async ctx =>", start));
    const names = [...section.matchAll(/name: "([^"]+)"/g)].map((match) => match[1]);
    assert.deepEqual(names, ["list", "show", "history", "health"]);
    assert.doesNotMatch(section, /"review"|"pause"|"cancel"|"resume"|"replan"|"recover"/);
});

test("task UI renders stored content only through textContent, never as executable HTML", async () => {
    const source = await readFile(new URL("./task-app.js", import.meta.url), "utf8");
    assert.match(source, /textContent/);
    assert.doesNotMatch(source, /innerHTML|outerHTML|insertAdjacentHTML|document\.write|eval\(/);
});

// ---------------------------------------------------------------------------
// DOM behavior: hostile content, budget/unknown/cancellation states, paging
// ---------------------------------------------------------------------------

function domFixture() {
    const text = [];
    const byId = new Map();
    class Node {
        constructor(tag) { this.tag = tag; this.children = []; this.attrs = {}; this.listeners = {}; this._hidden = false; }
        set textContent(value) { this.text = value; text.push(value); }
        get textContent() { return this.text; }
        set id(value) { byId.set(value, this); }
        set innerHTML(_value) { throw new Error("HTML injection"); }
        set hidden(value) { this._hidden = value; }
        get hidden() { return this._hidden; }
        set className(value) { this._className = value; }
        get className() { return this._className; }
        append(...nodes) { this.children.push(...nodes); }
        replaceChildren(...nodes) { this.children = nodes; }
        addEventListener(name, callback) { this.listeners[name] = callback; }
        setAttribute(name, value) { this.attrs[name] = String(value); }
        getAttribute(name) { return this.attrs[name]; }
        focus() { this.focused = true; }
        querySelectorAll(selector) {
            const all = this.descendants();
            if (selector === "button,input,select") return all.filter((node) => ["button", "input", "select"].includes(node.tag));
            return all;
        }
        descendants() {
            return this.children.flatMap((child) => child instanceof Node ? [child, ...child.descendants()] : []);
        }
        hasAttribute(name) { return name in this.attrs; }
    }
    for (const id of ["account", "health-summary", "health-detail", "health", "views", "notice", "workspace", "count", "runs", "more", "detail"]) {
        byId.set(id, new Node(id === "health" || id === "more" ? "button" : "div"));
    }
    const document = {
        getElementById: (id) => byId.get(id),
        createElement: (tag) => new Node(tag),
        createTextNode: (value) => { text.push(value); return new Node("text"); },
        documentElement: new Node("html"),
        querySelectorAll: (selector) => document.documentElement.descendants().concat([...byId.values()])
            .filter((node) => selector === "button,input,select" ? ["button", "input", "select"].includes(node.tag) : false),
    };
    return { Node, byId, document, text };
}

async function loadTaskApp({ document, text }, fetchImpl) {
    const source = await readFile(new URL("./task-app.js", import.meta.url), "utf8");
    vm.runInNewContext(source, {
        document, location: { hash: "#token=test-token" }, URL, URLSearchParams, AbortSignal,
        MutationObserver: class { observe() {} }, setInterval() {}, fetch: fetchImpl, console,
    });
    await new Promise(setImmediate);
    await new Promise(setImmediate);
    return text;
}

test("DOM renders hostile content as text, shows cancellation-with-unknown-effects and unmeasured budgets, and never fakes health", async () => {
    const dom = domFixture();
    const malicious = '<img src=x onerror="alert(1)"><script>bad()</script>';
    const run = sampleRun({
        id: "task_hostile00000000000000000000",
        status: "cancelled", state: "cancelled",
        plan: { ...sampleRun().plan, goal: malicious },
        unresolved_effects: ["collect-notes"],
        expired_claims: ["collect-notes"],
        steps: [{ ...sampleRun().steps[0], blocked_reasons: [malicious], action_state: malicious, owned_action_state: malicious }],
    });
    const requests = [];
    const fetchImpl = async (path, init) => {
        requests.push(path);
        if (path.endsWith("/api/task/health")) {
            return { ok: false, json: async () => ({ error: { code: "task_unavailable", message: "Health check failed." } }) };
        }
        if (path.endsWith("/api/task/list")) return { ok: true, json: async () => ({ account: "synthetic-account", runs: [listRow(run)], next_cursor: null }) };
        if (path.endsWith("/api/task/show")) return { ok: true, json: async () => run };
        return { ok: true, json: async () => ({ run_id: run.id, events: [] }) };
    };
    const text = await loadTaskApp(dom, fetchImpl);
    assert.ok(text.includes(malicious));
    assert.ok(dom.byId.get("health-summary").text.includes("unavailable"));
    assert.ok(!dom.byId.get("health-summary").text.toLowerCase().includes("all clear"));
    const runsList = dom.byId.get("runs");
    const button = runsList.children.find((node) => node.tag === "button");
    await button.listeners.click();
    await new Promise(setImmediate);
    const detail = dom.byId.get("detail");
    const flat = detail.descendants();
    assert.ok(flat.some((node) => node.className === "warn" && node.text?.includes("Cancelled with unresolved effect")));
    assert.ok(flat.some((node) => node.className === "warn" && node.text?.includes("Expired claim")));
    assert.ok(flat.some((node) => node.text?.includes("not measured within this tracked path")));
    assert.ok(flat.some((node) => node.text?.includes("Approval granted: false")));
    assert.ok(flat.some((node) => node.text?.includes(`linked action state: ${malicious}`)));
    assert.ok(flat.some((node) => node.text?.includes(`this run's own execution: ${malicious}`)));
    assert.ok(requests.every((path) => path.startsWith("/api/task/")));
});

test("DOM distinguishes no tasks from an uninitialized/unavailable account, without initializing anything", async () => {
    const dom = domFixture();
    const fetchImpl = async (path) => {
        if (path.endsWith("/api/task/health")) throw Object.assign(new Error("net"), { name: "AbortError" });
        if (path.endsWith("/api/task/list")) return { ok: true, json: async () => ({ account: "synthetic-account", runs: [], next_cursor: null }) };
        return { ok: true, json: async () => ({}) };
    };
    await loadTaskApp(dom, fetchImpl);
    assert.match(dom.byId.get("notice").text, /No task runs yet/);
    assert.doesNotMatch(dom.byId.get("notice").text, /error/i);
});

test("DOM paging loads the next page via next_cursor without duplicating already-loaded runs", async () => {
    const dom = domFixture();
    const runA = sampleRun({ id: "task_pageaaaaaaaaaaaaaaaaaaaaaaa" });
    const runB = sampleRun({ id: "task_pagebbbbbbbbbbbbbbbbbbbbbbb" });
    let page = 0;
    const fetchImpl = async (path, init) => {
        if (path.endsWith("/api/task/health")) return { ok: true, json: async () => ({ account: "synthetic-account", status: "available", runs_by_state: {}, expired_claims: 0, unreconciled_attempts: 0 }) };
        if (path.endsWith("/api/task/list")) {
            page += 1;
            if (page === 1) return { ok: true, json: async () => ({ account: "synthetic-account", runs: [listRow(runA)], next_cursor: runA.id }) };
            const body = JSON.parse(init.body);
            assert.equal(body.after, runA.id);
            return { ok: true, json: async () => ({ account: "synthetic-account", runs: [listRow(runB)], next_cursor: null }) };
        }
        return { ok: true, json: async () => ({}) };
    };
    await loadTaskApp(dom, fetchImpl);
    assert.equal(dom.byId.get("more").hidden, false);
    assert.equal(dom.byId.get("runs").children.filter((node) => node.tag === "button").length, 1);
    await dom.byId.get("more").listeners.click();
    await new Promise(setImmediate);
    await new Promise(setImmediate);
    assert.equal(dom.byId.get("more").hidden, true);
    assert.equal(dom.byId.get("runs").children.filter((node) => node.tag === "button").length, 2);
    assert.equal(page, 2);
});

test("core resolution for task_state.py inherits the same project/installed/home search order", async () => {
    const checked = [];
    const extensionDirectory = resolve("project/.github/extensions/margo-action-desk");
    const script = await resolveTaskScript(async () => {
        const path = resolve("project/skills/chief-of-staff/scripts/work_state.py");
        checked.push(path);
        return path;
    });
    assert.equal(script, resolve("project/skills/chief-of-staff/scripts/task_state.py"));
    assert.equal(checked.length, 1);
});

test("DOM preserves the real limit on overrun and does not call partial work completed", async () => {
    const dom = domFixture();
    const run = sampleRun({ status: "partial",
        usage: { ...sampleRun().usage, tool_calls: 12 },
        remaining: { ...sampleRun().remaining, tool_calls: 0 } });
    await loadTaskApp(dom, async (path) => ({
        ok: true,
        json: async () => path.endsWith("/health")
            ? { account: run.account, status: "available", runs_by_state: {}, expired_claims: 0, unreconciled_attempts: 0 }
            : path.endsWith("/list") ? { account: run.account, runs: [listRow(run)], next_cursor: null }
            : run,
    }));
    await dom.byId.get("runs").children.find((node) => node.tag === "button").listeners.click();
    const all = dom.byId.get("detail").descendants();
    assert.ok(all.some((node) => node.text?.includes("usage exceeded the displayed plan limit")));
    const budget = all.find((node) => node.tag === "table"
        && node.children.some((child) => child.tag === "caption" && child.text === "Usage vs tracked remaining"));
    const toolRow = budget.children.find((node) => node.tag === "tbody").children[0];
    assert.equal(toolRow.children[1].text, "12");
    assert.equal(toolRow.children[3].text, "10");
    await dom.byId.get("views").children.find((node) => node.text === "Completed").listeners.click();
    assert.equal(dom.byId.get("runs").children.filter((node) => node.tag === "button").length, 0);
    await dom.byId.get("views").children.find((node) => node.text === "Attention").listeners.click();
    assert.equal(dom.byId.get("runs").children.filter((node) => node.tag === "button").length, 1);
});
