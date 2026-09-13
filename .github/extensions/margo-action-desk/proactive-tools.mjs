import { dirname, join } from "node:path";
import { randomUUID } from "node:crypto";
import { executeWithInput, resolveCore, BackendError } from "./backend.mjs";

export const SCHEDULED_AGENT = "margo-proactive";
export const AGENT_TOOLS = Object.freeze(["margo_proactive_context", "margo_proactive_prepare",
    "workiq/fetch", "workiq/get_schema", "workiq/search_paths"]);
export const RUNTIME_TOOLS = Object.freeze(["margo_proactive_context", "margo_proactive_prepare",
    "workiq-fetch", "workiq-get_schema", "workiq-search_paths"]);
const routines = ["morning", "sweep", "eod"];
const string = maxLength => ({ type: "string", minLength: 1, maxLength });
const object = (properties, required = Object.keys(properties)) => ({ type: "object", properties, required, additionalProperties: false });
const observation = object({
    id: string(300), revision: string(200), title: string(200), summary: string(800), web_link: string(2000),
    sensitivity: { enum: ["normal", "private", "restricted", "unknown"] }, ask: string(500), due_at: string(60),
}, ["id", "revision", "title", "summary", "web_link", "sensitivity"]);
const nullable = maxLength => ({ type: ["string", "null"], maxLength });
export const contextSchema = object({ routine: { enum: routines } });
const prepareVariants = [
    object({ operation: { const: "start" }, routine: { enum: routines },
        identity: object({ principal: string(512), observed_at: string(60), evidence_ref: string(300) }) }),
    object({ operation: { const: "finish" },
        sources: { type: "array", minItems: 4, maxItems: 4, items: object({
            source: { enum: ["mail", "calendar", "direct_messages", "mentions"] },
            status: { enum: ["complete", "partial", "blocked", "failed"] }, kind: { enum: ["enumeration", "search"] },
            error_class: { enum: ["access_denied", "expired_token", "capability_unsupported", "throttled", "timeout", "network",
                "unavailable", "invalid_response", "authentication_required", "binding_unavailable", "unknown"] },
            evidence_ref: string(300), observations: { type: "array", maxItems: 25, items: observation },
        }, ["source", "status", "kind", "evidence_ref", "observations"]) },
        summary: string(4000),
        candidates: { type: "array", maxItems: 5, items: object({
            source: { enum: ["mail", "calendar", "direct_messages", "mentions"] }, source_id: string(300), claim_key: string(500),
            title: string(500), owner: nullable(200), direction: { enum: ["owe", "waiting_on", "own"] },
            due: nullable(100), next_step: string(500),
        }) },
        artifact: object({ title: string(200), markdown: string(4000), source_keys: { type: "array", minItems: 1, maxItems: 5, items: string(400) },
            work_item_id: nullable(200) }),
    }, ["operation", "sources", "summary", "candidates"]),
];
export const prepareSchema = object({
    ...prepareVariants[0].properties, ...prepareVariants[1].properties,
    operation: { enum: ["start", "finish"] },
}, ["operation"]);

function validate(schema, value) {
    if (schema.oneOf) {
        if (schema.oneOf.some(candidate => { try { validate(candidate, value); return true; } catch { return false; } })) return;
        throw new BackendError("invalid_input", "Unsupported restricted preparation payload.", 400);
    }
    if (schema.const !== undefined && schema.const !== value || schema.enum && !schema.enum.includes(value)) {
        throw new BackendError("invalid_input", "Unsupported restricted input value.", 400);
    }
    if (value === null && Array.isArray(schema.type) && schema.type.includes("null")) return;
    if (schema.type === "object") {
        if (!value || typeof value !== "object" || Array.isArray(value)
            || Object.keys(value).some(key => !Object.hasOwn(schema.properties, key))
            || schema.required.some(key => !Object.hasOwn(value, key))) throw new BackendError("invalid_input", "Unknown or missing input fields.", 400);
        for (const [key, entry] of Object.entries(value)) validate(schema.properties[key], entry);
    } else if (schema.type === "array") {
        if (!Array.isArray(value) || value.length < (schema.minItems || 0) || value.length > schema.maxItems) throw new BackendError("invalid_input", "Array exceeds restricted limits.", 400);
        value.forEach(entry => validate(schema.items, entry));
    } else if (schema.type === "string" || Array.isArray(schema.type)) {
        if (typeof value !== "string" || value.length < (schema.minLength || 0) || value.length > schema.maxLength || value.includes("\0")) {
            throw new BackendError("invalid_input", "Text exceeds restricted limits.", 400);
        }
    }
}

