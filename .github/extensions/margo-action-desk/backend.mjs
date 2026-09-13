import { execFile } from "node:child_process";
import { stat } from "node:fs/promises";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);
const commands = new Set(["list", "show", "profile", "profile-save", "desk", "desk-request", "desk-dispatched", "revise", "defer", "dismiss"]);

export async function executeWithInput(command, args, options, launch = execFileAsync) {
    const { input, ...processOptions } = options;
    const pending = launch(command, args, processOptions);
    // No intermediate files, shell expansion, or payload exposure in process argv.
    let writeError;
    let delivered = false;
    const written = new Promise((resolve) => {
        const stream = pending.child.stdin;
        stream.on("error", (error) => { writeError ||= error; resolve(); });
        stream.once("close", () => {
            if (!delivered && !writeError) writeError = Object.assign(new Error("Stdin closed before delivery."), { code: "ERR_STREAM_PREMATURE_CLOSE" });
            resolve();
        });
        try {
            stream.end(input, (error) => {
                if (error) writeError ||= error;
                else delivered = true;
                resolve();
            });
        } catch (error) {
            writeError = error;
            resolve();
        }
    });
    const [result] = await Promise.allSettled([pending, written]);
    if (writeError && writeError.code !== "EPIPE") {
        throw new BackendError("backend_input_failed", "The work ledger input stream failed. Reload the item before retrying.", 503);
    }
    // Preserve a CLI rejection (often accompanied by EPIPE), but never call an
    // edit successful when its required input was not fully written.
    if (result.status === "rejected") throw result.reason;
    if (input !== undefined && (!delivered || writeError)) {
        throw new BackendError("backend_input_failed", "The action input was not delivered. Reload the item before retrying.", 503);
    }
    return result.value;
}

export class BackendError extends Error {
    constructor(code, message, status = 500) {
        super(message);
        this.code = code;
        this.status = status;
    }
}

function invalid(message) {
    throw new BackendError("invalid_input", message, 400);
}

export function validateInput(operation, input = {}) {
    if (!commands.has(operation) && operation !== "review") invalid("This operation is not available in the canvas.");
    if (!input || typeof input !== "object" || Array.isArray(input)) invalid("Expected an input object.");
    const fields = {
        list: [],
        desk: [],
        profile: [],
        "profile-save": ["expected_revision", "assistant_name", "work_root"],
        "desk-request": ["id", "expected_revision", "expected_hash", "intent", "host"],
        "desk-dispatched": ["attempt_id", "token", "accepted", "reference"],
        show: ["id"],
        revise: ["id", "expected_revision", "expected_hash", "payload"],
        defer: ["id", "expected_revision", "expected_hash", "until"],
        dismiss: ["id", "expected_revision", "expected_hash"],
        review: ["id", "expected_revision", "expected_hash"],
    }[operation];
    if (Object.keys(input).some((key) => !fields.includes(key))) invalid("Unknown input fields are not accepted.");
    if (["list", "desk", "profile"].includes(operation)) return;
    if (operation === "profile-save") {
        if (typeof input.expected_revision !== "string" || !/^[a-f0-9]{64}$/.test(input.expected_revision)
            || typeof input.assistant_name !== "string" || !/^[\p{L}\p{M}\p{N} .'-]{1,60}$/u.test(input.assistant_name)
            || input.assistant_name !== input.assistant_name.trim() || !/[\p{L}\p{N}]/u.test(input.assistant_name)
            || (input.work_root !== null && (typeof input.work_root !== "string" || !input.work_root
                || input.work_root.length > 4000 || /[\x00-\x1f\x7f]/.test(input.work_root)))) invalid("Invalid profile name, workspace or revision.");
        return;
    }
    if (operation === "desk-dispatched") {
        if (typeof input.attempt_id !== "string" || !/^task_attempt_[a-f0-9]{32}$/.test(input.attempt_id)
            || typeof input.token !== "string" || !/^[a-f0-9]{48}$/.test(input.token)
            || typeof input.accepted !== "boolean" || typeof input.reference !== "string"
            || !input.reference || input.reference.length > 1000) invalid("Invalid dispatch receipt.");
        return;
    }
    if (typeof input.id !== "string" || !/^[A-Za-z0-9][A-Za-z0-9._:-]{0,199}$/.test(input.id)) invalid("Invalid work item ID.");
    if (operation === "show") return;
    if (!Number.isSafeInteger(input.expected_revision) || input.expected_revision < 1) invalid("A positive expected revision is required.");
    if (operation === "desk-request") {
        if (!["prepare", "recommend", "review"].includes(input.intent)
            || typeof input.host !== "string" || !/^[A-Za-z0-9._:-]{1,200}$/.test(input.host)) invalid("Invalid decision request.");
        if (input.expected_hash === undefined || input.expected_hash === null) return;
    }
    if (typeof input.expected_hash !== "string" || !/^[a-f0-9]{64}$/i.test(input.expected_hash)) invalid("The exact expected payload hash is required.");
    if (operation === "revise") {
        if (!input.payload || typeof input.payload !== "object" || Array.isArray(input.payload)) invalid("Payload must be a JSON object.");
        if (Buffer.byteLength(JSON.stringify(input.payload)) > 64 * 1024) invalid("Payload exceeds the 64 KiB canvas limit.");
    }
    if (operation === "defer") {
        if (typeof input.until !== "string" || !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)$/.test(input.until)
            || !Number.isFinite(Date.parse(input.until)) || Date.parse(input.until) <= Date.now()) invalid("Defer until must be a future ISO timestamp with a timezone.");
    }
}

