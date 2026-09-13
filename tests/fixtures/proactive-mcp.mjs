// Synthetic MCP metadata/read fixture. No network, files or actual provider calls.
import { createInterface } from "node:readline";
const tools = ["fetch", "get_schema", "search_paths", "do_action", "create_entity", "update_entity", "delete_entity", "call_function", "ask", "retrieve"];
const input = createInterface({ input: process.stdin });
input.on("line", line => {
    const value = JSON.parse(line);
    if (value.id === undefined) return;
    let result;
    if (value.method === "initialize") result = { protocolVersion: "2024-11-05", capabilities: { tools: {} }, serverInfo: { name: "synthetic-workiq", version: "1" } };
    else if (value.method === "tools/list") result = { tools: tools.map(name => ({ name, description: "Synthetic metadata only",
        inputSchema: { type: "object", additionalProperties: true } })) };
    else if (value.method === "tools/call") result = { content: [{ type: "text", text: JSON.stringify({ userPrincipalName: "synthetic@example.com" }) }] };
    else result = {};
    process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id: value.id, result }) + "\n");
});