export function validatePreparation(input) {
    if (Buffer.byteLength(JSON.stringify(input)) > 96 * 1024) throw new BackendError("invalid_input", "Input exceeds 96 KiB.", 400);
    validate({ oneOf: prepareVariants }, input);
}

export function createProactiveBackend({ resolveScript = resolveCore, execute = executeWithInput } = {}) {
    return {
        async run(operation, input, host, claim) {
            if (!["context", "start", "charge", "finish"].includes(operation)) throw new BackendError("invalid_input", "Restricted operation unavailable.", 400);
            const script = join(dirname(await resolveScript()), "app_proactive.py");
            let result;
            try {
                const output = await execute(process.platform === "win32" ? "python" : "python3", ["-B", script, "tool"], {
                    shell: false, windowsHide: true, cwd: dirname(script), timeout: 15000, maxBuffer: 256 * 1024, encoding: "utf8",
                    input: JSON.stringify({ operation, input, host, ...(claim ? { claim } : {}) }),
                });
                result = JSON.parse(output.stdout);
            } catch (error) {
                let code = "preparation_unavailable", message = "Restricted local preparation failed; inspect configured policy/state. Do not retry an uncertain finish blindly.";
                try {
                    const value = JSON.parse(error.stderr);
                    if (typeof value.error === "string") { code = value.code || code; message = value.error; }
                } catch { /* Raw stderr is not exposed. */ }
                throw new BackendError(code, message, 503);
            }
            if (!result || typeof result !== "object" || Array.isArray(result) || result.error) throw new BackendError("invalid_backend_response", "Invalid restricted preparation result.", 502);
            return result;
        },
    };
}

export function allowedRead(name, args, context, identityOnly = false) {
    if (!args || typeof args !== "object" || Array.isArray(args) || args.agentId != null) return false;
    if (name === "workiq-search_paths") return !identityOnly && Object.keys(args).every(key => key === "query")
        && ["my mail", "my calendar", "my Teams direct messages", "messages mentioning me"].includes(args.query);
    if (name === "workiq-get_schema") return !identityOnly
        && Object.keys(args).every(key => ["path", "operationType", "format", "agentId"].includes(key))
        && args.operationType === "fetch" && ["cddl", "jsonschema", "typescript", null, undefined].includes(args.format)
        && typeof args.path === "string" && /^\/me\/(?:messages|calendarView|chats)(?:\/[^/?]+(?:\/messages)?)?$/.test(args.path);
    if (name !== "workiq-fetch" || Object.keys(args).some(key => !["entityUrls", "agentId"].includes(key))
        || !Array.isArray(args.entityUrls) || args.entityUrls.length !== 1) return false;
    const raw = args.entityUrls[0];
    if (typeof raw !== "string" || raw.length > 5000 || !raw.startsWith("/me") || /%2f|%5c|\\|\.\./i.test(raw)) return false;
    const url = new URL(raw, "https://example.invalid");
    if (url.origin !== "https://example.invalid") return false;
    const select = url.searchParams.get("$select");
    if (!select || select.length > 400 || url.searchParams.size !== new Set(url.searchParams.keys()).size) return false;
    if ([...url.searchParams.keys()].some(key => !["$select", "$top", "$filter", "$orderby", "$skiptoken", "startDateTime", "endDateTime"].includes(key))) return false;
    if (url.pathname === "/me") return [...url.searchParams.keys()].every(key => key === "$select")
        && select.split(",").sort().join(",") === "id,userPrincipalName";
    if (identityOnly) return false;
    if (!/^\/me\/(?:messages(?:\/[^/]+)?|calendarView|chats(?:\/[^/]+\/messages)?)$/.test(url.pathname)) return false;
    const top = Number(url.searchParams.get("$top"));
    if (!/\/messages\/[^/]+$/.test(url.pathname) && (!Number.isInteger(top) || top < 1 || top > 25)) return false;
    if (url.pathname === "/me/messages") {
        const filter = url.searchParams.get("$filter") || "";
        if (filter !== `receivedDateTime ge ${context.window.start} and receivedDateTime lt ${context.window.end}`) return false;
    }
    if (url.pathname === "/me/calendarView" && (url.searchParams.get("startDateTime") !== context.calendar_window.start
        || url.searchParams.get("endDateTime") !== context.calendar_window.end)) return false;
    return true;
}

