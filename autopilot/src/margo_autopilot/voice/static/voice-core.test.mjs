// node --test autopilot/src/margo_autopilot/voice/static/*.test.mjs
import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import {
  FRAME_SAMPLES,
  MAX_AUDIO_BASE64,
  MAX_TEXT,
  accountLabelFromIdToken,
  audioMessage,
  authorizeUrl,
  base64ToBytes,
  base64ToPcm16,
  base64UrlDecode,
  base64UrlEncode,
  bytesToBase64,
  chunkSamples,
  createPkce,
  floatToPcm16,
  helloMessage,
  isPkceVerifier,
  parseRedirect,
  parseServerMessage,
  pcm16ToBase64,
  pcm16ToFloat,
  pkceChallenge,
  randomToken,
  redirectUriFor,
  scopeString,
  stopMessage,
  tokenFromResponse,
  tokenRequestBody,
  tokenUrl,
  validateConfig,
  wsUrlFor,
} from "./voice-core.mjs";

// Fictional values only.
const config = {
  tenantId: "tenant.example.com",
  clientId: "voice-client-example",
  scope: "api://voice.example.com/Voice.Talk",
};
const PAGE = "https://margo.example.com/i/abc123/voice/";
const UNRESERVED = /^[A-Za-z0-9\-._~]+$/;

// ---------------------------------------------------------------- base64

test("bytesToBase64 matches Buffer for every padding case and all byte values", () => {
  assert.equal(bytesToBase64(new Uint8Array()), "");
  for (const text of ["f", "fo", "foo", "foob", "fooba", "foobar"]) {
    const bytes = new TextEncoder().encode(text);
    assert.equal(bytesToBase64(bytes), Buffer.from(bytes).toString("base64"), text);
  }
  const all = Uint8Array.from({ length: 256 }, (_, i) => i);
  assert.equal(bytesToBase64(all), Buffer.from(all).toString("base64"));
  assert.throws(() => bytesToBase64([1, 2, 3]), TypeError);
});

test("base64ToBytes inverts bytesToBase64, accepts missing padding, rejects junk", () => {
  const all = Uint8Array.from({ length: 256 }, (_, i) => 255 - i);
  assert.deepEqual(base64ToBytes(bytesToBase64(all)), all);
  assert.deepEqual(base64ToBytes(""), new Uint8Array());
  assert.deepEqual(base64ToBytes("Zm9vYg=="), new TextEncoder().encode("foob"));
  assert.deepEqual(base64ToBytes("Zm9vYg"), new TextEncoder().encode("foob"));
  assert.deepEqual(base64ToBytes("Zm9vYmE="), new TextEncoder().encode("fooba"));
  assert.throws(() => base64ToBytes("Zm9v!mE="), SyntaxError);
  assert.throws(() => base64ToBytes("Zm9vY"), SyntaxError); // impossible length
  assert.throws(() => base64ToBytes("Zm9vYg="), SyntaxError); // bad padding
  assert.throws(() => base64ToBytes("Zg==="), SyntaxError);
  assert.throws(() => base64ToBytes("Zm9vé"), SyntaxError);
  assert.throws(() => base64ToBytes(42), TypeError);
});

test("base64url encodes without padding and round trips", () => {
  const bytes = Uint8Array.from([0xfb, 0xff, 0xbf, 0x01]);
  assert.equal(base64UrlEncode(bytes), Buffer.from(bytes).toString("base64url"));
  assert.ok(!base64UrlEncode(bytes).includes("="));
  assert.deepEqual(base64UrlDecode(base64UrlEncode(bytes)), bytes);
  assert.throws(() => base64UrlDecode("+/8="), SyntaxError);
});

// ---------------------------------------------------------------- PCM16

test("floatToPcm16 scales by 0x7fff, rounds, clamps and zeroes NaN", () => {
  const pcm = floatToPcm16(Float32Array.from([0, 1, -1, 0.5, -0.5, 2, -2, Infinity, -Infinity, NaN, 1e-6]));
  assert.ok(pcm instanceof Int16Array);
  assert.deepEqual(Array.from(pcm), [
    0, 32767, -32767, Math.round(0.5 * 32767), Math.round(-0.5 * 32767), 32767, -32767, 32767, -32767, 0, 0,
  ]);
  assert.throws(() => floatToPcm16([0.1]), TypeError);
});

test("pcm16ToFloat maps the full range into [-1, 1]", () => {
  const f = pcm16ToFloat(Int16Array.from([0, 32767, -32767, -32768, 16384]));
  assert.ok(f instanceof Float32Array);
  assert.equal(f[0], 0);
  assert.equal(f[1], 1);
  assert.equal(f[2], -1);
  assert.equal(f[3], -1);
  assert.ok(Math.abs(f[4] - 16384 / 32767) < 1e-7);
  assert.throws(() => pcm16ToFloat(new Float32Array(2)), TypeError);
});

