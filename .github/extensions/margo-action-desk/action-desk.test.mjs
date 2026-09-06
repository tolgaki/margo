import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { request } from "node:http";
import { Writable } from "node:stream";
import vm from "node:vm";
import { BackendError, commandArguments, createBackend, executeWithInput, normalizeItem, resolveCore, validateInput } from "./backend.mjs";
import { reviewPrompt, startServer } from "./server.mjs";

const hash = "a".repeat(64);
const version = { expected_revision: 1, expected_hash: hash };
const sample = () => ({
    id: "work-1", type: "action", account: "test-account", revision: 1, action_hash: hash, payload_hash: hash, title: "Local test fixture",
    state: "ready", status: "ready", payload: { body: "Test only" },
    kind: "mail", target: { to: ["test@example.com"] }, why: "Test only",
    source_refs: [], target_fingerprint: "fingerprint", work_item_id: null,
});

test("UTF-8 split across HTTP chunks survives local proposal edits", async (t) => {
    let received;
    const panel = await startServer({
        backend: { run: async (_operation, input) => {
            received = input.payload;
            return { item: { ...sample(), payload: received } };
        } },
        sendReview: async () => {},
    });
    t.after(() => panel.close());
    const url = new URL(panel.url);
    const token = new URLSearchParams(url.hash.slice(1)).get("token");
    const value = "Fee: \u20ac10";
    const bytes = Buffer.from(JSON.stringify({ ...version, payload: { body: value } }));
    const boundary = bytes.indexOf(Buffer.from("\u20ac")) + 1;
    const status = await new Promise((resolveStatus, reject) => {
        const req = request(new URL("/api/items/work-1/revise", url), {
            method: "POST",
            headers: { Origin: url.origin, Authorization: `Bearer ${token}`,
                "Content-Type": "application/json" },
        }, (res) => {
            res.resume();
            res.on("end", () => resolveStatus(res.statusCode));
        });
        req.on("error", reject);
        req.write(bytes.subarray(0, boundary));
        setTimeout(() => req.end(bytes.subarray(boundary)), 10);
    });
    assert.equal(status, 200);
    assert.equal(received.body, value);
});

test("core resolution checks project, adjacent installed skills, then configured home", async () => {
    const checked = [];
    const extensionDirectory = resolve("project/.github/extensions/margo-action-desk");
    const home = resolve("user");
    const result = await resolveCore({
        extensionDirectory,
        home,
        copilotHome: resolve(home, ".copilot"),
        inspect: async (path) => {
            checked.push(path);
            if (checked.length < 3) throw Object.assign(new Error(), { code: "ENOENT" });
            return { isFile: () => true };
        },
    });
    assert.deepEqual(checked, [
        resolve("project/skills/chief-of-staff/scripts/work_state.py"),
        resolve("project/.github/skills/chief-of-staff/scripts/work_state.py"),
        resolve("user/.copilot/skills/chief-of-staff/scripts/work_state.py"),
    ]);
    assert.equal(result, checked[2]);
    await assert.rejects(resolveCore({ inspect: async () => ({ isFile: () => false }) }), { code: "missing_core" });
});

test("custom installation destination resolves adjacent skills before the home fallback", async () => {
    const checked = [];
    const adjacent = resolve("custom-destination/skills/chief-of-staff/scripts/work_state.py");
    const result = await resolveCore({
        extensionDirectory: resolve("custom-destination/extensions/margo-action-desk"),
        copilotHome: resolve("different-home"),
        inspect: async (path) => {
            checked.push(path);
            return { isFile: () => path === adjacent };
        },
    });
    assert.equal(result, adjacent);
    assert.equal(checked.length, 2);
});

