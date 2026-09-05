export type VoiceState = "closed" | "connecting" | "listening" | "user_speaking" | "transcribing" |
  "thinking" | "speaking" | "interrupted" | "reconnecting" | "failed";

export interface VoiceSnapshot {
  state: VoiceState;
  transcript: string;
  text: string;
  level: number;
  muted: boolean;
  error?: string;
  underruns: number;
}

interface VoiceOptions {
  conversationId: string;
  model: string;
  ttsModel?: string;
  language?: string;
  deviceId?: string;
  outputDeviceId?: string;
  mode: "half_duplex" | "duplex_experimental" | "manual";
}

interface ServerEvent {
  type: string;
  sequence: number;
  state?: VoiceState;
  response_epoch?: number;
  response_id?: string;
  item_id?: string;
  utterance_id?: string;
  text?: string;
  delta?: string;
  message?: string;
  code?: string;
  fatal?: boolean;
}

export class RealtimeVoiceSession {
  private socket?: WebSocket;
  private context?: AudioContext;
  private stream?: MediaStream;
  private node?: AudioWorkletNode;
  private source?: MediaStreamAudioSourceNode;
  private filter?: BiquadFilterNode;
  private controller = new AbortController();
  private heartbeat?: ReturnType<typeof setInterval>;
  private reconnectTimer?: ReturnType<typeof setTimeout>;
  private deadlineTimer?: ReturnType<typeof setTimeout>;
  private handshakeTimer?: ReturnType<typeof setTimeout>;
  private sequence = 0;
  private serverSequence = 0;
  private audioSequence = 0;
  private outputSequence = 0;
  private outputTimestamp = 0;
  private captureId = crypto.randomUUID().replaceAll("-", "");
  private responseId = "";
  private itemId = "";
  private utteranceId = "";
  private epoch = 0;
  private acknowledgedCharacters = 0;
  private reconnects = 0;
  private stopped = false;
  private ready = false;
  private started = performance.now();
  private awaitingPong = 0;
  private heartbeatTicks = 0;
  private snapshot: VoiceSnapshot = { state: "connecting", transcript: "", text: "", level: 0, muted: false, underruns: 0 };

  constructor(private options: VoiceOptions, private changed: (snapshot: VoiceSnapshot) => void,
    private committed: () => void) {}

  private update(value: Partial<VoiceSnapshot>) {
    this.snapshot = { ...this.snapshot, ...value };
    this.changed(this.snapshot);
  }

