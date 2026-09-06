import { joinSession, createCanvas, CanvasError } from "@github/copilot-sdk/extension";
import { createBackend } from "./backend.mjs";
import { startServer } from "./server.mjs";

const backend = createBackend();
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
    ],
});
