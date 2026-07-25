using System.Runtime.InteropServices;
using System.Text.Json;
using Tomur.Config;
using Tomur.Inference;
using Tomur.Native;

namespace Tomur.PlateRecognition;

/// <summary>管理 HyperLPR3/MNN 资产检查、原生调用和公开结果规范化。</summary>
public sealed class PlateRecognitionService
{
    internal const string ModelId = "hyperlpr3-r2-mobile";
    private const string ComponentId = "plate";
    private const string LibraryName = "tomur-plate";
    private const int MaximumImageBytes = 20 * 1024 * 1024;
    private const long MaximumDecodedPixels = 50L * 1000L * 1000L;
    private static readonly string[] RequiredModelFiles =
    [
        "b320_backbone_h.mnn",
        "b320_header_h.mnn",
        "b640x_backbone_h.mnn",
        "b640x_head_h.mnn",
        "litemodel_cls_96xh.mnn",
        "rpv3_mdict_160h.mnn"
    ];

    private readonly DataPaths paths;
    private readonly LlamaImportResolver importResolver;
    private readonly INativeLibraryResolver libraryResolver;

    /// <summary>创建使用 Tomur 托管 runtime 和 models 目录的车牌识别服务。</summary>
    public PlateRecognitionService(
        DataPaths paths,
        LlamaImportResolver importResolver,
        INativeLibraryResolver libraryResolver)
    {
        this.paths = paths;
        this.importResolver = importResolver;
        this.libraryResolver = libraryResolver;
    }

    /// <summary>检查原生库和六个 HyperLPR3 r2_mobile 模型文件是否可用。</summary>
    internal PlateRecognitionRuntimeStatus GetStatus()
    {
        var modelDirectory = ResolveModelDirectory();
        var missingModels = RequiredModelFiles
            .Where(fileName => !IsNonEmptyFile(Path.Combine(modelDirectory, fileName)))
            .ToArray();

        NativeLibraryResolution resolution;
        try
        {
            resolution = libraryResolver.Resolve(ComponentId, LibraryName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _ = exception;
            return new PlateRecognitionRuntimeStatus(
                "error",
                false,
                missingModels.Length == 0 ? ModelId : null,
                modelDirectory,
                "Plate recognition runtime state could not be inspected.",
                ["Run tomur doctor and inspect the native bundle manifest before retrying."]);
        }

        if (!resolution.Exists ||
            resolution.ChecksumStatus == "mismatch" ||
            string.Equals(resolution.ComponentStatus, "error", StringComparison.OrdinalIgnoreCase))
        {
            return new PlateRecognitionRuntimeStatus(
                "not_found",
                false,
                missingModels.Length == 0 ? ModelId : null,
                modelDirectory,
                resolution.Message,
                [
                    "Build or install the tomur-plate native bridge for the current RID.",
                    "Run tomur native prepare and inspect the plate component with tomur doctor."
                ]);
        }

        if (missingModels.Length > 0)
        {
            return new PlateRecognitionRuntimeStatus(
                "not_found",
                false,
                null,
                modelDirectory,
                $"HyperLPR3 r2_mobile model assets are incomplete ({missingModels.Length} file(s) missing).",
                [
                    $"Install the six HyperLPR3 r2_mobile .mnn files under '{modelDirectory}'.",
                    "Review the upstream model license and provenance before production redistribution."
                ]);
        }

        return new PlateRecognitionRuntimeStatus(
            "ready",
            true,
            ModelId,
            modelDirectory,
            "HyperLPR3/MNN plate recognition runtime and r2_mobile model assets are ready.",
            []);
    }

    /// <summary>同步调用本地原生桥接，并返回经过边界校验的结构化候选。</summary>
    internal unsafe PlateRecognitionOperationResult Recognize(
        ReadOnlySpan<byte> imageBytes,
        int maximumResults,
        double minimumConfidence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateArguments(imageBytes, maximumResults, minimumConfidence);

        var status = GetStatus();
        if (!status.Callable)
        {
            throw new InferenceException(
                "plate_runtime_unavailable",
                status.Message,
                status.Actions);
        }

        importResolver.Register();
        try
        {
            fixed (byte* imagePointer = imageBytes)
            {
                using var resultHandle = new PlateRecognitionResultHandle(
                    PlateRecognitionNativeMethods.RecognizeImage(
                        status.ModelDirectory,
                        imagePointer,
                        checked((nuint)imageBytes.Length),
                        maximumResults,
                        (float)minimumConfidence));
                cancellationToken.ThrowIfCancellationRequested();
                return ReadResult(resultHandle, maximumResults, minimumConfidence);
            }
        }
        catch (InferenceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new InferenceException(
                "plate_native_runtime_unavailable",
                "The tomur-plate native bridge could not be loaded for the current runtime.",
                [
                    "Run tomur native prepare to repair the managed native bundle.",
                    "Use tomur doctor to inspect the plate component and its shared dependencies."
                ],
                exception);
        }
    }

    /// <summary>解析原生 JSON，并强制执行候选数量、置信度和业务色码边界。</summary>
    internal static PlateRecognitionData ParseNativePayload(
        string json,
        int maximumResults,
        double minimumConfidence)
    {
        PlateRecognitionNativePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(
                json,
                PlateRecognitionJsonSerializerContext.Default.PlateRecognitionNativePayload);
        }
        catch (JsonException exception)
        {
            throw new InferenceException(
                "plate_native_contract_invalid",
                "The plate recognition bridge returned invalid JSON.",
                ["Inspect the tomur-plate bridge version and rebuild it against the current ABI."],
                exception);
        }

        if (payload?.Results is null)
        {
            throw new InferenceException(
                "plate_native_contract_invalid",
                "The plate recognition bridge response is missing the results array.",
                ["Inspect the tomur-plate bridge version and rebuild it against the current ABI."]);
        }

        var results = payload.Results
            .Where(static candidate => IsValidCandidate(candidate))
            .Select(static candidate => candidate!)
            .Where(candidate => candidate.RecognitionConfidence >= minimumConfidence)
            .Select(static candidate => NormalizeCandidate(candidate))
            .OrderByDescending(static candidate => candidate.RecognitionConfidence)
            .Take(maximumResults)
            .ToArray();
        return new PlateRecognitionData(results);
    }

