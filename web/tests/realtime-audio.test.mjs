import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const source = readFileSync(new URL("../public/realtime-audio.js", import.meta.url), "utf8");

function processor(rate) {
  const messages = [];
  let instance;
  const context = vm.createContext({
    sampleRate: rate, currentTime: 0,
    AudioWorkletProcessor: class {
      port = {
        onmessage: undefined,
        postMessage(message) {
          messages.push(message);
          if (message.type === "pcm") instance.port.onmessage({ data: { type: "recycle", buffer: message.buffer } });
        }
      };
    },
    registerProcessor(_name, type) { instance = new type(); }
  });
  new vm.Script(source).runInContext(context, { timeout: 2000 });
  return { instance, messages, context };
}

for (const rate of [44100, 48000]) {
  test(`capture preserves frame count, level and timestamps at ${rate} Hz`, { timeout: 3000 }, () => {
    const { instance, messages, context } = processor(rate);
    instance.port.onmessage({ data: { type: "capture", enabled: true } });
    const deadline = performance.now() + 2000;
    for (let block = 0; block < Math.ceil(rate / 128); block++) {
      assert.ok(performance.now() < deadline);
      const count = Math.min(128, rate - block * 128);
      context.currentTime = block * 128 / rate;
      instance.process([[new Float32Array(count).fill(0.25)]], [[new Float32Array(count)]]);
    }
    const frames = messages.filter(message => message.type === "pcm");
    assert.equal(frames.length, 50);
    assert.equal(frames[0].timestamp, 0);
    assert.equal(frames[49].timestamp, 980000);
    assert.equal(new Int16Array(frames[0].buffer)[0], 8192);
    assert.equal(messages.filter(message => message.type === "error").length, 0);
  });

  test(`playback acknowledges rendered boundaries and clears cancelled audio at ${rate} Hz`, { timeout: 3000 }, () => {
    const { instance, messages, context } = processor(rate);
    instance.port.onmessage({ data: { type: "clear", response: "response", epoch: 3 } });
    for (let sequence = 1; sequence <= 2; sequence++) {
      const pcm = new Int16Array(2400).fill(8192);
      instance.port.onmessage({ data: { type: "audio", response: "response", buffer: pcm.buffer, sequence, timestamp: sequence * 100000 } });
    }
    instance.port.onmessage({ data: { type: "end" } });
    let peak = 0;
    const deadline = performance.now() + 2000;
    for (let block = 0; block < 120; block++) {
      assert.ok(performance.now() < deadline);
      context.currentTime = block * 128 / rate;
      const output = new Float32Array(128);
      instance.process([], [[output]]);
      peak = Math.max(peak, ...output);
    }
    assert.equal(peak, 0.25);
    const played = messages.filter(message => message.type === "played");
    assert.deepEqual(played.map(message => message.sequence), [1, 2]);
    assert.equal(played[1].timestamp, 200000);
    instance.port.onmessage({ data: { type: "clear" } });
    const output = new Float32Array(128);
    instance.process([], [[output]]);
    assert.ok(output.every(sample => sample === 0));
  });
}
