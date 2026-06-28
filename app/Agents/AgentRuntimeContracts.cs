using System.Text.Json.Serialization;

namespace Tomur.Agents;

public sealed record AgentRuntimeStatus(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("chat_client")] AgentChatClientStatus ChatClient,
    [property: JsonPropertyName("agent_framework")] AgentFrameworkStatus AgentFramework,
    [property: JsonPropertyName("orchestration")] AgentOrchestrationStatus Orchestration,
    [property: JsonPropertyName("tools")] IReadOnlyList<AgentToolStatus> Tools,
    [property: JsonPropertyName("notes")] IReadOnlyList<string> Notes);

public sealed record AgentChatClientStatus(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("default_model")] string? DefaultModel,
    [property: JsonPropertyName("message")] string Message);

public sealed record AgentFrameworkStatus(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions);

public sealed record AgentOrchestrationStatus(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("agent_type")] string AgentType,
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("message")] string Message);

public sealed record AgentToolStatus(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions);

public sealed record AgentToolMapResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("tools")] IReadOnlyList<AgentToolDescriptor> Tools);

public sealed record AgentFrameworkToolBindingResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("tool_type")] string ToolType,
    [property: JsonPropertyName("safe_tools")] IReadOnlyList<AgentFrameworkToolBinding> SafeTools,
    [property: JsonPropertyName("declaration_tools")] IReadOnlyList<AgentFrameworkToolBinding> DeclarationTools,
    [property: JsonPropertyName("notes")] IReadOnlyList<string> Notes);

public sealed record AgentFrameworkToolBinding(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("implementation")] string Implementation,
    [property: JsonPropertyName("status")] string Status);

public sealed record AgentToolDescriptor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("route")] string? Route,
    [property: JsonPropertyName("input_schema")] string InputSchema,
    [property: JsonPropertyName("side_effect")] string SideEffect,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions);

public sealed record AgentChatRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("messages")] IReadOnlyList<AgentChatMessage>? Messages,
    [property: JsonPropertyName("instructions")] string? Instructions,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens,
    [property: JsonPropertyName("temperature")] double? Temperature,
    [property: JsonPropertyName("top_p")] double? TopP);

public sealed record AgentChatMessage(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content);

public sealed record AgentChatResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("runtime")] string Runtime,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics);
