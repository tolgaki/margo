# Margo Cloud: live verification checklist

A step-by-step checklist for checking a Margo Cloud deployment in a **test tenant**. It cannot be run from the repository:
it needs the tenant's agent blueprint, a hired Margo instance, a manager account and Azure resources. Use only test
accounts and invented content; do not paste real messages, tokens or tenant identifiers into notes, issues or this file.

Record for each step: passed, failed (with the log line), or not attempted (and why). A missing log line is a failure,
not a pass.

## 0. Before you start

1. Build the image from the repository root: `sh cloud/scripts/build-image.sh` (Azure Container Registry), or locally
   `docker build --file cloud/Dockerfile --tag margo-cloud:dev .`. The build publishes the host, router and provisioner
   in Release and refuses the image if any contract-probe assembly reached an output. Inside the image, confirm
   `/app/host` holds `Margo.Cloud.Host.dll`, `margo-state.dll` and `margo-state.runtimeconfig.json`, and **no**
   `Margo.Cloud.State.Probes.dll`.
2. Image: `cloud/Dockerfile` (ASP.NET Core 10 runtime on Alpine, `agents/` and `skills/` under `/app`, non-root user).
   `ls /usr/share/zoneinfo/America/Los_Angeles` inside the container must succeed (the `tzdata` package). The default
   command is `dotnet /app/host/Margo.Cloud.Host.dll`. Keep the Dockerfile's `ENV` lines unchanged.
3. Deploy one instance with `cloud/infra/instance.bicep` (through the provisioner or by hand), and the router and
   provisioner with `cloud/infra/main.bicep` (`azd provision` from `cloud/`).
4. Turn on `LOG_LEVEL=INFO` (the default). Have the container's log stream and Application Insights open.

## 1. Start-up

Expect, in order, and nothing at Error:

- no `... is required` line and exit code 0 (otherwise the message names the missing variable);
- `margo.autopilot: Application Insights configured (message content capture is off).`;
- `Microsoft.Hosting.Lifetime: Now listening on: http://0.0.0.0:8088` (or the `HOST`/`PORT` set).

Then: the Container Apps liveness and readiness probes stay healthy (`GET /liveness`, `/readiness` answer
`{"status": "ok"}`). From inside the environment, `curl -i -X POST http://<app>:8088/api/messages` without a token
answers `401 {"error": "Authorization header not found"}`.

## 2. The token check behind the router

Send any Teams message to the hired Margo (step 3). Expect no `margo.autopilot.auth` warning for it. A warning such as
`JWT audience not accepted.` or `JWT tenant binding mismatch.` means the blueprint client ID or tenant in the
environment does not match the platform's token: fix the variables, do not loosen the check.
`JWT issuer not accepted: <issuer>` (in the router's or the instance's logs) means the platform signs with an issuer
other than Bot Framework's or your tenant's Entra issuers. Record the issuer. Admit it with `MARGO_EXTRA_TOKEN_ISSUERS`
only if it is Microsoft's and not another tenant's, and report it so the default list can include it.

Record the platform's application: with `MARGO_PLATFORM_APP_IDS` empty the first message is refused and the router
logs `Platform token refused from application <id>` once. Confirm the ID is Agent 365's, set
`MARGO_PLATFORM_APP_IDS` to it, provision, redeploy all, and send another message: it must be answered. A
refusal logged as `JWT is a person's token (scp)` or `JWT caller application not accepted` means Agent 365 delivers
with a kind of token this check does not expect; record the log line.

The instance answers 400 and logs `ServiceUrl host is not in the configured allowed hosts` if Agent 365's reply
address is not one of the SDK's Microsoft hosts. Record the host; admit it with `MARGO_EXTRA_SERVICE_HOSTS` only if it
is Microsoft's.

The model request: the first answered turn proves the deployment accepts `store: false`, the replayed conversation
and the reasoning settings. Ask a second question that needs the first answer, and check the `turn usage` line shows
`cached=` above zero on the second turn. If every turn fails with a 4xx from the model, set
`MARGO_MODEL_REASONING_EFFORT none` and try again; record which one worked.

Then, with the instance scaled to zero (`az containerapp update -n margo-<key> -g <group> --min-replicas 0`, and wait
until it has no replicas), send another message. Expect an answer, even if late, and possibly the router's
`activity delivered after the caller stopped waiting` warning. Record whether Margo answered once or twice (a repeat
delivery to the same container is dropped as `duplicate`).

Stop a turn mid-way: ask something that needs several reads, and while the typing indicator shows, run
`az containerapp revision restart`. Expect the answer to arrive (a turn in flight gets up to nine minutes) and the
next message to see the state that turn saved.

## 3. Agentic sign-in and Margo's Work IQ token (manager, one-to-one chat)

As the manager, in the one-to-one Teams chat with Margo, send: `What is on my calendar today?`

Expect:

- a typing indicator in Teams within a few seconds;
- `margo.autopilot: workiq {... "paths": ["/me/manager"], "read_as": "margo", "status": "ok" ...}` (Margo's own token,
  from the agentic handler, accepted by Work IQ; the manager lookup ran);