    /// <summary>读取原生结果结构，错误响应只暴露有界诊断文本。</summary>
    private static PlateRecognitionOperationResult ReadResult(
        PlateRecognitionResultHandle handle,
        int maximumResults,
        double minimumConfidence)
    {
        if (handle.IsInvalid)
        {
            throw new InferenceException(
                "plate_native_runtime_failure",
                "The plate recognition bridge returned an empty result handle.",
                ["Use tomur doctor to inspect the plate runtime before retrying."]);
        }

        var nativeResult = Marshal.PtrToStructure<PlateRecognitionNativeResult>(handle.DangerousGetHandle());
        if (nativeResult.StatusCode != 0)
        {
            var detail = Marshal.PtrToStringUTF8(nativeResult.ErrorUtf8)?.Trim();
            throw new InferenceException(
                "plate_recognition_failed",
                string.IsNullOrWhiteSpace(detail)
                    ? $"Plate recognition failed with native status {nativeResult.StatusCode}."
                    : detail.Length <= 500 ? detail : detail[..500],
                ["Verify the image payload, HyperLPR3 model files, and tomur-plate native dependencies."]);
        }

        var json = Marshal.PtrToStringUTF8(nativeResult.JsonUtf8);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InferenceException(
                "plate_native_contract_invalid",
                "The plate recognition bridge returned an empty JSON payload.",
                ["Inspect the tomur-plate bridge version and rebuild it against the current ABI."]);
        }

