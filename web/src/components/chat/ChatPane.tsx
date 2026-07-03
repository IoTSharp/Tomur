import { Bubble, Prompts, Welcome } from "@ant-design/x";
import type { BubbleItemType, PromptsItemType } from "@ant-design/x";
import XMarkdown from "@ant-design/x-markdown";

export function ChatPane({
  hasMessages,
  bubbleItems,
  promptItems,
  onPromptSelect
}: {
  hasMessages: boolean;
  bubbleItems: BubbleItemType[];
  promptItems: PromptsItemType[];
  onPromptSelect: (key: string, label: unknown) => void;
}) {
  return (
    <section className="chat-area">
      {!hasMessages ? (
        <div className="empty-chat">
          <Welcome
            variant="borderless"
            title="本地 Chat 工作台"
            description="连接 Tomur 本地 OpenAI 兼容接口，使用已安装模型进行对话。"
          />
          <Prompts
            wrap
            items={promptItems}
            onItemClick={({ data }) => {
              onPromptSelect(String(data.key), data.label);
            }}
          />
        </div>
      ) : (
        <Bubble.List
          className="bubble-list"
          items={bubbleItems}
          autoScroll
          role={{
            ai: {
              placement: "start",
              variant: "borderless",
              contentRender: (content) => (
                <XMarkdown
                  content={String(content ?? "")}
                  openLinksInNewTab
                  escapeRawHtml
                />
              )
            },
            user: {
              placement: "end",
              variant: "filled"
            }
          }}
        />
      )}
    </section>
  );
}

