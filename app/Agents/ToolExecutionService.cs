using System.Globalization;
using System.Text.Json;
using Tomur.Api;
using Tomur.Config;
using Tomur.Inference;
using Tomur.Multimodal;
using Tomur.Native;
using Tomur.Runtime;
using Tomur.Serialization;

namespace Tomur.Agents;

public sealed class ToolExecutionService
{
    private readonly LocalModelCatalog modelCatalog;
    private readonly MultimodalExecutionService multimodalExecution;
    private readonly IsolatedImageGenerationService isolatedImageGeneration;
    private readonly RuntimeDiagnosticsProvider diagnosticsProvider;
    private readonly DataPaths paths;
    private readonly FileIndexStore fileIndex;
    private readonly INativeBundlePreparer nativeBundlePreparer;
    private readonly LocalInferenceService inferenceService;

    public ToolExecutionService(
        LocalModelCatalog modelCatalog,
        MultimodalExecutionService multimodalExecution,
        IsolatedImageGenerationService isolatedImageGeneration,
        RuntimeDiagnosticsProvider diagnosticsProvider,
        DataPaths paths,
        FileIndexStore fileIndex,
        INativeBundlePreparer nativeBundlePreparer,
        LocalInferenceService inferenceService)
    {
        this.modelCatalog = modelCatalog;
        this.multimodalExecution = multimodalExecution;
        this.isolatedImageGeneration = isolatedImageGeneration;
        this.diagnosticsProvider = diagnosticsProvider;
        this.paths = paths;
        this.fileIndex = fileIndex;
        this.nativeBundlePreparer = nativeBundlePreparer;
        this.inferenceService = inferenceService;
    }

    public async Task<AgentToolExecutionResult> ExecuteAsync(
        AgentToolDescriptor descriptor,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return descriptor.Name switch
        {
            "image.generate" => await ExecuteImageGenerationAsync(descriptor, arguments, cancellationToken).ConfigureAwait(false),
            "vision.analyze" => ExecuteVisionAnalysis(descriptor, arguments, cancellationToken),
            "ocr.recognize" => ExecuteOcr(descriptor, arguments, cancellationToken),
            "audio.transcribe" => ExecuteAudioTranscription(descriptor, arguments, cancellationToken),
            "audio.speak" => ExecuteSpeechSynthesis(descriptor, arguments, cancellationToken),
            "files.search" => ExecuteFileSearch(descriptor, arguments, cancellationToken),
            "runtime.repair" => ExecuteRuntimeRepair(descriptor, arguments, cancellationToken),
            _ => throw new InferenceException(
                "tool_not_callable",
                $"Tool '{descriptor.Name}' is not connected to a local R9 adapter.",
                ["Use GET /api/agents/tools to inspect callable tools and routes."])
        };
    }

    private async Task<AgentToolExecutionResult> ExecuteImageGenerationAsync(
        AgentToolDescriptor descriptor,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        var model = ResolveModel(arguments, "model", "image", "image-generation", descriptor.Route);
        var prompt = RequireString(arguments, "prompt");
        var width = 1024;
        var height = 1024;
        if (TryReadString(arguments, "size") is { } size &&
            !TryParseImageSize(size, out width, out height, out var sizeError))
        {
            throw InvalidRequest(sizeError);
        }

        var isFlux2Klein = IsFlux2Klein(model);
        var steps = ClampInt(TryReadInt(arguments, "steps"), isFlux2Klein ? 4 : 20, 1, 100);
        var cfgScale = ClampFloat(TryReadFloat(arguments, "cfg_scale"), isFlux2Klein ? 1.0f : 7.0f, 1.0f, 20.0f);
        var seed = TryReadLong(arguments, "seed") ?? -1;
        var options = new ImageGenerationOptions(
            prompt,
            TryReadString(arguments, "negative_prompt"),
            width,
            height,
            steps,
            cfgScale,
            seed,
            TryReadFloat(arguments, "distilled_guidance"),
            TryReadFloat(arguments, "flow_shift"),
            TryReadString(arguments, "sample_method") ?? (isFlux2Klein ? "euler" : null),
            TryReadString(arguments, "scheduler"));

        try
        {
            var result = await isolatedImageGeneration.GenerateImageAsync(model, options, cancellationToken)
                .ConfigureAwait(false);
            var artifact = WriteArtifact(
                result.Bytes,
                "image",
                ResolveImageMimeType(result.Format),
                result.Format,
                "agent-image");
            return new AgentToolExecutionResult(
                "ok",
                descriptor.Name,
                descriptor.Backend,
                model.Id,
                descriptor.Route,
                null,
                artifact,
                (long)Math.Round(result.Elapsed.TotalMilliseconds),
                result.Diagnostics,
                null);
        }
        catch (InferenceException exception)
        {
            return CreateFailureResult(descriptor, model.Id, diagnosticsProvider.GetRuntimeFailure(model.Id, exception));
        }
    }

