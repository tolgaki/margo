import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import { request as httpRequest } from "node:http";
import { createMemoryBackend } from "./memory-backend.mjs";
import { BackendError } from "./backend.mjs";
import { startServer } from "./server.mjs";

test("memory backend fixes command paths, sends queries on stdin and has no write operations", async () => {
    let call;
    const backend = createMemoryBackend({
        resolveScript: async () => "/private/skills/chief-of-staff/scripts/work_state.py",
        execute: async (...args) => { call = args; return { stdout: '{"results":[]}' }; },
    });
    const query = 'review "$(touch nope)"';
    await backend.run("search", { query });
    assert.ok(call[1][1].endsWith("/memory_state.py"));
    assert.deepEqual(call[1].slice(2), ["search", "--input", "-"]);
    assert.equal(call[2].shell, false);
    assert.equal(JSON.parse(call[2].input).query, query);
    assert.ok(!call[1].includes(query));
    await backend.run("search", { query, routine: "calendar" });
    assert.deepEqual(call[1].slice(-2), ["--routine", "calendar"]);
    await assert.rejects(backend.run("search", { query, routine: "../../other" }), { code: "invalid_input" });
    for (const operation of ["forget", "put", "activate", "index", "download"]) {
        await assert.rejects(backend.run(operation, {}), { code: "invalid_input" });
    }
    await assert.rejects(backend.run("show", { id: "../../other" }), { code: "invalid_input" });
    await assert.rejects(backend.run("list", { account: "different" }), { code: "invalid_input" });
});

test("memory panel shares token/origin rules and requests review without changing memory", async t => {
    const sent = [];
    const operations = [];
    const memory = { id: "mem_123", revision: 2, account: "synthetic-account", status: "active" };
    const panel = await startServer({
        backend: { run: async () => ({ items: [] }) },
        memoryBackend: { run: async (operation, input) => {
            operations.push(operation);
            return operation === "show" ? memory : { memories: [memory] };
        } },
        sendReview: async value => sent.push(value),
    });
    t.after(() => panel.close());
    const base = new URL(panel.url);
    const token = new URLSearchParams(base.hash.slice(1)).get("token");
    const headers = { Origin: base.origin, Authorization: `Bearer ${token}`, "Content-Type": "application/json" };
    const request = (operation, input, override = {}) => fetch(new URL("/api/memory/" + operation, base), {
        method: "POST", headers: { ...headers, ...override }, body: JSON.stringify(input),
    });
    const html = await (await fetch(new URL("/memory", base))).text();
    assert.match(html, /--cp-bg:/);
    assert.doesNotMatch(html, /__THEME__|__NONCE__/);
    assert.equal((await request("list", {}, { Origin: "https://example.com" })).status, 403);
    assert.equal((await request("list", {}, { Authorization: "" })).status, 403);
    assert.equal((await request("forget", { id: memory.id, revision: 2 })).status, 404);
    assert.equal((await request("review", { id: memory.id, revision: 1 })).status, 409);
    assert.equal(sent.length, 0);
    const reviewed = await (await request("review", { id: memory.id, revision: 2 })).json();
    assert.equal(reviewed.approved, false);
    assert.equal(sent.length, 1);
    assert.match(sent[0].prompt, /NOT approval/);
    assert.ok(operations.every(operation => operation === "show"));
});

