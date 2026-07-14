using System.Text.Json;
using Tomur.Config;
using Tomur.Hardware;
using Tomur.Inference;
using Tomur.Multimodal;
using Tomur.Native;
using Tomur.Providers;
using Tomur.Runtime;
using Tomur.Serialization;

namespace Tomur.Cli;

internal static class InternalCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Internal command requires a subcommand.");
            return 1;
        }

        return args[0] switch
        {
            "image-worker" => RunImageWorker(args[1..]),
            "model-fixture" => RunModelFixture(args[1..]),
            _ => RunUnknownCommand(args[0])
        };
    }

    private static int RunModelFixture(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || CommandLineHelpers.HasHelp(args))
        {
            Console.Error.WriteLine(
                "Usage: tomur internal model-fixture <generate|verify> --provider <id> --output <directory>");
            return args.Count == 0 ? 1 : 0;
        }

        if (!CommandLineHelpers.TryReadOption(args, "--provider", out var providerId, out var providerError))
        {
            return WriteError(providerError);
        }

        if (!CommandLineHelpers.TryReadOption(args, "--output", out var outputDirectory, out var outputError))
        {
            return WriteError(outputError);
        }

        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(outputDirectory))
        {
            return WriteError("model-fixture requires --provider and --output values.");
        }

        using var registry = ModelProviderRegistry.CreateDefault();
        var provider = registry.FindFixtureProvider(providerId);
        if (provider is null)
        {
            return WriteError(
                $"Managed fixture provider '{providerId}' was not found. " +
                $"Set {ModelProviderRegistry.ProviderPathEnvironmentVariable} to the provider output directory.");
        }

        try
        {
            var result = args[0] switch
            {
                "generate" => provider.GenerateFixture(outputDirectory),
                "verify" => provider.VerifyFixture(outputDirectory),
                _ => throw new ArgumentException($"Unknown model-fixture operation: {args[0]}")
            };

            Console.WriteLine($"Fixture: {result.FixtureId}");
            Console.WriteLine($"  Provider: {result.ProviderId}");
            Console.WriteLine($"  Directory: {result.Directory}");
            Console.WriteLine($"  Schema: {result.SchemaVersion}");
            Console.WriteLine($"  Files: {result.FileCount}");
            Console.WriteLine($"  Tensors: {result.TensorCount}");
            Console.WriteLine($"  Oracle checkpoints: {result.OracleCheckpointCount}");
            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidDataException or JsonException or OverflowException)
        {
            return WriteError($"Model fixture operation failed: {exception.Message}");
        }
    }

    private static int RunImageWorker(IReadOnlyList<string> args)
    {
        if (!CommandLineHelpers.TryReadOption(args, "--request", out var requestPath, out var requestError))
        {
            return WriteError(requestError);
        }

        if (!CommandLineHelpers.TryReadOption(args, "--response", out var responsePath, out var responseError))
        {
            return WriteError(responseError);
        }

        if (string.IsNullOrWhiteSpace(requestPath) || string.IsNullOrWhiteSpace(responsePath))
        {
            return WriteError("image-worker requires --request and --response paths.");
        }

        try
        {
            var request = ReadRequest(requestPath);
            var paths = ResolvePaths(args);
            var prepareResult = new NativeBundlePreparer(paths).Prepare();
            if (prepareResult.Status == "error")
            {
                WriteResponse(responsePath, CreateErrorResponse(
                    "native_runtime_unavailable",
                    $"Native runtime bundle could not be prepared. {prepareResult.Message}",
                    ["Run tomur native prepare to extract or repair the managed runtime bundle."]));
                return 2;
            }

            var modelCatalog = new LocalModelCatalog(paths);
            var model = modelCatalog.Find(request.Model);
            if (model is null)
            {
                WriteResponse(responsePath, CreateErrorResponse(
                    "model_not_downloaded",
                    $"The requested image model '{request.Model}' is not available.",
                    [
                        "Run tomur pull recommended to install the default local model package set.",
                        "Use /v1/models or /api/models/installed to inspect models visible to Tomur."
                    ]));
                return 2;
            }

            var nativeProbe = new NativeBundleProbe(paths);
            var resolver = new NativeLibraryResolver(nativeProbe);
            var runtimePreference = new NativeRuntimePreference();
            var importResolver = new LlamaImportResolver(resolver, runtimePreference);
            var configurationStore = new ConfigurationStore(paths);
            var backendInitializer = new LlamaBackendInitializer(importResolver, resolver, configurationStore);
            var accelerationService = new HardwareAccelerationService(backendInitializer, nativeProbe, configurationStore);
            var execution = new MultimodalExecutionService(
                new MultimodalRuntimeService(nativeProbe, modelCatalog),
                new RuntimeDiagnosticsProvider(
                    configurationStore,
                    paths,
                    nativeProbe,
                    accelerationService: accelerationService),
                paths,
                importResolver,
                resolver,
                accelerationService,
                runtimePreference);

            var result = execution.GenerateImage(model, request.Options, CancellationToken.None);
            WriteResponse(responsePath, new ImageGenerationWorkerResponse(
                "ok",
                result.Format,
                Convert.ToBase64String(result.Bytes),
                (long)Math.Round(result.Elapsed.TotalMilliseconds),
                result.Diagnostics,
                null));
            return 0;
        }
        catch (InferenceException exception)
        {
            WriteResponse(responsePath, CreateErrorResponse(exception.Code, exception.Message, exception.Actions));
            return 2;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            WriteResponse(responsePath, CreateErrorResponse(
                "image_worker_failed",
                exception.Message,
                ["Use /api/runtime/multimodal to inspect image generation readiness."]));
            return 2;
        }
    }

    private static ImageGenerationWorkerRequest ReadRequest(string requestPath)
    {
        using var stream = File.OpenRead(requestPath);
        return JsonSerializer.Deserialize(stream, AppJsonSerializerContext.Default.ImageGenerationWorkerRequest)
            ?? throw new JsonException("Image worker request is empty.");
    }

    private static DataPaths ResolvePaths(IReadOnlyList<string> args)
    {
        if (!PathOptions.TryFromArgs(args, out var pathOptions, out var pathError))
        {
            throw new ArgumentException(pathError);
        }

        var basePaths = new DataPaths(pathOptions);
        var configurationStore = new ConfigurationStore(basePaths);
        var configuration = configurationStore.EnsureConfiguration();
        if (configuration.Status == "error")
        {
            throw new InvalidOperationException(configuration.Message);
        }

        return basePaths.WithConfiguration(configuration.Configuration);
    }

    private static ImageGenerationWorkerResponse CreateErrorResponse(
        string code,
        string message,
        IReadOnlyList<string> actions)
    {
        return new ImageGenerationWorkerResponse(
            "error",
            null,
            null,
            0,
            [],
            new RuntimeWorkerError(code, message, actions));
    }

    private static void WriteResponse(string responsePath, ImageGenerationWorkerResponse response)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(responsePath))!);
        using var stream = File.Create(responsePath);
        JsonSerializer.Serialize(stream, response, AppJsonSerializerContext.Default.ImageGenerationWorkerResponse);
    }

    private static int WriteError(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static int RunUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown internal command: {command}");
        return 1;
    }
}