export async function resolveCore({
    extensionDirectory = dirname(fileURLToPath(import.meta.url)),
    home = homedir(),
    copilotHome = process.env.COPILOT_HOME || join(home, ".copilot"),
    inspect = stat,
} = {}) {
    const candidates = [
        join(extensionDirectory, "../../../skills/chief-of-staff/scripts/work_state.py"),
        join(extensionDirectory, "../../skills/chief-of-staff/scripts/work_state.py"),
        resolve(copilotHome, "skills/chief-of-staff/scripts/work_state.py"),
    ];
    for (const candidate of candidates) {
        try {
            if ((await inspect(candidate)).isFile()) return candidate;
        } catch (error) {
            if (error.code !== "ENOENT" && error.code !== "ENOTDIR") {
                throw new BackendError("core_unavailable", "The work ledger script cannot be read. Check the local installation.");
            }
        }
    }
    throw new BackendError("missing_core", "Margo's work_state.py is not installed in this project or the standard skill directory.", 503);
}

export function commandArguments(script, operation, input = {}) {
    validateInput(operation, input);
    if (!commands.has(operation)) invalid("Only read and local ledger commands may execute.");
    const args = ["-B", script, operation === "revise" ? "edit" : operation];
    if (["desk-request", "desk-dispatched", "profile-save"].includes(operation)) return [...args, "--input", "-"];
    if (!["list", "desk", "profile"].includes(operation)) args.push(input.id);
    if (operation === "list") args.push("--view", "all", "--json");
    if (operation === "show") args.push("--json");
    if (!["list", "show", "desk", "profile"].includes(operation)) {
        args.push("--revision", String(input.expected_revision), "--expected-hash", input.expected_hash);
    }
    if (operation === "revise") args.push("--input", "-");
    if (operation === "defer") args.push("--until", input.until);
    return args;
}

function backendFailure(result, fallback = "The work ledger command failed.") {
    const message = typeof result?.error === "string" ? result.error : result?.error?.message || fallback;
    const code = result?.error?.code || result?.code
        || (/revision|hash.*match|changed|stale/i.test(message) ? "revision_conflict"
            : /unknown entity/i.test(message) ? "not_found"
            : /account|configur|state root|state-root/i.test(message) ? "setup_needed"
            : /auth|credential|permission|access denied/i.test(message) ? "auth_blocked" : "backend_error");
    const status = /not_found/.test(code) ? 404
        : /conflict|revision|stale/.test(code) ? 409
        : /invalid/.test(code) ? 400
        : /setup|config|account|auth|location|not_initialized/.test(code) ? 503 : 500;
    return new BackendError(code, message, status);
}

export function localSetupFailure(error) {
    if (typeof error?.stderr !== "string" || error.stderr.length > 64 * 1024) return null;
    let value;
    try { value = JSON.parse(error.stderr); } catch { return null; }
    if (value.code === "account_setup_required") return new BackendError(value.code,
        "No local owner is configured at the resolved location. Run margo_store.py locations and bind the approved existing private root, or explicitly configure an owner. This is not an M365 sign-in result.", 503);
    if (value.code === "location_unavailable") return new BackendError(value.code,
        "The explicit private-location binding or configuration is unavailable. Inspect margo_store.py locations and its revision; correct the binding or override. No fallback or new store was created.", 503);
    return null;
}