    private AgentToolExecutionResult ExecuteVisionAnalysis(
        AgentToolDescriptor descriptor,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = ResolveModel(arguments, "model", "vision", "vlm", descriptor.Route);
        var prompt = RequireString(arguments, "prompt");
        var images = RequireImages(arguments);
        var options = ResolveCompletionOptions(arguments);

        try
        {
            var result = multimodalExecution.AnalyzeVision(model, prompt, images, options, cancellationToken);
            return CreateTextResult(descriptor, model.Id, result);
        }
        catch (InferenceException exception)
        {
            return CreateFailureResult(descriptor, model.Id, diagnosticsProvider.GetRuntimeFailure(model.Id, exception));
        }
    }

    private AgentToolExecutionResult ExecuteOcr(
        AgentToolDescriptor descriptor,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = ResolveModel(arguments, "model", "ocr", "ocr", descriptor.Route);
        var image = RequireImage(arguments);
        var options = ResolveCompletionOptions(arguments);

        try
        {
            var result = multimodalExecution.AnalyzeOcr(
                model,
                image,
                TryReadString(arguments, "prompt"),
                TryReadString(arguments, "language"),
                options,
                cancellationToken);
            return CreateTextResult(descriptor, model.Id, result);
        }
        catch (InferenceException exception)
        {
            return CreateFailureResult(descriptor, model.Id, diagnosticsProvider.GetRuntimeFailure(model.Id, exception));
        }
    }

    private AgentToolExecutionResult ExecuteAudioTranscription(
        AgentToolDescriptor descriptor,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = ResolveModel(arguments, "model", "audio", "asr", descriptor.Route);
        var audio = RequireAudio(arguments, out _);

        try
        {
            var result = multimodalExecution.TranscribeAudio(
                model,
                audio,
                TryReadString(arguments, "language"),
                cancellationToken);
            return CreateTextResult(descriptor, model.Id, result);
        }
        catch (InferenceException exception)
        {
            return CreateFailureResult(descriptor, model.Id, diagnosticsProvider.GetRuntimeFailure(model.Id, exception));
        }
    }