        return new PlateRecognitionOperationResult(
            ParseNativePayload(json, maximumResults, minimumConfidence),
            TimeSpan.FromMilliseconds(Math.Max(0, nativeResult.ElapsedMs)),
            [
                "provider: HyperLPR3",
                "runtime: MNN",
                "model-profile: r2_mobile",
                "detection-confidence: unavailable-from-upstream-c-api"
            ]);
    }

    /// <summary>校验图片大小和公开工具参数，避免把无界输入传入 C++。</summary>
    internal static void ValidateArguments(
        ReadOnlySpan<byte> imageBytes,
        int maximumResults,
        double minimumConfidence)
    {
        if (imageBytes.IsEmpty)
        {
            throw new InferenceException(
                "invalid_request",
                "The plate recognition image payload is empty.",
                ["Send a non-empty JPEG, PNG, WebP or BMP image as a data URI."]);
        }

        if (imageBytes.Length > MaximumImageBytes)
        {
            throw new InferenceException(
                "invalid_request",
                $"The plate recognition image exceeds the {MaximumImageBytes} byte limit.",
                ["Resize or recompress the image before invoking plate.recognize."]);
        }

        if (!PlateImageHeaderReader.TryRead(imageBytes, out var dimensions))
        {
            throw new InferenceException(
                "invalid_request",
                "The plate recognition image header is not a supported JPEG, PNG, WebP or BMP image.",
                ["Send one complete JPEG, PNG, WebP or BMP image as a data URI."]);
        }

        if ((long)dimensions.Width * dimensions.Height > MaximumDecodedPixels)
        {
            throw new InferenceException(
                "invalid_request",
                $"The plate recognition image exceeds the {MaximumDecodedPixels} decoded-pixel limit.",
                ["Resize the image before invoking plate.recognize."]);
        }

        if (maximumResults is < 1 or > 10)
        {
            throw new InferenceException(
                "invalid_request",
                "The max_results field must be between 1 and 10.",
                ["Set max_results to an integer from 1 through 10."]);
        }

        if (!double.IsFinite(minimumConfidence) || minimumConfidence is < 0 or > 1)
        {
            throw new InferenceException(
                "invalid_request",
                "The min_confidence field must be between 0 and 1.",
                ["Set min_confidence to a finite number from 0 through 1."]);
        }
    }

    /// <summary>拒绝空车牌、异常置信度和过长原生字符串。</summary>
    private static bool IsValidCandidate(PlateRecognitionCandidate? candidate)
        => candidate is not null &&
            !string.IsNullOrWhiteSpace(candidate.PlateNumber) &&
            candidate.PlateNumber.Trim().Length <= 32 &&
            double.IsFinite(candidate.RecognitionConfidence) &&
            candidate.RecognitionConfidence is >= 0 and <= 1;

    /// <summary>重建 VehicleId，防止原生层返回不一致的业务组合值。</summary>
    private static PlateRecognitionCandidate NormalizeCandidate(PlateRecognitionCandidate candidate)
    {
        var plateNumber = candidate.PlateNumber.Trim();
        var colorCode = NormalizeColorCode(candidate.PlateColorCode);
        var plateType = string.IsNullOrWhiteSpace(candidate.PlateType)
            ? "unknown"
            : candidate.PlateType.Trim();
        double? detectionConfidence = candidate.DetectionConfidence is { } confidence &&
            double.IsFinite(confidence) &&
            confidence is >= 0 and <= 1
                ? confidence
                : null;
        var box = candidate.Box is { Count: 4 }
            ? candidate.Box.ToArray()
            : [];
        return new PlateRecognitionCandidate(
            plateNumber,
            plateType,
            colorCode,
            $"{plateNumber}_{colorCode}",
            candidate.RecognitionConfidence,
            detectionConfidence,
            box);
    }

    /// <summary>仅保留现有业务协议色码，未知类型统一使用 9。</summary>
    private static string NormalizeColorCode(string? colorCode)
    {
        var normalized = colorCode?.Trim();
        return normalized switch
        {
            "0" or "1" or "2" or "3" or "9" or "11" or "12" => normalized,
            _ => "9"
        };
    }

    /// <summary>解析不随部署目录变化的 HyperLPR3 模型位置。</summary>
    private string ResolveModelDirectory()
        => Path.Combine(paths.ModelsDirectory, "plate", "hyperlpr3", "r2_mobile");

    /// <summary>只有存在且非空的模型文件才视为可用。</summary>
    private static bool IsNonEmptyFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
