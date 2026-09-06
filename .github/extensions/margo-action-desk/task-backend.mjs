// Read-only adapter over task_state.py. Every command is a bounded, side-effect-free read;
// this module never writes, initializes or migrates the task journal. Pause/cancel/resume/
// replan/recover/reconcile are exposed only as foreground REVIEW REQUESTS from server.mjs,
// never as executable commands here.
import { dirname, join } from "node:path";
import { executeWithInput, resolveCore, BackendError } from "./backend.mjs";

const operations = new Set(["list", "show", "history", "health"]);
const idPattern = /^[A-Za-z0-9][A-Za-z0-9_-]{0,199}$/;
const fields = { list: ["limit", "after"], show: ["id"], history: ["id", "limit"], health: [] };
const METRICS = ["tool_calls", "pages", "items", "model_calls", "output_chars"];

function invalid(message) {
    throw new BackendError("invalid_input", message, 400);
}

export function validateTaskInput(operation, input = {}) {
    const allowed = fields[operation];
    if (!allowed) invalid("This task command is not available in the canvas.");
    if (!input || typeof input !== "object" || Array.isArray(input)) invalid("Expected an input object.");
    if (Object.keys(input).some((key) => !allowed.includes(key))) invalid("Unknown input fields are not accepted.");
    if (["show", "history"].includes(operation) && (typeof input.id !== "string" || !idPattern.test(input.id))) {
        invalid("Invalid task run ID.");
    }
    if (operation === "list" && input.after !== undefined
        && (typeof input.after !== "string" || (input.after !== "" && !idPattern.test(input.after)))) {
        invalid("Invalid list cursor.");
    }
    if (operation === "list" && input.limit !== undefined
        && (!Number.isSafeInteger(input.limit) || input.limit < 1 || input.limit > 50)) {
        invalid("List limit must be between 1 and 50.");
    }
    if (operation === "history" && input.limit !== undefined
        && (!Number.isSafeInteger(input.limit) || input.limit < 1 || input.limit > 100)) {
        invalid("History limit must be between 1 and 100.");
    }
}

function isRecord(value) {
    return !!value && typeof value === "object" && !Array.isArray(value);
}

function isCostMap(value) {
    return isRecord(value) && Object.keys(value).length === METRICS.length
        && METRICS.every((metric) => Number.isSafeInteger(value[metric]) && value[metric] >= 0);
}

function containsCredential(value, depth = 0) {
    if (depth > 32) return true;
    if (!value || typeof value !== "object") return false;
    if (Array.isArray(value)) return value.some((item) => containsCredential(item, depth + 1));
    return Object.entries(value).some(([key, item]) =>
        ["token", "claim_token", "token_hash", "access_token", "refresh_token", "authorization"].includes(key.toLowerCase())
        || containsCredential(item, depth + 1));
}

function isRunSummary(value) {
    return isRecord(value) && typeof value.id === "string" && typeof value.account === "string"
        && Number.isSafeInteger(value.revision) && typeof value.state === "string" && typeof value.status === "string"
        && typeof value.plan_hash === "string" && isCostMap(value.usage) && isCostMap(value.remaining)
        && Array.isArray(value.expired_claims) && Array.isArray(value.unresolved_effects)
        && Array.isArray(value.blocked_reasons) && typeof value.created_at === "string" && typeof value.updated_at === "string";
}

function isListRow(value) {
    return isRunSummary(value) && typeof value.goal === "string" && typeof value.routine === "string"
        && typeof value.mode === "string" && isRecord(value.step_counts);
}

function isStep(value) {
    return isRecord(value) && typeof value.key === "string" && Number.isSafeInteger(value.revision)
        && typeof value.state === "string" && isRecord(value.definition) && Number.isSafeInteger(value.attempts)
        && Array.isArray(value.blocked_reasons) && typeof value.in_current_plan === "boolean"
        && !("token" in value) && !("claim_token" in value);
}

