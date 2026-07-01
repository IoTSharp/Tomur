using System.Text.Json;
using Microsoft.Extensions.AI;
using Tomur.Runtime;
using Tomur.Serialization;

namespace Tomur.Agents;

public interface ILocalAgentTool
{
    ValueTask<object?> InvokeLocalAsync(JsonElement? arguments, CancellationToken cancellationToken);
}

public sealed class RuntimeDiagnoseFunction : AIFunction, ILocalAgentTool
{
    public const string ToolName = "runtime.diagnose";

    public const string ToolDescription =
        "Read Tomur local runtime diagnostics, including paths, native bundle status, database state, proxy, disk and visible failures.";

    private static readonly JsonElement InputSchema = ToolJsonSchema.Parse(
        """{"type":"object","additionalProperties":false,"properties":{}}""");

    private readonly RuntimeDiagnosticsProvider diagnosticsProvider;

    public RuntimeDiagnoseFunction(RuntimeDiagnosticsProvider diagnosticsProvider)
    {
        this.diagnosticsProvider = diagnosticsProvider;
    }

    public override string Name => ToolName;

    public override string Description => ToolDescription;

    public override JsonElement JsonSchema => InputSchema;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = arguments;
        return InvokeLocalAsync(null, cancellationToken);
    }

    public ValueTask<object?> InvokeLocalAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = arguments;

        var status = diagnosticsProvider.GetRuntimeStatus();
        var summary = new RuntimeDiagnoseToolResult(
            status.Status,
            status.CheckedAt,
            status.Runtime,
            status.NativeBundle.Status,
            status.Directories,
            status.Database,
            status.ApiKeys,
            status.Disk,
            status.Proxy,
            status.Port,
            status.Diagnostics);

        return ValueTask.FromResult<object?>(summary);
    }
}

public sealed class ToolMapFunction : AIFunction, ILocalAgentTool
{
    public const string ToolName = "tools.inspect";

    public const string ToolDescription =
        "Inspect the current Tomur Agent Framework tool map and multimodal backend readiness without invoking side-effect tools.";

    private static readonly JsonElement InputSchema = ToolJsonSchema.Parse(
        """{"type":"object","additionalProperties":false,"properties":{}}""");

    private readonly AgentRuntimeService agentRuntime;

    public ToolMapFunction(AgentRuntimeService agentRuntime)
    {
        this.agentRuntime = agentRuntime;
    }

    public override string Name => ToolName;

    public override string Description => ToolDescription;

    public override JsonElement JsonSchema => InputSchema;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = arguments;
        return InvokeLocalAsync(null, cancellationToken);
    }

    public ValueTask<object?> InvokeLocalAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = arguments;
        return ValueTask.FromResult<object?>(agentRuntime.GetToolMap());
    }
}

public sealed class FileSearchFunction : AIFunction, ILocalAgentTool
{
    public const string ToolName = "files.search";

    public const string ToolDescription =
        "Search text files indexed in Tomur's managed local files directory through SQLite FTS, returning bounded snippets for local file Q&A.";

    private static readonly JsonElement InputSchema = ToolJsonSchema.Parse(
        """{"type":"object","additionalProperties":false,"required":["query"],"properties":{"query":{"type":"string"},"root":{"type":"string"},"top_k":{"type":"integer","minimum":1,"maximum":20},"refresh":{"type":"boolean"},"max_files":{"type":"integer","minimum":1,"maximum":4096},"max_file_bytes":{"type":"integer","minimum":1,"maximum":5242880}}}""");

    private readonly FileIndexStore fileIndex;

    public FileSearchFunction(FileIndexStore fileIndex)
    {
        this.fileIndex = fileIndex;
    }

    public override string Name => ToolName;

    public override string Description => ToolDescription;

    public override JsonElement JsonSchema => InputSchema;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = new FileSearchToolArguments(
            TryGetString(arguments, "query"),
            TryGetString(arguments, "root"),
            TryGetInt(arguments, "top_k"),
            TryGetBool(arguments, "refresh"),
            TryGetInt(arguments, "max_files"),
            TryGetLong(arguments, "max_file_bytes"));
        return InvokeAsync(parsed, cancellationToken);
    }

    public ValueTask<object?> InvokeLocalAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = Deserialize(arguments, AppJsonSerializerContext.Default.FileSearchToolArguments) ??
            new FileSearchToolArguments(null, null, null, null, null, null);
        return InvokeAsync(parsed, cancellationToken);
    }

    private ValueTask<object?> InvokeAsync(
        FileSearchToolArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<object?>(fileIndex.Search(arguments));
    }

    private static T? Deserialize<T>(
        JsonElement? arguments,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (arguments is null || arguments.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return default;
        }

        return JsonSerializer.Deserialize(arguments.Value.GetRawText(), typeInfo);
    }

    private static string? TryGetString(AIFunctionArguments arguments, string name)
        => arguments.TryGetValue(name, out var value) && value is not null
            ? value.ToString()
            : null;

    private static int? TryGetInt(AIFunctionArguments arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int number => number,
            long number when number >= int.MinValue && number <= int.MaxValue => (int)number,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number) => number,
            string text when int.TryParse(text, out var number) => number,
            _ => null
        };
    }

    private static long? TryGetLong(AIFunctionArguments arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            long number => number,
            int number => number,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number) => number,
            string text when long.TryParse(text, out var number) => number,
            _ => null
        };
    }

    private static bool? TryGetBool(AIFunctionArguments arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            bool flag => flag,
            JsonElement element when element.ValueKind is JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
            string text when bool.TryParse(text, out var flag) => flag,
            _ => null
        };
    }
}

