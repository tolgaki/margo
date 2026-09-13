import test from "node:test";
import assert from "node:assert/strict";
import { mkdir, realpath, rm, stat, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { randomUUID } from "node:crypto";
import { createBackend, executeWithInput } from "./backend.mjs";
import { createMemoryBackend } from "./memory-backend.mjs";
import { createTaskBackend } from "./task-backend.mjs";
import { startServer } from "./server.mjs";

const directory = dirname(fileURLToPath(import.meta.url));
const script = resolve(directory, "../../../skills/chief-of-staff/scripts/work_state.py");
const python = process.platform === "win32" ? "python" : "python3";
const testParent = process.env.MARGO_CANVAS_TEST_PARENT;
let corePresent = true;
const coreFiles = ["work_state.py", "work_ledger.py", "work_productivity.py", "margo_store.py"]
    .map((name) => join(dirname(script), name));
for (const path of coreFiles) {
    try { await stat(path); } catch (error) {
        if (error.code !== "ENOENT") throw error;
        corePresent = false;
    }
}

test("real CLI rejects an unsafe explicit config without creating user state", { skip: !corePresent }, async () => {
    const env = { ...process.env, MARGO_CONFIG: join(directory, `not-configured-${randomUUID()}.json`) };
    delete env.MARGO_ACCOUNT;
    delete env.MARGO_ALLOW_UNSAFE_STATE_DIR;
    const backend = createBackend({
        resolveScript: async () => script,
        python,
        execute: (command, args, options) => executeWithInput(command, args, { ...options, env }),
    });
    await assert.rejects(backend.run("list"), { code: "location_unavailable", status: 503 });
});

const skip = !corePresent ? "Portable core modules are not installed."
    : !testParent ? "Set MARGO_CANVAS_TEST_PARENT to an existing private non-repository fixture directory." : false;

test("real CLI persists revisions across panels without touching configured user state", { skip }, async (t) => {
    const parent = await realpath(testParent);
    const fixture = join(parent, `canvas-test-${randomUUID()}`);
    await mkdir(fixture, { mode: 0o700 });
    const stateRoot = await realpath(fixture);
    t.after(() => rm(stateRoot, { recursive: true, force: true }));
    const scope = ["--account", "canvas-integration-test", "--state-root", stateRoot];
    const env = { ...process.env };
    delete env.MARGO_ALLOW_UNSAFE_STATE_DIR;
    async function execute(command, args, options = {}) {
        return executeWithInput(command, [...args.slice(0, 2), ...scope, ...args.slice(2)], {
            ...options, env, shell: false, encoding: "utf8",
        });
    }
    async function cli(command, document) {
        const result = await execute(python, ["-B", script, command, "--input", "-"], {
            input: JSON.stringify(document),
        });
        return JSON.parse(result.stdout);
    }
    const ref = await cli("source", {
        family: "canvas-test", scope: "isolated-test", external_id: "source-1", revision: "1",
        evidence: "Local automated test only", web_link: "https://example.invalid/test",
    });
    const action = await cli("propose", {
        kind: "mail", target: { to: ["nobody@example.com"] }, payload: { subject: "Integration test", body: "Unsent" },
        why: "Testing the local ledger contract", source_refs: [ref], target_fingerprint: "local-test",
        work_item_id: null,
    });
    const backend = createBackend({ resolveScript: async () => script, execute, python });
    const first = await startServer({ backend, sendReview: async () => {} });
    const second = await startServer({ backend, sendReview: async () => {} });
    t.after(async () => { await first.close(); await second.close(); });
    async function api(panel, path, body) {
        const url = new URL(panel.url);
        const response = await fetch(new URL(path, url), {
            method: body ? "POST" : "GET",
            headers: {
                Origin: url.origin,
                Authorization: `Bearer ${new URLSearchParams(url.hash.slice(1)).get("token")}`,
                ...(body ? { "Content-Type": "application/json" } : {}),
            },
            ...(body ? { body: JSON.stringify(body) } : {}),
        });
        const data = await response.json();
        assert.equal(response.status, 200, JSON.stringify(data));
        return data;
    }
    const version = (item) => ({ expected_revision: item.revision, expected_hash: item.action_hash });
    assert.equal((await api(first, "/api/items")).items.length, 1);
    const sourceDetail = (await api(first, `/api/items/${ref.source_id}`)).item;
    assert.equal(sourceDetail.id, ref.source_id);
    assert.equal(sourceDetail.type, "source");
    assert.equal(sourceDetail.revisions[0].data.web_link, "https://example.invalid/test");
    const edited = (await api(first, `/api/items/${action.id}/revise`, {
        ...version(action), payload: { subject: "Revised test", body: "<script>not executed</script>" },
    })).item;
    assert.equal(edited.revision, action.revision + 1);
    assert.notEqual(edited.action_hash, action.action_hash);
    assert.equal((await api(second, `/api/items/${action.id}`)).item.payload.body, "<script>not executed</script>");
    await assert.rejects(backend.run("dismiss", { id: action.id, ...version(action) }), { code: "revision_conflict" });
    const deferred = (await api(second, `/api/items/${action.id}/defer`, {
        ...version(edited), until: "2999-01-01T00:00:00Z",
    })).item;
    assert.equal(deferred.state, "deferred");
    const dismissed = (await api(first, `/api/items/${action.id}/dismiss`, version(deferred))).item;
    assert.equal(dismissed.state, "dismissed");
    const reopened = createBackend({ resolveScript: async () => script, execute, python });
    assert.equal((await reopened.run("show", { id: action.id })).item.state, "dismissed");
    assert.equal((await reopened.run("show", { id: action.id })).item.approvals.length, 0);
});

test("real memory CLI, graph, review and forgetting share one private account across panels", { skip }, async (t) => {
    const parent = await realpath(testParent);
    const fixture = join(parent, `memory-canvas-test-${randomUUID()}`);
    await mkdir(fixture, { mode: 0o700 });
    t.after(() => rm(fixture, { recursive: true, force: true }));
    const memoryScript = join(dirname(script), "memory_state.py");
    const env = { ...process.env };
    delete env.MARGO_ALLOW_UNSAFE_STATE_DIR;
    const scope = ["--account", "memory-canvas-integration-test", "--state-dir", fixture];
    async function execute(command, args, options = {}) {
        return executeWithInput(command, [...args.slice(0, 2), ...scope, ...args.slice(2)], {
            ...options, env, shell: false, encoding: "utf8",
        });
    }
    async function cli(args, input) {
        const response = await execute(python, ["-B", memoryScript, ...args], {
            input: input === undefined ? undefined : JSON.stringify(input),
        });
        return JSON.parse(response.stdout);
    }
    const data = {
        domain: "user", kind: "profile", authority: "source_observed",
        title: "Synthetic preparation context", text: "Prepare before the fictional review.",
        scope: "synthetic", source_refs: [{ kind: "tool_result", ref: "synthetic:memory-canvas" }],
    };
    const memory = await cli(["put", "fixture", "--input", "-"], { data, status: "active" });
    const reviews = [];
    const options = {
        backend: { run: async () => ({ items: [] }) },
        memoryBackend: createMemoryBackend({ resolveScript: async () => script, execute }),
        sendReview: async request => { reviews.push(request); },
    };
    const first = await startServer(options);
    const second = await startServer(options);
    t.after(async () => { await first.close(); await second.close(); });
    async function api(panel, operation, input = {}, expectedStatus = 200) {
        const base = new URL(panel.url);
        const response = await fetch(new URL("/api/memory/" + operation, base), {
            method: "POST",
            headers: {
                Origin: base.origin, "Content-Type": "application/json",
                Authorization: `Bearer ${new URLSearchParams(base.hash.slice(1)).get("token")}`,
            },
            body: JSON.stringify(input),
        });
        const result = await response.json();
        assert.equal(response.status, expectedStatus, JSON.stringify(result));
        return result;
    }
    assert.equal((await api(first, "policy")).configured, false);
    assert.equal((await api(first, "inspect", { id: memory.id })).history.length, 1);
    const found = await api(first, "search", { query: "preparation", domain: "user", mode: "lexical" });
    assert.equal(found.results[0].memory.id, memory.id);
    const graph = await api(first, "graph", { id: memory.id, depth: 1, limit: 2 });
    assert.equal(graph.nodes[0].id, memory.id);
    const edited = await cli(["revise", memory.id, "--revision", "1", "--input", "-"], {
        data: { ...data, text: "Updated fictional preparation context." }, status: "active",
    });
    assert.equal((await api(second, "inspect", { id: memory.id })).memory.revision, edited.revision);
    await api(second, "review", { id: memory.id, revision: 1, intent: "correct" }, 409);
    assert.equal(reviews.length, 0);
    assert.equal((await api(second, "review", {
        id: memory.id, revision: edited.revision, intent: "forget",
    })).approved, false);
    assert.equal(reviews.length, 1);
    assert.equal((await cli(["show", memory.id])).status, "active");
    await cli(["forget", memory.id, "--revision", String(edited.revision), "--evidence", "-"], {
        kind: "human_confirmation", actor: "dana@example.com", subject_id: memory.id,
        revision: edited.revision, decision: "forget", statement: "Forget this synthetic test record.",
        evidence_ref: "conversation:synthetic-memory-canvas", decided_at: new Date().toISOString(),
    });
    const forgotten = await api(first, "inspect", { id: memory.id });
    assert.equal(forgotten.memory.status, "forgotten");
    assert.equal(forgotten.memory.text, undefined);
    assert.ok(forgotten.history.every(revision => Object.keys(revision.data).length === 0));
});

test("real task canvas reads bounded progress without creating state or executing a review request", { skip }, async (t) => {
    const fixture = join(await realpath(testParent), `task-canvas-test-${randomUUID()}`);
    await mkdir(fixture, { mode: 0o700 });
    t.after(() => rm(fixture, { recursive: true, force: true }));
    const env = { ...process.env };
    delete env.MARGO_ALLOW_UNSAFE_STATE_DIR;
    const account = "task-canvas-integration-test";
    const scope = ["--account", account, "--state-dir", fixture];
    const taskScript = join(dirname(script), "task_state.py");
    const execute = (command, args, options = {}) => executeWithInput(
        command, [...args.slice(0, 2), ...scope, ...args.slice(2)], { ...options, env, shell: false, encoding: "utf8" });
    const cli = async (args, value) => JSON.parse((await execute(python, ["-B", taskScript, ...args], {
        input: value === undefined ? undefined : JSON.stringify(value),
    })).stdout);
    const backend = createTaskBackend({ resolveScript: async () => script, execute });
    await assert.rejects(backend.run("list"), { code: "not_initialized" });
    await cli(["init"]);
    const now = Date.now();
    const costs = { tool_calls: 0, pages: 0, items: 0, model_calls: 0, output_chars: 1000 };
    const binding = { account, host: "synthetic-host", capabilities: { "local.prepare": "fixture-v1" },
        observed_at: new Date(now).toISOString() };
    const run = await cli(["create", "fixture", "--input", "-"], {
        goal: "Prepare a fictional note", routine: "work-products", request_ref: "conversation:synthetic-task-canvas",
        mode: "foreground", environment: binding,
        window: { start: new Date(now - 60000).toISOString(), end: new Date(now).toISOString() },
        limits: { ...costs, output_chars: 2000, max_steps: 2, max_attempts_per_step: 2, max_parallel: 1,
            deadline_at: new Date(now + 600000).toISOString(), lease_seconds: 60 },
        steps: [{ key: "prepare", title: "Prepare note", kind: "local", capability: "local.prepare",
            depends_on: [], allow_partial: false, cost: costs }],
    });
    const claim = await cli(["start", "--input", "-"], {
        run_id: run.id, step_key: "prepare", revision: 1, plan_hash: run.plan_hash, binding,
    });
    const sent = [];
    const panel = await startServer({ backend: { run: async () => ({ items: [] }) },
        taskBackend: backend, sendReview: async value => sent.push(value) });
    t.after(() => panel.close());
    const base = new URL(panel.url);
    const detail = await backend.run("show", { id: run.id });
    assert.equal(detail.status, "running");
    assert.equal(JSON.stringify(detail).includes(claim.token), false);
    const response = await fetch(new URL("/api/task/review", base), {
        method: "POST", headers: { Origin: base.origin, "Content-Type": "application/json",
            Authorization: `Bearer ${new URLSearchParams(base.hash.slice(1)).get("token")}` },
        body: JSON.stringify({ id: run.id, revision: detail.revision, plan_hash: detail.plan_hash, intent: "pause" }),
    });
    assert.equal(response.status, 200);
    assert.equal(sent.length, 1);
    assert.equal((await backend.run("show", { id: run.id })).state, "active");
    await cli(["pause", run.id, "--reason", "Fixture pause"]);
    assert.equal((await backend.run("list")).runs[0].status, "paused");
    assert.ok((await backend.run("history", { id: run.id })).events.some(event => event.event === "paused"));
});

test("real decision HTTP requests persist once across panels and show actual preparation receipts", { skip }, async t => {
    const fixture = join(await realpath(testParent), `decision-canvas-test-${randomUUID()}`);
    await mkdir(fixture, { mode: 0o700 });
    t.after(() => rm(fixture, { recursive: true, force: true }));
    const account = "decision-canvas-fixture";
    await writeFile(join(fixture, "config.json"), JSON.stringify({ account }), { mode: 0o600 });
    const env = { ...process.env, MARGO_CONFIG: join(fixture, "config.json"),
        COPILOT_HOME: join(fixture, "private-install") };
    delete env.MARGO_ALLOW_UNSAFE_STATE_DIR;
    const execute = (command, args, options = {}) => executeWithInput(command,
        [...args.slice(0, 2), "--account", account, "--state-dir", fixture, ...args.slice(2)],
        { ...options, env, shell: false, encoding: "utf8", cwd: fixture });
    const cli = async (file, args, input) => JSON.parse((await execute(python,
        ["-B", join(dirname(script), file), ...args],
        { input: input === undefined ? undefined : JSON.stringify(input) })).stdout);
    const backend = createBackend({ resolveScript: async () => script, execute });
    await assert.rejects(backend.run("desk"), { code: "not_initialized", status: 503 });
    await cli("task_state.py", ["init"]);
    const ref = await cli("work_state.py", ["source", "--input", "-"], {
        family: "mail", scope: "fixture", external_id: "synthetic-ask", revision: "1",
        evidence: { quote: "Choose an approach." }, web_link: "https://example.com/design",
    });
    const item = await cli("work_state.py", ["ingest", "--input", "-"], {
        claim_key: "decision", data: { title: "Choose an approach", direction: "owe",
            owner: null, due: null, next_step: "Review A versus B", source_refs: [ref] },
    });
    let messages = 0;
    const options = { backend, host: "sdk:synthetic-integration", sendReview: async message => {
        messages++;
        assert.equal(message.mode, "enqueue");
        assert.match(message.prompt, /NOT approval/);
        return "synthetic-message";
    } };
    const first = await startServer(options), second = await startServer(options);
    t.after(async () => { await first.close(); await second.close(); });
    const api = async (panel, path, input, expected = 200) => {
        const base = new URL(panel.url);
        const response = await fetch(new URL(path, base), {
            method: input ? "POST" : "GET", headers: {
                Origin: base.origin, Authorization: `Bearer ${new URLSearchParams(base.hash.slice(1)).get("token")}`,
                ...(input ? { "Content-Type": "application/json" } : {}),
            }, ...(input ? { body: JSON.stringify(input) } : {}),
        });
        const value = await response.json();
        assert.equal(response.status, expected, JSON.stringify(value));
        return value;
    };
    const snapshot = await api(first, "/api/desk");
    assert.equal(snapshot.request_capability.available, true);
    assert.equal(snapshot.profile.assistant_name, "Margo");
    const input = { id: item.id, expected_revision: item.revision, intent: "recommend" };
    await api(first, "/api/decision-request", { ...input, prompt: "arbitrary instructions" }, 400);
    const results = await Promise.all([api(first, "/api/decision-request", input), api(second, "/api/decision-request", input)]);
    assert.equal(messages, 1);
    assert.equal(JSON.stringify(results).includes('"token"'), false);
    assert.equal((await api(second, "/api/desk")).requests[item.id].recommend.phase, "accepted");
    const claim = await cli("work_state.py", ["desk-start", results[0].request.run_id, "--host", "sdk:synthetic-integration"]);
    assert.equal((await api(first, "/api/desk")).requests[item.id].recommend.phase, "working");
    await cli("task_state.py", ["finish", "--input", "-"], {
        attempt_id: claim.attempt_id, token: claim.token, outcome: "succeeded",
        result: { kind: "local_result", reference: "conversation:synthetic-result",
            summary: "Review the recorded comparison before deciding.", work_ids: [item.id], source_refs: [ref] },
    });
    const ready = (await api(second, "/api/desk")).requests[item.id].recommend;
    assert.equal(ready.phase, "ready");
    assert.equal(ready.approved, false);
    await api(first, "/api/decision-request", input);
    assert.equal(messages, 1);
    assert.equal((await api(first, `/api/items/${item.id}`)).item.confirmed, false);
});
