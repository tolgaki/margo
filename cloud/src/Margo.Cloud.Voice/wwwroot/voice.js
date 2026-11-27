// Page logic for "Talk to Margo". Thin client: sign-in (Entra auth code + PKCE),
// microphone capture, playback and the WebSocket protocol described in README.md.
// The access token is held in memory only. Nothing here logs tokens or audio.

import {
  FRAME_SAMPLES,
  SAMPLE_RATE,
  accountLabelFromIdToken,
  audioMessage,
  authorizeUrl,
  base64ToPcm16,
  chunkSamples,
  createPkce,
  floatToPcm16,
  helloMessage,
  parseRedirect,
  parseServerMessage,
  pcm16ToFloat,
  randomToken,
  redirectUriFor,
  stopMessage,
  tokenFromResponse,
  tokenRequestBody,
  tokenUrl,
  validateConfig,
  wsUrlFor,
} from "./voice-core.mjs";

const PENDING_KEY = "margo-voice-sign-in"; // sessionStorage, redirect round trip only
const PENDING_MAX_AGE_MS = 10 * 60 * 1000;
const TOKEN_SKEW_MS = 60 * 1000;
const MAX_BUFFERED_BYTES = 256 * 1024; // about 4 s of outgoing audio
const MAX_CAPTIONS = 200;

const $ = (id) => document.getElementById(id);
const ui = {
  signIn: $("sign-in"),
  start: $("start"),
  end: $("end"),
  status: $("status"),
  error: $("error"),
  captions: $("captions"),
  captionsEmpty: $("captions-empty"),
  serverDisclosure: $("server-disclosure"),
  account: $("account"),
};

// The access token lives only in this object, never in any storage.
const session = { config: null, token: null, expiresAt: 0, busy: false, expiryTimer: 0 };
let call = null;

class VoiceError extends Error {}

// ------------------------------------------------------------------ UI helpers

function setStatus(text) {
  ui.status.textContent = text || "";
}

function showError(text) {
  ui.error.textContent = text;
  ui.error.hidden = !text;
}

function clearError() {
  showError("");
}

function messageOf(err) {
  const text = err && typeof err.message === "string" ? err.message : "";
  return text ? text.slice(0, 200) : "unknown error";
}

function hasToken() {
  return Boolean(session.token) && Date.now() < session.expiresAt - TOKEN_SKEW_MS;
}

function renderButtons() {
  const inCall = call !== null;
  ui.signIn.disabled = !session.config || inCall || session.busy;
  ui.start.disabled = inCall || session.busy || !hasToken();
  ui.end.disabled = !inCall;
  // Keep keyboard focus on something usable when the focused button turns off.
  const active = document.activeElement;
  if (active && active !== document.body && !(active instanceof HTMLButtonElement && active.disabled)) return;
  if (inCall) ui.end.focus();
  else if (active instanceof HTMLButtonElement) (ui.start.disabled ? ui.signIn : ui.start).focus();
}

function idleStatus() {
  if (!session.config) return;
  setStatus(hasToken() ? "Signed in. Press Start talking when you're ready." : "Sign in with your work account to start.");
}

function clearCaptions() {
  for (const line of ui.captions.querySelectorAll(".caption")) line.remove();
  ui.captionsEmpty.hidden = false;
}

function addCaption(who, text) {
  const box = ui.captions;
  const atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 24;
  const line = document.createElement("p");
  line.className = `caption caption-${who}`;
  const label = document.createElement("span");
  label.className = "caption-who";
  label.textContent = who === "you" ? "You:" : "Margo:";
  const body = document.createElement("span");
  body.textContent = ` ${text}`;
  line.append(label, body);
  ui.captionsEmpty.hidden = true;
  box.append(line);
  const lines = box.querySelectorAll(".caption");
  for (let i = 0; i < lines.length - MAX_CAPTIONS; i += 1) lines[i].remove();
  if (atBottom) box.scrollTop = box.scrollHeight;
}

// ------------------------------------------------------------------ sign-in

async function loadConfig() {
  const response = await fetch("config", {
    headers: { Accept: "application/json" },
    cache: "no-store",
    credentials: "same-origin",
  });
  if (!response.ok) throw new VoiceError(`config returned HTTP ${response.status}`);
  return validateConfig(await response.json());
}

// Reads and deletes the pending sign-in record. It only exists between pressing
// Sign in and returning from Entra, so it is removed on every page load.
function takePending() {
  try {
    const raw = sessionStorage.getItem(PENDING_KEY);
    sessionStorage.removeItem(PENDING_KEY);
    const pending = raw ? JSON.parse(raw) : null;
    if (!pending || typeof pending !== "object") return null;
    if (typeof pending.verifier !== "string" || typeof pending.state !== "string") return null;
    if (typeof pending.redirectUri !== "string") return null;
    if (!(Date.now() - Number(pending.createdAt) < PENDING_MAX_AGE_MS)) return null;
    return pending;
  } catch {
    return null;
  }
}