export function validateTaskResult(operation, data, input = {}) {
    let valid = isRecord(data) && !("error" in data) && !containsCredential(data);
    if (valid) {
        if (operation === "list") {
            valid = typeof data.account === "string" && Array.isArray(data.runs) && data.runs.every(isListRow)
                && data.runs.length <= (input.limit ?? 20)
                && (data.next_cursor === null || typeof data.next_cursor === "string");
        }
        if (operation === "show") {
            valid = isRunSummary(data) && data.id === input.id && isRecord(data.plan) && Array.isArray(data.steps)
                && data.steps.every(isStep) && Array.isArray(data.ready_steps) && data.token_usage === null
                && data.model_cost === null && data.approval_granted === false && typeof data.limits_enforcement === "string"
                && isRecord(data.plan.limits)
                && METRICS.every((metric) => Number.isSafeInteger(data.plan.limits[metric]) && data.plan.limits[metric] >= 0);
        }
        if (operation === "history") {
            valid = data.run_id === input.id && Array.isArray(data.events) && data.events.every((event) =>
                isRecord(event) && Number.isSafeInteger(event.revision) && typeof event.event === "string"
                && typeof event.created_at === "string" && !("token" in event) && !("claim_token" in event));
        }
        if (operation === "health") {
            valid = typeof data.account === "string" && typeof data.status === "string" && isRecord(data.runs_by_state)
                && Number.isSafeInteger(data.expired_claims) && Number.isSafeInteger(data.unreconciled_attempts);
        }
    }
    if (!valid) throw new BackendError("invalid_backend_response", "The task journal returned an incompatible response. Update the core and canvas together.", 502);
    return data;
}

function commandFailureCode(error) {
    if (typeof error?.stderr !== "string" || error.stderr.length > 64 * 1024) return undefined;
    try {
        const value = JSON.parse(error.stderr);
        if (value?.code === "not_initialized") return "not_initialized";
        if (typeof value?.error === "string" && /unknown task run/i.test(value.error)) return "not_found";
        return undefined;
    } catch {
        return undefined;
    }
}

export async function resolveTaskScript(resolveScript = resolveCore) {
    return join(dirname(await resolveScript()), "task_state.py");
}

export function createTaskBackend({ resolveScript = resolveCore, execute = executeWithInput } = {}) {
    return {
        async run(operation, input = {}) {
            if (!operations.has(operation)) invalid("Task commands here are read-only.");
            validateTaskInput(operation, input);
            const script = await resolveTaskScript(resolveScript);
            const args = ["-B", script, operation];
            if (["show", "history"].includes(operation)) args.push(input.id);
            if (operation === "list") {
                args.push("--limit", String(input.limit ?? 20));
                if (input.after) args.push("--after", input.after);
            }
            if (operation === "history") args.push("--limit", String(input.limit ?? 20));
            let output;
            try {
                output = await execute(process.platform === "win32" ? "python" : "python3", args, {
                    shell: false,
                    cwd: dirname(script),
                    timeout: 10_000,
                    maxBuffer: 4 * 1024 * 1024,
                    windowsHide: true,
                    encoding: "utf8",
                });
            } catch (error) {
                if (error instanceof BackendError) throw error;
                if (error.code === "ENOENT") throw new BackendError("setup_needed", "Python is not available on the extension's PATH.", 503);
                if (error.killed) throw new BackendError("backend_timeout", "The task journal did not respond within 10 seconds.", 504);
                const code = commandFailureCode(error);
                if (code === "not_initialized") {
                    throw new BackendError("not_initialized",
                        "Task tracking is not initialized for this account. This panel never creates task state; start or resume a task in the foreground conversation first.", 503);
                }
                if (code === "not_found") throw new BackendError("not_found", "That task run does not exist for the configured account.", 404);
                throw new BackendError("task_unavailable", "The task journal command failed. Check task_state.py in the conversation, then retry.", 503);
            }
            let data;
            try {
                data = JSON.parse(output.stdout);
            } catch {
                throw new BackendError("invalid_backend_response", "The task journal returned an incompatible response.", 502);
            }
            return validateTaskResult(operation, data, input);
        },
    };
}