test("memory UI renders stored content as text, not executable HTML", async () => {
    const source = await readFile(new URL("./memory-app.js", import.meta.url), "utf8");
    assert.match(source, /textContent/);
    assert.doesNotMatch(source, /innerHTML|outerHTML|insertAdjacentHTML|document\.write|eval\(/);
});

function fixture() {
    const memory = {
        id: "mem_synthetic", revision: 2, account: "synthetic-account", domain: "agent",
        kind: "lesson", status: "active", title: "Check the supported input",
        text: "Use a bounded synthetic query.", authority: "user_confirmed", scope: "synthetic-host",
        source_refs: [{ kind: "user_statement", ref: "synthetic-source", excerpt: "Confirmed in review" }],
    };
    return {
        memory,
        policy: { account: memory.account, revision: 1, configured: true,
            data: { capture: { enabled: false, domains: [], kinds: [], scopes: [], source_kinds: [] },
                usage_enabled: false, usage_retention_days: 30, retention_days: { lesson: 90 }, review_days: {} } },
        status: { account: memory.account, memory: {}, index: {}, embedding_runtime: { status: "unavailable" } },
        inspect: { memory, history: [{ revision: 1, status: "candidate", created_at: "2026-09-01T12:00:00Z",
            data: { title: "Earlier candidate" }, evidence: null }],
        links: [{ source_id: memory.id, relation: "derives_from", target_id: "mem_source" }],
        forget_preview: { subject_id: memory.id, revision: 2,
            affected: [{ id: memory.id, revision: 2, domain: "agent", kind: "lesson" }],
            retained: ["Minimal tombstones; source documents remain.", "Prior exports and backups are not recalled."] },
        usage: [] },
        graph: { account: memory.account,
            nodes: [{ id: memory.id, revision: 2, kind: "lesson", title: memory.title,
                source_record: { store: "memory" }, selection_reasons: ["retrieval"] },
            { id: "item_synthetic", revision: 3, kind: "item", title: "Canonical work",
                source_record: { store: "work" }, selection_reasons: ["explicit_work_reference"] }],
            edges: [{ source_id: memory.id, target_id: "item_synthetic", relation: "advances" }],
            gaps: [{ code: "source_unavailable" }] },
    };
}

function resultFor(data, operation, input = {}) {
    if (operation === "show") return data.memory;
    if (operation === "list") return { account: data.memory.account, memories: [data.memory] };
    if (operation === "search") return { account: data.memory.account, mode: input.mode || "hybrid",
        results: [{ memory: data.memory, matched_by: [input.mode === "lexical" ? "lexical" : "semantic"] }], warnings: [] };
    return data[operation];
}

async function serverFixture(t, options = {}) {
    const data = fixture();
    const calls = [];
    const sent = [];
    const panel = await startServer({
        backend: { run: async () => ({ items: [] }) },
        memoryBackend: { run: async (operation, input) => {
            calls.push({ operation, input });
            return options.run ? options.run(operation, input, data) : resultFor(data, operation, input);
        } },
        sendReview: async value => {
            if (options.sendReview) await options.sendReview(value);
            sent.push(value);
        },
    });
    t.after(() => panel.close());
    const base = new URL(panel.url);
    const token = new URLSearchParams(base.hash.slice(1)).get("token");
    const headers = { Origin: base.origin, Authorization: `Bearer ${token}`, "Content-Type": "application/json" };
    const request = (operation, input = {}, overrides = {}) => fetch(new URL("/api/memory/" + operation, base), {
        method: "POST", headers, body: JSON.stringify(input), ...overrides,
    });
    return { data, calls, sent, panel, base, token, headers, request };
}

test("all memory read commands preserve CLI contracts, bounded graph and explicit search mode", async () => {
    const data = fixture();
    const calls = [];
    const backend = createMemoryBackend({
        resolveScript: async () => "/private/skills/chief-of-staff/scripts/work_state.py",
        execute: async (command, args, options) => {
            calls.push({ command, args, options });
            const mode = args.includes("--mode") ? args[args.indexOf("--mode") + 1] : "hybrid";
            return { stdout: JSON.stringify(resultFor(data, args[2], { mode })) };
        },
    });
    for (const [operation, input, expected] of [
        ["list", {}, ["list"]],
        ["list", { domain: "agent", status: "disputed" }, ["list", "--domain", "agent", "--status", "disputed"]],
        ["show", { id: data.memory.id }, ["show", data.memory.id]],
        ["inspect", { id: data.memory.id }, ["inspect", data.memory.id]],
        ["graph", { id: data.memory.id }, ["graph", data.memory.id, "--depth", "2", "--limit", "20"]],
        ["graph", { id: data.memory.id, depth: 1, limit: 5 }, ["graph", data.memory.id, "--depth", "1", "--limit", "5"]],
        ["graph", { id: data.memory.id, routine: "drafting", domain: "user" },
            ["graph", data.memory.id, "--depth", "2", "--limit", "20", "--routine", "drafting", "--domain", "user"]],
        ["status", {}, ["status"]], ["policy", {}, ["policy"]],
        ["search", { query: "bounded query", mode: "lexical", domain: "agent" },
            ["search", "--input", "-", "--mode", "lexical", "--domain", "agent"]],
        ["search", { query: "bounded query", mode: "hybrid", routine: "drafting" },
            ["search", "--input", "-", "--routine", "drafting", "--mode", "hybrid"]],
    ]) {
        await backend.run(operation, input);
        assert.deepEqual(calls.at(-1).args.slice(2), expected);
        assert.equal(calls.at(-1).options.shell, false);
        assert.equal(calls.at(-1).options.timeout, 60_000);
        assert.equal(calls.at(-1).options.maxBuffer, 4 * 1024 * 1024);
        if (operation !== "search") assert.equal(calls.at(-1).options.input, undefined);
    }
});

test("memory input rejects all mutation commands, operation fields and unbounded reads before execution", async () => {
    let executions = 0;
    const backend = createMemoryBackend({
        resolveScript: async () => { executions++; throw new Error("must not resolve"); },
    });
    for (const operation of ["review", "approve", "forget", "revise", "put", "capture", "activate", "suppress",
        "link", "unlink", "policy-set", "policy-preview", "export", "export-preview", "index", "download", "model-install"]) {
        await assert.rejects(backend.run(operation), { code: "invalid_input" });
    }
    for (const operation of ["list", "show", "inspect", "graph", "search", "status", "policy"]) {
        for (const input of [null, [], "", 42, true, { account: "other" }, { path: "/other" }, { operation: "forget" }]) {
            await assert.rejects(backend.run(operation, input), { code: "invalid_input" });
        }
    }
    for (const [operation, input] of [
        ["list", { domain: "other" }], ["list", { status: "approved" }], ["list", { kind: "lesson" }],
        ["search", { query: " " }], ["search", { query: "a".repeat(4001) }],
        ["search", { query: "q", mode: "semantic" }], ["search", { query: "q", mode: "lexical" }],
        ["graph", { id: "mem_ok", depth: 3 }], ["graph", { id: "mem_ok", limit: 21 }],
        ["graph", { id: "mem_ok", limit: 0 }], ["graph", { id: "mem_ok", depth: true }],
        ["graph", { id: "mem_ok", limit: 1.5 }], ["inspect", { id: "../other" }],
        ["show", { id: "--account=other" }],
    ]) await assert.rejects(backend.run(operation, input), { code: "invalid_input" });
    assert.equal(executions, 0);
});

test("malformed and failed backend responses never become successful reads or expose stderr", async () => {
    const data = fixture();
    let output;
    const backend = createMemoryBackend({
        resolveScript: async () => "/private/scripts/work_state.py",
        execute: async () => {
            if (output instanceof Error) throw output;
            return { stdout: output };
        },
    });
    for (const bad of ["invalid", "[]", "null", '{"error":"private details"}', '{"results":"wrong"}']) {
        output = bad;
        await assert.rejects(backend.run("search", { query: "q" }), { code: "invalid_backend_response" });
    }
    output = JSON.stringify({ ...resultFor(data, "search"), mode: "lexical" });
    await assert.rejects(backend.run("search", { query: "q", mode: "hybrid" }), { code: "invalid_backend_response" });
    for (const [operation, value] of [
        ["show", { ...data.memory, id: "mem_wrong" }],
        ["inspect", { ...data.inspect, forget_preview: { ...data.inspect.forget_preview, revision: 1 } }],
        ["graph", { nodes: Array(21).fill(data.memory), edges: [], gaps: [] }],
        ["graph", { status: "ready", nodes: [] }],
        ["graph", { status: "blocked", nodes: [], reason: "Unexpected response" }],
        ["status", {}],
        ["policy", { revision: 1, configured: true, data: {} }],
    ]) {
        output = JSON.stringify(value);
        await assert.rejects(backend.run(operation, ["policy", "status"].includes(operation) ? {} : { id: data.memory.id }), { code: "invalid_backend_response" });
    }
    output = Object.assign(new Error("private stderr with credentials"), { stderr: "private stderr" });
    await assert.rejects(backend.run("search", { query: "q" }), error => {
        assert.equal(error.code, "memory_unavailable");
        assert.match(error.message, /explicitly select Keyword only/);
        assert.doesNotMatch(error.message, /private stderr|credentials/);
        return true;
    });
});

test("memory HTTP exposes only validated read operations and preserves read results", async t => {
    const { data, calls, sent, request } = await serverFixture(t);
    for (const operation of ["list", "show", "inspect", "graph", "search", "status", "policy"]) {
        const input = ["show", "inspect", "graph"].includes(operation) ? { id: data.memory.id }
            : operation === "search" ? { query: "q", mode: "lexical", domain: "agent" } : {};
        const response = await request(operation, input);
        assert.equal(response.status, 200, operation);
        assert.deepEqual(await response.json(), resultFor(data, operation, input));
        assert.equal((await request(operation, { ...input, account: "other" })).status, 400);
        assert.equal((await request(operation, input, { method: "PUT" })).status, 405);
    }
    assert.deepEqual(calls.map(value => value.operation), ["list", "show", "inspect", "graph", "search", "status", "policy"]);
    assert.equal(sent.length, 0);
    for (const operation of ["approve", "activate", "forget", "revise", "export", "export-preview",
        "policy-set", "capture", "model-install", "index", "download"]) {
        assert.equal((await request(operation)).status, 404);
    }
});

test("memory routes retain token, origin, body, query and static content protections", async t => {
    const { request, base, token, headers, calls } = await serverFixture(t);
    for (const overrides of [
        { Authorization: "" }, { Authorization: "Bearer wrong" }, { Origin: "" },
        { Origin: "https://example.com" }, { "Sec-Fetch-Site": "cross-site" },
    ]) assert.equal((await request("policy", {}, { headers: { ...headers, ...overrides } })).status, 403, JSON.stringify(overrides));
    const wrongHost = await new Promise((resolve, reject) => {
        const req = httpRequest(new URL("/api/memory/policy", base), {
            method: "POST", headers: { ...headers, Host: "example.com" },
        }, response => { response.resume(); response.on("end", () => resolve(response.statusCode)); });
        req.on("error", reject);
        req.end("{}");
    });
    assert.equal(wrongHost, 403);
    assert.equal((await request("policy?account=other")).status, 400);
    assert.equal((await request("policy", {}, { body: "invalid" })).status, 400);
    assert.equal((await request("policy", {}, { headers: { ...headers, "Content-Type": "text/plain" } })).status, 415);
    assert.equal((await request("policy", {}, { body: JSON.stringify({ extra: "x".repeat(130 * 1024) }) })).status, 413);
    assert.equal(calls.length, 0);
    for (const path of ["/memory", "/memory?scoutTheme=dark", "/memory?scoutTheme=light", "/memory.js"]) {
        const response = await fetch(new URL(path, base));
        const body = await response.text();
        assert.equal(response.headers.get("cache-control"), "no-store");
        assert.equal(response.headers.get("referrer-policy"), "no-referrer");
        assert.match(response.headers.get("content-security-policy"), /default-src 'none'/);
        assert.doesNotMatch(response.headers.get("content-security-policy"), /unsafe-inline|unsafe-eval/);
        assert.ok(!body.includes(token));
    }
    assert.equal((await fetch(new URL("/memory?scoutTheme=invalid", base))).status, 404);
    assert.equal((await fetch(new URL("/memory?scoutTheme=dark&account=other", base))).status, 404);
});

test("every foreground review intent carries exact identity only and never approval or recipe content", async t => {
    const { data, request, calls, sent } = await serverFixture(t);
    data.memory.title = "<script>hostile stored title</script>";
    data.memory.text = "Private recipe that must never be sent by this route";
    for (const intent of [undefined, "correct", "forget", "supersede", "do-not-use", "export"]) {
        const input = { id: data.memory.id, revision: data.memory.revision, ...(intent ? { intent } : {}) };
        const response = await request("review", input);
        assert.equal(response.status, 200);
        const result = await response.json();
        assert.equal(result.approved, false);
        assert.equal(result.review_requested, true);
        const delivered = sent.at(-1);
        assert.equal(delivered.mode, "immediate");
        assert.equal(delivered.agentMode, "interactive");
        assert.match(delivered.prompt, /NOT approval or consent/);
        assert.match(delivered.prompt, /must never serve as approval evidence/);
        assert.match(delivered.prompt, /subsequent specific foreground user confirmation/);
        assert.ok(!delivered.prompt.includes(data.memory.title));
        assert.ok(!delivered.prompt.includes(data.memory.text));
        assert.deepEqual(JSON.parse(delivered.prompt.split("\n").at(-1)), { account: data.memory.account, ...input });
    }
    assert.equal(sent.length, 6);
    assert.ok(calls.every(call => call.operation === "show"));
});

test("review rejects stale, forgotten, invalid export, extra fields and bad revision without consent", async t => {
    const { data, request, sent } = await serverFixture(t);
    const exact = { id: data.memory.id, revision: 2 };
    for (const input of [null, [], "review", { ...exact, revision: 0 }, { ...exact, revision: 2.1 },
        { ...exact, revision: true }, { ...exact, intent: "activate" }, { ...exact, approved: true },
        { ...exact, evidence: {} }, { ...exact, content: "export me" }, { ...exact, out: "/somewhere" },
        { ...exact, id: "../other" }, { id: data.memory.id }]) {
        assert.equal((await request("review", input)).status, 400);
    }
    assert.equal((await request("review", { ...exact, revision: 1 })).status, 409);
    data.memory.kind = "profile";
    assert.equal((await request("review", { ...exact, intent: "export" })).status, 400);
    data.memory.kind = "lesson";
    data.memory.authority = "inferred";
    assert.equal((await request("review", { ...exact, intent: "export" })).status, 400);
    data.memory.authority = "source_observed";
    assert.equal((await request("review", { ...exact, intent: "export" })).status, 400);
    data.memory.authority = "user_confirmed";
    data.memory.status = "candidate";
    assert.equal((await request("review", { ...exact, intent: "export" })).status, 400);
    data.memory.status = "forgotten";
    assert.equal((await request("review", exact)).status, 409);
    assert.equal(sent.length, 0);
});

test("review rejects concurrent submissions, then rechecks revision on retry", async t => {
    let release;
    let entered;
    const started = new Promise(resolve => { entered = resolve; });
    const gate = new Promise(resolve => { release = resolve; });
    const { data, request, sent, calls } = await serverFixture(t, {
        run: async (operation, input, value) => {
            if (operation === "show") { entered(); await gate; }
            return resultFor(value, operation, input);
        },
    });
    t.after(() => release());
    const exact = { id: data.memory.id, revision: 2, intent: "forget" };
    const first = request("review", exact);
    await started;
    assert.equal((await request("review", exact)).status, 409);
    assert.equal(calls.length, 1);
    data.memory.revision = 3;
    release();
    assert.equal((await first).status, 409);
    assert.equal(sent.length, 0);
    assert.equal((await request("review", { ...exact, revision: 3 })).status, 200);
    assert.equal(sent.length, 1);
});

test("backend error objects and send failures cannot masquerade as review success; retries recover", async t => {
    let fail = true;
    const { data, request, sent } = await serverFixture(t, {
        sendReview: async () => { if (fail) throw new Error("private transport payload"); },
        run: async (operation, input, value) => resultFor(value, operation, input),
    });
    const exact = { id: data.memory.id, revision: 2 };
    const failed = await request("review", exact);
    assert.equal(failed.status, 503);
    const body = await failed.text();
    assert.match(body, /review_unavailable/);
    assert.doesNotMatch(body, /private transport payload/);
    assert.equal(sent.length, 0);
    fail = false;
    assert.equal((await request("review", exact)).status, 200);
    assert.equal(sent.length, 1);
    data.memory.error = "private backend error";
    assert.equal((await request("review", exact)).status, 502);
    delete data.memory.error;
    data.memory.id = "mem_wrong";
    assert.equal((await request("review", exact)).status, 502);
    assert.equal(sent.length, 1);
});

test("backend exceptions stay errors and release the review lock", async t => {
    let failure = new BackendError("memory_unavailable", "Read unavailable.", 503);
    const { data, request, sent } = await serverFixture(t, {
        run: (operation, input, value) => {
            if (failure) throw failure;
            return resultFor(value, operation, input);
        },
    });
    const exact = { id: data.memory.id, revision: 2 };
    assert.equal((await request("review", exact)).status, 503);
    failure = new Error("private details");
    const failed = await request("review", exact);
    assert.equal(failed.status, 500);
    assert.doesNotMatch(await failed.text(), /private details/);
    failure = null;
    assert.equal((await request("review", exact)).status, 200);
    assert.equal(sent.length, 1);
});

async function browserFixture(options = {}) {
    const source = await readFile(new URL("./memory-app.js", import.meta.url), "utf8");
    const html = await readFile(new URL("./memory.html", import.meta.url), "utf8");
    const data = fixture();
    const elements = [];
    let focused;
    class Element {
        constructor(tag) {
            this.tag = tag;
            this.nodeType = 1;
            this.children = [];
            this.attributes = new Map();
            this.listeners = {};
            this.value = "";
            this.disabled = false;
            this.text = "";
            elements.push(this);
        }
        set textContent(value) { this.text = String(value); this.children = []; }
        get textContent() { return this.text + this.children.map(child => child.textContent).join(""); }
        set innerHTML(_) { throw new Error("Unsafe HTML rendering"); }
        set outerHTML(_) { throw new Error("Unsafe HTML rendering"); }
        append(...children) { this.children.push(...children); }
        replaceChildren(...children) { this.text = ""; this.children = children; }
        setAttribute(name, value) { this.attributes.set(name, value); }
        hasAttribute(name) { return this.attributes.has(name); }
        getAttribute(name) { return this.attributes.get(name); }
        addEventListener(event, callback) { this.listeners[event] = callback; }
        focus() { focused = this; }
        async click() {
            assert.equal(this.disabled, false, `Control disabled: ${this.textContent}`);
            await (this.onclick || this.listeners.click)?.();
        }
    }
    const byId = new Map([...html.matchAll(/<(\w+)[^>]*\bid="([^"]+)"/g)].map(match => [match[2], new Element(match[1])]));
    byId.get("mode").value = "hybrid";
    const document = {
        getElementById: id => byId.get(id),
        createElement: tag => new Element(tag),
        querySelectorAll: () => elements.filter(element => ["button", "input", "select"].includes(element.tag)),
    };
    const requests = [];
    if (options.prepare) options.prepare(data);
    vm.runInNewContext(source, {
        document, location: { hash: "#token=synthetic-test-token" }, URLSearchParams, AbortSignal,
        fetch: async (path, init) => {
            const operation = path.split("/").at(-1);
            const input = JSON.parse(init.body);
            requests.push({ operation, input, init });
            const result = options.respond ? await options.respond(operation, input, data) : resultFor(data, operation, input);
            return { ok: !result?.error, json: async () => result };
        },
    });
    await new Promise(setImmediate);
    const descendants = element => element.children.flatMap(child => [child, ...descendants(child)]);
    const findButton = (id, label) => descendants(byId.get(id)).find(element => element.tag === "button" && element.textContent === label);
    return { data, byId, requests, elements, descendants, findButton, focused: () => focused };
}

test("UI safely renders hostile evidence, history, policy, relationships, forgetting and exact review requests", async () => {
    const hostile = '<img src=x onerror="globalThis.executed=true">';
    const browser = await browserFixture({
        prepare: data => {
            data.memory.title = hostile;
            data.memory.text = hostile;
            data.memory.source_refs[0].excerpt = hostile;
            data.inspect.history[0].data.title = hostile;
            data.inspect.links[0].data = { source_refs: [hostile] };
            data.inspect.forget_preview.retained.push(hostile);
            data.graph.nodes[0].title = hostile;
            data.graph.gaps.push({ code: hostile });
            data.graph.status = "ready";
            data.graph.truncated = true;
            data.graph.omitted = { entries: 1, edges: 1, gaps: 0 };
            data.graph.budget_scope = "complete_canonical_json";
        },
        respond: (operation, input, data) => operation === "review"
            ? { approved: false, review_requested: true, message: "Foreground review requested, not approved." }
            : resultFor(data, operation, input),
    });
    const { data, byId, requests, findButton, descendants } = browser;
    assert.equal(byId.get("workspace").getAttribute("aria-busy"), "false");
    assert.match(byId.get("account").textContent, /synthetic-account/);
    assert.match(byId.get("policy-summary").textContent, /Capture: off.*lesson: 90 days/);
    assert.match(byId.get("runtime-summary").textContent, /unavailable/);
    await byId.get("results").children[0].click();
    const detail = byId.get("detail");
    assert.ok(detail.textContent.includes(hostile));
    assert.match(detail.textContent, /Why selected: Selected from the stored-memory list/);
    assert.match(detail.textContent, /Recent revisions.*Earlier|Recent revisions/);
    assert.match(detail.textContent, /mem_source/);
    assert.match(detail.textContent, /Minimal tombstones/);
    assert.match(detail.textContent, /No usage events returned/);
    assert.equal(browser.focused().tag, "h2");
    assert.ok(descendants(detail).some(element => element.tag === "th" && element.getAttribute("scope") === "col"));
    byId.get("routine").value = "drafting";
    byId.get("domain").value = "agent";
    await findButton("detail", "Inspect current relationship graph (2 hops, at most 20 nodes)").click();
    assert.deepEqual(requests.at(-1).input, { id: data.memory.id, depth: 2, limit: 20,
        routine: "drafting", domain: "agent" });
    assert.match(detail.textContent, /Eligible graph nodes.*Canonical work.*source_unavailable/s);
    assert.match(detail.textContent, /output budget omitted records; this is not a complete map/);
    assert.match(detail.textContent, /complete_canonical_json/);
    assert.match(detail.textContent, /Local evidence only; no live source permission verification/);
    assert.equal(findButton("detail", "Canonical work"), undefined, "Canonical work is not routed to memory inspection");
    for (const [intent, label] of [
        ["correct", "Request correction review"], ["do-not-use", "Request do-not-use review"],
        ["supersede", "Request supersession review"], ["forget", "Request forgetting review"],
        ["export", "Request sanitized lesson export review"],
    ]) {
        await findButton("detail", label).click();
        assert.deepEqual(requests.at(-1).input, { id: data.memory.id, revision: 2, intent });
        assert.equal(requests.at(-1).operation, "review");
    }
    assert.ok(!browser.elements.some(element => ["img", "script", "iframe", "a"].includes(element.tag)));
    assert.ok(browser.elements.every(element => ![...element.attributes.keys()].some(name => name.startsWith("on"))));
    assert.ok(requests.every(request => request.init.credentials === "omit" && request.init.cache === "no-store"));
    assert.ok(requests.every(request => !request.init.body.includes("synthetic-test-token")));
});

test("UI conflict view includes current contradiction links without changing record status", async () => {
    const browser = await browserFixture({
        respond: (operation, input, data) => operation === "list"
            ? { account: data.memory.account, memories: [data.memory], conflicted_ids: [data.memory.id] }
            : resultFor(data, operation, input),
    });
    await browser.findButton("views", "Conflicts").click();
    assert.match(browser.byId.get("count").textContent, /1 of 1/);
    assert.equal(browser.data.memory.status, "active");
});

test("UI filters facts, people, projects, lessons, conflicts and historical statuses locally", async () => {
    const browser = await browserFixture({
        respond: (operation, input, data) => operation === "list" ? { account: data.memory.account, memories: [
            { ...data.memory, id: "mem_fact", domain: "user", kind: "profile" },
            { ...data.memory, id: "mem_person", domain: "user", kind: "person" },
            { ...data.memory, id: "mem_project", domain: "user", kind: "project", status: "disputed" },
            data.memory, { ...data.memory, id: "mem_retired", status: "superseded" },
        ] } : resultFor(data, operation, input),
    });
    const { byId, requests, findButton } = browser;
    const reads = requests.length;
    for (const [name, count] of [["Facts", 1], ["People", 1], ["Projects", 1], ["Lessons", 2], ["Conflicts", 1], ["History", 5]]) {
        await findButton("views", name).click();
        assert.match(byId.get("count").textContent, new RegExp(`^${count} of 5`));
        assert.equal(findButton("views", name).getAttribute("aria-pressed"), "true");
    }
    byId.get("domain").value = "agent";
    byId.get("status").value = "superseded";
    byId.get("kind").value = "lesson";
    byId.get("status").listeners.change();
    assert.match(byId.get("count").textContent, /^1 of 5/);
    assert.equal(requests.length, reads);
});

test("missing model never triggers silent fallback; lexical search requires explicit scoped selection", async () => {
    const browser = await browserFixture({
        respond: (operation, input, data) => operation === "search" && input.mode === "hybrid"
            ? { error: { code: "memory_unavailable", message: "Local model unavailable. Explicitly select Keyword only." } }
            : resultFor(data, operation, input),
    });
    const { byId, requests } = browser;
    byId.get("query").value = "supported input";
    await byId.get("search").click();
    assert.match(byId.get("notice").textContent, /Local model unavailable/);
    assert.equal(byId.get("mode").value, "hybrid");
    assert.deepEqual(requests.filter(call => call.operation === "search").map(call => call.input.mode), ["hybrid"]);
    assert.equal(byId.get("search").disabled, false);
    byId.get("mode").value = "lexical";
    await byId.get("search").click();
    assert.match(byId.get("notice").textContent, /Choose a domain or routine scope/);
    assert.equal(requests.filter(call => call.operation === "search").length, 1);
    byId.get("domain").value = "agent";
    await byId.get("search").click();
    assert.deepEqual(requests.at(-1).input, { query: "supported input", mode: "lexical", domain: "agent" });
    assert.match(byId.get("count").textContent, /Keyword only · no semantic matching/);
    await byId.get("results").children[0].click();
    assert.match(byId.get("detail").textContent, /Why selected: lexical/);
    assert.ok(requests.every(call => !["index", "download", "model-install"].includes(call.operation)));
});

test("stale reviews disable all review requests until an exact current inspection succeeds", async () => {
    let stale = true;
    const browser = await browserFixture({
        respond: (operation, input, data) => operation === "review"
            ? stale ? { error: { code: "conflict", message: "Memory changed; reload before review." } }
                : { approved: false, message: "Foreground review requested." }
            : resultFor(data, operation, input),
    });
    const { byId, findButton, descendants, requests, data } = browser;
    await byId.get("results").children[0].click();
    await findButton("detail", "Request correction review").click();
    assert.match(byId.get("notice").textContent, /Reload the selected memory/);
    const reviewButtons = descendants(byId.get("detail")).filter(element => element.hasAttribute("data-review"));
    assert.ok(reviewButtons.every(element => element.disabled));
    stale = false;
    data.memory.revision = 3;
    data.inspect.forget_preview.revision = 3;
    await findButton("detail", "Reload selected memory").click();
    await findButton("detail", "Request correction review").click();
    assert.equal(requests.at(-1).input.revision, 3);
    assert.equal(byId.get("workspace").getAttribute("aria-busy"), "false");
});

test("failed graph reads retain inspection and remain retryable; capture status is never inferred", async () => {
    let fail = true;
    const browser = await browserFixture({
        respond: (operation, input, data) => ["policy", "graph"].includes(operation) && fail
            ? { error: { code: "memory_unavailable", message: "Read unavailable" } }
            : resultFor(data, operation, input),
    });
    const { byId, findButton } = browser;
    assert.match(byId.get("policy-summary").textContent, /Capture and retention unknown/);
    await byId.get("results").children[0].click();
    const label = "Inspect current relationship graph (2 hops, at most 20 nodes)";
    await findButton("detail", label).click();
    assert.match(byId.get("notice").textContent, /Read unavailable/);
    assert.match(byId.get("detail").textContent, /Forgetting preview/);
    fail = false;
    await findButton("detail", label).click();
    assert.match(byId.get("detail").textContent, /Eligible graph nodes/);
    await byId.get("health").click();
    assert.match(byId.get("policy-summary").textContent, /Capture: off/);
});

test("account changes clear prior results and selected contents instead of mixing account context", async () => {
    const { byId, data } = await browserFixture();
    await byId.get("results").children[0].click();
    data.memory.account = "different-synthetic-account";
    await byId.get("list").click();
    assert.match(byId.get("notice").textContent, /configured account changed/);
    assert.equal(byId.get("detail").children.length, 0);
    assert.equal(byId.get("results").children.length, 0);
    assert.match(byId.get("policy-summary").textContent, /must be refreshed/);
});

test("UI omits export for unreviewed lessons and all review controls for forgotten memories", async () => {
    for (const [status, authority] of [["candidate", "user_confirmed"], ["forgotten", "user_confirmed"],
        ["active", "inferred"], ["active", "source_observed"]]) {
        const { byId, findButton, descendants } = await browserFixture({
            prepare: data => { data.memory.status = status; data.memory.authority = authority; },
        });
        await byId.get("results").children[0].click();
        assert.equal(findButton("detail", "Request sanitized lesson export review"), undefined);
        if (status === "forgotten") {
            assert.ok(descendants(byId.get("detail")).every(element => !element.hasAttribute("data-review")));
        }
    }
});

test("schema-v1 errors report explicit migration without stderr leakage or automatic repair", async () => {
    const operations = [];
    let failure = Object.assign(new Error("private path"), {
        stderr: JSON.stringify({ error: "memory schema marker mismatch; explicit migration required", command: "list",
            private_detail: "must never appear" }),
    });
    const backend = createMemoryBackend({
        resolveScript: async () => "/private/scripts/work_state.py",
        execute: async (_command, args) => { operations.push(args[2]); throw failure; },
    });
    for (const operation of ["list", "status", "policy", "inspect"]) {
        await assert.rejects(backend.run(operation, operation === "inspect" ? { id: "mem_synthetic" } : {}), error => {
            assert.equal(error.code, "migration_required");
            assert.equal(error.status, 409);
            assert.match(error.message, /pause memory writers.*private backup.*explicitly run memory_state.py migrate/);
            assert.match(error.message, /never migrates or repairs/);
            assert.doesNotMatch(error.message, /private path|must never appear/);
            return true;
        });
    }
    failure.stderr = JSON.stringify({ error: { code: "migration_required", message: "private detail" } });
    await assert.rejects(backend.run("list"), { code: "migration_required" });
    failure.stderr = "memory schema marker mismatch; explicit migration required";
    await assert.rejects(backend.run("list"), { code: "memory_unavailable" });
    assert.deepEqual(operations, ["list", "status", "policy", "inspect", "list", "list"]);
    await assert.rejects(backend.run("migrate"), { code: "invalid_input" });
});

test("UI shows migration-required as a recoverable read failure and distinguishes suppressed from forgotten", async () => {
    let migrationRequired = true;
    const { byId, requests } = await browserFixture({
        prepare: data => { data.memory.status = "suppressed"; },
        respond: (operation, input, data) => migrationRequired
            ? { error: { code: "migration_required", message: "Memory schema migration is required. Explicit foreground migration only; no automatic repair." } }
            : resultFor(data, operation, input),
    });
    assert.match(byId.get("notice").textContent, /migration is required/);
    assert.match(byId.get("policy-summary").textContent, /Capture and retention unknown/);
    assert.equal(byId.get("list").disabled, false);
    migrationRequired = false;
    await byId.get("health").click();
    await byId.get("list").click();
    assert.match(byId.get("results").textContent, /suppressed \(do-not-use, not forgotten\)/);
    await byId.get("results").children[0].click();
    assert.match(byId.get("detail").textContent, /Do-not-use suppresses recall; it does not erase memory or change capture policy/);
    assert.ok(requests.every(request => ["status", "policy", "list", "inspect"].includes(request.operation)));
});

test("keyboard search and in-flight reads expose busy state, then recover without parallel requests", async () => {
    let release;
    const gate = new Promise(resolve => { release = resolve; });
    const { byId, requests } = await browserFixture({
        respond: async (operation, input, data) => {
            if (operation === "search") await gate;
            return resultFor(data, operation, input);
        },
    });
    byId.get("query").value = "synthetic";
    byId.get("query").listeners.keydown({ key: "Enter" });
    assert.equal(byId.get("workspace").getAttribute("aria-busy"), "true");
    assert.equal(byId.get("search").disabled, true);
    byId.get("query").listeners.keydown({ key: "Enter" });
    assert.equal(requests.filter(value => value.operation === "search").length, 1);
    release();
    await new Promise(setImmediate);
    assert.equal(byId.get("workspace").getAttribute("aria-busy"), "false");
    assert.equal(byId.get("search").disabled, false);
    const html = await readFile(new URL("./memory.html", import.meta.url), "utf8");
    assert.match(html, /id="notice" role="status" aria-live="polite" aria-atomic="true"/);
    for (const id of ["query", "mode", "purpose", "routine", "domain", "status", "kind"]) {
        assert.ok(html.includes(`for="${id}"`), `${id} has an explicit label`);
    }
});

test("budget-blocked graph fallback remains a blocked inspection, not malformed success or expanded retrieval", async t => {
    const blocked = { status: "blocked", nodes: [], reason: "Graph wrapper exceeds the budget.",
        requires_larger_budget: true, account: "synthetic-account", context_is_consent: false,
        budget_chars: 12000, used_chars: 500, budget_scope: "complete_canonical_json" };
    const { request } = await serverFixture(t, {
        run: (operation, input, data) => operation === "graph" ? blocked : resultFor(data, operation, input),
    });
    const response = await request("graph", { id: "mem_synthetic" });
    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), { edges: [], gaps: [], ...blocked });
    const { byId, findButton, requests } = await browserFixture({
        respond: (operation, input, data) => operation === "graph" ? blocked : resultFor(data, operation, input),
    });
    await byId.get("results").children[0].click();
    await findButton("detail", "Inspect current relationship graph (2 hops, at most 20 nodes)").click();
    assert.match(byId.get("detail").textContent, /Graph status: blocked/);
    assert.match(byId.get("detail").textContent, /Scope or eligibility may be unavailable/);
    assert.match(byId.get("detail").textContent, /does not automatically expand its budget/);
    assert.match(byId.get("detail").textContent, /Graph wrapper exceeds the budget/);
    assert.equal(requests.filter(value => value.operation === "graph").length, 1);
});