// Reads an authorization response from the address bar, then removes it from the
// address bar and history before anything else runs.
function takeRedirect() {
  const pending = takePending();
  const params = parseRedirect(location.href);
  if (!params.code && !params.error) return null;
  history.replaceState(history.state, "", redirectUriFor(location.href));
  return { params, pending };
}

async function completeSignIn({ params, pending }) {
  // A response whose state is not ours did not come from the sign-in this tab
  // started; its text is not shown.
  if (!pending || !params.state || params.state !== pending.state) {
    showError("That sign-in response didn't come from this tab or has expired. Sign in again.");
    return;
  }
  if (params.error) {
    const detail = (params.errorDescription || params.error).split(/\r?\n/)[0].slice(0, 300);
    showError(`Sign-in didn't complete: ${detail}`);
    return;
  }
  session.busy = true;
  renderButtons();
  setStatus("Finishing sign-in…");
  try {
    let response;
    try {
      response = await fetch(tokenUrl(session.config), {
        method: "POST",
        body: tokenRequestBody(session.config, {
          code: params.code,
          verifier: pending.verifier,
          redirectUri: pending.redirectUri,
        }),
        headers: { Accept: "application/json" },
        credentials: "omit",
        cache: "no-store",
      });
    } catch {
      throw new VoiceError("Couldn't reach Microsoft sign-in. Check your connection and sign in again.");
    }
    let body = null;
    try {
      body = await response.json();
    } catch {
      body = null;
    }
    if (!response.ok) {
      const description = body && typeof body.error_description === "string"
        ? body.error_description.split(/\r?\n/)[0].slice(0, 300)
        : `HTTP ${response.status}`;
      throw new VoiceError(`Sign-in failed: ${description}`);
    }
    const token = tokenFromResponse(body);
    session.token = token.accessToken;
    session.expiresAt = token.expiresAt;
    const label = accountLabelFromIdToken(body.id_token);
    ui.account.textContent = label ? `Signed in as ${label}` : "Signed in";
    ui.account.hidden = false;
    scheduleExpiry();
  } catch (err) {
    showError(err instanceof VoiceError ? err.message : `Sign-in failed: ${messageOf(err)}`);
  } finally {
    session.busy = false;
  }
}

function scheduleExpiry() {
  clearTimeout(session.expiryTimer);
  const wait = session.expiresAt - TOKEN_SKEW_MS - Date.now();
  if (wait <= 0) return;
  session.expiryTimer = setTimeout(() => {
    if (call) return; // the server decides about a call already in progress
    session.token = null;
    ui.account.hidden = true;
    setStatus("Your sign-in expired. Sign in again to keep talking.");
    renderButtons();
  }, Math.min(wait, 2 ** 31 - 1));
}

async function signIn() {
  if (!session.config || call || session.busy) return;
  clearError();
  session.busy = true;
  renderButtons();
  try {
    const { verifier, challenge } = await createPkce();
    const state = randomToken(32);
    const redirectUri = redirectUriFor(location.href);
    try {
      sessionStorage.setItem(PENDING_KEY, JSON.stringify({ verifier, state, redirectUri, createdAt: Date.now() }));
    } catch {
      throw new VoiceError("This browser blocked session storage, which sign-in needs. Allow site data for this page and try again.");
    }
    setStatus("Opening Microsoft sign-in…");
    location.assign(authorizeUrl(session.config, { challenge, state, redirectUri }));
  } catch (err) {
    session.busy = false;
    renderButtons();
    idleStatus();
    showError(err instanceof VoiceError ? err.message : `Couldn't start sign-in: ${messageOf(err)}`);
  }
}

// ------------------------------------------------------------------ call

function describeStartError(err) {
  if (err instanceof VoiceError) return err.message;
  switch (err && err.name) {
    case "NotAllowedError":
    case "SecurityError":
      return "Microphone access was blocked. Allow the microphone for this site in your browser settings, then press Start talking again.";
    case "NotFoundError":
    case "OverconstrainedError":
      return "No microphone was found. Connect one and try again.";
    case "NotReadableError":
    case "AbortError":
      return "The microphone couldn't be opened. Another app may be using it.";
    case "NotSupportedError":
      return "This browser can't connect the microphone to 24 kHz audio. Try a current version of Edge or Chrome.";
    default:
      return `Couldn't start the call: ${messageOf(err)}`;
  }
}