- `workiq {... "tool": "margo-state", "command": "init", "status": "ok" ...}` for `memory_state.py` and `task_state.py`
  on the first turn;
- further `workiq` receipts for the reads the model asked for;
- `margo.autopilot: turn status=answered`;
- a reply in Teams, rendered as markdown.

If instead you see `token exchange failed for margo` or `turn status=no_agent_token`: the agentic user token could not
be obtained (blueprint federated credential, `FEDERATEDCLIENTID`, the instance's agent identity, or the handler's
scopes). Note which.

No log line may contain the message text, the reply, or a token (search the stream for a distinctive word from your
message: zero hits).

## 4. State across turns and revisions

1. Send `/replies acknowledge`. Expect `turn status=setting_changed` and the confirmation.
2. Restart the revision (or scale to zero and back). Send `What did I just change?` Expect `turn status=answered`,
   and in the state storage account the instance container (`MARGO_STATE_CONTAINER`) holding a `.../state.zip` whose
   last-modified time moves with each turn and a `daily/<today>.zip`.
3. Two quick messages in a row: both answered, one after the other (the second waits for the lease; no
   `instance state is busy or unavailable` unless a turn takes over two minutes).

## 5. Colleagues, group chats and mail

1. From a second test account, message Margo one-to-one: `Can you send me the board deck?` Expect
   `turn status=queued` and, in `acknowledge` mode, one acknowledgment in that conversation only; nothing sent anywhere
   else.
2. Add Margo to a group chat with the manager and, as the manager, @mention her there: `@Margo who is in this chat?`
   Expect `turn status=answered` and the answer in that chat. From the second account, @mention her in the same chat:
   expect `turn status=queued` and no answer.
3. Send an email to Margo's mailbox from the second account. Expect, for the activity Agent 365 delivers:
   `turn status=queued` (mail arriving on the message route) or `notification status=queued` (an email notification
   event), and **no reply email and no Teams message**. Then, as the manager, ask `Any requests waiting for me?`: the
   mail is listed as an untrusted request.
4. Re-send the same email notification if the platform redelivers it: still one request (stable Outlook IDs).

## 6. The manager's on-behalf-of token (`MARGO_PRINCIPAL_ACCESS=obo`)

With `PRINCIPAL_AUTH_HANDLER` set to the configured on-behalf-of handler, as the manager ask
`Summarize my unread mail.` Record whether the turn's receipts show `"read_as": "principal"` with `"status": "ok"`, or
`token exchange failed for principal` / the principal reads reported unavailable. PORTING.md, open question 1, predicts
the latter (the route signs in only the agentic handler); the result decides the fix.

With `MARGO_PRINCIPAL_ACCESS=delegation`, no principal token is requested: no `token exchange failed for principal`
line, and Teams/files reads report unavailable.

## 7. Notifications and installation

1. Install or update the app for the manager in Teams. Expect `installation update: add` (or the action sent) and
   nothing else.
2. Lifecycle events go to the router, not here. If one reaches the host (single-process setup only), expect
   `lifecycle notification: <event>` and no turn.

## 8. Voice (when `VOICE_LIVE_MODEL` is set)

1. As the manager, send `/voice`. Expect `turn status=voice_link` and a link
   `https://<public base>/i/<instance key>/voice/`.
2. Open it. The page loads without a platform token (`/voice/` is outside the check); `/voice/config` answers the
   the tenant, the voice app's client ID (`MARGO_VOICE_CLIENT_ID`) and `api://<voice app id>/Voice.Talk`, the voice
   app's own scope, never the blueprint's.
3. Sign in as the manager and start a call. Expect `margo.voice` lines for the session (no audio, transcript or token
   content) and Margo's Work IQ token obtained through the SDK connection (`GetAgenticUserTokenAsync`); no
   on-behalf-of exchange. Ask `What is my next meeting?`: an answer by voice.
4. Sign in as the second account: refused (`Your sign-in can't be used to talk to this Margo.`), with no token
   exchanged for it.
5. Take a voice sign-in token (from the page's network trace) and POST it as the bearer of an activity to the
   router's `/api/messages`: expect 401 and `JWT is a person's token (scp)` or an audience refusal. A voice sign-in
   must never be accepted as a platform token.

## 9. Telemetry

In Application Insights: requests for `/api/messages` (or `/activity/messages`) with 202, traces with the
`margo.autopilot` lines above, dependencies to Work IQ, Blob Storage and Azure OpenAI. Confirm that:

- no trace, request or dependency holds message text, model input/output (GenAI content capture is off) or a token;
- `workiq` receipts show path templates such as `/users/{user}/messages/{id}`, never an address, a file name or search
  text;
- there are no request traces for `/liveness` or `/readiness`;
- the voice page's request URL does **not** keep the one-time `?code=` value (PORTING.md, open question 2).

## 10. Failure behavior (optional, test tenant only)

1. Temporarily deny the instance identity's Blob role. Send a message: expect `turn status=state_unsafe` and the
   "couldn't load my records safely" notice to the manager; nothing answered from partial state. Restore the role.
2. Set `MARGO_MODEL_DEPLOYMENT` to a deployment name that does not exist on the account: expect
   `turn failed` with the exception type and `turn status=error`, and the "Something failed on my side" notice.
