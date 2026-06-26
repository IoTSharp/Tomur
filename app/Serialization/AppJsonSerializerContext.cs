using System.Text.Json.Serialization;
using Tomur.Api;
using Tomur.Api.OpenAI;
using Tomur.Api.Ollama;
using Tomur.Runtime;
using Tomur.Services;

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
[JsonSerializable(typeof(RuntimeDiagnostic))]
[JsonSerializable(typeof(DoctorReport))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
public sealed partial class AppJsonSerializerContext : JsonSerializerContext;
