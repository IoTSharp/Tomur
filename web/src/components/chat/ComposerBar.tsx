import { Button, Flex, Space, Tooltip, Typography, Upload } from "antd";
import { Attachments, Sender } from "@ant-design/x";
import { FileAudio, Image, Mic, Paperclip, RotateCcw, Square, Volume2 } from "lucide-react";
import type { ChatOptions } from "../../app/chatOptions";
import type { ConversationAttachment } from "../../types";
import { buildComposerStatus, toAttachmentItem } from "../../app/attachments";
import { ChatOptionsPopover } from "./ChatOptionsPopover";

export function ComposerBar({
  input,
  sending,
  recording,
  uploadingAttachment,
  multimodalAction,
  speechEnabled,
  chatOptions,
  ttsModelOptions,
  chatReady,
  inputPlaceholder,
  selectedModelLabel,
  pendingAttachments,
  activeMessageCount,
  onInputChange,
  onSubmitMessage,
  onAddPendingAttachment,
  onRemovePendingAttachment,
  onGenerateImage,
  onTranscribeAudio,
  onStartVoiceRecording,
  onStopVoiceRecording,
  onToggleSpeech,
  onChatOptionsChange,
  onStopGeneration,
  onRegenerate
}: {
  input: string;
  sending: boolean;
  recording: boolean;
  uploadingAttachment: boolean;
  multimodalAction: "image" | "transcription" | null;
  speechEnabled: boolean;
  chatOptions: ChatOptions;
  ttsModelOptions: Array<{ value: string; label: string }>;
  chatReady: boolean;
  inputPlaceholder: string;
  selectedModelLabel?: string;
  pendingAttachments: ConversationAttachment[];
  activeMessageCount: number;
  onInputChange: (value: string) => void;
  onSubmitMessage: (value: string) => void;
  onAddPendingAttachment: (file: File) => void;
  onRemovePendingAttachment: (id: string) => void;
  onGenerateImage: () => void;
  onTranscribeAudio: (file: File) => void;
  onStartVoiceRecording: () => void;
  onStopVoiceRecording: () => void;
  onToggleSpeech: () => void;
  onChatOptionsChange: (value: ChatOptions) => void;
  onStopGeneration: () => void;
  onRegenerate: () => void;
}) {
  return (
    <footer className="composer">
      <div className="attachment-strip">
        <Attachments
          disabled={sending || recording || uploadingAttachment}
          items={pendingAttachments.map(toAttachmentItem)}
          overflow="wrap"
          beforeUpload={(file) => {
            onAddPendingAttachment(file);
            return false;
          }}
          onRemove={(file) => {
            onRemovePendingAttachment(String(file.uid));
            return true;
          }}
          placeholder={{
            title: "附件入口",
            description: "选择图片、音频或文本文件后随下一轮会话发送。"
          }}
        >
          <Button
            icon={<Paperclip size={16} />}
            loading={uploadingAttachment}
            disabled={sending || recording}
          >
            附件
          </Button>
        </Attachments>
        <Tooltip title={input.trim() ? "用当前输入生成本地图片" : "输入图像提示词后生成图片"}>
          <Button
            icon={<Image size={16} />}
            loading={multimodalAction === "image"}
            disabled={sending || recording || uploadingAttachment || !input.trim()}
            aria-label="生成本地图片"
            onClick={onGenerateImage}
          />
        </Tooltip>
        <Upload
          accept="audio/*"
          disabled={sending || recording || uploadingAttachment}
          maxCount={1}
          showUploadList={false}
          beforeUpload={(file) => {
            onTranscribeAudio(file);
            return false;
          }}
        >
          <Tooltip title="选择音频文件，仅转写并保存到当前会话">
            <Button
              icon={<FileAudio size={16} />}
              loading={multimodalAction === "transcription"}
              disabled={sending || recording || uploadingAttachment}
              aria-label="选择音频文件并转写"
            />
          </Tooltip>
        </Upload>
        <Tooltip title={recording ? "停止录音并发送" : "录音语音回合"}>
          <Button
            type={recording ? "primary" : "default"}
            danger={recording}
            icon={recording ? <Square size={16} /> : <Mic size={16} />}
            disabled={sending || uploadingAttachment}
            onClick={recording ? onStopVoiceRecording : onStartVoiceRecording}
          />
        </Tooltip>
        <Tooltip title={speechEnabled ? "本轮请求 TTS 朗读" : "本轮只返回文字"}>
          <Button
            type={speechEnabled ? "primary" : "default"}
            icon={<Volume2 size={16} />}
            disabled={sending || recording}
            onClick={onToggleSpeech}
          />
        </Tooltip>
        <ChatOptionsPopover
          value={chatOptions}
          disabled={sending || recording}
          speechEnabled={speechEnabled}
          ttsModelOptions={ttsModelOptions}
          onChange={onChatOptionsChange}
        />
        {pendingAttachments.length > 0 && (
          <Button
            type="primary"
            disabled={sending || recording || uploadingAttachment}
            onClick={() => onSubmitMessage(input)}
          >
            发送附件
          </Button>
        )}
      </div>

      <Sender
        value={input}
        loading={sending}
        disabled={!chatReady || recording}
        placeholder={recording ? "正在录音" : inputPlaceholder}
        submitType="enter"
        onChange={onInputChange}
        onSubmit={onSubmitMessage}
        onCancel={onStopGeneration}
        prefix={
          sending ? (
            <Tooltip title="停止生成">
              <Button icon={<Square size={14} />} onClick={onStopGeneration} />
            </Tooltip>
          ) : undefined
        }
        footer={() => (
          <Flex justify="space-between" align="center" wrap gap={8}>
            <Typography.Text type="secondary">
              {chatReady
                ? buildComposerStatus(selectedModelLabel, pendingAttachments.length, speechEnabled, recording)
                : "模型缺失时不会伪造回复。"}
            </Typography.Text>
            <Space size={4}>
              <Tooltip title="重新生成上一条">
                <Button
                  type="text"
                  size="small"
                  icon={<RotateCcw size={14} />}
                  disabled={sending || activeMessageCount === 0}
                  onClick={onRegenerate}
                />
              </Tooltip>
            </Space>
          </Flex>
        )}
      />
    </footer>
  );
}