test("COPILOT_HOME controls the final fallback without relying on the default home", async () => {
    const previous = process.env.COPILOT_HOME;
    process.env.COPILOT_HOME = resolve("configured-copilot-home");
    try {
        const expected = resolve(process.env.COPILOT_HOME, "skills/chief-of-staff/scripts/work_state.py");
        const result = await resolveCore({
            extensionDirectory: resolve("missing-project/.github/extensions/margo-action-desk"),
            inspect: async (path) => ({ isFile: () => path === expected }),
        });
        assert.equal(result, expected);
    } finally {
        if (previous === undefined) delete process.env.COPILOT_HOME;
        else process.env.COPILOT_HOME = previous;
    }
});

test("process argv is fixed, shell-free, and treats hostile payloads only as JSON", async () => {
    const payload = { body: '$(touch ignored); <script>alert("no")</script>', db: "/not-a-command" };
    let call;
    const backend = createBackend({
        resolveScript: async () => "/project/skills/chief-of-staff/scripts/work_state.py",
        execute: async (...args) => { call = args; return { stdout: JSON.stringify(sample()) }; },
    });
    await backend.run("revise", { id: "work-1", ...version, payload });
    assert.equal(call[0], process.platform === "win32" ? "python" : "python3");
    assert.equal(call[2].shell, false);
    assert.equal(call[2].timeout, 10_000);
    assert.deepEqual(call[1].slice(2), ["edit", "work-1", "--revision", "1", "--expected-hash", hash, "--input", "-"]);
    const document = JSON.parse(call[2].input);
    assert.deepEqual(document.payload, payload);
    assert.deepEqual(document.target, sample().target);
    assert.deepEqual(Object.keys(document).sort(), ["kind", "payload", "source_refs", "target", "target_fingerprint", "why", "work_item_id"]);
    assert.equal(call[1][0], "-B");
    assert.ok(!call[1].includes("/not-a-command"));
});

test("command and request validation deny approval, arbitrary paths, extra fields and stale identity omissions", () => {
    for (const operation of ["approve", "execute", "send", "delete", "review", "init", "config"]) {
        assert.throws(() => commandArguments("script.py", operation, { id: "work-1", ...version }), { code: "invalid_input" });
    }
    assert.throws(() => validateInput("list", { db: "/somewhere" }), { code: "invalid_input" });
    assert.throws(() => validateInput("show", { id: "--db=somewhere" }), { code: "invalid_input" });
    assert.throws(() => validateInput("show", { id: "../file" }), { code: "invalid_input" });
    assert.throws(() => validateInput("dismiss", { id: "work-1" }), { code: "invalid_input" });
    assert.throws(() => validateInput("revise", { id: "work-1", ...version, payload: [] }), { code: "invalid_input" });
    assert.throws(() => validateInput("defer", { id: "work-1", ...version, until: "2020-01-01T00:00:00Z" }), { code: "invalid_input" });
});

test("stdin failures cannot report successful edits and preserve CLI errors for early EPIPE", async () => {
    function launch(code, processError) {
        return () => {
            const pending = processError ? Promise.reject(processError) : Promise.resolve({ stdout: "{}" });
            pending.child = {
                stdin: new Writable({
                    write(_chunk, _encoding, callback) { callback(code ? Object.assign(new Error("Write failed."), { code }) : null); },
                }),
            };
            return pending;
        };
    }
    for (const code of ["EACCES", "ERR_STREAM_DESTROYED", "EPIPE"]) {
        await assert.rejects(executeWithInput("python", [], { input: "{}" }, launch(code)), { code: "backend_input_failed" });
    }
    const failure = Object.assign(new Error("CLI failed"), { code: 2, stderr: '{"error":"bad input"}' });
    await assert.rejects(executeWithInput("python", [], { input: "{}" }, launch("EPIPE", failure)), failure);
    assert.deepEqual(await executeWithInput("python", [], { input: "{}" }, launch()), { stdout: "{}" });
});

