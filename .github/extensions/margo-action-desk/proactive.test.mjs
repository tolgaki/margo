import test from "node:test";
import assert from "node:assert/strict";
import { readFile, mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { pathToFileURL, fileURLToPath } from "node:url";
import { AGENT_TOOLS, RUNTIME_TOOLS, allowedRead, createProactiveBackend, createProactiveTools, validatePreparation } from "./proactive-tools.mjs";

const context = { status: "due", routine: "morning", window: { start: "2026-09-13T12:00:00Z", end: "2026-09-14T12:00:00Z" },
    calendar_window: { start: "2026-09-14T00:00:00Z", end: "2026-09-15T00:00:00Z" } };
const start = { operation: "start", routine: "morning", identity: {
    principal: "synthetic@example.com", observed_at: "2026-09-14T12:00:00Z", evidence_ref: "tool:identity" } };
test("scheduled profile has an exact allowlist, no escape aliases, and ordinary Margo is unchanged", async () => {
    const source = await readFile(new URL("../../../agents/margo-proactive.agent.md", import.meta.url), "utf8");
    const names = [...source.split("---")[1].matchAll(/^  - (.+)$/gm)].map(match => match[1]);
    assert.deepEqual(names, AGENT_TOOLS);
    assert.ok(!names.some(name => /[*]|powershell|bash|shell|execute|agent$|read$|edit|sql|send|canvas|do_action|create_entity|update_entity|delete_entity|call_function|retrieve|ask$/.test(name)));
    const normal = await readFile(new URL("../../../agents/margo.agent.md", import.meta.url), "utf8");
    assert.doesNotMatch(normal.split("---")[1], /^tools:/m);
});

test("restricted schemas and fixed adapter reject unknown operations, oversized content and arbitrary identity/paths", async () => {
    for (const key of ["sql", "path", "command", "account", "approved"]) assert.throws(() => validatePreparation({ ...start, [key]: "bad" }));
    assert.throws(() => validatePreparation({ operation: "approve", id: "anything" }));
    assert.throws(() => validatePreparation({ ...start, identity: { ...start.identity, evidence_ref: "x".repeat(200000) } }));
    let call;
    const backend = createProactiveBackend({ resolveScript: async () => resolve("fixture", "work_state.py"),
        execute: async (...args) => { call = args; return { stdout: '{"status":"skipped","reason":"disabled"}' }; } });
    await backend.run("context", { routine: "morning" }, "sdk:fixture");
    assert.deepEqual(call[1].slice(2), ["tool"]);
    assert.equal(call[2].shell, false);
    assert.match(call[1][1], /app_proactive.py$/);
    assert.equal(JSON.parse(call[2].input).host, "sdk:fixture");
    await assert.rejects(backend.run("execute", {}, "sdk:fixture"));
});

test("provider parameters bound mail/calendar/DM paths and deny mutation/federated/file/function surfaces", () => {
    assert.equal(allowedRead("workiq-fetch", { entityUrls: ["/me?$select=id,userPrincipalName"] }, context, true), true);
    for (const path of ["/me/drive/root", "/teams/channel/messages", "https://example.com", "/me/chats/%2fother/messages",
        "/me/messages?$select=id&$top=500", "/me/messages?$select=id&$top=25&$expand=attachments"]) {
        assert.equal(allowedRead("workiq-fetch", { entityUrls: [path] }, context), false);
    }
    const filter = `receivedDateTime ge ${context.window.start} and receivedDateTime lt ${context.window.end}`;
    assert.equal(allowedRead("workiq-fetch", { entityUrls: [`/me/messages?$select=id,subject&$top=25&$filter=${filter}`] }, context), true);
    assert.equal(allowedRead("workiq-fetch", { entityUrls: [`/me/messages?$select=id,subject&$top=25&$filter=${filter} or isRead eq false`] }, context), false);
    assert.equal(allowedRead("workiq-get_schema", { operationType: "action", path: "/me/messages" }, context), false);
    assert.equal(allowedRead("workiq-call_function", { functionUrl: "/me/action" }, context), false);
    assert.equal(allowedRead("workiq-retrieve", { query: ["anything"] }, context), false);
    assert.equal(allowedRead("workiq-fetch", { entityUrls: ["/me?$select=id,userPrincipalName"], agentId: "other" }, context), false);
});

test("scoped hooks deny unexpected SQL/delegation and charge reads, without changing default Margo", async () => {
    const calls = [];
    let selected = { name: "margo", tools: null };
    const session = { sessionId: "fixture", rpc: { agent: { getCurrent: async () => ({ agent: selected }) },
        tools: { getCurrentMetadata: async () => ({ tools: [...RUNTIME_TOOLS, "sql"].map(name => ({ name })) }) } } };
    const api = createProactiveTools({ getSession: () => session, backend: { run: async (op, input) => {
        calls.push([op, input]);
        if (op === "context") return context;
        if (op === "start") return { status: "started", claim: { token: "secret", attempt_id: "attempt" }, timing: context };
        if (op === "charge") return { execute: true };
        return { status: "ready", display: "silent" };
    } } });
    assert.equal(await api.hooks.onPreToolUse({ toolName: "powershell", toolArgs: {} }), undefined);
    assert.equal((await api.tools[0].handler({ routine: "morning" })).resultType, "failure");
    selected = { name: "margo-proactive", tools: AGENT_TOOLS };
    for (const toolName of ["sql", "task", "send_session_message", "workiq-do_action", "powershell"]) {
        assert.equal((await api.hooks.onPreToolUse({ toolName, toolArgs: {} })).permissionDecision, "deny");
    }
    assert.equal((await api.tools[0].handler({ routine: "morning" })).resultType, "success");
    assert.equal(await api.hooks.onPreToolUse({ toolName: "workiq-fetch", toolArgs: { entityUrls: ["/me?$select=id,userPrincipalName"] } }), undefined);
    const result = await api.tools[1].handler(start);
    assert.equal(result.resultType, "success", JSON.stringify(result));
    assert.doesNotMatch(result.textResultForLlm, /secret/);
    assert.equal(await api.hooks.onPreToolUse({ toolName: "workiq-fetch", toolArgs: { entityUrls: ["/me/chats?$select=id,chatType&$top=10"] } }), undefined);
    assert.equal(calls.at(-1)[0], "charge");
    session.rpc.agent.getCurrent = async () => { throw new Error("Metadata unavailable"); };
    const fresh = createProactiveTools({ getSession: () => session });
    assert.equal((await fresh.hooks.onPreToolUse({ toolName: "sql", toolArgs: {} })).permissionDecision, "deny");
});

test("installed SDK selects exact profile, filters synthetic MCP tools and enforces deny hook before SQL", {
    skip: !process.env.MARGO_SDK_MODULE || !process.env.MARGO_CLI_LOADER ? "Set installed SDK module and existing CLI loader for no-model runtime verification." : false,
}, async t => {
    const { CopilotClient, RuntimeConnection } = await import(pathToFileURL(process.env.MARGO_SDK_MODULE).href);
    const root = await mkdtemp(join(tmpdir(), "margo-agent-runtime-"));
    const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !/TOKEN|SECRET|PASSWORD|MARGO_|COPILOT_/i.test(key)));
    const client = new CopilotClient({ connection: RuntimeConnection.forStdio({ path: process.execPath, args: [process.env.MARGO_CLI_LOADER] }),
        baseDirectory: root, workingDirectory: root, env, useLoggedInUser: false, logLevel: "error" });
    t.after(async () => { await client.stop(); await rm(root, { recursive: true, force: true, maxRetries: 3, retryDelay: 200 }); });
    let session;
    const api = createProactiveTools({ getSession: () => session, backend: { run: async () => ({ status: "skipped", reason: "synthetic_disabled" }) } });
    const profile = await readFile(new URL("../../../agents/margo-proactive.agent.md", import.meta.url), "utf8");
    try {
    session = await client.createSession({ enableConfigDiscovery: false, workingDirectory: root,
        customAgents: [{ name: "margo-proactive", description: "Synthetic profile verification", tools: [...AGENT_TOOLS], prompt: profile.split("---").slice(2).join("---") }],
        agent: "margo-proactive", tools: [...api.tools, { name: "escape_fixture", description: "Not allowed", handler: () => { throw new Error("Must never execute"); } }],
        hooks: api.hooks, mcpServers: { workiq: { type: "local", command: process.execPath,
            args: [fileURLToPath(new URL("../../../tests/fixtures/proactive-mcp.mjs", import.meta.url))], tools: ["*"] } },
        onPermissionRequest: async request => request.kind === "custom-tool" && request.toolName === "margo_proactive_context"
            ? { kind: "approve-once" } : { kind: "reject" } });
    await session.rpc.tools.initializeAndValidate();
    assert.equal((await session.rpc.agent.getCurrent()).agent.name, "margo-proactive");
    const names = (await session.rpc.tools.getCurrentMetadata()).tools.map(tool => tool.name);
    for (const name of RUNTIME_TOOLS) assert.ok(names.includes(name), `${name} must resolve, not be silently ignored: ${names.join(",")}`);
    assert.ok(names.every(name => RUNTIME_TOOLS.includes(name) || name === "sql"));
    const sql = await session.rpc.tools.execute({ name: "sql", arguments: { description: "Harmless denied read", query: "SELECT 1" } });
    assert.equal(sql.resultType, "denied");
    assert.match(sql.textResultForLlm, /restricted scheduled profile/);
    const result = await session.rpc.tools.execute({ name: "margo_proactive_context", arguments: { routine: "morning" } });
    assert.equal(result.resultType, "success", JSON.stringify(result));
    assert.match(result.textResultForLlm, /synthetic_disabled/);
    await session.rpc.agent.deselect();
    await session.rpc.tools.initializeAndValidate();
    const denied = await session.rpc.tools.execute({ name: "margo_proactive_context", arguments: { routine: "morning" } });
    assert.equal(denied.resultType, "failure");
    assert.match(denied.textResultForLlm, /exact reviewed margo-proactive/);
    // No model request or Work IQ read was made.
    } catch (error) { process.stderr.write(`Synthetic SDK check failed: ${error.stack}\n`); throw error; }
});
