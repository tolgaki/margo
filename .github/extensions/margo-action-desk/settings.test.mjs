import test from "node:test";
import assert from "node:assert/strict";
import { cp, mkdir, mkdtemp, realpath, rm, readFile, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createBackend, executeWithInput, validateInput } from "./backend.mjs";
import { createAutomationBackend, createAutomationTool } from "./automation-backend.mjs";
import { startServer } from "./server.mjs";

test("Config save rejects arbitrary account, command and invalid name/fields before running", () => {
    const valid = { expected_revision: "a".repeat(64), assistant_name: "Rowan", work_root: null };
    validateInput("profile-save", valid);
    for (const field of ["account", "config", "sql", "command", "state_root", "payload"]) {
        assert.throws(() => validateInput("profile-save", { ...valid, [field]: "value" }));
    }
    for (const name of ["", "<script>", "A\nB", " leading", "x".repeat(61)]) assert.throws(() => validateInput("profile-save", { ...valid, assistant_name: name }));
});

test("authoring tool refuses scheduled/default-autopilot sessions and never selects native workflow tools", async () => {
    let agent = "margo-proactive", mode = "interactive", calls = 0;
    const tool = createAutomationTool(() => ({ rpc: { agent: { getCurrent: async () => ({ agent: { name: agent } }) }, mode: { get: async () => mode } } }),
        { run: async () => { calls++; return { status: "available" }; } });
    assert.equal((await tool.handler({ operation: "list", input: {} })).resultType, "failure");
    agent = "margo"; mode = "autopilot";
    assert.equal((await tool.handler({ operation: "list", input: {} })).resultType, "failure");
    mode = "interactive";
    assert.equal((await tool.handler({ operation: "list", input: {} })).resultType, "success");
    assert.equal(calls, 1);
});

test("real Config and shared UI/NL automation pipeline persist from nonrepo cwd without touching runtime binding", {
    skip: !process.env.MARGO_CANVAS_TEST_PARENT && "Set a private synthetic fixture parent.",
}, async t => {
    const root = await mkdtemp(join(await realpath(process.env.MARGO_CANVAS_TEST_PARENT), "config-manager-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const scripts = join(root, "installed", "skills", "chief-of-staff", "scripts");
    await cp(resolve(dirname(fileURLToPath(import.meta.url)), "../../../skills/chief-of-staff/scripts"), scripts, { recursive: true });
    await writeFile(join(root, "installed", ".margo-install"), "mode=copy\n", { mode: 0o600 });
    const privateDir = join(root, "private"), state = join(privateDir, "state"), workspace = join(root, "Daily Work");
    await mkdir(privateDir, { mode: 0o700 }); await mkdir(state, { mode: 0o700 }); await mkdir(workspace, { mode: 0o700 });
    const config = join(privateDir, "config.json");
    await writeFile(config, JSON.stringify({ account: "fixture-account" }), { mode: 0o600 });
    const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.startsWith("MARGO_") && key !== "COPILOT_HOME"));
    const execute = (command, args, options) => executeWithInput(command, args, { ...options, env, cwd: workspace });
    const python = process.platform === "win32" ? "python" : "python3";
    await execute(python, ["-B", join(scripts, "margo_store.py"), "locations-bind", "--account", "fixture-account", "--config-path", config,
        "--state-dir", state, "--expected-revision", "missing"], { encoding: "utf8" });
    const binding = join(root, "installed", "margo", "locations.json"), beforeBinding = await readFile(binding, "utf8");
    const backend = createBackend({ resolveScript: async () => join(scripts, "work_state.py"), execute });
    const automationBackend = createAutomationBackend({ resolveScript: async () => join(scripts, "work_state.py"), execute });
    const initial = await backend.run("profile");
    assert.equal(initial.assistant_name, "Margo");
    const saved = await backend.run("profile-save", { expected_revision: initial.revision, assistant_name: "Rowan", work_root: workspace });
    assert.equal(saved.assistant_name, "Rowan"); assert.equal(saved.account, initial.account);
    await assert.rejects(backend.run("profile-save", { expected_revision: initial.revision, assistant_name: "Casey", work_root: workspace }));
    assert.equal(await readFile(binding, "utf8"), beforeBinding);
    const panel = await startServer({ backend, automationBackend }); t.after(() => panel.close());
    const base = new URL(panel.url), headers = { Origin: base.origin, Authorization: `Bearer ${new URLSearchParams(base.hash.slice(1)).get("token")}`, "Content-Type": "application/json" };
    const post = async (path, data, expected = 200, override = {}) => {
        const response = await fetch(new URL(path, base), { method: "POST", headers: { ...headers, ...override }, body: JSON.stringify(data) });
        const value = await response.json(); assert.equal(response.status, expected, JSON.stringify(value)); return value;
    };
    await post("/api/profile", { expected_revision: saved.revision, assistant_name: "Casey", work_root: workspace }, 403, { Origin: "https://example.com" });
    const list = await post("/api/automations/list", {});
    const change = { operation: "create", id: "example-brief", expected_revision: "missing",
        controller_revision: list.controller_revision, profile_revision: list.profile_revision,
        patch: { title: "Example brief", timezone: "Etc/UTC", schedule: { kind: "cron", expression: "30 8 * * 1-5" },
            sections: Object.fromEntries(["Description/Purpose", "Conditions", "Inputs", "Steps", "Outputs", "Completion/Idempotency", "Failure/Retry", "Permissions/Review", "Source references"].map(heading => [heading, "Synthetic content."])) } };
    const preview = await post("/api/automations/preview", change);
    const created = await post("/api/automations/commit", { change, preview_hash: preview.preview_hash });
    assert.equal(created.saved, true); assert.equal(created.metadata.enabled, false);
    const nl = createAutomationTool(() => ({ rpc: { agent: { getCurrent: async () => ({ agent: { name: "margo" } }) }, mode: { get: async () => "interactive" } } }), automationBackend);
    const nlRead = JSON.parse((await nl.handler({ operation: "show", input: { id: "example-brief" } })).textResultForLlm);
    assert.equal(nlRead.revision, created.revision);
    const update = { ...change, operation: "update", expected_revision: nlRead.revision,
        controller_revision: nlRead.controller_revision, profile_revision: nlRead.profile_revision,
        patch: { title: "Updated through NL tool" } };
    const nlPreview = JSON.parse((await nl.handler({ operation: "preview", input: update })).textResultForLlm);
    assert.equal((await nl.handler({ operation: "commit", input: { change: update, preview_hash: nlPreview.preview_hash } })).resultType, "success");
    assert.equal((await post("/api/automations/show", { id: "example-brief" })).metadata.title, "Updated through NL tool");
    const other = createBackend({ resolveScript: async () => join(scripts, "work_state.py"), execute });
    assert.equal((await other.run("profile")).assistant_name, "Rowan");
    await post("/api/automations/commit", { change: update, preview_hash: nlPreview.preview_hash }, 409);
    assert.equal(await readFile(binding, "utf8"), beforeBinding);
});