  async start() {
    try {
      this.deadlineTimer = setTimeout(() => this.fail("语音会话已达到 15 分钟上限。"), 15 * 60 * 1000);
      if (!navigator.mediaDevices?.getUserMedia || !window.AudioWorkletNode) throw new Error("当前浏览器不支持连续语音采集，请使用录音入口。");
      this.context = new AudioContext({ latencyHint: "interactive" });
      await this.bounded(this.context.resume(), 10_000);
      const request = navigator.mediaDevices.getUserMedia({ audio: {
        channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true,
        deviceId: this.options.deviceId ? { exact: this.options.deviceId } : undefined
      } });
      void request.then(stream => { if (this.stopped) stream.getTracks().forEach(track => track.stop()); }, () => undefined);
      const stream = await this.bounded(request, 60_000);
      if (this.stopped) { stream.getTracks().forEach(track => track.stop()); return; }
      this.stream = stream;
      for (const track of stream.getTracks()) track.onended = () => this.fail("麦克风连接已结束。");
      const aec = stream.getAudioTracks()[0].getSettings().echoCancellation === true;
      if (this.options.mode === "duplex_experimental" && !aec) throw new Error("当前设备未启用回声消除，请选择半双工。");
      const context = this.context;
      if (this.options.outputDeviceId) {
        const output = context as AudioContext & { setSinkId?: (id: string) => Promise<void> };
        if (!output.setSinkId) throw new Error("当前浏览器不支持选择播放设备。");
        await this.bounded(output.setSinkId(this.options.outputDeviceId), 10_000);
      }
      await this.bounded(context.audioWorklet.addModule("/realtime-audio.js"), 10_000);
      if (this.stopped) return;
      this.node = new AudioWorkletNode(context, "voice-audio", { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1] });
      this.source = context.createMediaStreamSource(stream);
      this.filter = context.createBiquadFilter();
      this.filter.type = "lowpass";
      this.filter.frequency.value = 7000;
      this.filter.Q.value = 0.707;
      this.source.connect(this.filter).connect(this.node).connect(context.destination);
      this.node.port.onmessage = ({ data }) => this.onAudio(data);
      this.node.onprocessorerror = () => this.fail("音频处理器已停止。");
      context.onstatechange = () => {
        if (this.ready && context.state !== "running") this.fail("音频设备已暂停，请重新连接。");
      };
      window.addEventListener("pagehide", this.onPageHide);
      await this.connect(aec);
    } catch (error) {
      if (!this.stopped) this.fail(error instanceof Error ? error.message : "语音会话连接失败。");
    }
  }

  private bounded<T>(operation: Promise<T>, timeoutMs: number): Promise<T> {
    const signal = this.controller.signal;
    return new Promise<T>((resolve, reject) => {
      const finish = () => { clearTimeout(timer); signal.removeEventListener("abort", abort); };
      const abort = () => { finish(); reject(new DOMException("Voice session cancelled", "AbortError")); };
      const timer = setTimeout(() => { finish(); reject(new Error("音频设备准备超时。")); }, timeoutMs);
      signal.addEventListener("abort", abort, { once: true });
      if (signal.aborted) abort();
      operation.then(value => { finish(); resolve(value); }, error => { finish(); reject(error); });
    });
  }

  private async connect(aec: boolean) {
    this.ready = false;
    this.sequence = this.serverSequence = this.audioSequence = 0;
    this.epoch = this.outputSequence = this.outputTimestamp = this.acknowledgedCharacters = 0;
    this.responseId = this.itemId = this.utteranceId = "";
    this.awaitingPong = 0;
    this.heartbeatTicks = 0;
    this.captureId = crypto.randomUUID().replaceAll("-", "");
    const ticketResponse = await fetch("/api/realtime/tickets", {
      method: "POST", credentials: "same-origin", signal: AbortSignal.any([this.controller.signal, AbortSignal.timeout(5000)])
    });
    const ticket = await ticketResponse.json();
    if (!ticketResponse.ok) throw new Error(ticket.message ?? "语音认证失败。");
    if (this.stopped) return;
    const url = new URL("/api/realtime/v1", location.href);
    url.protocol = location.protocol === "https:" ? "wss:" : "ws:";
    const socket = new WebSocket(url, "tomur.realtime.v1");
    this.socket = socket;
    socket.binaryType = "arraybuffer";
    this.handshakeTimer = setTimeout(() => this.fail("语音模型准备超时。"), 90_000);
    socket.onopen = () => { if (socket === this.socket && !this.stopped) this.send("session.authenticate", { ticket: ticket.ticket }); };
    socket.onmessage = ({ data }) => {
      if (socket !== this.socket || this.stopped) return;
      try {
        if (data instanceof ArrayBuffer) this.onBinary(data);
        else this.onEvent(JSON.parse(data) as ServerEvent, aec);
      } catch (error) { this.fail(error instanceof Error ? error.message : "语音协议错误。"); }
    };
    socket.onerror = () => { if (socket === this.socket && !this.stopped) this.update({ error: "语音连接中断。" }); };
    socket.onclose = ({ code }) => {
      if (socket !== this.socket) return;
      clearInterval(this.heartbeat);
      clearTimeout(this.handshakeTimer);
      this.ready = false;
      this.node?.port.postMessage({ type: "capture", enabled: false });
      this.clearPlayback();
      if (this.stopped) { this.committed(); return; }
      if (code !== 1006 && code !== 1011) { this.fail("语音连接已关闭，请重新连接。"); return; }
      if (++this.reconnects > 3 || performance.now() - this.started >= 15 * 60 * 1000) {
        this.fail("语音重连次数已用尽。"); return;
      }
      this.update({ state: "reconnecting", transcript: "", text: "" });
      this.reconnectTimer = setTimeout(() => {
        this.committed();
        void this.connect(aec).catch(error => this.fail(error instanceof Error ? error.message : "语音重连失败。"));
      }, this.reconnects * 1500);
    };
  }

  private send(type: string, fields: Record<string, unknown> = {}) {
    const socket = this.socket;
    if (!socket || socket.readyState !== WebSocket.OPEN || this.stopped) return;
    if (socket.bufferedAmount > 128 * 1024) { this.fail("语音发送缓冲区已满。"); return; }
    try {
      socket.send(JSON.stringify({ type, event_id: crypto.randomUUID(), sequence: ++this.sequence,
        timestamp_us: Math.floor(performance.now() * 1000), ...fields }));
    } catch { this.fail("语音连接已断开。"); }
  }

  private onEvent(event: ServerEvent, aec: boolean) {
    if (event.sequence !== ++this.serverSequence) throw new Error("语音控制事件丢失或乱序。");
    if (event.type === "session.created") {
      this.send("session.update", { session: {
        conversation_id: this.options.conversationId, model: this.options.model,
        tts_model: this.options.ttsModel || null, language: this.options.language || null,
        turn_detection: this.options.mode === "manual" ? "manual" : "server_vad",
        mode: this.options.mode === "duplex_experimental" ? "duplex_experimental" : "half_duplex",
        echo_cancellation: aec, input_audio_format: "pcm16le", input_sample_rate: 16000,
        input_channels: 1, input_frame_duration_ms: 20, output_audio_format: "pcm16le",
        output_sample_rate: 24000, output_channels: 1
      } });
      this.heartbeat = setInterval(() => {
        if (++this.heartbeatTicks > 90 || performance.now() - this.started >= 15 * 60 * 1000) { this.fail("语音会话已超时。"); return; }
        if (this.awaitingPong && performance.now() - this.awaitingPong > 20_000 && this.ready) {
          this.fail("语音服务器心跳超时。"); return;
        }
        if (!this.awaitingPong) this.awaitingPong = performance.now();
        this.send("session.ping");
      }, 10_000);
    } else if (event.type === "session.updated") {
      clearTimeout(this.handshakeTimer);
      this.ready = true;
      this.setMuted(this.options.mode === "manual");
      this.update({ state: "listening", error: undefined });
    } else if (event.type === "session.pong") {
      this.awaitingPong = 0;
    } else if (event.type === "input_audio_buffer.speech_started") {
      this.utteranceId = event.utterance_id ?? "";
      this.clearPlayback();
      this.update({ transcript: "", text: "", state: "user_speaking" });
    } else if (event.type === "input_audio_transcription.delta" || event.type === "input_audio_transcription.done") {
      if (event.utterance_id === this.utteranceId) this.update({ transcript: event.text ?? "" });
    } else if (event.type === "response.created") {
      this.epoch = event.response_epoch!;
      this.responseId = event.response_id!;
      this.itemId = event.item_id!;
      this.outputSequence = this.outputTimestamp = this.acknowledgedCharacters = 0;
      this.node?.port.postMessage({ type: "clear", response: this.responseId, epoch: this.epoch });
      this.update({ text: "" });
    } else if (event.type === "response.text.delta" && event.response_epoch === this.epoch) {
      const text = this.snapshot.text + (event.delta ?? "");
      if (text.length > 8192) throw new Error("语音回复超出长度上限。");
      this.update({ text });
    } else if (event.type === "response.cancelled") {
      if (event.response_epoch === this.epoch) this.clearPlayback();
      this.update({ state: "interrupted" });
    } else if (event.type === "response.audio.done" && event.response_epoch === this.epoch) {
      this.node?.port.postMessage({ type: "end" });
    } else if (event.type === "response.done" && event.response_epoch === this.epoch) {
      this.committed();
      this.update({ transcript: "", text: "" });
    } else if (event.type === "error") {
      if (event.fatal || !this.ready) this.fail(event.message ?? event.code ?? "语音会话失败。");
      else { this.clearPlayback(); this.update({ error: event.message ?? event.code }); }
    } else if (event.type === "session.state" && event.state) {
      this.update({ state: event.state });
    } else if (event.type === "session.closed") this.stop();
  }

  displayed(text: string) {
    if (!this.ready || !this.epoch || text !== this.snapshot.text) return;
    let count = text.length;
    const last = text.charCodeAt(count - 1);
    if (last >= 0xd800 && last <= 0xdbff) count--;
    if (count <= this.acknowledgedCharacters) return;
    this.acknowledgedCharacters = count;
    this.send("response.text.displayed", { response_epoch: this.epoch, item_id: this.itemId, character_count: count });
  }

  private onBinary(frame: ArrayBuffer) {
    if (frame.byteLength < 44 || frame.byteLength > 4844) throw new Error("输出语音帧大小无效。");
    const view = new DataView(frame);
    if (view.getUint32(0, false) !== 0x544d5231 || view.getUint8(4) !== 1 || view.getUint8(5) !== 2 || view.getUint16(6, true) !== 0)
      throw new Error("输出语音帧头无效。");
    const id = Array.from(new Uint8Array(frame, 8, 16), b => b.toString(16).padStart(2, "0")).join("");
    if (id !== this.responseId) return;
    const sequence = Number(view.getBigUint64(24, true));
    const timestamp = Number(view.getBigInt64(32, true));
    const bytes = view.getUint32(40, true);
    if (!Number.isSafeInteger(sequence) || sequence !== ++this.outputSequence || timestamp !== this.outputTimestamp ||
      bytes < 2 || bytes % 2 !== 0 || bytes !== frame.byteLength - 44) throw new Error("输出语音存在缺帧或时间线错误。");
    this.outputTimestamp += Math.floor(bytes / 2 * 1000000 / 24000);
    const buffer = frame.slice(44);
    this.node?.port.postMessage({ type: "audio", buffer, response: id, sequence, timestamp: this.outputTimestamp }, [buffer]);
  }

  private onAudio(data: { type: string; buffer?: ArrayBuffer; timestamp?: number; level?: number; epoch?: number; sequence?: number; code?: string }) {
    if (data.type === "pcm" && data.buffer) {
      const buffer = data.buffer;
      if (this.ready && !this.snapshot.muted && this.socket?.readyState === WebSocket.OPEN) {
        if (this.socket.bufferedAmount > 128 * 1024) this.fail("语音发送缓冲区已满。");
        else {
          const frame = new ArrayBuffer(684);
          const view = new DataView(frame);
          view.setUint32(0, 0x544d5231, false); view.setUint8(4, 1); view.setUint8(5, 1);
          for (let i = 0; i < 16; i++) view.setUint8(8 + i, parseInt(this.captureId.slice(i * 2, i * 2 + 2), 16));
          view.setBigUint64(24, BigInt(++this.audioSequence), true);
          view.setBigInt64(32, BigInt(Math.floor(data.timestamp!)), true);
          view.setUint32(40, 640, true);
          new Uint8Array(frame, 44).set(new Uint8Array(buffer));
          try { this.socket.send(frame); } catch { this.fail("语音连接已断开。"); }
        }
      }
      this.node?.port.postMessage({ type: "recycle", buffer }, [buffer]);
    } else if (data.type === "played" && data.epoch === this.epoch) {
      this.send("response.audio.playback_consumed", { response_epoch: this.epoch,
        audio_sequence: data.sequence, played_through_timestamp_us: data.timestamp });
    } else if (data.type === "level") this.update({ level: data.level ?? 0 });
    else if (data.type === "underrun") this.update({ underruns: this.snapshot.underruns + 1, error: "播放缓冲不足，正在等待后续语音。" });
    else if (data.type === "error") this.fail(`音频管线已停止：${data.code}`);
  }

  setMuted(muted: boolean) {
    if (this.options.mode === "manual" && muted && !this.snapshot.muted && this.audioSequence > 0)
      this.send("input_audio_buffer.commit", { capture_stream_id: this.captureId });
    if (muted) {
      this.send("input_audio_buffer.clear", { capture_stream_id: this.audioSequence > 0 ? this.captureId : null });
      this.captureId = crypto.randomUUID().replaceAll("-", "");
      this.audioSequence = 0;
    }
    for (const track of this.stream?.getAudioTracks() ?? []) track.enabled = !muted;
    this.node?.port.postMessage({ type: "capture", enabled: this.ready && !muted });
    this.update({ muted });
  }

  cancel() { if (this.epoch) this.send("response.cancel", { response_epoch: this.epoch }); }

  private clearPlayback() {
    this.node?.port.postMessage({ type: "clear" });
    this.responseId = "";
  }

  private fail(error: string) {
    this.update({ state: "failed", error });
    this.stop(false);
  }

  private onPageHide = () => this.stop();

  stop(update = true) {
    if (this.stopped) return;
    this.stopped = true;
    try {
      if (this.socket?.readyState === WebSocket.OPEN && this.socket.bufferedAmount < 120 * 1024)
        this.socket.send(JSON.stringify({ type: "session.close", reason: "user_requested", event_id: crypto.randomUUID(),
          sequence: ++this.sequence, timestamp_us: Math.floor(performance.now() * 1000) }));
    } catch { /* Device cleanup must finish even when the transport has failed. */ }
    this.ready = false;
    this.controller.abort();
    clearInterval(this.heartbeat);
    clearTimeout(this.reconnectTimer);
    clearTimeout(this.deadlineTimer);
    clearTimeout(this.handshakeTimer);
    window.removeEventListener("pagehide", this.onPageHide);
    if (this.socket) {
      this.socket.onopen = this.socket.onerror = null;
      try { this.socket.close(1000); } catch { /* Tracks and the audio context still belong to this session. */ }
    }
    this.clearPlayback();
    this.node?.disconnect();
    this.node?.port.close();
    this.source?.disconnect();
    this.filter?.disconnect();
    for (const track of this.stream?.getTracks() ?? []) { track.onended = null; track.stop(); }
    if (this.context) { this.context.onstatechange = null; void this.context.close().catch(() => undefined); }
    if (!this.socket || this.socket.readyState === WebSocket.CLOSED) this.committed();
    if (update) this.update({ state: "closed", level: 0 });
  }
}
