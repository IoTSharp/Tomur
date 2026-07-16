using Tomur.Config;
using Tomur.Hardware;
using Tomur.Inference;
using Tomur.Native;
using Tomur.Providers;
using Tomur.Runtime;

namespace Tomur.Cli;

internal static class DoctorCommand
{
    public static int Run(string[] args)
    {
        if (CommandLineHelpers.HasHelp(args))
        {
            WriteHelp();
            return 0;
        }

        if (!PathOptions.TryFromArgs(args, out var pathOptions, out var pathError))
        {
            Console.Error.WriteLine(pathError);
            return 1;
        }

        var paths = new DataPaths(pathOptions);
        var configurationStore = new ConfigurationStore(paths);
        var nativeBundleProbe = new NativeBundleProbe(paths);
        var libraryResolver = new NativeLibraryResolver(nativeBundleProbe);
        var importResolver = new LlamaImportResolver(libraryResolver);
        var backendInitializer = new LlamaBackendInitializer(importResolver, libraryResolver, configurationStore);
        var accelerationService = new HardwareAccelerationService(backendInitializer, nativeBundleProbe, configurationStore);
        using var modelProviderRegistry = ModelProviderRegistry.CreateDefault();
        var diagnostics = new RuntimeDiagnosticsProvider(
            configurationStore,
            paths,
            nativeBundleProbe,
            serverOptions: null,
            inferenceService: null,
            accelerationService: accelerationService,
            modelProviderRegistry: modelProviderRegistry,
            logger: null).GetDoctorReport();

        Console.WriteLine($"{Defaults.ProductName} doctor");
        Console.WriteLine($"  Version: {diagnostics.Version}");
        Console.WriteLine($"  Status: {diagnostics.Status}");
        Console.WriteLine($"  OS: {diagnostics.OSDescription}");
        Console.WriteLine($"  Architecture: {diagnostics.ProcessArchitecture}");
        Console.WriteLine($"  Framework: {diagnostics.FrameworkDescription}");
        Console.WriteLine($"  CPU: {diagnostics.Details.System.ProcessorCount} logical processors");
        if (!string.IsNullOrWhiteSpace(diagnostics.Details.System.CpuName))
        {
            Console.WriteLine($"  CPU name: {diagnostics.Details.System.CpuName}");
        }

        if (diagnostics.Details.System.TotalMemoryBytes is not null)
        {
            Console.WriteLine($"  Memory: {CommandLineHelpers.FormatBytes((long)diagnostics.Details.System.TotalMemoryBytes.Value)}");
        }

        Console.WriteLine($"  Data directory: {diagnostics.Details.Paths.DataDirectory}");
        Console.WriteLine($"  Config file: {diagnostics.Details.Configuration.Path}");
        Console.WriteLine($"  Database: {diagnostics.Details.Database.Path}");
        Console.WriteLine($"  Runtime directory: {diagnostics.Details.Paths.RuntimeDirectory}");
        Console.WriteLine($"  Models directory: {diagnostics.Details.Paths.ModelsDirectory}");
        Console.WriteLine($"  Logs directory: {diagnostics.Details.Paths.LogsDirectory}");
        Console.WriteLine($"  Disk free: {CommandLineHelpers.FormatNullableBytes(diagnostics.Details.Disk.AvailableBytes)}");
        Console.WriteLine($"  Proxy: {diagnostics.Details.Proxy.Status}");
        Console.WriteLine($"  Port: {diagnostics.Details.Port.Status} ({diagnostics.Details.Port.Url})");
        Console.WriteLine($"  API keys: {diagnostics.Details.ApiKeys.Status} ({diagnostics.Details.ApiKeys.ActiveKeyCount} active)");
        Console.WriteLine($"  Acceleration: {diagnostics.Details.Acceleration.Status} ({diagnostics.Details.Acceleration.EffectiveBackend})");
        Console.WriteLine($"  Acceleration preference: {diagnostics.Details.Acceleration.PreferredBackend}");
        if (!string.IsNullOrWhiteSpace(diagnostics.Details.Acceleration.OpenVinoDevice))
        {
            Console.WriteLine($"  OpenVINO device: {diagnostics.Details.Acceleration.OpenVinoDevice}");
        }

        Console.WriteLine($"  NPU opt-in: {diagnostics.Details.Acceleration.AllowNpu}");
        if (diagnostics.Details.Acceleration.NpuPrefillChunk is not null)
        {
            Console.WriteLine($"  NPU prefill chunk: {diagnostics.Details.Acceleration.NpuPrefillChunk}");
        }

        if (!string.IsNullOrWhiteSpace(diagnostics.Details.Acceleration.FallbackReason))
        {
            Console.WriteLine($"  Acceleration fallback: {diagnostics.Details.Acceleration.FallbackReason}");
        }

        if (diagnostics.Details.Acceleration.SelectedAccelerator is not null)
        {
            var accelerator = diagnostics.Details.Acceleration.SelectedAccelerator;
            Console.WriteLine($"  Accelerator: {accelerator.Name} [{accelerator.Kind}]");
            Console.WriteLine($"  Accelerator key: {accelerator.SelectionKey}");
            Console.WriteLine($"  Accelerator memory: {CommandLineHelpers.FormatNullableBytes(accelerator.MemoryBytes is null ? null : (long)accelerator.MemoryBytes.Value)}");
            Console.WriteLine($"  GPU layers: {diagnostics.Details.Acceleration.EffectiveGpuLayers}");
        }

        foreach (var backend in diagnostics.Details.Acceleration.Backends
                     .Where(static backend => backend.Id is "sycl" or "openvino" or "vulkan"))
        {
            Console.WriteLine($"  Backend {backend.Id}: {backend.Status} ({backend.LibraryName})");
            foreach (var action in backend.Actions.Take(2))
            {
                Console.WriteLine($"      Action: {action}");
            }
        }

        Console.WriteLine($"  Native bundle: {diagnostics.NativeBundle.Status} ({diagnostics.NativeBundle.Rid})");
        Console.WriteLine($"  Runtime: {diagnostics.Runtime.Status} / {diagnostics.Runtime.Code}");
        Console.WriteLine($"  Managed providers: {diagnostics.Details.ManagedProviders.Status} ({diagnostics.Details.ManagedProviders.Loaded.Count} loaded)");
        foreach (var provider in diagnostics.Details.ManagedProviders.Loaded)
        {
            Console.WriteLine($"    {provider.Id}: {provider.Assembly} {provider.Version ?? "unknown"}");
            Console.WriteLine($"      Path: {provider.Path}");
        }

        foreach (var directory in diagnostics.Details.ManagedProviders.SearchDirectories)
        {
            Console.WriteLine($"  Managed provider search path: {directory}");
        }

        foreach (var model in diagnostics.Details.ManagedModels)
        {
            Console.WriteLine($"  Managed model: {model.ModelId} [{model.Status}]");
            Console.WriteLine($"    Provider: {model.ProviderId ?? "unavailable"}");
            Console.WriteLine($"    Architecture: {model.Architecture}");
            Console.WriteLine($"    Quantization: {model.Quantization} / {model.QuantizationLayout ?? "unknown"}");
            Console.WriteLine($"    Readiness: provider={model.ProviderDiscovered}, metadata={model.MetadataValid}, assets={model.AssetsComplete}, forward={model.ForwardVerified}, session={model.SessionLoaded}");
            Console.WriteLine($"    Tensor shards/indexed: {model.TensorFileCount?.ToString() ?? "unknown"} / {model.TensorCount?.ToString() ?? "unknown"}");
            Console.WriteLine($"    Resident/KV/scratch: {CommandLineHelpers.FormatNullableBytes(model.ResidentBytes)} / {CommandLineHelpers.FormatNullableBytes(model.KvBytes)} / {CommandLineHelpers.FormatNullableBytes(model.ScratchBytes)}");
            Console.WriteLine($"    Expert cache: {CommandLineHelpers.FormatNullableBytes(model.ExpertCacheBytes)}");
            foreach (var modelDiagnostic in model.Diagnostics)
            {
                Console.WriteLine($"    Diagnostic: {modelDiagnostic.Code}: {modelDiagnostic.Message}");
            }
        }

        if (diagnostics.Details.Session.Loaded)
        {
            var session = diagnostics.Details.Session;
            Console.WriteLine($"  Session provider: {session.ProviderId ?? session.Mode ?? "unknown"}");
            Console.WriteLine($"  Session model: {session.ModelId}");
            Console.WriteLine($"  Session busy: {session.Busy}");
            Console.WriteLine($"  Session resident/KV/scratch: {CommandLineHelpers.FormatNullableBytes(session.ResidentBytes)} / {CommandLineHelpers.FormatNullableBytes(session.KvBytes)} / {CommandLineHelpers.FormatNullableBytes(session.ScratchBytes)}");
            Console.WriteLine($"  Session expert cache: {CommandLineHelpers.FormatNullableBytes(session.ExpertCacheBytes)}");
            Console.WriteLine($"  Session requests/tokens: {session.RequestCount} / {session.PromptTokens} / {session.CompletionTokens}");
            Console.WriteLine($"  Session load/first-token ms: {session.LoadElapsedMilliseconds?.ToString() ?? "unknown"} / {FormatMetric(session.LastFirstTokenMilliseconds)}");
            Console.WriteLine($"  Session generation ms: {FormatMetric(session.LastGenerationMilliseconds)}");
            Console.WriteLine($"  Session output/decode tokens/s: {FormatMetric(session.LastOutputTokensPerSecond)} / {FormatMetric(session.LastDecodeTokensPerSecond)}");
        }

        Console.WriteLine();
        Console.WriteLine("Diagnostics:");

        foreach (var item in diagnostics.Diagnostics)
        {
            Console.WriteLine($"  [{item.Severity}] {item.Name}: {item.Message}");
            if (!string.IsNullOrWhiteSpace(item.Value))
            {
                Console.WriteLine($"      Value: {item.Value}");
            }

            foreach (var action in item.Actions)
            {
                Console.WriteLine($"      Action: {action}");
            }
        }

        return 0;
    }

    private static string FormatMetric(double? value)
        => value is null ? "unknown" : value.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

    private static void WriteHelp()
    {
        Console.WriteLine($"""
{Defaults.ProductName} doctor

Usage:
  tomur doctor [--data-dir <path>]

Options:
  --data-dir      Override the local data directory for this process.

Prints local OS, data directory, SQLite, API key, port, provider and runtime diagnostics.
Run `tomur native prepare` to extract or repair native runtime files.
""");
    }
}