export function createProactiveTools({ getSession, backend = createProactiveBackend() }) {
    let state = null;
    let serial = Promise.resolve();
    const exclusive = operation => {
        const next = serial.then(operation);
        serial = next.catch(() => {});
        return next;
    };
    async function current() { return (await getSession().rpc.agent.getCurrent()).agent; }
    async function requireAgent() {
        const agent = await current();
        if (agent?.name !== SCHEDULED_AGENT || !Array.isArray(agent.tools)
            || agent.tools.length !== AGENT_TOOLS.length || AGENT_TOOLS.some(name => !agent.tools.includes(name))) {
            throw new BackendError("restricted_profile_required", "Select the exact reviewed margo-proactive profile; no fallback to ordinary Margo.", 403);
        }
    }
    const host = () => `sdk:${getSession().sessionId}`;
    async function result(operation) {
        try { await requireAgent(); return { textResultForLlm: JSON.stringify(await operation()), resultType: "success" }; }
        catch (error) { return { textResultForLlm: error.message, resultType: "failure" }; }
    }
    const tools = [
        {
            name: "margo_proactive_context", description: "Read only configured app-proactivity policy, due slot, bounded preferences and local commitments. No files chosen by caller; never initializes or reads M365.",
            parameters: contextSchema,
            handler: input => exclusive(() => result(async () => {
                validate(contextSchema, input);
                if (state) throw new BackendError("already_started", "This session already has a restricted context/run. Do not switch routines or restart it.", 409);
                const metadata = await getSession().rpc.tools.getCurrentMetadata();
                if (!Array.isArray(metadata.tools) || RUNTIME_TOOLS.some(name => !metadata.tools.some(tool => tool.name === name))
                    || metadata.tools.some(tool => !RUNTIME_TOOLS.includes(tool.name) && tool.name !== "sql")) {
                    throw new BackendError("tool_grant_unverified", "Unexpected effective tools. Keep automation disabled until the host grant is reviewed.", 403);
                }
                const context = await backend.run("context", input, host());
                state = { routine: input.routine, context, identityRead: false, readCalls: 0 };
                return context;
            })),
        },
        {
            name: "margo_proactive_prepare", description: "Only start a due local run after reported provider identity matches, or finish scoped observations/candidate asks/private artifact/local receipt. No approval, execution, settings, memory capture, export or arbitrary command.",
            parameters: prepareSchema,
            handler: input => exclusive(() => result(async () => {
                validatePreparation(input);
                if (!state || state.context.status !== "due") throw new BackendError("no_due_context", "No due restricted context. Stop; do not request shell or another agent.", 403);
                if (input.operation === "start") {
                    if (state.claim || input.routine !== state.routine || !state.identityRead) throw new BackendError("invalid_start", "Read /me once and start only this due routine.", 409);
                    const value = await backend.run("start", { routine: input.routine, identity: input.identity }, host());
                    if (value.claim) { state.claim = value.claim; state.context = value.timing; state.started = true; }
                    else state.context.status = "skipped";
                    const { claim: _claim, ...safe } = value;
                    return safe;
                }
                if (!state.claim) throw new BackendError("missing_claim", "No active local claim; no preparation can be persisted.", 403);
                const { operation: _operation, ...payload } = input;
                const response = await backend.run("finish", payload, host(), state.claim);
                state.finished = true;
                return response;
            })),
        },
    ];
    return {
        tools,
        hooks: {
            onPreToolUse: async input => {
                let agent;
                try { agent = await current(); } catch {
                    return { permissionDecision: "deny", permissionDecisionReason: "Cannot verify selected session agent. Tool use is withheld until agent metadata is available." };
                }
                if (agent?.name !== SCHEDULED_AGENT) return;
                if (!RUNTIME_TOOLS.includes(input.toolName)) return { permissionDecision: "deny", permissionDecisionReason: "This tool is outside the restricted scheduled profile, including any runtime-added SQL/delegation tool." };
                if (input.toolName.startsWith("margo_proactive_")) return;
                return exclusive(async () => {
                    try {
                        await requireAgent();
                        if (!state || state.context.status !== "due" || state.finished) throw new Error("No due active scheduled context.");
                        if (!allowedRead(input.toolName, input.toolArgs, state.context, !state.claim)) throw new Error("Read falls outside scoped mail/calendar/chat metadata paths or bounded parameters.");
                        if (!state.claim) {
                            if (state.identityRead) throw new Error("Identity read already attempted. Reauthentication is a foreground operation; stop, do not retry.");
                            state.identityRead = true;
                        } else {
                            if (++state.readCalls > 12) throw new Error("Provider read budget exhausted.");
                            const reservation = await backend.run("charge", { event_id: "read:" + randomUUID() }, host(), state.claim);
                            if (!reservation.execute) throw new Error("Read charge was already consumed.");
                        }
                    } catch (error) { return { permissionDecision: "deny", permissionDecisionReason: error.message }; }
                });
            },
        },
    };
}