public sealed class BlockedToolFunction : AIFunction, ILocalAgentTool
{
    private readonly string name;
    private readonly string description;
    private readonly string code;
    private readonly string message;
    private readonly IReadOnlyList<string> actions;
    private readonly JsonElement inputSchema;

    public BlockedToolFunction(
        AgentToolDescriptor descriptor,
        string code,
        string message,
        IReadOnlyList<string> actions)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        this.name = descriptor.Name;
        this.description = descriptor.Message;
        this.code = code;
        this.message = message;
        this.actions = actions;
        inputSchema = ToolJsonSchema.Parse(descriptor.InputSchema);
    }

    public override string Name => name;

    public override string Description => description;

    public override JsonElement JsonSchema => inputSchema;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = arguments;
        return InvokeLocalAsync(null, cancellationToken);
    }

    public ValueTask<object?> InvokeLocalAsync(
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = arguments;
        return ValueTask.FromResult<object?>(new BlockedToolResult(code, message, actions));
    }
}

public sealed class ControlledToolDeclarationFunction : AIFunction
{
    private readonly string name;
    private readonly string description;
    private readonly IReadOnlyList<string> actions;
    private readonly JsonElement inputSchema;

    public ControlledToolDeclarationFunction(AgentToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        name = descriptor.Name;
        description = descriptor.Message;
        actions = descriptor.RequiresConfirmation
            ? ["Use POST /api/agents/tools/invoke with mode=controlled and confirm=true after explicit user approval."]
            : ["Use POST /api/agents/tools/invoke with mode=controlled and valid local input arguments."];
        inputSchema = ToolJsonSchema.Parse(descriptor.InputSchema);
    }

    public override string Name => name;

    public override string Description => description;

    public override JsonElement JsonSchema => inputSchema;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = arguments;
        return ValueTask.FromResult<object?>(new BlockedToolResult(
            "tool_requires_controlled_mode",
            $"Tool '{name}' is declared for schema inspection and Tomur-controlled execution only.",
            actions));
    }
}

public static class AgentToolResultJson
{
    public static JsonElement ToJsonElement(object? value)
    {
        if (value is null)
        {
            return JsonSerializer.SerializeToElement((string?)null, AppJsonSerializerContext.Default.String);
        }

        return value switch
        {
            RuntimeDiagnoseToolResult result => JsonSerializer.SerializeToElement(
                result,
                AppJsonSerializerContext.Default.RuntimeDiagnoseToolResult),
            AgentToolMapResponse result => JsonSerializer.SerializeToElement(
                result,
                AppJsonSerializerContext.Default.AgentToolMapResponse),
            AgentToolExecutionResult result => JsonSerializer.SerializeToElement(
                result,
                AppJsonSerializerContext.Default.AgentToolExecutionResult),
            FileSearchToolResult result => JsonSerializer.SerializeToElement(
                result,
                AppJsonSerializerContext.Default.FileSearchToolResult),
            Tomur.Native.NativeBundlePrepareResult result => JsonSerializer.SerializeToElement(
                result,
                AppJsonSerializerContext.Default.NativeBundlePrepareResult),
            RuntimeStatusResponse result => JsonSerializer.SerializeToElement(
                result,
                AppJsonSerializerContext.Default.RuntimeStatusResponse),
            BlockedToolResult result => JsonSerializer.SerializeToElement(
                result,
                AppJsonSerializerContext.Default.BlockedToolResult),
            JsonElement result => result.Clone(),
            _ => JsonSerializer.SerializeToElement(
                new BlockedToolResult(
                    "unsupported_tool_result",
                    $"Tool result type '{value.GetType().FullName}' is not registered for source-generated JSON.",
                    ["Register the result type in AppJsonSerializerContext before exposing this tool."]),
                AppJsonSerializerContext.Default.BlockedToolResult)
        };
    }
}

internal static class ToolJsonSchema
{
    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

public sealed record RuntimeDiagnoseToolResult(
    string Status,
    DateTimeOffset CheckedAt,
    RuntimeDiagnostic Runtime,
    string NativeBundleStatus,
    IReadOnlyList<DirectoryState> Directories,
    Tomur.Storage.LocalDatabaseState Database,
    Tomur.Storage.ApiKeyStoreState ApiKeys,
    DiskState Disk,
    ProxyState Proxy,
    PortState Port,
    IReadOnlyList<DiagnosticItem> Diagnostics);

public sealed record BlockedToolResult(
    string Code,
    string Message,
    IReadOnlyList<string> Actions);