    private AgentToolExecutionResult ExecuteSpeechSynthesis(
        AgentToolDescriptor descriptor,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = ResolveModel(arguments, "model", "audio-output", "tts", descriptor.Route);
        var input = RequireString(arguments, "input");
        var responseFormat = TryReadString(arguments, "response_format") ?? "wav";
        if (!string.Equals(responseFormat, "wav", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidRequest("The response_format field currently supports only 'wav'.");
        }

        var options = new SpeechSynthesisOptions(
            input,
            TryReadString(arguments, "voice"),
            "wav",
            ClampDouble(TryReadDouble(arguments, "speed"), 1.0, 0.25, 4.0),
            TryReadString(arguments, "language"));

        try
        {
            var result = multimodalExecution.SynthesizeSpeech(model, options, cancellationToken);
            var artifact = WriteArtifact(
                result.Bytes,
                "audio",
                result.MediaType,
                result.Format,
                "agent-speech",
                result.SampleRate);
            return new AgentToolExecutionResult(
                "ok",
                descriptor.Name,
                descriptor.Backend,
                model.Id,
                descriptor.Route,
                null,
                artifact,
                (long)Math.Round(result.Elapsed.TotalMilliseconds),
                result.Diagnostics,
                null);
        }
        catch (InferenceException exception)
        {
            return CreateFailureResult(descriptor, model.Id, diagnosticsProvider.GetRuntimeFailure(model.Id, exception));
        }
    }

    private AgentToolExecutionResult ExecuteFileSearch(
        AgentToolDescriptor descriptor,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = DateTimeOffset.UtcNow;
        var parsed = Deserialize(arguments, AppJsonSerializerContext.Default.FileSearchToolArguments) ??
            new FileSearchToolArguments(null, null, null, null, null, null);
        var result = fileIndex.Search(parsed);
        return new AgentToolExecutionResult(
            result.Status,
            descriptor.Name,
            descriptor.Backend,
            null,
            descriptor.Route,
            result.Context,
            null,
            (long)Math.Round((DateTimeOffset.UtcNow - started).TotalMilliseconds),
            result.Diagnostics,
            null)
        {
            Data = AgentToolResultJson.ToJsonElement(result)
        };
    }

    private AgentToolExecutionResult ExecuteRuntimeRepair(
        AgentToolDescriptor descriptor,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = DateTimeOffset.UtcNow;
        var action = RequireString(arguments, "action").Trim().ToLowerInvariant().Replace('-', '.').Replace('_', '.');
        object result;
        IReadOnlyList<string> diagnostics;
        switch (action)
        {
            case "native.prepare":
            case "prepare.native":
                result = nativeBundlePreparer.Prepare();
                diagnostics = ["repair-action: native.prepare", "scope: explicit-confirmed-runtime-repair"];
                break;
            case "session.unload":
            case "unload.session":
                inferenceService.Unload();
                result = diagnosticsProvider.GetRuntimeStatus();
                diagnostics = ["repair-action: session.unload", "scope: explicit-confirmed-runtime-repair"];
                break;
            default:
                throw new InferenceException(
                    "unsupported_runtime_repair_action",
                    $"Runtime repair action '{action}' is not enabled in this R9 boundary.",
                    [
                        "Use action native.prepare to repair managed native runtime files.",
                        "Use action session.unload to unload the current local inference session.",
                        "Run tomur doctor for read-only diagnostics before requesting a repair action."
                    ]);
        }

        return new AgentToolExecutionResult(
            "ok",
            descriptor.Name,
            descriptor.Backend,
            null,
            descriptor.Route,
            null,
            null,
            (long)Math.Round((DateTimeOffset.UtcNow - started).TotalMilliseconds),
            diagnostics,
            null)
        {
            Data = AgentToolResultJson.ToJsonElement(result)
        };
    }

    private LocalModelDescriptor ResolveModel(
        JsonElement? arguments,
        string propertyName,
        string capability,
        string backendId,
        string? route)
    {
        var requestedModel = TryReadString(arguments, propertyName);
        if (!string.IsNullOrWhiteSpace(requestedModel))
        {
            var requested = modelCatalog.Find(requestedModel);
            if (requested is null)
            {
                throw new InferenceException(
                    "model_not_downloaded",
                    $"The requested model '{requestedModel}' is not available in the local models directory.",
                    [
                        "Run tomur pull recommended to install the default local model package set.",
                        "Use /v1/models or /api/models/installed to inspect models visible to Tomur."
                    ]);
            }

            if (!HasCapability(requested, capability))
            {
                throw new InferenceException(
                    "model_capability_mismatch",
                    $"The requested model '{requested.Id}' does not expose the '{capability}' capability required by {route ?? "this tool"}.",
                    [
                        "Use /v1/models or /api/models/installed to inspect model capabilities.",
                        "Run tomur pull recommended to install the default local multimodal model packages."
                    ]);
            }

            return requested;
        }

        var model = modelCatalog.ListModels().FirstOrDefault(item => HasCapability(item, capability));
        if (model is not null)
        {
            return model;
        }

        var diagnostic = multimodalExecution.CreateUnavailableDiagnostic(backendId, null);
        throw new InferenceException(diagnostic.Code, diagnostic.Message, diagnostic.Actions);
    }

    private AgentToolArtifact WriteArtifact(
        byte[] bytes,
        string type,
        string? mediaType,
        string? format,
        string fileNamePrefix,
        int? sampleRate = null)
    {
        var directory = Path.Combine(paths.DataDirectory, "files", "agents");
        Directory.CreateDirectory(directory);
        var extension = ResolveArtifactExtension(type, mediaType, format);
        var fileName = string.Concat(
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture),
            "-",
            fileNamePrefix,
            "-",
            Guid.NewGuid().ToString("N"),
            extension);
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        File.WriteAllBytes(path, bytes);
        return new AgentToolArtifact(type, path, mediaType, format, bytes.LongLength, sampleRate);
    }

    private static AgentToolExecutionResult CreateTextResult(
        AgentToolDescriptor descriptor,
        string model,
        NativeOperationResult result)
        => new(
            "ok",
            descriptor.Name,
            descriptor.Backend,
            model,
            descriptor.Route,
            result.Text,
            null,
            (long)Math.Round(result.Elapsed.TotalMilliseconds),
            result.Diagnostics,
            null);

    private static AgentToolExecutionResult CreateFailureResult(
        AgentToolDescriptor descriptor,
        string? model,
        RuntimeDiagnostic diagnostic)
        => new(
            "error",
            descriptor.Name,
            descriptor.Backend,
            model,
            descriptor.Route,
            null,
            null,
            0,
            [$"{diagnostic.Code}: {diagnostic.Message}"],
            diagnostic);

