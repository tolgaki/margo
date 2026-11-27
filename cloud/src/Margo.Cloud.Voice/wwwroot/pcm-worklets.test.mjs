// Drives the AudioWorklet processors in a vm sandbox with stand-ins for
// AudioWorkletProcessor and registerProcessor, one 128-sample render quantum at a time.
import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";

const QUANTUM = 128;
const source = await readFile(new URL("./pcm-worklets.js", import.meta.url), "utf8");

function load() {
  const processors = new Map();
  class AudioWorkletProcessor {
    constructor() {
      this.port = { onmessage: null, posted: [], postMessage(data) { this.posted.push(data); } };
    }
  }
  const context = vm.createContext({
    AudioWorkletProcessor,
    registerProcessor: (name, ctor) => processors.set(name, ctor),
  });
  vm.runInContext(source, context, { filename: "pcm-worklets.js" });
  return processors;
}

function create(name) {
  const Processor = load().get(name);
  assert.ok(Processor, `${name} is registered`);
  const node = new Processor();
  node.send = (data) => node.port.onmessage({ data });
  return node;
}

const ramp = (start, length) => Float32Array.from({ length }, (_, i) => (start + i) / 10000);
const play = (player) => {
  const out = [new Float32Array(QUANTUM)];
  assert.equal(player.process([], [out]), true);
  return out[0];
};

test("both processors are registered under their documented names", () => {
  assert.deepEqual([...load().keys()].sort(), ["pcm-capture", "pcm-player"]);
});

test("pcm-capture posts 480-sample Float32 chunks across render quanta", () => {
  const capture = create("pcm-capture");
  const all = ramp(0, QUANTUM * 8); // 1024 samples -> two chunks, 64 left over
  for (let q = 0; q < 8; q += 1) {
    const out = [new Float32Array(QUANTUM).fill(0.5)];
    assert.equal(capture.process([[all.subarray(q * QUANTUM, (q + 1) * QUANTUM)]], [out]), true);
    assert.ok(out[0].every((s) => s === 0), "capture outputs silence");
  }
  const chunks = capture.port.posted;
  assert.equal(chunks.length, 2);
  for (const chunk of chunks) {
    assert.equal(Object.prototype.toString.call(chunk), "[object Float32Array]");
    assert.equal(chunk.length, 480);
  }
  assert.deepEqual(Array.from(chunks[0]), Array.from(all.subarray(0, 480)));
  assert.deepEqual(Array.from(chunks[1]), Array.from(all.subarray(480, 960)));
  assert.notEqual(chunks[0].buffer, chunks[1].buffer);
});

test("pcm-capture averages multi-channel input to mono and tolerates no input", () => {
  const capture = create("pcm-capture");
  assert.equal(capture.process([[]], []), true);
  assert.equal(capture.process([], []), true);
  for (let q = 0; q < 4; q += 1) {
    capture.process([[new Float32Array(QUANTUM).fill(0.2), new Float32Array(QUANTUM).fill(0.6)]], []);
  }
  assert.equal(capture.port.posted.length, 1);
  assert.ok(capture.port.posted[0].every((s) => Math.abs(s - 0.4) < 1e-6));
});

test("pcm-capture stops on {type:'stop'}", () => {
  const capture = create("pcm-capture");
  capture.send({ type: "stop" });
  assert.equal(capture.process([[new Float32Array(QUANTUM)]], []), false);
});

test("pcm-player outputs silence when empty", () => {
  const player = create("pcm-player");
  assert.ok(play(player).every((s) => s === 0));
});

test("pcm-player plays queued chunks in order across quanta, then silence", () => {
  const player = create("pcm-player");
  const a = ramp(1, 200);
  const b = ramp(1000, 100);
  player.send(a);
  player.send(b);
  const heard = [...play(player), ...play(player), ...play(player)];
  assert.deepEqual(heard.slice(0, 300), [...a, ...b]);
  assert.ok(heard.slice(300).every((s) => s === 0));
});

test("pcm-player copies mono to every output channel", () => {
  const player = create("pcm-player");
  player.send(ramp(1, QUANTUM));
  const out = [new Float32Array(QUANTUM), new Float32Array(QUANTUM)];
  player.process([], [out]);
  assert.deepEqual(Array.from(out[1]), Array.from(out[0]));
  assert.ok(out[0][5] !== 0);
});

test("pcm-player {type:'clear'} drops the queue and the chunk mid-play", () => {
  const player = create("pcm-player");
  player.send(ramp(1, 300));
  player.send(ramp(1000, 300));
  play(player); // partway through the first chunk
  player.send({ type: "clear" });
  assert.ok(play(player).every((s) => s === 0), "silent right after clear");
  const next = ramp(5000, 10);
  player.send(next);
  assert.deepEqual(Array.from(play(player).subarray(0, 10)), Array.from(next));
});

test("pcm-player ignores junk and bounds its queue", () => {
  const player = create("pcm-player");
  player.send("junk");
  player.send(new Int16Array(10));
  player.send(new Float32Array(0));
  assert.ok(play(player).every((s) => s === 0));
  const minute = new Float32Array(24000 * 60).fill(0.1);
  player.send(minute);
  player.send(new Float32Array(10));
  player.send(new Float32Array(10));
  assert.equal(JSON.stringify(player.port.posted), '[{"type":"overflow"}]', "overflow reported once");
  player.send({ type: "clear" });
  player.send(new Float32Array(10).fill(0.3));
  assert.ok(Math.abs(play(player)[0] - 0.3) < 1e-6, "accepts audio again after clear");
});
