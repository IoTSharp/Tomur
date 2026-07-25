using System.Text.Json.Serialization;

namespace Tomur.PlateRecognition;

/// <summary>车牌识别工具返回的结构化候选集合。</summary>
public sealed record PlateRecognitionData(
    [property: JsonPropertyName("results")] IReadOnlyList<PlateRecognitionCandidate> Results);

/// <summary>HyperLPR3 返回并经 Tomur 规范化后的单个车牌候选。</summary>
public sealed record PlateRecognitionCandidate(
    [property: JsonPropertyName("plate_number"), JsonRequired] string PlateNumber,
    [property: JsonPropertyName("plate_type")] string? PlateType,
    [property: JsonPropertyName("plate_color_code")] string? PlateColorCode,
    [property: JsonPropertyName("vehicle_id")] string? VehicleId,
    [property: JsonPropertyName("recognition_confidence"), JsonRequired] double RecognitionConfidence,
    [property: JsonPropertyName("detection_confidence")] double? DetectionConfidence,
    [property: JsonPropertyName("box")] IReadOnlyList<int>? Box);

/// <summary>原生桥接 JSON 的内部反序列化信封。</summary>
internal sealed record PlateRecognitionNativePayload(
    [property: JsonPropertyName("results")] IReadOnlyList<PlateRecognitionCandidate?>? Results);

/// <summary>仅为原生车牌桥接响应生成 AOT 友好的 JSON 元数据。</summary>
[JsonSerializable(typeof(PlateRecognitionNativePayload))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
internal sealed partial class PlateRecognitionJsonSerializerContext : JsonSerializerContext;

/// <summary>车牌识别原生组件和模型资产的当前就绪状态。</summary>
internal sealed record PlateRecognitionRuntimeStatus(
    string Status,
    bool Callable,
    string? Model,
    string ModelDirectory,
    string Message,
    IReadOnlyList<string> Actions);

/// <summary>一次车牌识别的结构化结果和运行诊断。</summary>
internal sealed record PlateRecognitionOperationResult(
    PlateRecognitionData Data,
    TimeSpan Elapsed,
    IReadOnlyList<string> Diagnostics);
