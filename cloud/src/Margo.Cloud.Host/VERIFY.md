# Margo.Cloud.Host: first live verification (Phase 0 spike)

A step-by-step checklist for the first run of the C# host in a **test tenant**. It cannot be run from the repository:
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

## 5. Colleagues, group chats and mail (no outward action)

1. From a second test account, message Margo one-to-one: `Can you send me the board deck?` Expect
   `turn status=queued` and, in `acknowledge` mode, one acknowledgement in that conversation only; nothing sent anywhere
   else.
2. Add Margo to a group chat with the manager and send a message there. Expect `turn status=group_refused` and the
   refusal text in that chat; no `workiq` receipt other than the manager lookup.
3. Send an email to Margo's mailbox from the second account. Expect, for the activity Agent 365 delivers:
   `turn status=queued` (mail arriving on the message route) or `notification status=queued` (an email notification
   event), and **no reply email and no Teams message**. Then, as the manager, ask `Any requests waiting for me?`: the
   mail is listed as an untrusted request.
4. Re-send the same email notification if the platform redelivers it: still one request (stable Outlook IDs).

## 6. The manager's on-behalf-of token (`MARGO_PRINCIPAL_ACCESS=obo`)

With `PRINCIPAL_AUTH_HANDLER` set to the configured on-behalf-of handler, as the manager ask
`Summarise my unread mail.` Record whether the turn's receipts show `"read_as": "principal"` with `"status": "ok"`, or
`token exchange failed for principal` / the principal reads reported unavailable. PORTING.md, open question 1, predicts
the latter (the route signs in only the agentic handler, as Python did); the result decides the fix.

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
   tenant, the blueprint client ID and `api://<client id>/Voice.Talk`.
3. Sign in as the manager and start a call. Expect `margo.voice` lines for the session (no audio, transcript or token
   content), Margo's Work IQ token obtained through the SDK connection (`GetAgenticUserTokenAsync`), and, under `obo`,
   the on-behalf-of exchange with the person's voice token. Ask `What is my next meeting?`: an answer by voice.
4. Sign in as the second account: refused (`Your sign-in can't be used to talk to this Margo.`).

## 9. Telemetry

In Application Insights: requests for `/api/messages` (or `/activity/messages`) with 202, traces with the
`margo.autopilot` lines above, dependencies to Work IQ, Blob Storage and Azure OpenAI. Confirm that:

- no trace, request or dependency holds message text, model input/output (GenAI content capture is off) or a token;
- the voice page's request URL does **not** keep the one-time `?code=` value (PORTING.md, open question 2).

## 10. Failure behaviour (optional, test tenant only)

1. Temporarily deny the instance identity's Blob role. Send a message: expect `turn status=state_unsafe` and the
   "couldn't load my records safely" notice to the manager; nothing answered from partial state. Restore the role.
2. Set `MARGO_MODEL_DEPLOYMENT` to a deployment name that does not exist on the account: expect
   `turn failed` with the exception type and `turn status=error`, and the "Something failed on my side" notice.