function startCall() {
  if (call || session.busy) return;
  clearError();
  if (!hasToken()) {
    session.token = null;
    renderButtons();
    showError("Your sign-in expired. Sign in again, then press Start talking.");
    return;
  }
  if (!navigator.mediaDevices || typeof navigator.mediaDevices.getUserMedia !== "function"
      || typeof AudioContext === "undefined" || typeof AudioWorkletNode === "undefined") {
    showError("This browser can't make voice calls here. Use a current browser and open the page over HTTPS.");
    return;
  }
  const c = {
    ended: false, opened: false, ready: false, slow: false,
    ws: null, stream: null, ctx: null, source: null, capture: null, player: null,
  };
  call = c;
  clearCaptions();
  ui.serverDisclosure.hidden = true;
  renderButtons();
  // Create and resume the AudioContext inside the click so autoplay rules allow it.
  try {
    c.ctx = new AudioContext({ sampleRate: SAMPLE_RATE });
    c.ctx.resume().catch(() => {});
  } catch (err) {
    endCall(c, { status: "", error: "This browser can't run audio at 24 kHz, which the voice service needs." });
    return;
  }
  setupAudio(c).catch((err) => {
    if (!c.ended) endCall(c, { status: "", error: describeStartError(err) });
  });
}

async function setupAudio(c) {
  setStatus("Waiting for microphone permission…");
  const stream = await navigator.mediaDevices.getUserMedia({
    audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true, channelCount: 1 },
  });
  c.stream = stream;
  if (c.ended) {
    stopTracks(stream); // End was pressed while the permission prompt was open
    return;
  }
  if (c.ctx.sampleRate !== SAMPLE_RATE) {
    throw new VoiceError("This browser can't run audio at 24 kHz, which the voice service needs.");
  }
  await c.ctx.audioWorklet.addModule(new URL("pcm-worklets.js", import.meta.url).href);
  if (c.ended) return;
  c.source = c.ctx.createMediaStreamSource(stream);
  c.capture = new AudioWorkletNode(c.ctx, "pcm-capture", {
    numberOfInputs: 1,
    numberOfOutputs: 1,
    outputChannelCount: [1],
    channelCount: 1,
    channelCountMode: "explicit",
    channelInterpretation: "speakers",
  });
  c.player = new AudioWorkletNode(c.ctx, "pcm-player", {
    numberOfInputs: 0,
    numberOfOutputs: 1,
    outputChannelCount: [1],
  });
  c.capture.port.onmessage = (event) => sendFrame(c, event.data);
  c.player.port.onmessage = (event) => {
    if (event.data && event.data.type === "overflow" && !c.ended) {
      setStatus("Margo's audio is arriving faster than it can play; some was skipped.");
    }
  };
  c.source.connect(c.capture);
  c.capture.connect(c.ctx.destination); // outputs silence; keeps the capture node running
  c.player.connect(c.ctx.destination);
  if (c.ctx.state === "suspended") await c.ctx.resume();
  if (c.ended) return;
  openSocket(c);
}

function openSocket(c) {
  setStatus("Connecting to Margo…");
  let ws;
  try {
    ws = new WebSocket(wsUrlFor(location.href));
  } catch {
    endCall(c, { status: "", error: "Couldn't open a connection to Margo." });
    return;
  }
  c.ws = ws;
  ws.onopen = () => {
    if (c.ended) return;
    c.opened = true;
    if (!hasToken()) {
      endCall(c, { status: "", error: "Your sign-in expired. Sign in again, then press Start talking." });
      return;
    }
    ws.send(helloMessage(session.token));
    setStatus("Connected. Waiting for Margo…");
  };
  ws.onmessage = (event) => onServerMessage(c, event.data);
  ws.onerror = () => {}; // a close event always follows; it carries the message
  ws.onclose = (event) => {
    if (c.ended) return;
    endCall(c, { status: "Call ended.", error: closeMessage(c, event) });
  };
}

function closeMessage(c, event) {
  const reason = typeof event.reason === "string" && event.reason ? ` (${event.reason.slice(0, 120)})` : "";
  if (!c.opened) return "Couldn't connect to Margo. Check your connection and try again.";
  if (!c.ready) return `Margo's server closed the connection before the call started${reason}. Sign in again if this keeps happening.`;
  return `The connection to Margo was lost${reason}. Press Start talking to try again.`;
}