export function normalizeItem(record) {
    if (!record || typeof record !== "object" || typeof record.id !== "string") {
        throw new BackendError("invalid_backend_response", "The work ledger returned an incompatible record.");
    }
    const data = record.data || {};
    const approval = record.approvals?.filter((entry) => !entry.invalidated_at
        && entry.revision === record.revision && entry.action_hash === record.action_hash)
        .sort((a, b) => Date.parse(b.expires_at) - Date.parse(a.expires_at))[0];
    return {
        ...record,
        title: record.title || data.title || data.summary || record.payload?.subject || record.payload?.title || record.kind || record.id,
        status: record.state,
        payload_hash: record.action_hash,
        payload: record.payload || data,
        why_now: record.why || data.why || data.why_now || data.reason,
        evidence: record.source_refs || data.source_refs,
        source_changed: !!record.stale,
        expires_at: approval?.expires_at,
        provenance: {
            account: record.account, type: record.type, kind: record.kind,
            created_at: record.created_at, updated_at: record.updated_at,
            work_item_id: record.work_item_id, target_fingerprint: record.target_fingerprint,
            confirmation: record.confirmation, relationships: record.relationships,
        },
    };
}

export function createBackend({
    resolveScript = resolveCore,
    execute = executeWithInput,
    python = process.platform === "win32" ? "python" : "python3",
} = {}) {
    async function invoke(operation, input = {}, document) {
            validateInput(operation, input);
            if (!commands.has(operation)) invalid("This command cannot execute from the canvas.");
            const script = await resolveScript();
            let output;
            try {
                output = await execute(python, commandArguments(script, operation, input), {
                    shell: false,
                    cwd: dirname(script),
                    timeout: 10_000,
                    maxBuffer: 4 * 1024 * 1024,
                    windowsHide: true,
                    encoding: "utf8",
                    input: document === undefined ? undefined : JSON.stringify(document),
                });
            } catch (error) {
                if (error instanceof BackendError) throw error;
                if (error.code === "ENOENT") throw new BackendError("setup_needed", "Python is not available on the extension's PATH.", 503);
                if (error.killed) throw new BackendError("backend_timeout", "The work ledger did not respond within 10 seconds.", 504);
                try {
                    output = { failure: true, result: JSON.parse(error.stdout || error.stderr || "") };
                } catch {
                    throw new BackendError("backend_error", "The work ledger exited unsuccessfully without a JSON error. Check the local CLI installation.");
                }
            }
            let result = output.result;
            if (!result) {
                try { result = JSON.parse(output.stdout); } catch {
                    throw new BackendError("invalid_backend_response", "The work ledger returned invalid JSON. Update the core and canvas together.");
                }
            }
            if (output.failure || result?.error || result?.ok === false) throw backendFailure(result);
            if (["profile", "profile-save"].includes(operation)) {
                if (typeof result?.assistant_name !== "string" || !result.work_root
                    || result.runtime_state_moved !== false) {
                    throw new BackendError("invalid_backend_response", "The profile response is incompatible. Update the core and canvas together.");
                }
                return result;
            }
            const listing = ["list", "desk"].includes(operation);
            const request = ["desk-request", "desk-dispatched"].includes(operation);
            if (!result || (listing ? !Array.isArray(result.items) || !Array.isArray(result.actions) || !Array.isArray(result.records)
                : request ? typeof result.request?.run_id !== "string" || typeof result.request?.phase !== "string" : !result.id)) {
                throw new BackendError("invalid_backend_response", "The work ledger returned an incompatible response. Update the core and canvas together.");
            }
            if (operation === "desk" && (typeof result.read_at !== "string" || !result.coverage
                || !result.time_preferences || !result.request_capability || !result.requests || !Array.isArray(result.truncated))) {
                throw new BackendError("invalid_backend_response", "The decision workspace contract is incomplete. Update the core and canvas together.");
            }
            if (request) return result;
            if (listing) {
                return { ...result, items: [...result.actions, ...result.items, ...result.records].map(normalizeItem) };
            }
            return { item: normalizeItem(result) };
    }
    return {
        async run(operation, input = {}) {
            validateInput(operation, input);
            if (!commands.has(operation)) invalid("This command cannot execute from the canvas.");
            if (["list", "show", "desk", "profile"].includes(operation)) return invoke(operation, input);
            if (["desk-request", "desk-dispatched", "profile-save"].includes(operation)) return invoke(operation, input, input);
            const { item } = await invoke("show", { id: input.id });
            if (item.type !== "action") invalid("Only local action proposals can be changed here. Review work-item transitions in the conversation.");
            if (item.revision !== input.expected_revision || item.action_hash !== input.expected_hash) {
                throw new BackendError("revision_conflict", "The action changed. Reload its current revision and hash.", 409);
            }
            let document;
            if (operation === "revise") {
                document = {};
                for (const key of ["kind", "target", "payload", "why", "source_refs", "target_fingerprint", "work_item_id", "affected_people", "artifact_id", "artifact_revision", "dependencies"]) {
                    if (Object.hasOwn(item, key)) document[key] = item[key];
                }
                document.payload = input.payload;
            }
            return invoke(operation, input, document);
        },
    };
}
