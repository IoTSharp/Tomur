using System.Text.Json;
using Microsoft.Extensions.AI;
using Tomur.Runtime;

namespace Tomur.Agents;

public sealed class RuntimeDiagnoseFunction : AIFunction
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

public sealed class ToolMapFunction : AIFunction
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
        return ValueTask.FromResult<object?>(agentRuntime.GetToolMap());
    }
}

public sealed class BlockedToolFunction : AIFunction
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
        return ValueTask.FromResult<object?>(new BlockedToolResult(code, message, actions));
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
