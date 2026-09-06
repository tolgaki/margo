import { dirname, join } from "node:path";
import { executeWithInput, resolveCore, BackendError } from "./backend.mjs";

const fields = {
    list: ["domain", "status"], show: ["id"], inspect: ["id"],
    graph: ["id", "depth", "limit", "routine", "domain", "usage"], search: ["query", "routine", "mode", "domain", "usage"],
    status: [], policy: [], review: ["id", "revision", "intent"],
};
const statuses = ["candidate", "active", "rejected", "disputed", "stale", "superseded", "suppressed", "forgotten"];
export function validateMemoryInput(operation, input) {
    const allowed = fields[operation];
    if (!allowed || !input || typeof input !== "object" || Array.isArray(input)
        || Object.keys(input).some(key => !allowed.includes(key))) {
        throw new BackendError("invalid_input", "Unexpected memory operation or input fields.", 400);
    }
    if (["show", "inspect", "graph", "review"].includes(operation)
        && (typeof input.id !== "string" || !/^[A-Za-z0-9][A-Za-z0-9_-]{0,199}$/.test(input.id))) {
        throw new BackendError("invalid_input", "Invalid memory identity.", 400);
    }
    if (operation === "search" && (typeof input.query !== "string" || !input.query.trim() || input.query.length > 4000)) {
        throw new BackendError("invalid_input", "Supply a query of at most 4000 characters.", 400);
    }
    for (const [key, values] of Object.entries({
        domain: ["user", "agent"], status: statuses, mode: ["hybrid", "lexical"],
        usage: ["reasoning", "drafting"],
        routine: ["calendar", "drafting", "meeting-prep", "outcomes", "follow-through", "work-products"],
        intent: ["correct", "forget", "supersede", "do-not-use", "export"],
    })) {
        if (input[key] !== undefined && !values.includes(input[key])) {
            throw new BackendError("invalid_input", `Unsupported memory ${key}.`, 400);
        }
    }
    for (const [key, maximum] of [["depth", 2], ["limit", 20], ["revision", Number.MAX_SAFE_INTEGER]]) {
        if ((input[key] !== undefined || (key === "revision" && operation === "review"))
            && (!Number.isSafeInteger(input[key]) || input[key] < 1 || input[key] > maximum)) {
            throw new BackendError("invalid_input", `Invalid memory ${key}.`, 400);
        }
    }
    if (operation === "search" && input.mode === "lexical" && !input.domain && !input.routine) {
        throw new BackendError("invalid_input", "Explicit keyword-only search requires a domain or routine scope.", 400);
    }
}

function record(value, id) {
    return value && typeof value === "object" && !Array.isArray(value)
        && typeof value.id === "string" && (id === undefined || value.id === id)
        && Number.isSafeInteger(value.revision) && value.revision > 0;
}

function commandFailureCode(error) {
    if (typeof error?.stderr !== "string" || error.stderr.length > 64 * 1024) return undefined;
    try {
        const value = JSON.parse(error.stderr);
        if (value?.error === "memory schema marker mismatch; explicit migration required"
            || value?.error?.code === "migration_required" || value?.code === "migration_required") return "migration_required";
        if (value?.code === "not_initialized") return "not_initialized";
    } catch { return undefined; }
    return undefined;
}

