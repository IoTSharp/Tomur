using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Tomur.Config;

namespace Tomur.Runtime;

public sealed class ReleaseProfileService
{
    public ReleaseProfileStatus GetStatus()
    {
        var profile = ResolveProfile();
        var capabilities = BuildCapabilities(profile).ToArray();
        var status = capabilities.Any(static capability => capability.Status == "blocked")
            ? "blocked"
            : capabilities.Any(static capability => capability.Status is "fallback" or "diagnosed")
                ? "partial"
                : "supported";

        return new ReleaseProfileStatus(
            status,
            DateTimeOffset.UtcNow,
            profile,
            Defaults.Version,
            RuntimeInformation.RuntimeIdentifier,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version.ToString(),
            capabilities,
            BuildPublishCommands(profile),
            BuildNotes(profile));
    }

    private static string ResolveProfile()
    {
#if TOMUR_NATIVE_AOT
        return "native-aot-audit";
#else
        return "self-contained-single-file";
#endif
    }

    private static IEnumerable<ReleaseCapabilityStatus> BuildCapabilities(string profile)
    {
        var isAotAudit = string.Equals(profile, "native-aot-audit", StringComparison.OrdinalIgnoreCase);
        yield return Supported("core_cli", "Core CLI", "Program.cs command dispatch and local data directory handling stay in the single Tomur entry.");
        yield return Supported("http_api", "HTTP API", "Minimal API routes stay in the same local process.");
        yield return Supported("native_loader", "Native loader", "Native bundle manifest, prepare, probe and library load diagnostics stay source-generated JSON compatible.");
        yield return Supported("model_catalog", "Model catalog", "Catalog, installed manifest and local model visibility remain filesystem based.");
        yield return Supported("download", "Model download", "tomur pull keeps resume, checksum, proxy and local manifest behavior.");
        yield return Supported("chat_runtime", "Text runtime", "OpenAI/Ollama text endpoints continue to use the llama.cpp runtime boundary.");
        yield return new ReleaseCapabilityStatus(
            "multimodal_adapters",
            "Multimodal adapters",
            isAotAudit ? "supported" : "diagnosed",
            "R8 adapters stay available when their native libraries and model assets are present; missing backends return structured diagnostics.",
            ["/api/runtime/multimodal", "tomur native prepare"]);
        yield return Supported("agent_framework", "Agent Framework orchestration", "Microsoft Agent Framework chat, tools and read-only workflow orchestration remain in the release surface.");
        yield return Supported("open_telemetry", "OpenTelemetry export", "OTLP export remains opt-in through TOMUR_AGENTS_OTEL_EXPORTER and TOMUR_AGENTS_OTEL_ENDPOINT.");
        yield return Supported("web_static_hosting", "Web static hosting", "Embedded app/wwwroot assets continue to be served by the local HTTP host.");
        yield return new ReleaseCapabilityStatus(
            "native_bundle_manifest",
            "Native bundle manifest",
            "diagnosed",
            "Release packages keep native/runtimes outside the single file and prepare them into the Tomur data runtime directory; current inventory is recorded in docs/r12-native-bundle-inventory.md.",
            ["Ensure native/runtimes/<rid>/native is present in the publish output before distribution."]);
    }

    private static ReleaseCapabilityStatus Supported(string id, string name, string message)
        => new(id, name, "supported", message, []);

    private static IReadOnlyList<string> BuildPublishCommands(string profile)
        => string.Equals(profile, "native-aot-audit", StringComparison.OrdinalIgnoreCase)
            ? [
                "dotnet publish app/Tomur.csproj -c Release -r win-x64 -p:PublishProfile=native-aot-audit",
                "dotnet publish app/Tomur.csproj -c Release -r linux-x64 -p:PublishProfile=native-aot-audit",
                "dotnet publish app/Tomur.csproj -c Release -r osx-x64 -p:PublishProfile=native-aot-audit",
                "dotnet publish app/Tomur.csproj -c Release -r osx-arm64 -p:PublishProfile=native-aot-audit"
            ]
            : [
                "dotnet publish app/Tomur.csproj -c Release -r win-x64 -p:PublishProfile=self-contained-single-file",
                "dotnet publish app/Tomur.csproj -c Release -r linux-x64 -p:PublishProfile=self-contained-single-file",
                "dotnet publish app/Tomur.csproj -c Release -r osx-x64 -p:PublishProfile=self-contained-single-file",
                "dotnet publish app/Tomur.csproj -c Release -r osx-arm64 -p:PublishProfile=self-contained-single-file"
            ];

    private static IReadOnlyList<string> BuildNotes(string profile)
        => string.Equals(profile, "native-aot-audit", StringComparison.OrdinalIgnoreCase)
            ? [
                "This profile keeps the full Tomur build surface and publishes through Native AOT.",
                "R12 Native AOT warnings are cleared; remaining release work is platform package evidence and service smoke.",
                "The self-contained-single-file profile remains as a compatibility release path with the same public command and API surface."
            ]
            : [
                "This profile keeps the full Tomur feature set in a self-contained single-file publish.",
                "Native AOT publishing is tracked by the native-aot-audit profile and R12 release records."
            ];
}

public sealed record ReleaseProfileStatus(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("runtime_identifier")] string RuntimeIdentifier,
    [property: JsonPropertyName("architecture")] string Architecture,
    [property: JsonPropertyName("dotnet_version")] string DotnetVersion,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<ReleaseCapabilityStatus> Capabilities,
    [property: JsonPropertyName("publish_commands")] IReadOnlyList<string> PublishCommands,
    [property: JsonPropertyName("notes")] IReadOnlyList<string> Notes);

public sealed record ReleaseCapabilityStatus(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("actions")] IReadOnlyList<string> Actions);
