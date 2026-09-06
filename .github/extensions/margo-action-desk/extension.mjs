import { joinSession, createCanvas, CanvasError } from "@github/copilot-sdk/extension";
import { createBackend } from "./backend.mjs";
import { startServer } from "./server.mjs";
import { createMemoryBackend } from "./memory-backend.mjs";
import { createTaskBackend } from "./task-backend.mjs";

const backend = createBackend();
const memoryBackend = createMemoryBackend();
const taskBackend = createTaskBackend();
const servers = new Map();
const emptyInput = { type: "object", properties: {}, additionalProperties: false };

async function read(operation, input = {}) {
    try {
        return await backend.run(operation, input);
    } catch (error) {
        throw new CanvasError(error.code || "backend_error", error.message);
    }
}

const session = await joinSession({
    canvases: [
        createCanvas({
            id: "margo-action-desk",
            displayName: "Margo Action Desk",
            description: "Review persistent Margo proposals, evidence and local edits; request conversation review without approving or sending.",
            inputSchema: emptyInput,
            actions: [
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
            open: async (ctx) => {
                if (!servers.has(ctx.instanceId)) {
                    const pending = startServer({
                        backend,
                        sendReview: (options) => session.send(options),
                    });
                    servers.set(ctx.instanceId, pending);
                    pending.catch(() => servers.delete(ctx.instanceId));
                }
                const entry = await servers.get(ctx.instanceId);
                return { title: "Margo Action Desk", url: entry.url };
            },
            onClose: async (ctx) => {
                const pending = servers.get(ctx.instanceId);
                servers.delete(ctx.instanceId);
                if (pending) await (await pending).close();
            },
        }),
        createCanvas({
            id: "margo-memory",
            displayName: "Margo Memory",
            description: "Inspect private facts, people, projects, lessons, history and relationships; search local memory and request foreground discussion without consent.",
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
            open: async ctx => {
                if (!servers.has(ctx.instanceId)) {
                    const pending = startServer({ backend, memoryBackend, sendReview: options => session.send(options) });
                    servers.set(ctx.instanceId, pending);
                    pending.catch(() => servers.delete(ctx.instanceId));
                }
                const entry = await servers.get(ctx.instanceId);
                const url = new URL(entry.url);
                url.pathname = "/memory";
                return { title: "Margo Memory", url: url.href };
            },
            onClose: async ctx => {
                const pending = servers.get(ctx.instanceId);
                servers.delete(ctx.instanceId);
                if (pending) await (await pending).close();
            },
        }),
        createCanvas({
            id: "margo-task-progress",
            displayName: "Margo Task Progress",
            description: "Read-only view of bounded task runs: plan, steps, budgets and history. Requests foreground review of pause/cancel/resume/replan/recover/reconcile; never approves or executes them.",
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
            open: async ctx => {
                if (!servers.has(ctx.instanceId)) {
                    const pending = startServer({ backend, memoryBackend, taskBackend, sendReview: options => session.send(options) });
                    servers.set(ctx.instanceId, pending);
                    pending.catch(() => servers.delete(ctx.instanceId));
                }
                const entry = await servers.get(ctx.instanceId);
                const url = new URL(entry.url);
                url.pathname = "/tasks";
                return { title: "Margo Task Progress", url: url.href };
            },
            onClose: async ctx => {
                const pending = servers.get(ctx.instanceId);
                servers.delete(ctx.instanceId);
                if (pending) await (await pending).close();
            },
        }),
    ],
});
