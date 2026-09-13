import { joinSession, createCanvas, CanvasError } from "@github/copilot-sdk/extension";
import { createBackend } from "./backend.mjs";
import { startServer } from "./server.mjs";
import { createMemoryBackend } from "./memory-backend.mjs";
import { createTaskBackend } from "./task-backend.mjs";
import { createProactiveTools } from "./proactive-tools.mjs";
import { createAutomationBackend, createAutomationTool } from "./automation-backend.mjs";

const backend = createBackend();
const memoryBackend = createMemoryBackend();
const taskBackend = createTaskBackend();
const servers = new Map();
const emptyInput = { type: "object", properties: {}, additionalProperties: false };
const workspaceInput = { type: "object", properties: {
    section: { type: "string", enum: ["work", "memory", "tasks", "automations", "config"] },
}, additionalProperties: false };

async function read(operation, input = {}) {
    try {
        return await backend.run(operation, input);
    } catch (error) {
        throw new CanvasError(error.code || "backend_error", error.message);
    }
}

async function openWorkspace(ctx, section = "work") {
    const profile = await read("profile");
    if (!servers.has(ctx.instanceId)) {
        const pending = startServer({
            backend, memoryBackend, taskBackend,
            sendReview: options => session.send(options),
            host: `sdk:${session.sessionId}`,
        });
        servers.set(ctx.instanceId, pending);
        pending.catch(() => servers.delete(ctx.instanceId));
    }
    const entry = await servers.get(ctx.instanceId);
    const url = new URL(entry.url);
    url.pathname = section === "work" ? "/" : `/${section}`;
    return { title: `${profile.assistant_name} Workspace`, url: url.href };
}

async function closeWorkspace(ctx) {
    const pending = servers.get(ctx.instanceId);
    servers.delete(ctx.instanceId);
    if (pending) await (await pending).close();
}

const proactive = createProactiveTools({ getSession: () => session });
const automationBackend = createAutomationBackend();
const session = await joinSession({
    tools: [...proactive.tools, createAutomationTool(() => session, automationBackend)],
    hooks: proactive.hooks,
    canvases: [
        createCanvas({
            id: "margo-action-desk",
            displayName: "Margo Workspace",
            description: "One Margo workspace for Work, Memory, Tasks, Automations and Config. Local preparation and descriptor edits never approve, send or enable native schedules.",
            inputSchema: workspaceInput,
            actions: [
                {
                    name: "snapshot",
                    description: "Read bounded decision context, source coverage and persisted preparation requests. Does not refresh M365.",
                    inputSchema: emptyInput,
                    handler: () => read("desk"),
                },
                {
                    name: "list",
                    description: "Read work items from the configured Margo account.",
                    inputSchema: emptyInput,
                    handler: () => read("list"),
                },
                {
                    name: "show",
                    description: "Read one work item's current revision, payload and evidence.",
                    inputSchema: {
                        type: "object",
                        properties: { id: { type: "string", minLength: 1, maxLength: 200 } },
                        required: ["id"],
                        additionalProperties: false,
                    },
                    handler: (ctx) => read("show", ctx.input),
                },
                {
                    name: "refresh",
                    description: "Refresh open panels and read current work items. Does not modify work state.",
                    inputSchema: emptyInput,
                    handler: async () => {
                        for (const pending of servers.values()) (await pending).refresh();
                        return read("list");
                    },
                },
            ],
            open: ctx => openWorkspace(ctx, ctx.input?.section || "work"),
            onClose: closeWorkspace,
        }),
        createCanvas({
            id: "margo-memory",
            displayName: "Margo Memory (compatibility entry)",
            description: "Legacy entry opens the unified Margo Workspace on Memory. Prefer margo-action-desk with section: memory for new use. Existing read actions remain compatible.",
            inputSchema: emptyInput,
            actions: [
                { name: "list", description: "Read stored memory without changing it.",
                    inputSchema: { type: "object", properties: {
                        domain: { type: "string", enum: ["user", "agent"] },
                        status: { type: "string", enum: ["candidate", "active", "rejected", "disputed",
                            "stale", "superseded", "suppressed", "forgotten"] } },
                        additionalProperties: false }, handler: ctx => memoryBackend.run("list", ctx.input) },
                { name: "search", description: "Search eligible private memory by meaning, or explicitly choose scoped lexical search. No automatic fallback or model installation.",
                    inputSchema: { type: "object", properties: {
                        query: { type: "string", minLength: 1, maxLength: 4000 },
                        mode: { type: "string", enum: ["hybrid", "lexical"] },
                        usage: { type: "string", enum: ["reasoning", "drafting"] },
                        domain: { type: "string", enum: ["user", "agent"] },
                        routine: { type: "string", enum: ["calendar", "drafting", "meeting-prep",
                            "outcomes", "follow-through", "work-products"] } },
                        required: ["query"], additionalProperties: false },
                    handler: ctx => memoryBackend.run("search", ctx.input) },
                { name: "status", description: "Inspect memory and index health.",
                    inputSchema: emptyInput, handler: () => memoryBackend.run("status") },
            ],
            open: ctx => openWorkspace(ctx, "memory"),
            onClose: closeWorkspace,
        }),
        createCanvas({
            id: "margo-task-progress",
            displayName: "Margo Task Progress (compatibility entry)",
            description: "Legacy entry opens the unified Margo Workspace on Tasks. Prefer margo-action-desk with section: tasks for new use. Existing read actions remain compatible.",
            inputSchema: emptyInput,
            actions: [
                { name: "list", description: "Read task runs for the configured account, most recent first.",
                    inputSchema: { type: "object", properties: {
                        limit: { type: "integer", minimum: 1, maximum: 50 },
                        after: { type: "string", minLength: 1, maxLength: 200 } },
                        additionalProperties: false }, handler: ctx => taskBackend.run("list", ctx.input) },
                { name: "show", description: "Read one task run's current plan, steps, budgets and readiness.",
                    inputSchema: { type: "object", properties: {
                        id: { type: "string", minLength: 1, maxLength: 200 } },
                        required: ["id"], additionalProperties: false }, handler: ctx => taskBackend.run("show", ctx.input) },
                { name: "history", description: "Read one task run's recorded events, most recent first.",
                    inputSchema: { type: "object", properties: {
                        id: { type: "string", minLength: 1, maxLength: 200 },
                        limit: { type: "integer", minimum: 1, maximum: 100 } },
                        required: ["id"], additionalProperties: false }, handler: ctx => taskBackend.run("history", ctx.input) },
                { name: "health", description: "Inspect aggregate task journal health for the configured account.",
                    inputSchema: emptyInput, handler: () => taskBackend.run("health") },
            ],
            open: ctx => openWorkspace(ctx, "tasks"),
            onClose: closeWorkspace,
        }),
    ],
});
