# Talk to Margo: voice page (browser client)

Static files for the "talk to Margo by voice" page. No build step, no external
scripts, no CDNs. The server (separate code) serves this directory, a `config`
JSON document and a `ws` WebSocket, all relative to the page URL.

| File | What it is |
| --- | --- |
| `index.html` | The page. No inline script or style, so a strict CSP (`script-src 'self'; style-src 'self'`) works. The AI disclosure is static text, visible before and during the call. |
| `voice.css` | Layout down to 320 px, light and dark through CSS custom properties and `prefers-color-scheme`, visible focus rings. |
| `voice.js` | Page logic (ES module): sign-in, microphone, playback, WebSocket protocol, captions. |
| `voice-core.mjs` | Pure helpers with no DOM or audio APIs: base64, PCM16 conversion, framing, PKCE, Entra URLs, redirect parsing, protocol messages. |
| `pcm-worklets.js` | AudioWorklet processors `pcm-capture` (mic to 480-sample Float32 chunks, 20 ms at 24 kHz) and `pcm-player` (queued playback; `{type:"clear"}` drops the queue for barge-in; silence when empty). |
| `*.test.mjs` | `node --test` tests for the helpers and for both worklet processors. |

## Serving contract

- Serve the page at a URL that ends in `/` (for example `/i/abc123/voice/`).
  `config`, `ws` and `pcm-worklets.js` are resolved relative to it, so
  `/i/abc123/voice` without the slash would look one level up.
- `GET config` returns `{"tenantId": "...", "clientId": "...", "scope": "..."}`.
- Register the page URL, without query or fragment, as a **single-page application**
  redirect URI on the Entra app. It must match exactly (`.../voice/` and
  `.../voice/index.html` are different URIs).
- Page and socket need a secure context (HTTPS, or `localhost`) for the
  microphone and Web Crypto.

## Sign-in

OAuth 2.0 authorization code with PKCE against Entra v2 directly (no MSAL).
The PKCE verifier and `state` sit in `sessionStorage` only between pressing
Sign in and returning, and are deleted on the next page load whether or not
they were used. A response whose `state` does not match is rejected without
showing its text. The code is removed from the address bar with
`history.replaceState` before the token request. The access token stays in
memory. It is never stored and never logged, so a reload means signing in again.
No refresh token is requested.

## WebSocket protocol (JSON text frames)

Client to server:

| Message | When |
| --- | --- |
| `{"type":"hello","token":"<access token>"}` | First frame after the socket opens. |
| `{"type":"audio","audio":"<base64>"}` | One per 20 ms captured frame, only after `ready`. PCM16 mono 24 kHz little-endian. Frames are dropped, not queued, while more than 256 KiB is unsent. |
| `{"type":"stop"}` | When the call ends on the client (End, fatal error, page hide). |

Server to client:

| Message | Page behaviour |
| --- | --- |
| `{"type":"ready","disclosure":"...","voice":"..."}` | Starts sending audio; shows the disclosure and voice name under the static disclosure. |
| `{"type":"audio","audio":"<base64>"}` | PCM16 24 kHz, converted and queued on the player. Malformed frames are skipped. |
| `{"type":"interrupt"}` | Posts `{type:"clear"}` to the player at once (barge-in). |
| `{"type":"caption","who":"you"\|"margo","text":"..."}` | Appends a "You:" or "Margo:" line (text only, never HTML; last 200 kept). |
| `{"type":"status","text":"..."}` | Shows the text in the status line; `""` clears it. |
| `{"type":"error","message":"...","fatal":true\|false}` | Shows the message; `fatal: true` ends the call. |
| `{"type":"closed","reason":"..."}` | Ends the call and shows the reason. |

Unknown or malformed frames are ignored. A socket that closes on its own ends
the call with a message that says whether it never connected, closed before
`ready` (for example a rejected token), or dropped mid-call. Ending a call
stops the microphone tracks and closes the AudioContext.

## Tests

```sh
node --test autopilot/src/margo_autopilot/voice/static/*.test.mjs
```

Node 22 treats a bare directory argument (`node --test <dir>/`) as a file to
run, so pass the glob. These tests cover the pure helpers and the worklet
queue logic. They do not exercise a real browser, microphone or Entra tenant.

## Limitations

- The page needs a 24 kHz AudioContext and does not resample. If the browser
  will not run one (`ctx.sampleRate !== 24000`) or cannot connect the
  microphone to it (older Firefox raises `NotSupportedError` when the device
  rate differs), the call does not start and the page says why. Chromium-based
  browsers support 24 kHz contexts. Safari 14.1 and later accept the
  `sampleRate` option and AudioWorklet, but check iOS hardware before you rely
  on it.
- Echo cancellation is the browser's. With loudspeakers and no headset, Margo
  can hear herself in some environments.
- The ID token is decoded only to show the account name; the server must
  validate the access token sent in `hello`.
- Voice cannot approve or send anything; approvals happen in Teams.
