import { useEffect, useRef, useState } from "react";
import { Alert, Button, Select, Space, Tooltip, Typography } from "antd";
import { Bubble } from "@ant-design/x";
import { Headphones, Mic, MicOff, PhoneOff, Square } from "lucide-react";
import { RealtimeVoiceSession, type VoiceSnapshot } from "../../app/realtime";
import type { ChatOptions } from "../../app/chatOptions";

const stateLabels: Record<VoiceSnapshot["state"], string> = {
  closed: "语音", connecting: "正在连接", listening: "正在聆听", user_speaking: "正在收音",
  transcribing: "正在转写", thinking: "正在思考", speaking: "正在播报",
  interrupted: "已打断", reconnecting: "正在重连", failed: "语音不可用"
};

export function RealtimeVoiceBar({ model, options, disabled, ensureConversation, onCommitted, onActiveChange }: {
  model?: string;
  options: ChatOptions;
  disabled: boolean;
  ensureConversation: () => Promise<string>;
  onCommitted: () => void;
  onActiveChange: (active: boolean) => void;
}) {
  const session = useRef<RealtimeVoiceSession | undefined>(undefined);
  const text = useRef("");
  const mounted = useRef(true);
  const startAttempt = useRef(0);
  const starting = useRef(false);
  const callbacks = useRef({ onCommitted, onActiveChange });
  callbacks.current = { onCommitted, onActiveChange };
  const [snapshot, setSnapshot] = useState<VoiceSnapshot>({ state: "closed", transcript: "", text: "", level: 0, muted: false, underruns: 0 });
  const [devices, setDevices] = useState<MediaDeviceInfo[]>([]);
  const [inputDevice, setInputDevice] = useState<string>();
  const [outputDevice, setOutputDevice] = useState<string>();
  const [mode, setMode] = useState<"half_duplex" | "duplex_experimental" | "manual">("half_duplex");
  const active = snapshot.state !== "closed" && snapshot.state !== "failed";
  text.current = snapshot.text;

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; startAttempt.current++; session.current?.stop(); callbacks.current.onActiveChange(false); };
  }, []);

  useEffect(() => {
    if (!active) return;
    const started = performance.now();
    let ticks = 0;
    const timer = setInterval(() => {
      if (++ticks > 9000 || performance.now() - started > 15 * 60 * 1000) { clearInterval(timer); return; }
      session.current?.displayed(text.current);
    }, 100);
    return () => clearInterval(timer);
  }, [active]);

  async function refreshDevices() {
    try { setDevices(await navigator.mediaDevices.enumerateDevices()); } catch { /* Browser keeps system defaults. */ }
  }

  async function start() {
    if (!model || starting.current) return;
    const attempt = ++startAttempt.current;
    starting.current = true;
    setSnapshot(current => ({ ...current, state: "connecting", error: undefined }));
    callbacks.current.onActiveChange(true);
    try {
      const conversationId = await ensureConversation();
      if (!mounted.current || attempt !== startAttempt.current) return;
      session.current = new RealtimeVoiceSession({ conversationId, model, ttsModel: options.ttsModel,
        language: options.language, deviceId: inputDevice, outputDeviceId: outputDevice, mode },
        value => {
          if (!mounted.current) return;
          setSnapshot(value);
          callbacks.current.onActiveChange(value.state !== "closed" && value.state !== "failed");
        }, () => callbacks.current.onCommitted());
      await session.current.start();
      if (mounted.current && attempt === startAttempt.current) await refreshDevices();
    } catch (error) {
      if (mounted.current && attempt === startAttempt.current) {
        setSnapshot(current => ({ ...current, state: "failed", error: error instanceof Error ? error.message : "语音会话创建失败。" }));
        callbacks.current.onActiveChange(false);
      }
    } finally {
      if (attempt === startAttempt.current) starting.current = false;
    }
  }

  function stop() {
    startAttempt.current++;
    starting.current = false;
    session.current?.stop();
    setSnapshot(current => ({ ...current, state: "closed", level: 0 }));
    callbacks.current.onActiveChange(false);
  }

  return <section className="realtime-voice" aria-label="语音会话">
    <Space wrap size={8} className="realtime-controls">
      {active ? <>
        <Tooltip title={snapshot.muted ? "开启麦克风" : mode === "manual" ? "提交语音" : "静音麦克风"}>
          <Button aria-label={snapshot.muted ? "开启麦克风" : "静音麦克风"}
            icon={snapshot.muted ? <MicOff size={18} /> : <Mic size={18} />}
            disabled={snapshot.state === "connecting" || snapshot.state === "reconnecting"}
            onClick={() => session.current?.setMuted(!snapshot.muted)} />
        </Tooltip>
        <Tooltip title="取消回复"><Button aria-label="取消回复" icon={<Square size={18} />} onClick={() => session.current?.cancel()} /></Tooltip>
        <Tooltip title="结束语音"><Button danger aria-label="结束语音" icon={<PhoneOff size={18} />} onClick={stop} /></Tooltip>
      </> : <Tooltip title="进入语音会话"><Button aria-label="进入语音会话" icon={<Headphones size={18} />}
        disabled={disabled || !model} onClick={() => void start()} /></Tooltip>}
      <Typography.Text role="status" aria-live="polite">{stateLabels[snapshot.state]}</Typography.Text>
      <meter aria-label="麦克风声级" value={Math.min(snapshot.level * 4, 1)} min={0} max={1} />
      <Select aria-label="语音模式" value={mode} disabled={active} onChange={setMode}
        options={[{ value: "half_duplex", label: "半双工" }, { value: "manual", label: "手动提交" }, { value: "duplex_experimental", label: "双向（实验）" }]} />
      <Select aria-label="输入设备" placeholder="系统麦克风" value={inputDevice} disabled={active} allowClear
        onOpenChange={open => { if (open) void refreshDevices(); }} onChange={setInputDevice}
        options={devices.filter(device => device.kind === "audioinput").map((device, i) => ({ value: device.deviceId, label: device.label || `麦克风 ${i + 1}` }))} />
      <Select aria-label="播放设备" placeholder="系统扬声器" value={outputDevice} disabled={active} allowClear
        onOpenChange={open => { if (open) void refreshDevices(); }} onChange={setOutputDevice}
        options={devices.filter(device => device.kind === "audiooutput").map((device, i) => ({ value: device.deviceId, label: device.label || `扬声器 ${i + 1}` }))} />
    </Space>
    {snapshot.error && <Alert type="warning" showIcon title={snapshot.error} />}
    {active && (snapshot.transcript || snapshot.text) && <div className="realtime-transcript">
      {snapshot.transcript && <Bubble content={snapshot.transcript} placement="end" />}
      {snapshot.text && <Bubble content={snapshot.text} placement="start" />}
    </div>}
  </section>;
}
