import test from "node:test";
import assert from "node:assert/strict";
import { cp, mkdir, mkdtemp, readFile, readdir, realpath, rm, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { executeWithInput } from "./backend.mjs";
import { createProactiveBackend } from "./proactive-tools.mjs";

test("copied restricted adapter uses bound no-env state and only stages private candidate/output records", {
    skip: !process.env.MARGO_CANVAS_TEST_PARENT && "Set a private fixture parent for production-path integration.",
}, async t => {
    const root = await mkdtemp(join(await realpath(process.env.MARGO_CANVAS_TEST_PARENT), "app-preparation-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const install = join(root, "installed"), scripts = join(install, "skills", "chief-of-staff", "scripts");
    await cp(resolve(dirname(fileURLToPath(import.meta.url)), "../../../skills/chief-of-staff/scripts"), scripts, { recursive: true });
    await writeFile(join(install, ".margo-install"), "mode=copy\n", { mode: 0o600 });
    await writeFile(join(dirname(scripts), "preferences.md"), "## About me\n- **Role:** {not set}\n", { mode: 0o600 });
    const privateRoot = join(root, "private"), state = join(privateRoot, "state");
    await mkdir(privateRoot, { mode: 0o700 }); await mkdir(state, { mode: 0o700 });
    const config = join(privateRoot, "config.json"), account = "synthetic@example.com";
    await writeFile(config, JSON.stringify({ account, profiles: { [account]: { assistant_name: "Rowan" } } }), { mode: 0o600 });
    const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.startsWith("MARGO_") && key !== "COPILOT_HOME"));
    const execute = (command, args, options) => executeWithInput(command, args, { ...options, env, cwd: root });
    const python = process.platform === "win32" ? "python" : "python3";
    const cli = async (file, args, input) => JSON.parse((await execute(python, ["-B", join(scripts, file), ...args], {
        encoding: "utf8", shell: false, input: input === undefined ? undefined : JSON.stringify(input),
    })).stdout);
    await cli("margo_store.py", ["locations-bind", "--config-path", config, "--state-dir", state, "--account", account, "--expected-revision", "missing"]);
    await cli("task_state.py", ["init"]);
    const time = new Date();
    const slot = offset => { const value = new Date(time.getTime() + offset * 60000); return value.toISOString().slice(11, 16); };
    const setting = { enabled: true, timezone: "UTC", workdays: [0, 1, 2, 3, 4, 5, 6],
        slots: { morning: [slot(0)], sweep: [slot(30)], eod: [slot(60)] }, grace_minutes: 15 };
    await cli("app_proactive.py", ["policy-set", "--expected-revision", (await cli("margo_store.py", ["profile-show"])).revision], setting);
    const configBefore = await readFile(config, "utf8");
    const backend = createProactiveBackend({ resolveScript: async () => join(scripts, "work_state.py"), execute });
    const context = await backend.run("context", { routine: "morning" }, "sdk:synthetic");
    assert.equal(context.status, "due");
    assert.equal(context.assistant_name, "Rowan");
    assert.equal(context.memory_required, false);
    const started = await backend.run("start", { routine: "morning",
        identity: { principal: account, observed_at: new Date().toISOString(), evidence_ref: "tool:synthetic-me" } }, "sdk:synthetic");
    assert.equal(started.status, "started");
    const payload = { summary: "No new asks in this bounded synthetic source read.", candidates: [],
        sources: ["mail", "calendar", "direct_messages", "mentions"].map(source => ({
            source, kind: "enumeration", status: "complete", evidence_ref: "tool:synthetic-empty", observations: [],
        })) };
    const result = await backend.run("finish", payload, "sdk:synthetic", started.claim);
    assert.equal(result.status, "ready"); assert.equal(result.display, "notify");
    assert.equal(result.approval_granted, false);
    assert.equal((await backend.run("finish", payload, "sdk:synthetic", started.claim)).display, "silent");
    assert.equal((await backend.run("context", { routine: "morning" }, "sdk:new-session")).reason, "slot_already_recorded");
    assert.equal(await readFile(config, "utf8"), configBefore);
    assert.deepEqual(await readdir(join(install, "margo")), ["locations.json"]);
    const work = await cli("work_state.py", ["list", "--view", "all"]);
    assert.equal(work.actions.length, 0);
    assert.equal(work.items.length, 0);
    await assert.rejects(cli("memory_state.py", ["list"]), error => /not_initialized/.test(error.stderr));
});
