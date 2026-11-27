// Pure helpers for the "Talk to Margo" voice page.
//
// No DOM, audio or network APIs here, so every function runs under Node's test
// runner (node --test). The page logic lives in voice.js and the audio thread
// code in pcm-worklets.js.
//
// Audio on the wire is PCM16, mono, 24 kHz, little-endian, base64 encoded.

/** Samples per 20 ms frame at 24 kHz. */
export const SAMPLE_RATE = 24000;
export const FRAME_SAMPLES = 480;

/** Upper bound for one server audio payload (base64 chars, about 30 s of audio). */
export const MAX_AUDIO_BASE64 = 2 * 1024 * 1024;
/** Upper bound for caption and status text shown on the page. */
export const MAX_TEXT = 2000;

const B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
const B64_LOOKUP = (() => {
  const table = new Int16Array(128).fill(-1);
  for (let i = 0; i < B64.length; i += 1) table[B64.charCodeAt(i)] = i;
  return table;
})();

// ---------------------------------------------------------------- base64

/** Standard base64 (RFC 4648 section 4) with padding. */
export function bytesToBase64(bytes) {
  if (!(bytes instanceof Uint8Array)) throw new TypeError("bytesToBase64 expects a Uint8Array");
  let out = "";
  const n = bytes.length;
  let i = 0;
  for (; i + 2 < n; i += 3) {
    const v = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
    out += B64[(v >> 18) & 63] + B64[(v >> 12) & 63] + B64[(v >> 6) & 63] + B64[v & 63];
  }
  if (n - i === 1) {
    const v = bytes[i] << 16;
    out += B64[(v >> 18) & 63] + B64[(v >> 12) & 63] + "==";
  } else if (n - i === 2) {
    const v = (bytes[i] << 16) | (bytes[i + 1] << 8);
    out += B64[(v >> 18) & 63] + B64[(v >> 12) & 63] + B64[(v >> 6) & 63] + "=";
  }
  return out;
}

/** Decodes standard base64. Padding is optional; any other character is an error. */
export function base64ToBytes(text) {
  if (typeof text !== "string") throw new TypeError("base64ToBytes expects a string");
  let end = text.length;
  while (end > 0 && text.charCodeAt(end - 1) === 61 /* = */) end -= 1;
  if (text.length - end > 2 || (end < text.length && text.length % 4 !== 0)) {
    throw new SyntaxError("invalid base64 padding");
  }
  if (end % 4 === 1) throw new SyntaxError("invalid base64 length");
  const out = new Uint8Array(Math.floor((end * 3) / 4));
  let acc = 0;
  let bits = 0;
  let o = 0;
  for (let i = 0; i < end; i += 1) {
    const c = text.charCodeAt(i);
    const v = c < 128 ? B64_LOOKUP[c] : -1;
    if (v < 0) throw new SyntaxError("invalid base64 character");
    acc = ((acc << 6) | v) & 0xffffff;
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      out[o] = (acc >> bits) & 0xff;
      o += 1;
    }
  }
  return out;
}