export function validateMemoryResult(operation, data, input = {}) {
    if (operation === "graph" && data?.status === "blocked" && data.requires_larger_budget === true
        && typeof data.reason === "string" && Array.isArray(data.nodes) && data.nodes.length === 0) {
        data = { edges: [], gaps: [], ...data };
    }
    let valid = data && typeof data === "object" && !Array.isArray(data) && !("error" in data);
    if (valid) {
        if (operation === "list") valid = Array.isArray(data.memories) && data.memories.every(value => record(value));
        if (operation === "show") valid = record(data, input.id);
        if (operation === "search") valid = Array.isArray(data.results) && data.results.every(value => record(value?.memory))
            && (data.mode === undefined || data.mode === (input.mode ?? "hybrid"));
        if (operation === "inspect") valid = record(data.memory, input.id)
            && Array.isArray(data.history) && Array.isArray(data.links) && Array.isArray(data.usage)
            && data.forget_preview?.subject_id === input.id
            && data.forget_preview?.revision === data.memory.revision
            && Array.isArray(data.forget_preview?.affected) && Array.isArray(data.forget_preview?.retained);
        if (operation === "graph") valid = Array.isArray(data.nodes) && Array.isArray(data.edges) && Array.isArray(data.gaps)
            && data.nodes.length <= (input.limit ?? 20);
        if (operation === "status") valid = typeof data.account === "string" && !!data.account
            && data.memory && typeof data.memory === "object" && !Array.isArray(data.memory)
            && data.index && typeof data.index === "object" && !Array.isArray(data.index)
            && typeof data.embedding_runtime?.status === "string";
        if (operation === "policy") valid = Number.isSafeInteger(data.revision) && data.revision >= 0
            && typeof data.configured === "boolean" && data.data && typeof data.data === "object"
            && !Array.isArray(data.data) && typeof data.data.capture?.enabled === "boolean"
            && typeof data.data.usage_enabled === "boolean" && data.data.retention_days
            && typeof data.data.retention_days === "object" && !Array.isArray(data.data.retention_days);
    }
    if (!valid) throw new BackendError("invalid_backend_response", "Memory returned an incompatible response. Retry after checking the CLI in the conversation.", 502);
    return data;
}

export function createMemoryBackend({ resolveScript = resolveCore, execute = executeWithInput } = {}) {
    return {
        async run(operation, input = {}) {
            if (!["list", "show", "search", "status", "inspect", "graph", "policy"].includes(operation)) {
                throw new BackendError("invalid_input", "Memory operations here are read-only.", 400);
            }
            validateMemoryInput(operation, input);
            const script = join(dirname(await resolveScript()), "memory_state.py");
            const args = ["-B", script, operation];
            if (["show", "inspect", "graph"].includes(operation)) args.push(input.id);
            if (operation === "graph") args.push("--depth", String(input.depth ?? 2), "--limit", String(input.limit ?? 20));
            if (operation === "search") args.push("--input", "-");
            if (input.routine) args.push("--routine", input.routine);
            if (input.mode) args.push("--mode", input.mode);
            if (input.domain) args.push("--domain", input.domain);
            if (input.status) args.push("--status", input.status);
            if (input.usage) args.push("--usage", input.usage);
            let output;
            try {
                output = await execute(process.platform === "win32" ? "python" : "python3", args, {
                    cwd: dirname(script), shell: false, encoding: "utf8", windowsHide: true,
                    timeout: 60_000, maxBuffer: 4 * 1024 * 1024,
                    input: operation === "search" ? JSON.stringify({ query: input.query }) : undefined,
                });
            } catch (error) {
                const code = commandFailureCode(error);
                if (code === "migration_required") {
                    throw new BackendError("migration_required",
                        "Memory schema migration is required. In the foreground conversation, pause memory writers, review a private backup, and explicitly run memory_state.py migrate before retrying. This panel never migrates or repairs the database.", 409);
                }
                if (code === "not_initialized") {
                    throw new BackendError("setup_needed",
                        "Memory is not initialized. Confirm the account and explicitly run memory_state.py init in the foreground. This panel did not create storage.", 503);
                }
                throw new BackendError("memory_unavailable",
                    "Memory command failed. Check memory_state.py status in the conversation, then retry. If the local model is unavailable, explicitly select Keyword only and a routine/domain scope. No automatic keyword or cloud fallback was used.", 503);
            }
            let data;
            try {
                data = JSON.parse(output.stdout);
            } catch {
                throw new BackendError("invalid_backend_response", "Memory returned an incompatible response.", 502);
            }
            return validateMemoryResult(operation, data, input);
        },
    };
}
