# Work IQ from a custom agent: the smallest client

`Program.cs` is a complete Work IQ client in about 80 lines, using only the official
[MCP C# SDK](https://www.nuget.org/packages/ModelContextProtocol.Core) and `Azure.Identity`:

1. Get a delegated token for Work IQ (`api://workiq.svc.cloud.microsoft/.default`) as a signed-in user.
2. Load Work IQ as an MCP server (`https://workiq.svc.cloud.microsoft/mcp`, streamable HTTP).
3. List its tools: the server's own names, descriptions and argument schemas, which an agent gives its model.
4. `fetch` today's meetings with `$select` and `$top`.
5. `retrieve` with a semantic query, which returns cited hits.

Margo Cloud does the same in [`WorkIQMcpTransport.cs`](../../src/Margo.Cloud.WorkIQ/WorkIQMcpTransport.cs), adding what an
agent acting for a person needs: one session per identity, writes on their own session and never resent, bounds, and
[the guard](../../src/Margo.Cloud.Guard/WorkIQGuard.cs) between the model and every call.

## Run it

You need a Microsoft 365 tenant with Work IQ, and an app registration (public client, "Allow public client flows" on)
with Work IQ's delegated permission (`WorkIQAgent.Ask`, plus `WorkIQ.Read` for the newer path families), consented
for the user.

```sh
export WORKIQ_CLIENT_ID="<application (client) ID of your app registration>"
export WORKIQ_TENANT_ID="<your directory (tenant) ID>"
cd cloud/samples/WorkIQHello
dotnet run
```

Sign in with the device code it prints. It then lists the tools and prints today's meetings and a few search hits.

It also works as a first check of a tenant before deploying Margo Cloud: if this program cannot list tools or read
your calendar, Margo cannot either. A `401` means the token's audience or consent is wrong; `Access denied for path`
means the tenant turned that path family off; see [Work IQ errors](../../../docs/work-iq/errors.md).