test("permitted-use purpose reaches CLI search and graph without granting an action", async () => {
    const calls = [];
    const backend = createMemoryBackend({
        resolveScript: async () => "/private/synthetic/work_state.py",
        execute: async (_command, args) => {
            calls.push(args);
            return { stdout: JSON.stringify(args[2] === "search"
                ? { account: "synthetic", mode: "lexical", results: [] }
                : { nodes: [], edges: [], gaps: [] }) };
        },
    });
    await backend.run("search", { query: "recipient text", mode: "lexical", domain: "user", usage: "drafting" });
    assert.deepEqual(calls.at(-1).slice(-2), ["--usage", "drafting"]);
    await backend.run("graph", { id: "mem_synthetic", usage: "drafting" });
    assert.deepEqual(calls.at(-1).slice(-2), ["--usage", "drafting"]);
    await assert.rejects(backend.run("search", { query: "text", usage: "send" }), { code: "invalid_input" });
});

test("uninitialized reads explain explicit setup without leaking stderr or creating state", async () => {
    const backend = createMemoryBackend({
        resolveScript: async () => "/private/synthetic/work_state.py",
        execute: async () => { throw Object.assign(new Error("failed"), {
            stderr: JSON.stringify({ code: "not_initialized", error: "private detail", command: "list" }),
        }); },
    });
    await assert.rejects(backend.run("list"), error => {
        assert.equal(error.code, "setup_needed");
        assert.equal(error.status, 503);
        assert.match(error.message, /memory_state.py init/);
        assert.doesNotMatch(error.message, /private detail/);
        return true;
    });
});

test("UI explains non-copyable context and preference recovery and forwards the chosen purpose", async () => {
    const { byId, requests, findButton } = await browserFixture({
        prepare: data => {
            data.memory.allowed_uses = ["reasoning"];
            data.status.memory.recovery_actions = ["Run preferences-preview, then preferences-import with fresh approval."];
        },
    });
    assert.match(byId.get("runtime-summary").textContent, /preferences-preview/);
    byId.get("query").value = "synthetic";
    byId.get("purpose").value = "drafting";
    await byId.get("search").click();
    assert.equal(requests.at(-1).input.usage, "drafting");
    await byId.get("results").children[0].click();
    assert.match(byId.get("detail").textContent, /do not copy this text into a recipient-facing draft/);
    await findButton("detail", "Inspect current relationship graph (2 hops, at most 20 nodes)").click();
    assert.equal(requests.at(-1).input.usage, "drafting");
});
