import test from "node:test";
import assert from "node:assert/strict";
import { mkdir, realpath, rm, stat } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { randomUUID } from "node:crypto";
import { createBackend, executeWithInput } from "./backend.mjs";
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

test("real unconfigured CLI surfaces setup needed without creating user state", { skip: !corePresent }, async () => {
    const env = { ...process.env, MARGO_CONFIG: join(directory, `not-configured-${randomUUID()}.json`) };
    delete env.MARGO_ACCOUNT;
    delete env.MARGO_ALLOW_UNSAFE_STATE_DIR;
    const backend = createBackend({
        resolveScript: async () => script,
        python,
        execute: (command, args, options) => executeWithInput(command, args, { ...options, env }),
    });
    await assert.rejects(backend.run("list"), { code: "setup_needed", status: 503 });
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