    private static IReadOnlyList<ImageInputBytes> RequireImages(JsonElement? arguments)
    {
        if (arguments is null ||
            !TryGetProperty(arguments.Value, "images", out var imagesElement) ||
            imagesElement.ValueKind != JsonValueKind.Array)
        {
            return [RequireImage(arguments)];
        }

        var images = new List<ImageInputBytes>();
        foreach (var item in imagesElement.EnumerateArray())
        {
            images.Add(ReadImage(item));
        }

        if (images.Count == 0)
        {
            throw InvalidRequest("At least one image is required.");
        }

        if (images.Count > CompatibilityProtocolLimits.MaxImageCount)
        {
            throw InvalidRequest($"Too many images. Limit: {CompatibilityProtocolLimits.MaxImageCount}.");
        }

        return images;
    }

    private static ImageInputBytes RequireImage(JsonElement? arguments)
    {
        if (arguments is null)
        {
            throw InvalidRequest("An image object or data URI is required.");
        }

        if (TryGetProperty(arguments.Value, "image", out var imageElement))
        {
            return ReadImage(imageElement);
        }

        if (TryGetProperty(arguments.Value, "data_uri", out _) ||
            TryGetProperty(arguments.Value, "image_url", out _) ||
            TryGetProperty(arguments.Value, "base64", out _))
        {
            return ReadImage(arguments.Value);
        }

        throw InvalidRequest("The image field is required.");
    }

    private static ImageInputBytes ReadImage(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var stringSource = element.GetString();
            return DecodeImageSource(stringSource, null, null);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidRequest("Image input must be an object, data URI or raw base64 string.");
        }