/** base64url without padding (RFC 4648 section 5), as PKCE and JWTs use. */
export function base64UrlEncode(bytes) {
  return bytesToBase64(bytes).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** Decodes base64url with or without padding. */
export function base64UrlDecode(text) {
  if (typeof text !== "string") throw new TypeError("base64UrlDecode expects a string");
  if (/[+/]/.test(text)) throw new SyntaxError("not base64url");
  return base64ToBytes(text.replace(/-/g, "+").replace(/_/g, "/"));
}

// ---------------------------------------------------------------- PCM16

/** Float samples to PCM16: clamp to [-1, 1], scale by 0x7fff, round. NaN becomes 0. */
export function floatToPcm16(samples) {
  if (!(samples instanceof Float32Array)) throw new TypeError("floatToPcm16 expects a Float32Array");
  const out = new Int16Array(samples.length);
  for (let i = 0; i < samples.length; i += 1) {
    let s = samples[i];
    if (!(s === s)) s = 0; // NaN
    else if (s > 1) s = 1;
    else if (s < -1) s = -1;
    out[i] = Math.round(s * 0x7fff);
  }
  return out;
}

/** PCM16 to float samples in [-1, 1] (the inverse of floatToPcm16; -32768 maps to -1). */
export function pcm16ToFloat(samples) {
  if (!(samples instanceof Int16Array)) throw new TypeError("pcm16ToFloat expects an Int16Array");
  const out = new Float32Array(samples.length);
  for (let i = 0; i < samples.length; i += 1) {
    const s = samples[i] / 0x7fff;
    out[i] = s < -1 ? -1 : s;
  }
  return out;
}

/** PCM16 samples to base64 of their little-endian bytes, whatever the host byte order. */
export function pcm16ToBase64(samples) {
  if (!(samples instanceof Int16Array)) throw new TypeError("pcm16ToBase64 expects an Int16Array");
  const bytes = new Uint8Array(samples.length * 2);
  const view = new DataView(bytes.buffer);
  for (let i = 0; i < samples.length; i += 1) view.setInt16(i * 2, samples[i], true);
  return bytesToBase64(bytes);
}

/** base64 of little-endian PCM16 bytes to samples. An odd byte count is a protocol error. */
export function base64ToPcm16(text) {
  const bytes = base64ToBytes(text);
  if (bytes.length % 2 !== 0) throw new RangeError("PCM16 payload has an odd number of bytes");
  const out = new Int16Array(bytes.length / 2);
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  for (let i = 0; i < out.length; i += 1) out[i] = view.getInt16(i * 2, true);
  return out;
}

/** Splits a typed array into frames of `size` samples; the last frame may be shorter. */
export function chunkSamples(samples, size) {
  if (!ArrayBuffer.isView(samples) || samples instanceof DataView) {
    throw new TypeError("chunkSamples expects a typed array");
  }
  if (!Number.isInteger(size) || size <= 0) throw new RangeError("chunk size must be a positive integer");
  const frames = [];
  for (let i = 0; i < samples.length; i += size) frames.push(samples.slice(i, i + size));
  return frames;
}

// ---------------------------------------------------------------- OAuth (Entra v2, code + PKCE)

const AUTHORITY = "https://login.microsoftonline.com";

function cryptoOrThrow() {
  const c = globalThis.crypto;
  if (!c || typeof c.getRandomValues !== "function" || !c.subtle) {
    throw new Error("Web Crypto is unavailable. Open this page over HTTPS (or on localhost).");
  }
  return c;
}

/** `byteCount` random bytes as base64url. 32 bytes gives 43 characters. */
export function randomToken(byteCount = 32) {
  if (!Number.isInteger(byteCount) || byteCount <= 0 || byteCount > 96) {
    throw new RangeError("byteCount must be an integer from 1 to 96");
  }
  return base64UrlEncode(cryptoOrThrow().getRandomValues(new Uint8Array(byteCount)));
}

/** S256 code challenge for a verifier: base64url(SHA-256(ASCII verifier)), no padding. */
export async function pkceChallenge(verifier) {
  if (!isPkceVerifier(verifier)) throw new RangeError("invalid PKCE verifier");
  const digest = await cryptoOrThrow().subtle.digest("SHA-256", new TextEncoder().encode(verifier));
  return base64UrlEncode(new Uint8Array(digest));
}

/** True for a 43-128 character string from the RFC 7636 unreserved set. */
export function isPkceVerifier(value) {
  return typeof value === "string" && /^[A-Za-z0-9\-._~]{43,128}$/.test(value);
}

/** A fresh PKCE pair. The verifier is 64 base64url characters (48 random bytes). */
export async function createPkce() {
  const verifier = randomToken(48);
  return { verifier, challenge: await pkceChallenge(verifier) };
}

/** Validates the page config served at ./config and returns a clean copy. */
export function validateConfig(config) {
  if (!config || typeof config !== "object") throw new TypeError("voice config must be an object");
  const out = {};
  for (const key of ["tenantId", "clientId", "scope"]) {
    const value = config[key];
    if (typeof value !== "string" || !value.trim()) throw new TypeError(`voice config is missing ${key}`);
    out[key] = value.trim();
  }
  if (!/^[A-Za-z0-9.-]+$/.test(out.tenantId)) throw new TypeError("voice config tenantId is not a tenant id or domain");
  return out;
}

/** The scope string sent to Entra: config.scope plus "openid profile", without duplicates. */
export function scopeString(config) {
  const scopes = [];
  for (const s of `${config.scope} openid profile`.split(/\s+/)) {
    if (s && !scopes.includes(s)) scopes.push(s);
  }
  return scopes.join(" ");
}

function tenantBase(config) {
  return `${AUTHORITY}/${encodeURIComponent(validateConfig(config).tenantId)}/oauth2/v2.0`;
}

/** The Entra v2 authorize URL for the authorization code flow with PKCE. */
export function authorizeUrl(config, { challenge, state, redirectUri }) {
  const clean = validateConfig(config);
  for (const [name, value] of Object.entries({ challenge, state, redirectUri })) {
    if (typeof value !== "string" || !value) throw new TypeError(`authorizeUrl needs ${name}`);
  }
  const url = new URL(`${tenantBase(clean)}/authorize`);
  const p = url.searchParams;
  p.set("client_id", clean.clientId);
  p.set("response_type", "code");
  p.set("redirect_uri", redirectUri);
  p.set("scope", scopeString(clean));
  p.set("code_challenge", challenge);
  p.set("code_challenge_method", "S256");
  p.set("state", state);
  p.set("response_mode", "query");
  p.set("prompt", "select_account");
  return url.href;
}

/** The Entra v2 token endpoint for this tenant. */
export function tokenUrl(config) {
  return `${tenantBase(config)}/token`;
}

/** Form body for redeeming an authorization code at the token endpoint. */
export function tokenRequestBody(config, { code, verifier, redirectUri }) {
  const clean = validateConfig(config);
  for (const [name, value] of Object.entries({ code, verifier, redirectUri })) {
    if (typeof value !== "string" || !value) throw new TypeError(`tokenRequestBody needs ${name}`);
  }
  return new URLSearchParams({
    grant_type: "authorization_code",
    client_id: clean.clientId,
    code,
    redirect_uri: redirectUri,
    code_verifier: verifier,
    scope: scopeString(clean),
  });
}

/** Reads the authorization response from a redirect URL's query string. Missing values are null. */
export function parseRedirect(urlString) {
  const p = new URL(urlString).searchParams;
  return {
    code: p.get("code"),
    state: p.get("state"),
    error: p.get("error"),
    errorDescription: p.get("error_description"),
  };
}

/** The OAuth redirect URI for a page: its URL without query or fragment. */
export function redirectUriFor(pageUrl) {
  const url = new URL(pageUrl);
  url.search = "";
  url.hash = "";
  return url.href;
}

/** Normalizes a token endpoint response. Throws when it carries no usable access token. */
export function tokenFromResponse(body, nowMs = Date.now()) {
  if (!body || typeof body !== "object" || typeof body.access_token !== "string" || !body.access_token) {
    throw new Error("The sign-in response did not include an access token.");
  }
  const seconds = Number(body.expires_in);
  const lifetime = Number.isFinite(seconds) && seconds > 0 ? seconds : 300;
  return { accessToken: body.access_token, expiresAt: nowMs + lifetime * 1000 };
}

/**
 * A display label from an ID token (name and username claims), or null.
 * Display only: the signature is not checked here; the server validates the access token.
 */
export function accountLabelFromIdToken(idToken) {
  if (typeof idToken !== "string") return null;
  const parts = idToken.split(".");
  if (parts.length < 2) return null;
  try {
    const claims = JSON.parse(new TextDecoder().decode(base64UrlDecode(parts[1])));
    const name = typeof claims.name === "string" ? claims.name.trim() : "";
    const user = typeof claims.preferred_username === "string" ? claims.preferred_username.trim() : "";
    if (name && user && name !== user) return `${name} (${user})`;
    return name || user || null;
  } catch {
    return null;
  }
}

// ---------------------------------------------------------------- WebSocket protocol

/** Absolute ws:// or wss:// URL for a path relative to the page, so it works under a prefix. */
export function wsUrlFor(pageUrl, path = "ws") {
  const url = new URL(path, pageUrl);
  if (url.protocol === "https:") url.protocol = "wss:";
  else if (url.protocol === "http:") url.protocol = "ws:";
  else throw new TypeError(`cannot open a WebSocket from a ${url.protocol} page`);
  url.search = "";
  url.hash = "";
  return url.href;
}

export function helloMessage(token) {
  if (typeof token !== "string" || !token) throw new TypeError("hello needs an access token");
  return JSON.stringify({ type: "hello", token });
}

export function audioMessage(pcm16) {
  return JSON.stringify({ type: "audio", audio: pcm16ToBase64(pcm16) });
}

export function stopMessage() {
  return JSON.stringify({ type: "stop" });
}

function text(value, fallback = "") {
  if (typeof value !== "string") return fallback;
  return value.length > MAX_TEXT ? `${value.slice(0, MAX_TEXT)}…` : value;
}

/**
 * Parses and normalizes one server text frame. Returns null for anything that is
 * not a well-formed message of a known type, so the page can ignore it.
 */
export function parseServerMessage(data) {
  if (typeof data !== "string") return null;
  let msg;
  try {
    msg = JSON.parse(data);
  } catch {
    return null;
  }
  if (!msg || typeof msg !== "object" || Array.isArray(msg)) return null;
  switch (msg.type) {
    case "ready":
      return { type: "ready", disclosure: text(msg.disclosure), voice: text(msg.voice) };
    case "audio":
      if (typeof msg.audio !== "string" || msg.audio.length > MAX_AUDIO_BASE64) return null;
      return { type: "audio", audio: msg.audio };
    case "interrupt":
      return { type: "interrupt" };
    case "caption":
      if (msg.who !== "you" && msg.who !== "margo") return null;
      if (typeof msg.text !== "string") return null;
      return { type: "caption", who: msg.who, text: text(msg.text) };
    case "status":
      return { type: "status", text: text(msg.text) };
    case "error":
      return { type: "error", message: text(msg.message) || "Something went wrong.", fatal: msg.fatal === true };
    case "closed":
      return { type: "closed", reason: text(msg.reason) };
    default:
      return null;
  }
}