function onServerMessage(c, data) {
  if (c.ended) return;
  const msg = parseServerMessage(data);
  if (!msg) return; // unknown or malformed frames are ignored
  switch (msg.type) {
    case "ready":
      c.ready = true;
      ui.serverDisclosure.textContent = [msg.disclosure, msg.voice ? `Voice: ${msg.voice}` : ""]
        .filter(Boolean)
        .join(" · ");
      ui.serverDisclosure.hidden = !ui.serverDisclosure.textContent;
      setStatus("Margo is listening. Go ahead and talk.");
      break;
    case "audio":
      playAudio(c, msg.audio);
      break;
    case "interrupt":
      if (c.player) c.player.port.postMessage({ type: "clear" });
      break;
    case "caption":
      addCaption(msg.who, msg.text);
      break;
    case "status":
      setStatus(msg.text);
      break;
    case "error":
      if (msg.fatal) endCall(c, { status: "Call ended.", error: msg.message });
      else showError(msg.message);
      break;
    case "closed":
      endCall(c, { status: msg.reason ? `Call ended: ${msg.reason}` : "Margo ended the call." });
      break;
    default:
      break;
  }
}

function playAudio(c, base64) {
  if (!c.player) return;
  let samples;
  try {
    samples = pcm16ToFloat(base64ToPcm16(base64));
  } catch {
    return; // a malformed frame is skipped, not fatal
  }
  if (samples.length) c.player.port.postMessage(samples, [samples.buffer]);
}

function sendFrame(c, samples) {
  const ws = c.ws;
  if (c.ended || !c.ready || !ws || ws.readyState !== WebSocket.OPEN) return; // nothing is sent before "ready"
  if (Object.prototype.toString.call(samples) !== "[object Float32Array]") return;
  if (ws.bufferedAmount > MAX_BUFFERED_BYTES) {
    if (!c.slow) setStatus("Your connection is slow; some of what you say may not reach Margo.");
    c.slow = true;
    return; // drop rather than queue without bound
  }
  c.slow = false;
  for (const frame of chunkSamples(floatToPcm16(samples), FRAME_SAMPLES)) ws.send(audioMessage(frame));
}

function stopTracks(stream) {
  if (stream) for (const track of stream.getTracks()) track.stop();
}

function endCall(c, { status = "Call ended.", error = "" } = {}) {
  if (!c || c.ended) return;
  c.ended = true;
  const ws = c.ws;
  if (ws) {
    ws.onopen = null;
    ws.onmessage = null;
    ws.onerror = null;
    ws.onclose = null;
    try {
      if (ws.readyState === WebSocket.OPEN) {
        ws.send(stopMessage());
        ws.close(1000, "call ended");
      } else if (ws.readyState === WebSocket.CONNECTING) {
        ws.close();
      }
    } catch {
      // the socket is already gone
    }
  }
  for (const node of [c.capture, c.player]) {
    if (!node) continue;
    try {
      node.port.onmessage = null;
      node.port.postMessage({ type: node === c.capture ? "stop" : "clear" });
      node.disconnect();
    } catch {
      // the context may already be closed
    }
  }
  try {
    if (c.source) c.source.disconnect();
  } catch {
    // ignore
  }
  stopTracks(c.stream);
  if (c.ctx && c.ctx.state !== "closed") c.ctx.close().catch(() => {});
  if (call === c) call = null;
  if (session.token && !hasToken()) {
    // The sign-in expired during the call.
    session.token = null;
    ui.account.hidden = true;
    status = `${status} Sign in again to keep talking.`.trim();
  }
  if (error) showError(error);
  setStatus(status);
  if (!status && !error) idleStatus();
  renderButtons();
}

// ------------------------------------------------------------------ start-up

function bind() {
  ui.signIn.addEventListener("click", () => {
    signIn();
  });
  ui.start.addEventListener("click", () => startCall());
  ui.end.addEventListener("click", () => {
    if (call) endCall(call, { status: "Call ended." });
  });
  // Release the microphone when the page goes away.
  window.addEventListener("pagehide", () => {
    if (call) endCall(call, { status: "Call ended." });
  });
  // Coming back from the sign-in page via the back button restores this page
  // from cache with the sign-in still marked busy.
  window.addEventListener("pageshow", (event) => {
    if (!event.persisted) return;
    session.busy = false;
    takePending();
    idleStatus();
    renderButtons();
  });
}

async function init() {
  bind();
  if (!window.isSecureContext) {
    setStatus("");
    showError("Open this page over HTTPS. Sign-in and the microphone need a secure connection.");
    renderButtons();
    return;
  }
  const redirect = takeRedirect();
  try {
    session.config = await loadConfig();
  } catch {
    setStatus("");
    showError("Couldn't load the voice settings for this page. Reload to try again.");
    renderButtons();
    return;
  }
  if (redirect) await completeSignIn(redirect);
  idleStatus();
  renderButtons();
}

init().catch((err) => {
  showError(`The page couldn't start: ${messageOf(err)}`);
});
