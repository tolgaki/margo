import test from "node:test";
import assert from "node:assert/strict";
import { cp, mkdir, mkdtemp, readFile, readdir, realpath, rm, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createBackend, executeWithInput } from "./backend.mjs";
import { createMemoryBackend } from "./memory-backend.mjs";
import { createTaskBackend } from "./task-backend.mjs";
import { startServer } from "./server.mjs";

const parent = process.env.MARGO_CANVAS_TEST_PARENT;
test("installed no-env binding drives profile Work Memory Tasks and HTTP without a model or duplicate DB", {
    skip: !parent && "Set MARGO_CANVAS_TEST_PARENT to a deliberately private synthetic fixture parent.",
}, async t => {
    const fixture = await mkdtemp(join(await realpath(parent), "location-fixture-"));
    t.after(() => rm(fixture, { recursive: true, force: true }));
    const install = join(fixture, "Installed copy");
    const scripts = join(install, "skills", "chief-of-staff", "scripts");
    await cp(resolve(dirname(fileURLToPath(import.meta.url)), "../../../skills/chief-of-staff/scripts"), scripts, { recursive: true });
    await writeFile(join(install, ".margo-install"), "mode=copy\n", { mode: 0o600 });
    const privateRoot = join(fixture, "Private root");
    const state = join(privateRoot, "state");
    await mkdir(privateRoot, { mode: 0o700 });
    await mkdir(state, { mode: 0o700 });
    const account = "location-integration@example.com", config = join(privateRoot, "config.json");
    const configText = JSON.stringify({ account, profiles: { [account]: { assistant_name: "Rowan" } }, preserve: "fixture" });
    await writeFile(config, configText, { mode: 0o600 });
    const env = { ...process.env };
    for (const name of Object.keys(env)) if (name.startsWith("MARGO_") || name === "COPILOT_HOME") delete env[name];
    const execute = (command, args, options) => executeWithInput(command, args, { ...options, env, cwd: fixture });
    const python = process.platform === "win32" ? "python" : "python3";
    const cli = async (file, args, document) => JSON.parse((await execute(python, ["-B", join(scripts, file), ...args],
        { encoding: "utf8", shell: false, input: document === undefined ? undefined : JSON.stringify(document) })).stdout);
    const work = createBackend({ resolveScript: async () => join(scripts, "work_state.py"), execute });
    const memory = createMemoryBackend({ resolveScript: async () => join(scripts, "work_state.py"), execute });
    const tasks = createTaskBackend({ resolveScript: async () => join(scripts, "work_state.py"), execute });
    assert.equal((await work.run("profile")).configured, false);
    await assert.rejects(work.run("desk"), { code: "account_setup_required" });
    await assert.rejects(memory.run("list"), { code: "account_setup_required" });
    await assert.rejects(tasks.run("list"), { code: "account_setup_required" });
    const locations = await cli("margo_store.py", ["locations"]);
    assert.equal(locations.revision, "missing");
    await cli("margo_store.py", ["locations-bind", "--expected-revision", locations.revision, "--account", account,
        "--config-path", config, "--state-dir", state]);
    assert.equal((await work.run("profile")).assistant_name, "Rowan");
    assert.equal((await work.run("profile")).m365_authentication, "not_checked");
    assert.deepEqual(await readdir(state), [], "Binding does not initialize state");
    await cli("task_state.py", ["init"]);
    assert.equal((await work.run("desk")).account, account);
    assert.equal((await tasks.run("list")).account, account);
    await assert.rejects(memory.run("list"), { code: "not_initialized" });
    await assert.rejects(memory.run("status"), { code: "not_initialized" });
    await cli("memory_state.py", ["init"]);
    const health = await memory.run("status");
    assert.equal(health.basic_memory.status, "available");
    assert.equal(health.semantic_search.status, "unavailable");
    assert.equal(health.policy.data.capture.enabled, false);
    assert.equal(health.index.initialized, true);
    assert.equal(health.index.vectors, 0);
    assert.deepEqual((await memory.run("list")).memories, []);
    const entry = await cli("memory_state.py", ["put", "synthetic-recall", "--input", "-"], {
        status: "active", data: { domain: "user", kind: "episode", title: "Synthetic design review",
            text: "Read the fictional design before deciding.", authority: "source_observed", scope: "fixture",
            source_refs: [{ kind: "tool_result", ref: "synthetic:review" }] },
    });
    assert.equal((await memory.run("search", { query: "fictional", mode: "lexical", domain: "user" })).results[0].memory.id, entry.id);
    await assert.rejects(memory.run("search", { query: "fictional", mode: "hybrid", domain: "user" }), { code: "semantic_unavailable" });
    assert.equal((await memory.run("inspect", { id: entry.id })).memory.authority, "source_observed");
    const panel = await startServer({ backend: work, memoryBackend: memory, taskBackend: tasks,
        sendReview: async () => { throw new Error("This fixture must not send."); } });
    t.after(() => panel.close());
    const base = new URL(panel.url);
    async function api(path, input, status = 200) {
        const response = await fetch(new URL(path, base), {
            method: input ? "POST" : "GET",
            headers: { Origin: base.origin, Authorization: `Bearer ${new URLSearchParams(base.hash.slice(1)).get("token")}`,
                ...(input ? { "Content-Type": "application/json" } : {}) },
            ...(input ? { body: JSON.stringify(input) } : {}),
        });
        const value = await response.json();
        assert.equal(response.status, status, JSON.stringify(value));
        return value;
    }
    assert.equal((await api("/api/profile")).account, account);
    assert.equal((await api("/api/desk")).account, account);
    assert.equal((await api("/api/task/list", {})).account, account);
    assert.equal((await api("/api/memory/status", {})).basic_memory.status, "available");
    assert.equal((await api("/api/memory/search", { query: "fictional", mode: "lexical", domain: "user" })).results.length, 1);
    assert.equal((await api("/api/memory/search", { query: "fictional", mode: "hybrid", domain: "user" }, 503)).error.code, "semantic_unavailable");
    assert.equal(await readFile(config, "utf8"), configText, "Existing owner and profile were never rewritten");
    const installedPrivateFiles = await readdir(join(install, "margo"));
    assert.deepEqual(installedPrivateFiles, ["locations.json"], "No legacy config, database or model download");
    await writeFile(join(install, "margo", "locations.json"), "{malformed", { mode: 0o600 });
    assert.equal((await api("/api/memory/list", {}, 503)).error.code, "location_unavailable");
    assert.equal((await api("/api/task/list", {}, 503)).error.code, "location_unavailable");
    assert.equal((await api("/api/desk", undefined, 503)).error.code, "location_unavailable");
});