test("actual subprocess stdin is delivered and early successful exit is not a successful write", async () => {
    const input = '{"body":"literal $(not-a-shell)"}';
    const read = await executeWithInput(process.execPath, ["-e", "process.stdin.setEncoding('utf8'); let data=''; process.stdin.on('data', chunk => data+=chunk); process.stdin.on('end', () => process.stdout.write(data));"], {
        input, encoding: "utf8", shell: false, timeout: 5000,
    });
    assert.equal(read.stdout, input);
    await assert.rejects(executeWithInput(process.execPath, ["-e", "process.exit(0)"], {
        input: "x".repeat(2 * 1024 * 1024), encoding: "utf8", shell: false, timeout: 5000,
    }), { code: "backend_input_failed" });
});

test("structured CLI errors propagate; stderr and malformed responses do not leak", async () => {
    const backend = (execute) => createBackend({ resolveScript: async () => "/code/work_state.py", execute });
    await assert.rejects(backend(async () => {
        throw Object.assign(new Error("private stderr"), { stdout: JSON.stringify({ error: { code: "revision_conflict", message: "Refresh the item." } }) });
    }).run("list"), { code: "revision_conflict", message: "Refresh the item.", status: 409 });
    await assert.rejects(backend(async () => {
        throw Object.assign(new Error("private stderr"), { stderr: JSON.stringify({ error: "Configure an account first", command: "list" }) });
    }).run("list"), { code: "setup_needed", status: 503 });
    await assert.rejects(backend(async () => {
        throw Object.assign(new Error("private stderr"), { code: "ENOENT" });
    }).run("list"), { code: "setup_needed" });
    await assert.rejects(backend(async () => {
        throw Object.assign(new Error(), { killed: true });
    }).run("list"), { code: "backend_timeout", status: 504 });
    await assert.rejects(backend(async () => ({ stdout: "not JSON" })).run("list"), { code: "invalid_backend_response" });
    await assert.rejects(backend(async () => ({ stdout: '{"ok":true}' })).run("list"), { code: "invalid_backend_response" });
    await assert.rejects(backend(async () => { throw new Error("private stderr"); }).run("list"), (error) => !error.message.includes("private stderr"));
});

test("stable CLI list/show contract combines rows and normalizes action hashes and source changes", async () => {
    assert.deepEqual(commandArguments("core.py", "list"), ["-B", "core.py", "list", "--view", "all", "--json"]);
    assert.deepEqual(commandArguments("core.py", "show", { id: "act_123" }), ["-B", "core.py", "show", "act_123", "--json"]);
    assert.deepEqual(commandArguments("core.py", "dismiss", { id: "act_123", ...version }),
        ["-B", "core.py", "dismiss", "act_123", "--revision", "1", "--expected-hash", hash]);
    assert.deepEqual(commandArguments("core.py", "defer", { id: "act_123", ...version, until: "2999-01-01T00:00:00Z" }),
        ["-B", "core.py", "defer", "act_123", "--revision", "1", "--expected-hash", hash, "--until", "2999-01-01T00:00:00Z"]);
    const work = { id: "work_123", type: "item", state: "candidate", revision: 1, data: { title: "A work item" } };
    const backend = createBackend({
        resolveScript: async () => "/core.py",
        execute: async () => ({ stdout: JSON.stringify({ schema_version: 1, account: "test", actions: [sample()], items: [work], records: [] }) }),
    });
    const result = await backend.run("list");
    assert.equal(result.items.length, 2);
    assert.equal(result.items[0].payload_hash, hash);
    assert.equal(result.items[1].title, "A work item");
    assert.equal(normalizeItem({ ...sample(), stale: true }).source_changed, true);
});

test("local changes pre-read exact hash and cannot alter nonactions", async () => {
    let called = 0;
    const backend = createBackend({
        resolveScript: async () => "/core.py",
        execute: async () => { called += 1; return { stdout: JSON.stringify(sample()) }; },
    });
    await assert.rejects(backend.run("dismiss", { id: "work-1", ...version, expected_hash: "b".repeat(64) }), { code: "revision_conflict" });
    assert.equal(called, 1);
    const readonly = createBackend({
        resolveScript: async () => "/core.py",
        execute: async () => ({ stdout: JSON.stringify({ id: "work-1", type: "item", revision: 1, data: {} }) }),
    });
    await assert.rejects(readonly.run("dismiss", { id: "work-1", ...version }), { code: "invalid_input" });
});