test("pcm16ToBase64 writes little-endian bytes", () => {
  const samples = Int16Array.from([1, -2, 0x1234, -32768, 32767]);
  const expected = Buffer.from([0x01, 0x00, 0xfe, 0xff, 0x34, 0x12, 0x00, 0x80, 0xff, 0x7f]).toString("base64");
  assert.equal(pcm16ToBase64(samples), expected);
  assert.equal(pcm16ToBase64(new Int16Array()), "");
  assert.throws(() => pcm16ToBase64(new Uint16Array(1)), TypeError);
});

test("base64ToPcm16 reads little-endian bytes and rejects an odd byte count", () => {
  const b64 = Buffer.from([0x01, 0x00, 0xfe, 0xff, 0x34, 0x12]).toString("base64");
  assert.deepEqual(Array.from(base64ToPcm16(b64)), [1, -2, 0x1234]);
  assert.deepEqual(Array.from(base64ToPcm16("")), []);
  assert.throws(() => base64ToPcm16(Buffer.from([1, 2, 3]).toString("base64")), RangeError);
});

test("float -> pcm16 -> base64 -> pcm16 -> float round trip stays within one step", () => {
  const n = FRAME_SAMPLES * 3 + 7;
  const input = new Float32Array(n);
  for (let i = 0; i < n; i += 1) input[i] = 0.9 * Math.sin((2 * Math.PI * 440 * i) / 24000) + (i % 7) * 0.01;
  const wire = pcm16ToBase64(floatToPcm16(input));
  assert.equal(Buffer.from(wire, "base64").length, n * 2);
  const output = pcm16ToFloat(base64ToPcm16(wire));
  assert.equal(output.length, n);
  for (let i = 0; i < n; i += 1) {
    const expected = Math.max(-1, Math.min(1, input[i]));
    assert.ok(Math.abs(output[i] - expected) <= 1 / 32767, `sample ${i}`);
  }
});

test("chunkSamples splits into frames with a short final frame", () => {
  const samples = Int16Array.from({ length: 1000 }, (_, i) => i - 500);
  const frames = chunkSamples(samples, FRAME_SAMPLES);
  assert.deepEqual(frames.map((f) => f.length), [480, 480, 40]);
  assert.ok(frames.every((f) => f instanceof Int16Array));
  assert.deepEqual(Array.from(frames.flatMap((f) => Array.from(f))), Array.from(samples));
  frames[0][0] = 9999; // frames are copies, not views
  assert.equal(samples[0], -500);
  assert.deepEqual(chunkSamples(new Int16Array(), 480), []);
  assert.deepEqual(chunkSamples(new Int16Array(480), 480).map((f) => f.length), [480]);
  assert.deepEqual(chunkSamples(new Float32Array(5), 2).map((f) => f.length), [2, 2, 1]);
  for (const bad of [0, -1, 1.5, NaN, "480"]) assert.throws(() => chunkSamples(samples, bad), RangeError);
  assert.throws(() => chunkSamples([1, 2], 1), TypeError);
});

// ---------------------------------------------------------------- PKCE and OAuth

test("randomToken is base64url of the requested size and varies", () => {
  const a = randomToken(32);
  assert.equal(a.length, 43);
  assert.match(a, /^[A-Za-z0-9_-]+$/);
  assert.notEqual(randomToken(32), a);
  assert.throws(() => randomToken(0), RangeError);
  assert.throws(() => randomToken(97), RangeError);
});

test("createPkce returns an RFC 7636 verifier and its independent SHA-256 challenge", async () => {
  const seen = new Set();
  for (let i = 0; i < 20; i += 1) {
    const { verifier, challenge } = await createPkce();
    assert.ok(verifier.length >= 43 && verifier.length <= 128, `length ${verifier.length}`);
    assert.match(verifier, UNRESERVED);
    assert.ok(isPkceVerifier(verifier));
    const expected = createHash("sha256").update(verifier, "ascii").digest("base64url");
    assert.equal(challenge, expected);
    assert.equal(challenge.length, 43);
    assert.ok(!challenge.includes("="));
    assert.match(challenge, /^[A-Za-z0-9_-]+$/);
    seen.add(verifier);
  }
  assert.equal(seen.size, 20);
});

test("pkceChallenge matches the RFC 7636 appendix B example and rejects bad verifiers", async () => {
  assert.equal(
    await pkceChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"),
    "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
  );
  await assert.rejects(pkceChallenge("too-short"), RangeError);
  await assert.rejects(pkceChallenge("a".repeat(129)), RangeError);
  await assert.rejects(pkceChallenge(`${"a".repeat(43)}+`), RangeError);
  assert.equal(isPkceVerifier("a".repeat(42)), false);
  assert.equal(isPkceVerifier("a".repeat(128)), true);
  assert.equal(isPkceVerifier("~._-".repeat(11)), true);
});

