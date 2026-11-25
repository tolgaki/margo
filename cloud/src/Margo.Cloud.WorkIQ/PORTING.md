# Margo.Cloud.WorkIQ

Work IQ loaded as an MCP server, through the official MCP C# SDK (`ModelContextProtocol.Core`). This project was a
port of the Python runtime's `transport.py`; on Nov 25, 2026 the port was replaced, deliberately, by a client built on
the SDK, so the code a developer reads here is how a custom agent uses Work IQ, not a record of Python's behavior. The
frozen Python transport cases and their tests were removed with it. The state commands keep their parity; this
project has none.

## What it is

| Type | Role |
| --- | --- |
| `IWorkIQTransport` | What the guard needs: `Endpoint`, `CallAsync(tool, arguments, bearer)`, `ListToolsAsync(bearer)` |
| `WorkIQMcpTransport` | The SDK-based client |
| `WorkIQTool` | One tool from `tools/list`: name, description, input schema, `readOnlyHint` |
| `McpTransportOptions` | Bounds: response size, sessions, idle time, call timeout, protocol revision, pooled tools |
| `TransportException`, `TransportTimeoutException`, `TransportResponseTooLargeException` | The outcomes the guard maps to `error`, `unknown` and a "ask for less" error |
| `BoundedHandler` | Stops reading a response after `MaxResponseBytes` |

## Behavior

- **One MCP session per bearer token.** Work IQ answers as the token's subject, so each token has its own `McpClient`
  (at most `MaxSessions`, closed after `SessionIdleTimeout`; the key is the token's SHA-256). Closed sessions are
  ended with `DELETE`, as the SDK does on dispose.
- **Reads** (`PooledTools`, the guard's read tools) share their session's pooled connections. A read answered 404 on
  an established session (the server forgot it, so it did not process the read) is sent once more on a new session.
- **Writes** (any other tool) get a session and an HTTP handler of their own, closed after the call, and are never
  resent by this class or the HTTP stack. The guard records an unclear outcome as unknown.
- **Protocol revision pinned** to `2025-06-18` (`McpTransportOptions.ProtocolVersion`). The SDK's default probes for
  the 2026-07-28 revision first and waits up to five seconds for a server that does not answer the probe, on every new
  session; writes open one each.
- **Bounded**: `CallTimeout` (60 s, session set-up included) becomes `TransportTimeoutException`; an answer beyond
  `MaxResponseBytes` (8 MiB) becomes `TransportResponseTooLargeException`; redirects are never followed.
- **Results**: `structuredContent` when it is an object, otherwise the text items joined and parsed as JSON (an object
  as is, any other value as `{"value": ...}`, text that is not JSON as `{"text": ...}`). `resource_link` items are kept
  as `resource_links` (`uri`, `name`, `mimeType`). `isError` becomes `TransportException("Work IQ tool error")` with
  the payload's `statusCode`.
- **Errors**: HTTP errors carry their status (`TransportException.StatusCode`). A JSON-RPC error becomes a
  `TransportException` whose message names only the code; the server's text is in `ServerMessage`, which the guard
  passes to the model and never to a receipt or log (it may echo the request).
- **`tools/list`**: every page (at most ten), kept with the session. Any failure returns an empty list, and callers
  fall back to their own definitions.

## Tests

`WorkIQMcpTransportTests` against `ScriptedMcpServer`, a minimal streamable-HTTP MCP server behind an
`HttpMessageHandler`: sessions per token, writes on their own session and never resent, the forgotten-session
resend, tool and HTTP and JSON-RPC errors, timeouts versus the caller's cancellation, the response bound, resource
links, text results, `tools/list` paging and caching, session eviction, https only.