async function fixture(t, overrides = {}) {
    const state = { item: sample(), calls: [], messages: [] };
    const backend = {
        async run(operation, input = {}) {
            validateInput(operation, input);
            state.calls.push({ operation, input });
            if (operation === "list") return { account: "test-account", items: [state.item] };
            if (operation === "show") return { item: state.item };
            if (input.expected_revision !== state.item.revision) throw new BackendError("conflict", "Refresh.", 409);
            state.item = { ...state.item, revision: state.item.revision + 1 };
            if (operation === "revise") state.item.payload = input.payload;
            if (operation === "defer") state.item.status = "deferred";
            if (operation === "dismiss") state.item.status = "dismissed";
            return { item: state.item };
        },
    };
    const entry = await startServer({ backend, sendReview: async (message) => state.messages.push(message), ...overrides });
    t.after(entry.close);
    const url = new URL(entry.url);
    const token = new URLSearchParams(url.hash.slice(1)).get("token");
    const api = async (path, options = {}) => {
        const { body, headers, method, ...rest } = options;
        return fetch(new URL(path, url), {
            method: method || (body ? "POST" : "GET"),
            headers: { Authorization: `Bearer ${token}`, Origin: url.origin, ...(body ? { "Content-Type": "application/json" } : {}), ...headers },
            ...(body ? { body: typeof body === "string" ? body : JSON.stringify(body) } : {}),
            ...rest,
        });
    };
    return { entry, url, token, state, backend, api };
}

test("HTTP serves static UI without data and secures APIs with token, host, and origin", async (t) => {
    const { entry, url, token, state, api } = await fixture(t);
    assert.equal(entry.server.address().address, "127.0.0.1");
    const page = await fetch(url);
    const html = await page.text();
    assert.ok(!html.includes(token));
    assert.ok(!html.includes(state.item.title));
    assert.ok(page.headers.get("content-security-policy").includes("default-src 'none'"));
    assert.ok(!page.headers.get("content-security-policy").includes("unsafe-inline"));
    assert.equal(page.headers.get("referrer-policy"), "no-referrer");
    assert.equal(page.headers.get("cache-control"), "no-store");
    assert.equal((await api("/api/items", { headers: { Authorization: "" } })).status, 403);
    assert.equal((await api("/api/items", { headers: { Origin: "https://example.com" } })).status, 403);
    assert.equal((await api("/api/items/work-1/dismiss", { body: version, headers: { Origin: "" } })).status, 403);
    assert.equal((await api("/api/items/work-1/dismiss", { method: "OPTIONS" })).status, 405);
    const status = await new Promise((resolve, reject) => {
        const req = request(url, { headers: { Host: "rebound.example", Authorization: `Bearer ${token}` } }, (res) => { res.resume(); resolve(res.statusCode); });
        req.on("error", reject);
        req.end();
    });
    assert.equal(status, 403);
    assert.equal((await api("/api/items")).headers.get("access-control-allow-origin"), null);
});

test("HTTP allowlist rejects arbitrary commands, body identity, paths, wrong media type and malformed JSON", async (t) => {
    const { api, state } = await fixture(t);
    for (const operation of ["approve", "execute", "send", "config"]) {
        assert.equal((await api(`/api/items/work-1/${operation}`, { body: version })).status, 404);
    }
    assert.equal((await api("/api/items?db=/arbitrary")).status, 400);
    assert.equal((await api("/api/items/work-1/dismiss", { body: { ...version, id: "other" } })).status, 400);
    assert.equal((await api("/api/items/work-1/dismiss", { body: { ...version, database: "elsewhere" } })).status, 400);
    assert.equal((await api("/api/items/work-1/dismiss", { body: "{bad" })).status, 400);
    assert.equal((await api("/api/items/work-1/revise", { body: { ...version, payload: { body: "x".repeat(129 * 1024) } } })).status, 413);
    assert.equal((await api("/api/items/work-1/dismiss", { body: version, headers: { "Content-Type": "text/plain" } })).status, 415);
    assert.equal((await api("/api/items/work-1/dismiss")).status, 405);
    assert.equal(state.calls.length, 0);
});

