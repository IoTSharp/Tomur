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
        yield return isAotAudit
            ? Audited("http_api", "HTTP API", "Minimal API routes stay in the same local process. Known ASP.NET RequestDelegateGenerator issues are handled as build audit items, not by removing endpoints.")
            : Supported("http_api", "HTTP API", "Minimal API routes stay in the same local process.");
        yield return Supported("native_loader", "Native loader", "Native bundle manifest, prepare, probe and library load diagnostics stay source-generated JSON compatible.");
        yield return Supported("model_catalog", "Model catalog", "Catalog, installed manifest and local model visibility remain filesystem based.");
        yield return Supported("download", "Model download", "tomur pull keeps resume, checksum, proxy and local manifest behavior.");
        yield return Supported("chat_runtime", "Text runtime", "OpenAI/Ollama text endpoints continue to use the llama.cpp runtime boundary.");
        yield return new ReleaseCapabilityStatus(
            "multimodal_adapters",
            "Multimodal adapters",
            isAotAudit ? "audit" : "diagnosed",
            "R8 adapters stay available when their native libraries and model assets are present; missing backends return structured diagnostics.",
            ["/api/runtime/multimodal", "tomur native prepare"]);
        yield return isAotAudit
            ? Audited("agent_framework", "Agent Framework orchestration", "Agent Framework chat, tools and read-only workflow remain part of the build surface; R12 tracks any AOT incompatibility as an audit blocker instead of silently disabling it.")
            : Supported("agent_framework", "Agent Framework orchestration", "Full self-contained builds keep Microsoft Agent Framework chat, tools and read-only workflow orchestration.");
        yield return isAotAudit
            ? Audited("open_telemetry", "OpenTelemetry export", "The opt-in OTLP exporter remains part of the build surface; R12 records warnings or blockers explicitly instead of replacing exporter behavior.")
            : Supported("open_telemetry", "OpenTelemetry export", "OTLP export remains opt-in through TOMUR_AGENTS_OTEL_EXPORTER and TOMUR_AGENTS_OTEL_ENDPOINT.");
        yield return Supported("web_static_hosting", "Web static hosting", "Embedded app/wwwroot assets continue to be served by the local HTTP host.");
        yield return new ReleaseCapabilityStatus(
            "native_bundle_manifest",
            "Native bundle manifest",
            isAotAudit ? "audit" : "diagnosed",
            "Release packages keep native/runtimes outside the single file and prepare them into the Tomur data runtime directory.",
            ["Ensure native/runtimes/<rid>/native is present in the publish output before distribution."]);
    }

    private static ReleaseCapabilityStatus Supported(string id, string name, string message)
        => new(id, name, "supported", message, []);

    private static ReleaseCapabilityStatus Audited(string id, string name, string message)
        => new(id, name, "audit", message, ["Run the native-aot-audit publish profile and record any warning or blocker in docs/r12-aot-release-audit.md."]);

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
                "This profile keeps the full Tomur build surface and treats Native AOT as an audit target.",
                "R12 should reduce warnings and blockers without deleting product capabilities merely to satisfy AOT.",
                "The self-contained-single-file profile remains the primary full-capability release path until Native AOT is verified."
            ]
            : [
                "This profile keeps the full Tomur feature set in a self-contained single-file publish.",
                "Native AOT verification remains tracked separately by the native-aot-audit profile and R12 audit matrix."
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
