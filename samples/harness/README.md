# Register Work IQ in an agent harness

This quickstart connects the Work IQ MCP server to GitHub Copilot CLI, signs you in, lists the tools
and makes one bounded read. It is the first step of [getting started](../../docs/getting-started.md)
with desktop Margo, and the harness half of [Learn Work IQ](../../docs/work-iq/README.md).

You need a Microsoft 365 work account with a Microsoft 365 Copilot license, a tenant where Work IQ
is enabled and its application consented (the first sign-in shows a consent page; if you are not
an admin, ask yours to grant it), and [GitHub Copilot CLI](https://docs.github.com/copilot/how-tos/use-copilot-agents/use-copilot-cli).

## 1. Register the server

Microsoft's quickstart,
[Connect GitHub Copilot CLI to the Work IQ MCP server](https://learn.microsoft.com/microsoft-365/copilot/extensibility/work-iq/mcp/quickstart/github-copilot-cli),
installs Work IQ as a Copilot CLI plugin. Follow it; at the time of writing the steps are:

```text
copilot
/login                                   # only if Copilot CLI asks you to sign in
/plugin marketplace add microsoft/work-iq
/plugin install workiq@work-iq
```

Restart Copilot CLI after the install. The plugin registers an MCP server named `workiq` at
`https://workiq.svc.cloud.microsoft/mcp` and adds a `workiq` skill with Microsoft's own guidance,
including troubleshooting. Check Microsoft's page and the [`microsoft/work-iq`](https://github.com/microsoft/work-iq)
repository for the current commands; plugin names and marketplaces have changed during the preview.

**Without the plugin.** Copilot CLI can also register a remote MCP server directly, with `/mcp add`
or in `~/.copilot/mcp-config.json`. [`mcp-config.json`](mcp-config.json) here shows the shape: one
HTTP server, the public endpoint, no secrets. Merge it into your own file rather than replacing it.
The plugin supplies the OAuth client settings for sign-in; a hand-written entry may need the
host's own OAuth prompts, so prefer the plugin and confirm this route against Microsoft's
documentation before relying on it. This route also does not add the `workiq` skill.

**Other MCP hosts.** Any MCP client that supports Streamable HTTP and OAuth can load the same
endpoint. For example, Claude Code:

```sh
claude mcp add --transport http workiq https://workiq.svc.cloud.microsoft/mcp
```

Margo's persona and skills are written for Copilot CLI; another host needs its own agent and skill
loading. For Claude Code, [run Margo in Claude Code](../../docs/how-to/run-in-claude-code.md) covers
installing the skills, loading the persona, the different tool names and why scheduled runs need
the write tools removed.

## 2. Sign in and consent

The first Work IQ call (or the restart after installing) opens a browser for a Microsoft Entra ID
sign-in. Sign in with your work account. If you see an approval-required page, your tenant needs an
administrator to consent to the Work IQ application first; nothing in Margo can bypass that.

## 3. List the tools

```text
/mcp
```

You should see `workiq` with the endpoint above, connected. `/skills` should list `workiq` if you
installed the plugin. In a session, `list my tools` should include `workiq-fetch`, `workiq-retrieve`,
`workiq-ask`, `workiq-call_function`, `workiq-get_schema` and `workiq-search_paths`. The exact
spelling comes from the registered server name.

## 4. Make one bounded read

Ask the agent to make this exact call, with today's date in your time zone:

```text
Call workiq-fetch with entityUrls ["/me/calendarView?startDateTime=2026-11-25T00:00:00-08:00&endDateTime=2026-11-26T00:00:00-08:00&$select=subject,start,end,organizer,webLink&$top=10&$orderby=start/dateTime"]. Show me the raw result.
```

`calendarView` needs both `startDateTime` and `endDateTime`; `$select` and `$top` keep the payload
small. The result is JSON in this shape (fictional values):

```json
{
  "statusCode": 200,
  "results": [
    {
      "value": [
        {
          "subject": "Design review",
          "start": {"dateTime": "2026-11-25T17:00:00.0000000", "timeZone": "UTC"},
          "end": {"dateTime": "2026-11-25T17:30:00.0000000", "timeZone": "UTC"},
          "organizer": {"emailAddress": {"name": "Dana Park", "address": "dana@example.com"}},
          "webLink": "https://outlook.office365.com/owa/?itemid=..."
        }
      ]
    }
  ]
}
```

The envelope may differ by host and service version; what matters is a success status and a
`value` list. An empty list is a valid empty calendar. An error is not: see
[Work IQ errors and recovery](../../docs/work-iq/errors.md).

## Next

- Desktop Margo: continue with [getting started](../../docs/getting-started.md#3-teach-her-who-you-are).
- Your own agent: read [identity and consent](../../docs/work-iq/identity-and-consent.md) and the
  custom-agent sample, [`cloud/samples/WorkIQHello`](../../cloud/samples/WorkIQHello/).
