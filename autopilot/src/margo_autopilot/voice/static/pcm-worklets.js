// AudioWorklet processors for the "Talk to Margo" voice page.
//
// Loaded with audioContext.audioWorklet.addModule("pcm-worklets.js") on a context
// running at 24 kHz. Runs on the audio rendering thread: no DOM, no network, no
// logging. Conversion to PCM16 and base64 happens on the main thread (voice-core.mjs).

const CAPTURE_FRAME = 480; // 20 ms at 24 kHz
const PLAYER_MAX_QUEUED = 24000 * 60; // at most 60 s of audio waiting to play

// Collects mono Float32 input and posts Float32Array chunks of exactly 480 samples.
// Render quanta are 128 samples, so input accumulates across calls. Inputs with
// more than one channel are averaged to mono. Port message {type:"stop"} ends it.
class PcmCaptureProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.frame = new Float32Array(CAPTURE_FRAME);
    this.filled = 0;
    this.stopped = false;
    this.port.onmessage = (event) => {
      if (event.data && event.data.type === "stop") this.stopped = true;
    };
  }

  process(inputs, outputs) {
    // Any outputs carry silence; the node is wired to the destination only so
    // every browser keeps pulling it.
    for (const output of outputs) for (const channel of output) channel.fill(0);
    if (this.stopped) return false;
    const input = inputs[0];
    if (!input || input.length === 0) return true; // no source connected yet
    const channels = input.length;
    const length = input[0].length;
    for (let i = 0; i < length; i += 1) {
      let sample = input[0][i];
      if (channels > 1) {
        for (let c = 1; c < channels; c += 1) sample += input[c][i];
        sample /= channels;
      }
      this.frame[this.filled] = sample;
      this.filled += 1;
      if (this.filled === CAPTURE_FRAME) {
        const chunk = this.frame;
        this.port.postMessage(chunk, [chunk.buffer]);
        this.frame = new Float32Array(CAPTURE_FRAME);
        this.filled = 0;
      }
    }
    return true;
  }
}

// Plays Float32Array chunks posted to its port, in order, from a queue.
// {type:"clear"} drops everything queued, including the chunk mid-play, so the
// next render quantum is silent (barge-in). Outputs silence when the queue is empty.
// Chunks that would push the queue past PLAYER_MAX_QUEUED are dropped and reported
// once per overflow as {type:"overflow"}.
class PcmPlayerProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.queue = [];
    this.current = null;
    this.offset = 0;
    this.queued = 0;
    this.overflowing = false;
    this.port.onmessage = (event) => this.receive(event.data);
  }

  receive(data) {
    if (Object.prototype.toString.call(data) === "[object Float32Array]") {
      if (data.length === 0) return;
      if (this.queued + data.length > PLAYER_MAX_QUEUED) {
        if (!this.overflowing) this.port.postMessage({ type: "overflow" });
        this.overflowing = true;
        return;
      }
      this.overflowing = false;
      this.queue.push(data);
      this.queued += data.length;
    } else if (data && data.type === "clear") {
      this.queue = [];
      this.current = null;
      this.offset = 0;
      this.queued = 0;
      this.overflowing = false;
    }
  }

  process(_inputs, outputs) {
    const output = outputs[0];
    if (!output || output.length === 0) return true;
    const out = output[0];
    let written = 0;
    while (written < out.length) {
      if (!this.current) {
        if (this.queue.length === 0) break;
        this.current = this.queue.shift();
        this.offset = 0;
      }
      const n = Math.min(out.length - written, this.current.length - this.offset);
      out.set(this.current.subarray(this.offset, this.offset + n), written);
      written += n;
      this.offset += n;
      this.queued -= n;
      if (this.offset >= this.current.length) this.current = null;
    }
    out.fill(0, written);
    for (let c = 1; c < output.length; c += 1) output[c].set(out);
    return true;
  }
}

registerProcessor("pcm-capture", PcmCaptureProcessor);
registerProcessor("pcm-player", PcmPlayerProcessor);
