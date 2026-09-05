import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";
import { randomUUID } from "node:crypto";
import ts from "typescript";

const source = ts.transpileModule(readFileSync(new URL("../src/app/realtime.ts", import.meta.url), "utf8"), {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS }
}).outputText;

function client(overrides = {}) {
  const timers = new Map();
  const sockets = [];
  const snapshots = [];
  const audio = [];
  let nextTimer = 0;
  class Socket {
    static OPEN = 1;
    static CLOSED = 3;
    readyState = 0;
    bufferedAmount = 0;
    sent = [];
    constructor() { sockets.push(this); }
    send(value) { this.sent.push(JSON.parse(value)); }
    close(code = 1000) { this.readyState = 3; this.onclose?.({ code }); }
    open() { this.readyState = 1; this.onopen?.(); }
    event(value) { this.onmessage?.({ data: JSON.stringify(value) }); }
  }
  const schedule = (callback, delay) => {
    const id = ++nextTimer;
    timers.set(id, { callback, delay });
    return id;
  };
  const context = vm.createContext({
    exports: {}, crypto: { randomUUID }, performance, URL, WebSocket: Socket,
    AbortController, AbortSignal, DOMException, ArrayBuffer, Uint8Array, DataView,
    location: { href: "http://127.0.0.1:6180/", protocol: "http:" },
    window: { AudioWorkletNode: class {}, addEventListener() {}, removeEventListener() {} },
    fetch: async () => ({ ok: true, json: async () => ({ ticket: "test-only" }) }),
    setInterval: schedule, setTimeout: schedule,
    clearInterval: id => timers.delete(id), clearTimeout: id => timers.delete(id),
    ...overrides
  });
  new vm.Script(source).runInContext(context, { timeout: 2000 });
  const session = new context.exports.RealtimeVoiceSession({
    conversationId: "conversation", model: "local-model", mode: "half_duplex"
  }, snapshot => snapshots.push(snapshot), () => undefined);
  session.node = { port: { postMessage: value => audio.push(value), close() {} }, disconnect() {} };
  return { session, sockets, snapshots, audio, timers };
}

async function connected(harness) {
  await harness.session.connect(true);
  const socket = harness.sockets.at(-1);
  socket.open();
  socket.event({ type: "session.created", sequence: 1 });
  socket.event({ type: "session.updated", sequence: 2 });
  return socket;
}

function outputFrame(id) {
  const buffer = new ArrayBuffer(684);
  const view = new DataView(buffer);
  view.setUint32(0, 0x544d5231, false);
  view.setUint8(4, 1);
  view.setUint8(5, 2);
  for (let i = 0; i < 16; i++) view.setUint8(8 + i, parseInt(id.slice(i * 2, i * 2 + 2), 16));
  view.setBigUint64(24, 1n, true);
  view.setUint32(40, 640, true);
  return buffer;
}

test("display acknowledgements never split a streamed surrogate pair", { timeout: 3000 }, async () => {
  const harness = client();
  try {
    const socket = await connected(harness);
    socket.event({ type: "response.created", sequence: 3, response_epoch: 1, response_id: "1".repeat(32), item_id: "item" });
    socket.event({ type: "response.text.delta", sequence: 4, response_epoch: 1, delta: "hello\ud83d" });
    harness.session.displayed("hello\ud83d");
    assert.equal(socket.sent.at(-1).character_count, 5);
    socket.event({ type: "response.text.delta", sequence: 5, response_epoch: 1, delta: "\ude42" });
    harness.session.displayed("hello\ud83d\ude42");
    assert.equal(socket.sent.at(-1).character_count, 7);
  } finally { harness.session.stop(); }
});

test("late cancelled audio is discarded and the next response starts a fresh timeline", { timeout: 3000 }, async () => {
  const harness = client();
  try {
    const socket = await connected(harness);
    const oldId = "1".repeat(32);
    const newId = "2".repeat(32);
    socket.event({ type: "response.created", sequence: 3, response_epoch: 1, response_id: oldId, item_id: "old" });
    harness.session.cancel();
    socket.event({ type: "response.cancelled", sequence: 4, response_epoch: 1 });
    socket.onmessage({ data: outputFrame(oldId) });
    assert.equal(harness.audio.filter(value => value.type === "audio").length, 0);
    socket.event({ type: "response.created", sequence: 5, response_epoch: 2, response_id: newId, item_id: "new" });
    socket.onmessage({ data: outputFrame(newId) });
    assert.equal(harness.audio.filter(value => value.type === "audio").length, 1);
    assert.equal(harness.audio.at(-1).sequence, 1);
  } finally { harness.session.stop(); }
});

test("abnormal disconnects stop after three scheduled reconnects", { timeout: 3000 }, async () => {
  const harness = client();
  const deadline = performance.now() + 2000;
  try {
    let socket = await connected(harness);
    for (let attempt = 1; attempt <= 3; attempt++) {
      assert.ok(performance.now() < deadline);
      socket.close(1006);
      const timer = [...harness.timers.entries()].find(([, value]) => value.delay === attempt * 1500);
      assert.ok(timer);
      harness.timers.delete(timer[0]);
      timer[1].callback();
      await new Promise(setImmediate);
      socket = harness.sockets.at(-1);
      socket.open();
      socket.event({ type: "session.created", sequence: 1 });
      socket.event({ type: "session.updated", sequence: 2 });
    }
    socket.close(1006);
    assert.equal(harness.snapshots.at(-1).state, "failed");
    assert.equal(harness.sockets.length, 4);
    assert.equal(harness.timers.size, 0);
  } finally { harness.session.stop(); }
});

test("microphone granted after stop is released without opening a socket", { timeout: 3000 }, async () => {
  let grant;
  let stoppedTracks = 0;
  const permission = new Promise(resolve => { grant = resolve; });
  const harness = client({
    navigator: { mediaDevices: { getUserMedia: () => permission } },
    AudioContext: class { resume() { return Promise.resolve(); } close() { return Promise.resolve(); } }
  });
  try {
    const startup = harness.session.start();
    await new Promise(setImmediate);
    harness.session.stop();
    grant({ getTracks: () => [{ stop: () => stoppedTracks++ }] });
    await startup;
    await new Promise(setImmediate);
    assert.equal(stoppedTracks, 1);
    assert.equal(harness.sockets.length, 0);
    assert.equal(harness.timers.size, 0);
  } finally { harness.session.stop(); }
});

test("transport send failure cannot skip track and timer cleanup", { timeout: 3000 }, async () => {
  const harness = client();
  let released = 0;
  try {
    const socket = await connected(harness);
    harness.session.stream = { getTracks: () => [{ stop: () => released++ }] };
    socket.send = () => { throw new Error("transport unavailable"); };
    harness.session.stop();
    assert.equal(released, 1);
    assert.equal(harness.timers.size, 0);
    assert.equal(harness.snapshots.at(-1).state, "closed");
  } finally { harness.session.stop(); }
});