test("validateConfig requires tenantId, clientId and scope", () => {
  assert.deepEqual(validateConfig({ ...config, extra: 1, clientId: " voice-client-example " }), config);
  for (const key of ["tenantId", "clientId", "scope"]) {
    assert.throws(() => validateConfig({ ...config, [key]: "" }), TypeError, key);
    assert.throws(() => validateConfig({ ...config, [key]: undefined }), TypeError, key);
  }
  assert.throws(() => validateConfig(null), TypeError);
  assert.throws(() => validateConfig({ ...config, tenantId: "../common" }), TypeError);
});

test("scopeString adds openid and profile once", () => {
  assert.equal(scopeString(config), "api://voice.example.com/Voice.Talk openid profile");
  assert.equal(scopeString({ ...config, scope: "openid  api://voice.example.com/Voice.Talk" }),
    "openid api://voice.example.com/Voice.Talk profile");
});

test("authorizeUrl builds the Entra v2 authorize request", () => {
  const href = authorizeUrl(config, { challenge: "challenge-value", state: "state-value", redirectUri: PAGE });
  const url = new URL(href);
  assert.equal(url.origin, "https://login.microsoftonline.com");
  assert.equal(url.pathname, "/tenant.example.com/oauth2/v2.0/authorize");
  assert.equal(url.hash, "");
  const p = url.searchParams;
  assert.deepEqual(Object.fromEntries(p), {
    client_id: "voice-client-example",
    response_type: "code",
    redirect_uri: PAGE,
    scope: "api://voice.example.com/Voice.Talk openid profile",
    code_challenge: "challenge-value",
    code_challenge_method: "S256",
    state: "state-value",
    response_mode: "query",
    prompt: "select_account",
  });
  assert.ok(!p.has("client_secret"));
  assert.throws(() => authorizeUrl(config, { challenge: "", state: "s", redirectUri: PAGE }), TypeError);
  assert.throws(() => authorizeUrl({ ...config, clientId: "" }, { challenge: "c", state: "s", redirectUri: PAGE }), TypeError);
});

test("tokenUrl and tokenRequestBody describe the code redemption", () => {
  assert.equal(tokenUrl(config), "https://login.microsoftonline.com/tenant.example.com/oauth2/v2.0/token");
  const body = tokenRequestBody(config, { code: "code-value", verifier: "verifier-value", redirectUri: PAGE });
  assert.ok(body instanceof URLSearchParams);
  assert.deepEqual(Object.fromEntries(body), {
    grant_type: "authorization_code",
    client_id: "voice-client-example",
    code: "code-value",
    redirect_uri: PAGE,
    code_verifier: "verifier-value",
    scope: "api://voice.example.com/Voice.Talk openid profile",
  });
  assert.throws(() => tokenRequestBody(config, { code: "", verifier: "v", redirectUri: PAGE }), TypeError);
});

test("parseRedirect reads a code response", () => {
  assert.deepEqual(parseRedirect(`${PAGE}?code=abc.def-ghi&state=xyz&session_state=s1`), {
    code: "abc.def-ghi",
    state: "xyz",
    error: null,
    errorDescription: null,
  });
});

test("parseRedirect reads an error response", () => {
  const url = `${PAGE}?error=access_denied&error_description=AADSTS65004%3A+User+declined+to+consent.&state=xyz`;
  assert.deepEqual(parseRedirect(url), {
    code: null,
    state: "xyz",
    error: "access_denied",
    errorDescription: "AADSTS65004: User declined to consent.",
  });
});

test("parseRedirect returns nulls for a plain page URL", () => {
  assert.deepEqual(parseRedirect(`${PAGE}#code=not-from-query`), {
    code: null, state: null, error: null, errorDescription: null,
  });
});

test("redirectUriFor drops query and fragment but keeps the prefix path", () => {
  assert.equal(redirectUriFor(`${PAGE}?code=abc&state=xyz#frag`), PAGE);
  assert.equal(redirectUriFor("https://margo.example.com/i/abc123/voice/index.html?x=1"),
    "https://margo.example.com/i/abc123/voice/index.html");
});

test("tokenFromResponse keeps only the access token and its expiry", () => {
  const now = 1_700_000_000_000;
  assert.deepEqual(tokenFromResponse({ access_token: "at", expires_in: 3599, id_token: "x" }, now), {
    accessToken: "at",
    expiresAt: now + 3599 * 1000,
  });
  assert.equal(tokenFromResponse({ access_token: "at", expires_in: "bad" }, now).expiresAt, now + 300 * 1000);
  assert.throws(() => tokenFromResponse({ id_token: "x" }), Error);
  assert.throws(() => tokenFromResponse(null), Error);
});