test("local edit/defer/dismiss share one persistent backend across different panels", async (t) => {
    const first = await fixture(t);
    const second = await fixture(t, { backend: first.backend });
    let response = await first.api("/api/items/work-1/revise", { body: { ...version, payload: { body: "Revised" } } });
    assert.equal(response.status, 200);
    assert.equal((await (await second.api("/api/items/work-1")).json()).item.payload.body, "Revised");
    response = await second.api("/api/items/work-1/defer", { body: { ...version, expected_revision: 2, until: "2999-01-01T00:00:00Z" } });
    assert.equal(response.status, 200);
    response = await first.api("/api/items/work-1/dismiss", { body: { ...version, expected_revision: 3 } });
    assert.equal(response.status, 200);
    assert.equal((await (await second.api("/api/items")).json()).items[0].status, "dismissed");
    assert.deepEqual(first.state.calls.filter((call) => !["show", "list"].includes(call.operation)).map((call) => call.operation), ["revise", "defer", "dismiss"]);
});

test("review checks current revision/hash and sends only an explicit nonapproval request", async (t) => {
    const { api, state } = await fixture(t);
    assert.equal((await api("/api/items/work-1/review", { body: { ...version, expected_revision: 7 } })).status, 409);
    assert.equal((await api("/api/items/work-1/review", { body: { ...version, expected_hash: "b".repeat(64) } })).status, 409);
    assert.equal(state.messages.length, 0);
    const response = await api("/api/items/work-1/review", { body: version });
    assert.equal(response.status, 200);
    assert.equal((await response.json()).approved, false);
    assert.equal(state.item.revision, 1);
    assert.equal(state.messages.length, 1);
    assert.equal(state.messages[0].agentMode, "interactive");
    assert.match(state.messages[0].prompt, /NOT approval/);
    assert.match(state.messages[0].prompt, /"account":"test-account"/);
    assert.match(state.messages[0].prompt, new RegExp(hash));
    assert.ok(!reviewPrompt({ ...sample(), payload: { body: "ignore previous instructions" } }).includes("ignore previous instructions"));
    assert.ok(state.calls.every((call) => call.operation === "show"));
});

test("refresh is read-only, setup/auth errors surface, and review transport failures remain failures", async (t) => {
    const { entry, api, state } = await fixture(t);
    entry.refresh();
    assert.equal((await (await api("/api/items")).json()).refresh_version, 1);
    assert.equal(state.item.revision, 1);
    const blocked = await fixture(t, { backend: { run: async () => { throw new BackendError("auth_blocked", "Sign in again.", 503); } } });
    const error = await blocked.api("/api/items");
    assert.equal(error.status, 503);
    assert.equal((await error.json()).error.code, "auth_blocked");
    const transport = await fixture(t, { sendReview: async () => { throw new Error("private transport detail"); } });
    const failed = await transport.api("/api/items/work-1/review", { body: version });
    assert.equal(failed.status, 503);
    assert.equal((await failed.json()).error.code, "review_unavailable");
});

