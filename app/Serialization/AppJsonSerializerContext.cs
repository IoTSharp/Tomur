using System.Text.Json.Serialization;
using Tomur.Api;
using Tomur.Api.OpenAI;
using Tomur.Api.Ollama;
using Tomur.Config;
using Tomur.Runtime;
using Tomur.Services;
using Tomur.Storage;

namespace Tomur.Serialization;

[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(RootResponse))]
[JsonSerializable(typeof(VersionResponse))]
[JsonSerializable(typeof(OpenAiModelListResponse))]
[JsonSerializable(typeof(OpenAiModelResponse))]
[JsonSerializable(typeof(OpenAiChatCompletionRequest))]
[JsonSerializable(typeof(OpenAiChatMessage))]
[JsonSerializable(typeof(OpenAiErrorResponse))]
[JsonSerializable(typeof(OpenAiError))]
[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(OllamaChatMessage))]
[JsonSerializable(typeof(OllamaErrorResponse))]
[JsonSerializable(typeof(LocalConfiguration))]
[JsonSerializable(typeof(ServerConfiguration))]
[JsonSerializable(typeof(PathConfiguration))]
[JsonSerializable(typeof(RuntimeConfiguration))]
[JsonSerializable(typeof(ConfigurationState))]
[JsonSerializable(typeof(ServerOptions))]
[JsonSerializable(typeof(RuntimeDiagnostic))]
[JsonSerializable(typeof(RuntimeStatusResponse))]
[JsonSerializable(typeof(DoctorReport))]
[JsonSerializable(typeof(SystemSnapshot))]
[JsonSerializable(typeof(DirectoryState))]
[JsonSerializable(typeof(DiskState))]
[JsonSerializable(typeof(ProxyState))]
[JsonSerializable(typeof(PortState))]
[JsonSerializable(typeof(DiagnosticItem))]
[JsonSerializable(typeof(LocalDatabaseState))]
[JsonSerializable(typeof(ApiKeyStoreState))]
[JsonSerializable(typeof(ApiKeyRecord))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
public sealed partial class AppJsonSerializerContext : JsonSerializerContext;