test("accountLabelFromIdToken reads display claims and tolerates junk", () => {
  const enc = (obj) => Buffer.from(JSON.stringify(obj)).toString("base64url");
  const jwt = (claims) => `${enc({ alg: "none" })}.${enc(claims)}.sig`;
  assert.equal(accountLabelFromIdToken(jwt({ name: "Dana Example", preferred_username: "dana@example.com" })),
    "Dana Example (dana@example.com)");
  assert.equal(accountLabelFromIdToken(jwt({ preferred_username: "dana@example.com" })), "dana@example.com");
  assert.equal(accountLabelFromIdToken(jwt({ name: "Dana Example" })), "Dana Example");
  assert.equal(accountLabelFromIdToken(jwt({})), null);
  assert.equal(accountLabelFromIdToken("not-a-jwt"), null);
  assert.equal(accountLabelFromIdToken("a.!!!.c"), null);
  assert.equal(accountLabelFromIdToken(undefined), null);
});

// ---------------------------------------------------------------- WebSocket protocol

test("wsUrlFor resolves ws relative to the page and switches scheme", () => {
  assert.equal(wsUrlFor(`${PAGE}?code=abc#x`), "wss://margo.example.com/i/abc123/voice/ws");
  assert.equal(wsUrlFor("http://localhost:8080/voice/"), "ws://localhost:8080/voice/ws");
  assert.equal(wsUrlFor("https://margo.example.com/i/abc123/voice/index.html"),
    "wss://margo.example.com/i/abc123/voice/ws");
  assert.throws(() => wsUrlFor("file:///tmp/voice/index.html"), TypeError);
});

test("client messages are the documented JSON frames", () => {
  assert.deepEqual(JSON.parse(helloMessage("token-value")), { type: "hello", token: "token-value" });
  assert.throws(() => helloMessage(""), TypeError);
  const pcm = Int16Array.from([1, -2, 3]);
  assert.deepEqual(JSON.parse(audioMessage(pcm)), { type: "audio", audio: pcm16ToBase64(pcm) });
  assert.deepEqual(JSON.parse(stopMessage()), { type: "stop" });
});

test("parseServerMessage normalizes every server message type", () => {
  const p = (obj) => parseServerMessage(JSON.stringify(obj));
  assert.deepEqual(p({ type: "ready", disclosure: "AI voice.", voice: "alloy-example" }),
    { type: "ready", disclosure: "AI voice.", voice: "alloy-example" });
  assert.deepEqual(p({ type: "ready" }), { type: "ready", disclosure: "", voice: "" });
  assert.deepEqual(p({ type: "audio", audio: "AQA=" }), { type: "audio", audio: "AQA=" });
  assert.deepEqual(p({ type: "interrupt" }), { type: "interrupt" });
  assert.deepEqual(p({ type: "caption", who: "you", text: "Hello" }), { type: "caption", who: "you", text: "Hello" });
  assert.deepEqual(p({ type: "caption", who: "margo", text: "Hi" }), { type: "caption", who: "margo", text: "Hi" });
  assert.deepEqual(p({ type: "status", text: "" }), { type: "status", text: "" });
  assert.deepEqual(p({ type: "status" }), { type: "status", text: "" });
  assert.deepEqual(p({ type: "error", message: "Busy", fatal: true }), { type: "error", message: "Busy", fatal: true });
  assert.deepEqual(p({ type: "error", message: "Busy", fatal: "yes" }), { type: "error", message: "Busy", fatal: false });
  assert.deepEqual(p({ type: "error" }), { type: "error", message: "Something went wrong.", fatal: false });
  assert.deepEqual(p({ type: "closed", reason: "Idle" }), { type: "closed", reason: "Idle" });
  assert.deepEqual(p({ type: "closed" }), { type: "closed", reason: "" });
});

test("parseServerMessage ignores malformed, unknown and oversized frames", () => {
  const p = (obj) => parseServerMessage(JSON.stringify(obj));
  assert.equal(parseServerMessage("not json"), null);
  assert.equal(parseServerMessage(new ArrayBuffer(4)), null);
  assert.equal(p(null), null);
  assert.equal(p([1, 2]), null);
  assert.equal(p({ type: "approve" }), null);
  assert.equal(p({ type: "caption", who: "system", text: "x" }), null);
  assert.equal(p({ type: "caption", who: "you" }), null);
  assert.equal(p({ type: "audio" }), null);
  assert.equal(p({ type: "audio", audio: "A".repeat(MAX_AUDIO_BASE64 + 4) }), null);
  const long = p({ type: "caption", who: "margo", text: "x".repeat(MAX_TEXT + 50) });
  assert.equal(long.text.length, MAX_TEXT + 1);
});