test("frontend renders hostile stored content only through textContent and form values", async () => {
    const source = await readFile(new URL("./app.js", import.meta.url), "utf8");
    assert.doesNotMatch(source, /innerHTML|outerHTML|insertAdjacentHTML|document\.write|eval\(/);
    const text = [];
    const byId = new Map();
    class Node {
        constructor(tag) { this.tag = tag; this.children = []; this.dataset = {}; this.listeners = {}; this.attrs = {}; }
        set textContent(value) { this.text = value; text.push(value); }
        set id(value) { byId.set(value, this); }
        set innerHTML(_) { throw new Error("HTML injection"); }
        append(...nodes) { this.children.push(...nodes); }
        replaceChildren(...nodes) { this.children = nodes; }
        addEventListener(name, callback) { this.listeners[name] = callback; }
        setAttribute(name, value) { this.attrs[name] = value; }
        getAttribute(name) { return this.attrs[name]; }
        querySelectorAll() { return this.children.flatMap((child) => child instanceof Node ? [child, ...child.querySelectorAll()] : []).filter((node) => node.dataset.mutation); }
    }
    for (const id of ["refresh", "notice", "account", "filters", "items", "detail"]) byId.set(id, new Node("div"));
    const malicious = '<img src=x onerror="alert(1)"><script>bad()</script>';
    const item = { ...sample(), title: malicious, why_now: malicious, payload: { body: malicious }, evidence: [malicious, { source_id: "src_123", revision: "1" }], provenance: malicious };
    const sourceRecord = {
        id: "src_123", type: "source", web_link: "javascript:alert(1)", url: "file:///not-opened",
        revisions: [
            { data: { web_link: "https://example.invalid/doc", title: malicious } },
            { data: { url: "http://docs.example.invalid/read", title: "Legacy document" } },
            { data: { url: "data:text/html,<script>bad()</script>" } },
            { data: { url: "/relative" } },
            { data: { url: "https:relative" } },
            { data: { url: "//other.example.invalid/" } },
            { data: { url: "https://user:password@example.com/private" } },
            { data: { url: "https://example.invalid/doc" } },
        ],
    };
    const requests = [];
    const document = {
        getElementById: (id) => byId.get(id),
        createElement: (tag) => new Node(tag),
        createTextNode: (value) => { text.push(value); return new Node("text"); },
        documentElement: new Node("html"),
    };
    vm.runInNewContext(source, {
        document, location: { hash: "#token=token" }, URL, URLSearchParams, AbortSignal,
        MutationObserver: class { observe() {} }, setInterval() {},
        fetch: async (path) => {
            requests.push(path);
            return { ok: true, json: async () => path === "/api/items" ? { account: malicious, items: [item] }
                : { item: path === "/api/items/src_123" ? sourceRecord : item } };
        },
    });
    await new Promise(setImmediate);
    assert.ok(text.includes(malicious));
    await byId.get("items").children[0].listeners.click();
    await new Promise(setImmediate);
    assert.ok(text.includes(JSON.stringify(item.payload, null, 2)));
    assert.equal(byId.get("payload").value, JSON.stringify(item.payload, null, 2));
    const descendants = (node) => node.children.flatMap((child) => child instanceof Node ? [child, ...descendants(child)] : []);
    const evidence = descendants(byId.get("detail")).find((node) => node.tag === "details"
        && node.children[0].text?.startsWith("Source src_123"));
    evidence.open = true;
    await evidence.listeners.toggle();
    const links = descendants(evidence).filter((node) => node.tag === "a");
    assert.deepEqual(links.map((node) => node.href).sort(), ["http://docs.example.invalid/read", "https://example.invalid/doc"]);
    assert.ok(links.some((node) => node.text === `example.invalid — ${malicious}`));
    assert.ok(links.every((node) => node.target === "_blank" && node.rel === "noopener noreferrer"));
    assert.ok(requests.every((path) => path.startsWith("/api/")));
});

test("agent-facing work and memory actions are read-only", async () => {
    const source = await readFile(new URL("./extension.mjs", import.meta.url), "utf8");
    const names = [...source.matchAll(/name: "([^"]+)"/g)].map((match) => match[1]);
    assert.deepEqual(names, ["list", "show", "refresh", "list", "search", "status"]);
    assert.doesNotMatch(source, /onPermissionRequest|systemMessage|console\.log/);
});
