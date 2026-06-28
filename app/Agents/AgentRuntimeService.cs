using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Tomur.Inference;
using Tomur.Multimodal;
using Tomur.Runtime;

namespace Tomur.Agents;

public sealed class AgentRuntimeService
{
    private const string AgentName = "tomur-local-agent";
    private const string AgentRuntime = "Microsoft.Agents.AI.ChatClientAgent";

    private readonly LocalModelCatalog modelCatalog;
    private readonly MultimodalRuntimeService multimodalRuntime;
    private readonly LocalChatClient chatClient;
    private readonly IServiceProvider services;

    public AgentRuntimeService(
        LocalModelCatalog modelCatalog,
        MultimodalRuntimeService multimodalRuntime,
        LocalChatClient chatClient,
        IServiceProvider services)
    {
        this.modelCatalog = modelCatalog;
        this.multimodalRuntime = multimodalRuntime;
        this.chatClient = chatClient;
        this.services = services;
    }

    public AgentRuntimeStatus GetStatus()
    {
        var models = modelCatalog.ListModels();
        var defaultChatModel = models.FirstOrDefault(IsChatModel);
        var tools = BuildToolStatuses(models).ToArray();
        var chatStatus = defaultChatModel is null ? "not_found" : "ready";
        var overallStatus = ResolveRuntimeStatus(chatStatus, tools);

        return new AgentRuntimeStatus(
            overallStatus,
            DateTimeOffset.UtcNow,
            new AgentChatClientStatus(
                chatStatus,
                "Microsoft.Extensions.AI.IChatClient",
                defaultChatModel?.Id,
                defaultChatModel is null
                    ? "No local chat model is visible yet."
                    : "Tomur local chat runtime is available through Microsoft.Extensions.AI.IChatClient."),
            new AgentFrameworkStatus(
                "wired",
                "Microsoft.Agents.AI.ChatClientAgent / Microsoft.Agents.AI.Workflows",
                "Agent Framework packages and Tomur-local AI boundaries are present. Plain local ChatClientAgent execution is wired, and read-only Tomur diagnostics are exposed as Microsoft.Extensions.AI.AITool objects. Automatic multimodal tool-calling remains an R9 follow-up after R8 image and TTS blockers are cleared.",
                [
                    "POST /api/agents/chat runs the local ChatClientAgent text path.",
                    "GET /api/agents/tools exposes the Tomur tool map.",
                    "GET /api/agents/tool-bindings exposes the current AITool binding set.",
                    "Use /api/agents/runtime to inspect the local tool map.",
                    "Use /api/runtime/multimodal to inspect backend readiness.",
                    "Plain OpenAI/Ollama-compatible text APIs continue to work without Agent Framework."
                ]),
            new AgentOrchestrationStatus(
                defaultChatModel is null ? "not_ready" : "wired",
                AgentRuntime,
                "POST /api/agents/chat",
                defaultChatModel is null
                    ? "A ChatClientAgent can be constructed only after a local chat model is visible."
                    : "A ChatClientAgent can run plain local text conversations through Microsoft.Extensions.AI.IChatClient."),
            tools,
            [
                "Community keeps Agent Framework optional and local-first.",
                "Tool schemas are represented by source-generated JSON contracts before full workflow persistence is added.",
                "R9 does not make blocked R8 image-generation or TTS tools callable by default.",
                "Repair or download actions still require explicit user confirmation in later R10/R11 flows."
            ]);
    }

    public AgentToolMapResponse GetToolMap()
    {
        var models = modelCatalog.ListModels();
        var tools = BuildToolDescriptors(models).ToArray();
        var status = ResolveToolMapStatus(tools);

        return new AgentToolMapResponse(status, DateTimeOffset.UtcNow, tools);
    }

    public async Task<AgentChatResponse> RunChatAsync(
        AgentChatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = BuildMessages(request);
        var options = new ChatOptions
        {
            ModelId = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim(),
            Instructions = string.IsNullOrWhiteSpace(request.Instructions) ? null : request.Instructions.Trim(),
            Temperature = NormalizeFloat(request.Temperature),
            TopP = NormalizeFloat(request.TopP),
            MaxOutputTokens = request.MaxTokens is > 0 ? Math.Clamp(request.MaxTokens.Value, 1, 4096) : null,
            ToolMode = ChatToolMode.None
        };

        var modelId = options.ModelId ?? modelCatalog.ListModels().FirstOrDefault(IsChatModel)?.Id;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InferenceException(
                "model_not_downloaded",
                "No local chat model is available for the Agent Framework chat endpoint.",
                [
                    "Run tomur pull recommended to install the default local assistant model.",
                    "Use /v1/models or /api/tags to inspect models currently visible to Tomur."
                ]);
        }

