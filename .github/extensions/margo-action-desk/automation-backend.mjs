import { dirname, join } from "node:path";
import { BackendError, executeWithInput, resolveCore } from "./backend.mjs";

export function createAutomationBackend({ resolveScript = resolveCore, execute = executeWithInput } = {}) {
    return {
        async run(operation, input = {}) {
            if (!["list", "show", "preview", "commit"].includes(operation) || !input || typeof input !== "object" || Array.isArray(input)
                || Buffer.byteLength(JSON.stringify(input)) > 96 * 1024) throw new BackendError("invalid_input", "Unsupported or oversized automation request.", 400);
            const script = join(dirname(await resolveScript()), "automation_definitions.py");
            let response;
            try {
                response = await execute(process.platform === "win32" ? "python" : "python3", ["-B", script, "tool"], {
                    shell: false, windowsHide: true, cwd: dirname(script), timeout: 15000, maxBuffer: 4 * 1024 * 1024,
                    encoding: "utf8", input: JSON.stringify({ operation, input }),
                });
            } catch (error) {
                let result;
                try { result = JSON.parse(error.stderr); } catch { /* Do not expose raw process output. */ }
                throw new BackendError(result?.code || "automation_unavailable", result?.error
                    || "Automation management failed. Inspect the exact workspace files before retrying an uncertain save.", result?.code === "revision_conflict" ? 409 : 503);
            }
            let result;
            try { result = JSON.parse(response.stdout); } catch { throw new BackendError("invalid_backend_response", "Invalid automation response.", 502); }
            if (!result || typeof result !== "object" || result.error) throw new BackendError("invalid_backend_response", "Incompatible automation response.", 502);
            return result;
        },
    };
}

export function createAutomationTool(getSession, backend) {
    return {
        name: "margo_automation_definitions",
        description: "Interactive-only managed Markdown scenario list/show/preview/commit in the configured workspace. Use preview and present exact changes for explicit user confirmation before commit. No native workflow creation, execution or permission approval; new/edited definitions remain disabled and review-required.",
        parameters: { type: "object", properties: { operation: { type: "string", enum: ["list", "show", "preview", "commit"] },
            input: { type: "object", description: "Exact canonical manager envelope from the automation-authoring procedure; no paths, account, shell or SQL." } },
            required: ["operation", "input"], additionalProperties: false },
        handler: async args => {
            try {
                const session = getSession();
                const [agent, mode] = await Promise.all([session.rpc.agent.getCurrent(), session.rpc.mode.get()]);
                if (agent.agent?.name === "margo-proactive" || mode !== "interactive") throw new Error("Automation authoring requires a foreground interactive session, never the restricted scheduled profile.");
                if (!args || Object.keys(args).some(key => !["operation", "input"].includes(key))) throw new Error("Unexpected automation tool input.");
                return { textResultForLlm: JSON.stringify(await backend.run(args.operation, args.input)), resultType: "success" };
            } catch (error) { return { textResultForLlm: error.message, resultType: "failure" }; }
        },
    };
}
