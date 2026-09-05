class VoiceAudioProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.pool = Array.from({ length: 8 }, () => new ArrayBuffer(640));
    this.capture = new Int16Array(this.pool.pop());
    this.captureOffset = 0;
    this.captureSamples = 0;
    this.inputPhase = 0;
    this.inputSum = 0;
    this.inputWeight = 0;
    this.enabled = false;
    this.output = new Float32Array(72000);
    this.read = 0;
    this.write = 0;
    this.boundaries = new Array(32);
    this.boundaryRead = 0;
    this.boundaryWrite = 0;
    this.response = "";
    this.epoch = 0;
    this.playing = false;
    this.ended = false;
    this.failed = false;
    this.duckUntil = 0;
    this.levelFrames = 0;
    this.port.onmessage = ({ data }) => {
      if (data.type === "recycle") {
        if (this.pool.length < 8) this.pool.push(data.buffer);
      } else if (data.type === "capture") {
        this.enabled = data.enabled;
        this.captureOffset = this.inputPhase = this.inputSum = this.inputWeight = 0;
      } else if (data.type === "clear") {
        this.read = this.write = 0;
        this.boundaryRead = this.boundaryWrite = 0;
        this.response = data.response ?? "";
        this.epoch = data.epoch ?? 0;
        this.playing = this.ended = false;
        this.duckUntil = 0;
      } else if (data.type === "end") {
        this.ended = true;
      } else if (data.type === "audio" && data.response === this.response) {
        const pcm = new DataView(data.buffer);
        const count = pcm.byteLength / 2;
        if (count > 2400 || this.write - this.read + count > 72000 || this.boundaryWrite - this.boundaryRead >= 32) {
          this.fail("playback_overflow");
          return;
        }
        for (let i = 0; i < count; i++) this.output[(this.write + i) % this.output.length] = pcm.getInt16(i * 2, true) / 32768;
        this.write += count;
        this.boundaries[this.boundaryWrite++ % 32] = { end: this.write, sequence: data.sequence, timestamp: data.timestamp };
      }
    };
  }

  fail(code) {
    if (!this.failed) this.port.postMessage({ type: "error", code });
    this.failed = true;
    this.enabled = false;
  }

  process(inputs, outputs) {
    if (this.failed) return false;
    const input = inputs[0]?.[0];
    let energy = 0;
    if (input && this.enabled) {
      const ratio = 16000 / sampleRate;
      for (let i = 0; i < input.length; i++) {
        energy += input[i] * input[i];
        const weight = Math.min(ratio, 1 - this.inputPhase);
        this.inputSum += input[i] * weight;
        this.inputWeight += weight;
        this.inputPhase += ratio;
        if (this.inputPhase >= 1 - 1e-10) {
          const value = Math.max(-1, Math.min(1, this.inputSum / this.inputWeight));
          this.capture[this.captureOffset++] = Math.round(value * (value < 0 ? 32768 : 32767));
          this.inputPhase = Math.max(0, this.inputPhase - 1);
          this.inputSum = input[i] * this.inputPhase;
          this.inputWeight = this.inputPhase;
          if (this.captureOffset === 320) {
            if (this.pool.length === 0) { this.fail("capture_backpressure"); break; }
            const buffer = this.capture.buffer;
            this.port.postMessage({ type: "pcm", buffer, timestamp: this.captureSamples * 1000000 / 16000 }, [buffer]);
            this.captureSamples += 320;
            this.capture = new Int16Array(this.pool.pop());
            this.captureOffset = 0;
          }
        }
      }
    }
    if (++this.levelFrames >= 20) {
      this.levelFrames = 0;
      const level = input ? Math.sqrt(energy / input.length) : 0;
      this.port.postMessage({ type: "level", level });
      // A possible local interruption only ducks temporarily. Only the
      // authoritative server event is allowed to discard queued audio.
      if (level > 0.06 && this.playing && this.duckUntil === 0) this.duckUntil = currentTime + 0.3;
    }
    const output = outputs[0]?.[0];
    if (!output) return true;
    if (!this.playing && (this.write - this.read >= 4800 || (this.ended && this.write > this.read))) this.playing = true;
    const drift = Math.max(-0.002, Math.min(0.002, (this.write - this.read - 4800) / 2400000));
    const step = 24000 / sampleRate * (1 + drift);
    for (let i = 0; i < output.length; i++) {
      if (!this.playing) { output[i] = 0; continue; }
      if (this.read >= this.write) {
        output[i] = 0;
        this.playing = false;
        if (!this.ended) this.port.postMessage({ type: "underrun", epoch: this.epoch });
        continue;
      }
      const index = Math.floor(this.read);
      const fraction = this.read - index;
      const a = this.output[index % this.output.length];
      const b = this.output[Math.min(index + 1, this.write - 1) % this.output.length];
      output[i] = (a + (b - a) * fraction) * (currentTime < this.duckUntil ? 0.2 : 1);
      this.read = Math.min(this.write, this.read + step);
      for (let boundary = 0; boundary < 32 && this.boundaryRead < this.boundaryWrite; boundary++) {
        const item = this.boundaries[this.boundaryRead % 32];
        if (this.read < item.end) break;
        this.boundaryRead++;
        this.port.postMessage({ type: "played", epoch: this.epoch, sequence: item.sequence, timestamp: item.timestamp });
      }
    }
    if (currentTime >= this.duckUntil && energy < 0.001) this.duckUntil = 0;
    return true;
  }
}

registerProcessor("voice-audio", VoiceAudioProcessor);