        options.ModelId = modelId;
        var started = DateTimeOffset.UtcNow;
        var agent = CreateAgent(modelId);
        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var response = await agent.RunAsync(
                messages,
                session,
                new ChatClientAgentRunOptions(options),
                cancellationToken)
            .ConfigureAwait(false);

        return new AgentChatResponse(
            "ok",
            AgentName,
            AgentRuntime,
            modelId,
            response.Text ?? string.Empty,
            (long)Math.Round((DateTimeOffset.UtcNow - started).TotalMilliseconds),
            [
                "source: Microsoft.Agents.AI.ChatClientAgent",
                "chat-client: Microsoft.Extensions.AI.IChatClient",
                "tool-mode: none",
                "scope: local text orchestration"
            ]);
    }

    private ChatClientAgent CreateAgent(string modelId)
    {
        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Id = "tomur.local",
                Name = AgentName,
                Description = "Local-first Tomur Community agent backed by the monolithic Tomur runtime.",
                ChatOptions = new ChatOptions
                {
                    ModelId = modelId,
                    ToolMode = ChatToolMode.None
                },
                UseProvidedChatClientAsIs = true
            },
            NullLoggerFactory.Instance,
            services);
    }

    private static IReadOnlyList<ChatMessage> BuildMessages(AgentChatRequest request)
    {
        if (request.Messages is { Count: > 0 })
        {
            var messages = request.Messages
                .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
                .Select(static message => new ChatMessage(NormalizeChatRole(message.Role), message.Content!.Trim()))
                .ToArray();
            if (messages.Length > 0)
            {
                return messages;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Message))
        {
            return [new ChatMessage(ChatRole.User, request.Message.Trim())];
        }

        throw new InferenceException(
            "invalid_request",
            "The agent chat request requires message or messages[].content.",
            ["Provide a user message before invoking /api/agents/chat."]);
    }

    private IEnumerable<AgentToolStatus> BuildToolStatuses(IReadOnlyList<LocalModelDescriptor> models)
        => BuildToolDescriptors(models)
            .Select(static tool => new AgentToolStatus(
                tool.Name,
                tool.DisplayName,
                tool.Status,
                tool.Backend,
                tool.Model,
                tool.Message,
                tool.Actions));

    private static string ResolveRuntimeStatus(string chatStatus, IReadOnlyList<AgentToolStatus> tools)
    {
        if (!string.Equals(chatStatus, "ready", StringComparison.OrdinalIgnoreCase))
        {
            return tools.Any(static tool => string.Equals(tool.Status, "ready", StringComparison.OrdinalIgnoreCase))
                ? "partial"
                : "not_ready";
        }

        return tools.All(static tool => string.Equals(tool.Status, "ready", StringComparison.OrdinalIgnoreCase))
            ? "ready"
            : "partial";
    }

    private static string ResolveToolMapStatus(IReadOnlyList<AgentToolDescriptor> tools)
    {
        if (tools.All(static tool => string.Equals(tool.Status, "ready", StringComparison.OrdinalIgnoreCase)))
        {
            return "ready";
        }

        return tools.Any(static tool => string.Equals(tool.Status, "ready", StringComparison.OrdinalIgnoreCase))
            ? "partial"
            : "not_ready";
    }

    private IEnumerable<AgentToolDescriptor> BuildToolDescriptors(IReadOnlyList<LocalModelDescriptor> models)
    {
        var defaultChatModel = models.FirstOrDefault(IsChatModel);
        yield return new AgentToolDescriptor(
            "chat.respond",
            "Local Chat",
            defaultChatModel is null ? "not_found" : "ready",
            "llama.cpp via IChatClient",
            defaultChatModel?.Id,
            "/api/agents/chat",
            """{"type":"object","required":["message"],"properties":{"message":{"type":"string"},"model":{"type":"string"},"instructions":{"type":"string"},"max_tokens":{"type":"integer"}}}""",
            "none",
            defaultChatModel is null
                ? "No local chat model is visible."
                : "Local chat model can answer through Microsoft.Extensions.AI.IChatClient.",
            defaultChatModel is null ? ["Run tomur pull recommended to install the default assistant model."] : []);

        yield return CreateMultimodalTool("image.generate", "Image Generation", "image-generation", "stable-diffusion.cpp");
        yield return CreateMultimodalTool("vision.analyze", "Vision Analysis", "vlm", "llama.cpp mtmd VLM");
        yield return CreateMultimodalTool("ocr.recognize", "OCR", "ocr", "Tomur OCR native bridge");
        yield return CreateMultimodalTool("audio.transcribe", "Speech To Text", "asr", "whisper.cpp");
        yield return CreateMultimodalTool("audio.speak", "Text To Speech", "tts", "llama.cpp GGUF TTS");

        yield return new AgentToolDescriptor(
            "files.search",
            "Local Files",
            "planned",
            "SQLite/local files",
            null,
            null,
            """{"type":"object","required":["query"],"properties":{"query":{"type":"string"},"top_k":{"type":"integer"}}}""",
            "read",
            "Local file Q&A tool is reserved for R9/R10 and will not require PostgreSQL in Community.",
            ["Index local files in a later R10/R11 flow before enabling this tool."]);

        yield return new AgentToolDescriptor(
            "runtime.diagnose",
            "Runtime Diagnostics",
            "ready",
            "tomur doctor/runtime APIs",
            null,
            "/api/runtime/status",
            """{"type":"object","properties":{}}""",
            "read",
            "Runtime status can be exposed as a read-only local tool; repair actions remain user-confirmed.",
            []);

        yield return new AgentToolDescriptor(
            "tools.inspect",
            "Tool Map",
            "ready",
            "Microsoft.Extensions.AI.AITool metadata",
            null,
            "/api/agents/tool-bindings",
            """{"type":"object","properties":{}}""",
            "read",
            "Agent Framework tool declarations can be inspected without invoking side-effect tools.",
            []);
    }

    private AgentToolDescriptor CreateMultimodalTool(
        string name,
        string displayName,
        string backendId,
        string backendName)
    {
        var backend = multimodalRuntime.GetBackendStatus(backendId);
        var model = backend.VisibleModelIds.FirstOrDefault();
        var status = string.Equals(backend.Status, "ready", StringComparison.OrdinalIgnoreCase)
            ? ResolveExecutableToolStatus(backendId)
            : backend.Status;
        var message = backendId switch
        {
            "image-generation" when status == "blocked" => "Backend assets are visible, but current stable-diffusion.cpp FLUX.2 smoke is isolated because native generation can still fail inside the worker.",
            "tts" when status == "blocked" => "Backend assets are visible, but the tomur-tts bridge still returns the pending llama.cpp tools/tts adapter diagnostic.",
            _ => backend.Message
        };

        return new AgentToolDescriptor(
            name,
            displayName,
            status,
            backendName,
            model,
            ResolveToolRoute(backendId),
            ResolveToolInputSchema(backendId),
            ResolveToolSideEffect(backendId),
            message,
            backend.Actions);
    }

    private static string ResolveExecutableToolStatus(string backendId)
        => backendId is "image-generation" or "tts" ? "blocked" : "ready";

    private static bool IsChatModel(LocalModelDescriptor model)
        => model.Capabilities.Any(static capability =>
            string.Equals(capability, "chat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(capability, "completion", StringComparison.OrdinalIgnoreCase));

    private static ChatRole NormalizeChatRole(string? role)
    {
        var normalized = role?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "system" => ChatRole.System,
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            _ => ChatRole.User
        };
    }

    private static float? NormalizeFloat(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        return (float)value.Value;
    }

    private static string? ResolveToolRoute(string backendId)
        => backendId switch
        {
            "image-generation" => "/v1/images/generations",
            "vlm" => "/api/vision/analyze",
            "ocr" => "/api/ocr/analyze",
            "asr" => "/v1/audio/transcriptions",
            "tts" => "/v1/audio/speech",
            _ => null
        };

    private static string ResolveToolInputSchema(string backendId)
        => backendId switch
        {
            "image-generation" => """{"type":"object","required":["prompt"],"properties":{"prompt":{"type":"string"},"size":{"type":"string"},"steps":{"type":"integer"}}}""",
            "vlm" => """{"type":"object","required":["prompt","images"],"properties":{"prompt":{"type":"string"},"images":{"type":"array"}}}""",
            "ocr" => """{"type":"object","required":["image"],"properties":{"image":{"type":"object"},"language":{"type":"string"},"prompt":{"type":"string"}}}""",
            "asr" => """{"type":"object","required":["file"],"properties":{"file":{"type":"string","description":"multipart/form-data file field"},"language":{"type":"string"}}}""",
            "tts" => """{"type":"object","required":["input"],"properties":{"input":{"type":"string"},"voice":{"type":"string"},"response_format":{"type":"string"}}}""",
            _ => """{"type":"object","properties":{}}"""
        };

    private static string ResolveToolSideEffect(string backendId)
        => backendId switch
        {
            "image-generation" or "tts" => "generates-local-artifact",
            _ => "read"
        };
}