        var source = TryReadString(element, "data_uri") ??
            TryReadString(element, "image_url") ??
            TryReadString(element, "base64") ??
            TryReadString(element, "data");
        return DecodeImageSource(
            source,
            TryReadString(element, "media_type"),
            TryReadString(element, "detail"));
    }

    private static ImageInputBytes DecodeImageSource(string? source, string? fallbackMediaType, string? detail)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw InvalidRequest("Image input must include data_uri, image_url or base64.");
        }

        var trimmed = source.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            throw InvalidRequest("Remote image URLs are not fetched by local agent tools yet; send a data URI instead.");
        }

        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = trimmed.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0)
            {
                throw InvalidRequest("Data URI image input is missing the payload separator.");
            }

            var metadata = trimmed[5..comma];
            if (!metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidRequest("Data URI image input must use base64 encoding.");
            }

            var mediaType = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(static item => item.Contains('/', StringComparison.Ordinal))
                ?? fallbackMediaType;
            return DecodeImageBase64(trimmed[(comma + 1)..], mediaType, detail);
        }

        return DecodeImageBase64(trimmed, fallbackMediaType, detail);
    }

    private static ImageInputBytes DecodeImageBase64(string source, string? mediaType, string? detail)
    {
        try
        {
            var bytes = Convert.FromBase64String(source.Trim());
            if (bytes.Length == 0)
            {
                throw InvalidRequest("Image payload is empty.");
            }

            if (bytes.Length > CompatibilityProtocolLimits.MaxImageBytes)
            {
                throw InvalidRequest($"Image payload is too large. Limit: {CompatibilityProtocolLimits.MaxImageBytes} bytes.");
            }

            return new ImageInputBytes(bytes, mediaType, detail);
        }
        catch (FormatException exception)
        {
            throw InvalidRequest("Image payload is not valid base64.", exception);
        }
    }

    private static byte[] RequireAudio(JsonElement? arguments, out string? mediaType)
    {
        mediaType = TryReadString(arguments, "media_type") ?? TryReadString(arguments, "audio_media_type");
        var source = TryReadString(arguments, "audio_data_uri") ??
            TryReadString(arguments, "data_uri") ??
            TryReadString(arguments, "audio_base64") ??
            TryReadString(arguments, "base64");
        if (string.IsNullOrWhiteSpace(source))
        {
            throw InvalidRequest("Audio tool arguments require audio_data_uri or audio_base64.");
        }

        var trimmed = source.Trim();
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = trimmed.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0)
            {
                throw InvalidRequest("Audio data URI is missing the payload separator.");
            }

            var metadata = trimmed[5..comma];
            if (!metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                throw InvalidRequest("Audio data URI must use base64 encoding.");
            }

            mediaType = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(static item => item.Contains('/', StringComparison.Ordinal))
                ?? mediaType;
            trimmed = trimmed[(comma + 1)..];
        }

        try
        {
            var bytes = Convert.FromBase64String(trimmed);
            if (bytes.Length == 0)
            {
                throw InvalidRequest("Audio payload is empty.");
            }

            if (bytes.LongLength > CompatibilityProtocolLimits.MaxAudioBytes)
            {
                throw InvalidRequest($"Audio payload is too large. Limit: {CompatibilityProtocolLimits.MaxAudioBytes} bytes.");
            }

            return bytes;
        }
        catch (FormatException exception)
        {
            throw InvalidRequest("Audio payload is not valid base64.", exception);
        }
    }

    private static CompletionOptions ResolveCompletionOptions(JsonElement? arguments)
        => CompletionOptions.Default with
        {
            MaxOutputTokens = ClampInt(TryReadInt(arguments, "max_tokens"), CompletionOptions.Default.MaxOutputTokens, 1, 4096),
            Temperature = ClampFloat(TryReadFloat(arguments, "temperature"), CompletionOptions.Default.Temperature, 0.0f, 2.0f)
        };

    private static bool TryParseImageSize(string value, out int width, out int height, out string error)
    {
        var parts = value.Split(['x', 'X'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height))
        {
            width = 0;
            height = 0;
            error = "The size field must use WIDTHxHEIGHT format, for example 1024x1024.";
            return false;
        }

        if (width < 256 || height < 256 || width > 2048 || height > 2048 || width % 64 != 0 || height % 64 != 0)
        {
            error = "The size field must be between 256x256 and 2048x2048, with width and height divisible by 64.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string RequireString(JsonElement? arguments, string propertyName)
    {
        var value = TryReadString(arguments, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidRequest($"The {propertyName} field is required.");
        }

        return value;
    }

    private static string? TryReadString(JsonElement? arguments, string propertyName)
    {
        if (arguments is null)
        {
            return null;
        }

        return TryReadString(arguments.Value, propertyName);
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? NullIfWhiteSpace(property.GetString())
            : null;
    }

    private static int? TryReadInt(JsonElement? arguments, string propertyName)
    {
        if (arguments is null ||
            !TryGetProperty(arguments.Value, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static long? TryReadLong(JsonElement? arguments, string propertyName)
    {
        if (arguments is null ||
            !TryGetProperty(arguments.Value, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            JsonValueKind.String when long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static float? TryReadFloat(JsonElement? arguments, string propertyName)
    {
        if (arguments is null ||
            !TryGetProperty(arguments.Value, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetSingle(out var value) => value,
            JsonValueKind.String when float.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static double? TryReadDouble(JsonElement? arguments, string propertyName)
    {
        if (arguments is null ||
            !TryGetProperty(arguments.Value, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out property))
        {
            return true;
        }

        property = default;
        return false;
    }

    private static int ClampInt(int? value, int fallback, int min, int max)
        => Math.Clamp(value ?? fallback, min, max);

    private static float ClampFloat(float? value, float fallback, float min, float max)
    {
        if (value is null || float.IsNaN(value.Value) || float.IsInfinity(value.Value))
        {
            return fallback;
        }

        return Math.Clamp(value.Value, min, max);
    }

    private static double ClampDouble(double? value, double fallback, double min, double max)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return fallback;
        }

        return Math.Clamp(value.Value, min, max);
    }

    private static bool HasCapability(LocalModelDescriptor model, string capability)
        => model.Capabilities.Any(item => string.Equals(item, capability, StringComparison.OrdinalIgnoreCase));

    private static bool IsFlux2Klein(LocalModelDescriptor model)
        => model.Id.Contains("flux2-klein", StringComparison.OrdinalIgnoreCase) ||
            model.Name.Contains("FLUX.2 klein", StringComparison.OrdinalIgnoreCase) ||
            model.FileName.Contains("flux-2-klein", StringComparison.OrdinalIgnoreCase) ||
            model.PackageId?.Contains("flux2-klein", StringComparison.OrdinalIgnoreCase) == true;

    private static string ResolveImageMimeType(string format)
        => format.Trim().ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png"
        };

    private static string ResolveArtifactExtension(string type, string? mediaType, string? format)
    {
        var normalizedFormat = format?.Trim().ToLowerInvariant();
        if (type == "image")
        {
            return normalizedFormat switch
            {
                "jpg" or "jpeg" => ".jpg",
                "webp" => ".webp",
                _ => ".png"
            };
        }

        return mediaType?.Trim().ToLowerInvariant() switch
        {
            "audio/wav" or "audio/wave" or "audio/x-wav" => ".wav",
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" => ".mp4",
            "audio/webm" => ".webm",
            "audio/ogg" => ".ogg",
            _ => ".wav"
        };
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static InferenceException InvalidRequest(string message, Exception? innerException = null)
        => new("invalid_request", message, ["Fix the tool arguments before invoking the local adapter."], innerException);

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
}
