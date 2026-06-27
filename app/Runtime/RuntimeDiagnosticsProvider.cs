using System.Globalization;
using System.Runtime.InteropServices;
using Tomur.Config;
using Tomur.Inference;
using Tomur.Multimodal;
using Tomur.Native;
using Tomur.Storage;

namespace Tomur.Runtime;

public sealed class RuntimeDiagnosticsProvider
{
    private const long LowDiskWarningBytes = 10L * 1024L * 1024L * 1024L;

    private readonly ConfigurationStore configurationStore;
    private readonly DataPaths basePaths;
    private readonly INativeBundleProbe nativeBundleProbe;
    private readonly ServerOptions serverOptions;
    private readonly LocalInferenceService? inferenceService;

    public RuntimeDiagnosticsProvider(
        ConfigurationStore configurationStore,
        DataPaths basePaths,
        INativeBundleProbe? nativeBundleProbe = null,
        ServerOptions? serverOptions = null,
        LocalInferenceService? inferenceService = null)
    {
        this.configurationStore = configurationStore;
        this.basePaths = basePaths;
        this.nativeBundleProbe = nativeBundleProbe ?? new NativeBundleProbe(basePaths);
        this.serverOptions = serverOptions ?? new ServerOptions();
        this.inferenceService = inferenceService;
    }

    public RuntimeDiagnostic GetRuntimeUnavailable(string? model)
    {
        return new RuntimeDiagnostic(
            "unavailable",
            "runtime_not_configured",
            "The requested local runtime backend is not connected yet.",
            string.IsNullOrWhiteSpace(model) ? null : model,
            [
                "Run tomur doctor to inspect the local runtime status.",
                "Verify that the requested API belongs to an implemented runtime milestone.",
                "Text generation uses the R7 llama.cpp runtime; image, audio and vision backend readiness is reported by /api/runtime/multimodal."
            ]);
    }

    public RuntimeDiagnostic GetMultimodalRuntimeUnavailable(string? model, MultimodalBackendStatus backend)
    {
        ArgumentNullException.ThrowIfNull(backend);

        return new RuntimeDiagnostic(
            backend.Status == "ready" ? "unavailable" : backend.Status,
            $"multimodal_{backend.Id}_not_connected",
            backend.Status == "ready"
                ? $"{backend.DisplayName} is ready, but this request could not be executed. {backend.Message}"
                : $"{backend.DisplayName} is not available for executable R8 requests. {backend.Message}",
            string.IsNullOrWhiteSpace(model) ? null : model,
            backend.Actions.Count == 0
                ? [
                    "Use /api/runtime/multimodal to inspect multimodal native and model readiness.",
                    "Text generation remains available through the R7 llama.cpp runtime."
                ]
                : backend.Actions);
    }

    public RuntimeDiagnostic GetRuntimeFailure(string? model, Tomur.Inference.InferenceException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new RuntimeDiagnostic(
            "error",
            exception.Code,
            exception.Message,
            string.IsNullOrWhiteSpace(model) ? null : model,
            exception.Actions.Count == 0
                ? [
                    "Run tomur doctor to inspect the local runtime status.",
                    "Use /v1/models or /api/tags to verify the selected local model."
                ]
                : exception.Actions);
    }

    public RuntimeDiagnostic GetModelNotDownloaded(string? model)
    {
        return new RuntimeDiagnostic(
            "not_found",
            "model_not_downloaded",
            "The requested model is not available in the local models directory.",
            string.IsNullOrWhiteSpace(model) ? null : model,
            [
                "Run tomur pull recommended to install the default local model package set.",
                "Place a supported model file under the Tomur models directory if you are importing manually.",
                "Use /v1/models or /api/tags to inspect models currently visible to Tomur.",
                "Use tomur list --catalog to inspect package ids."
            ]);
    }

    public RuntimeDiagnostic GetContextLengthExceeded(string? model, int characterCount)
    {
        return new RuntimeDiagnostic(
            "error",
            "context_length_exceeded",
            $"The request input is too large for the current R4 protocol limit ({characterCount} characters).",
            string.IsNullOrWhiteSpace(model) ? null : model,
            [
                $"Reduce the request input below {Tomur.Api.CompatibilityProtocolLimits.MaxInputCharacters} characters.",
                "Context window aware token accounting is planned with the local inference runtime."
            ]);
    }

    public DoctorReport GetDoctorReport()
    {
        var status = GetRuntimeStatus();

        return new DoctorReport(
            Defaults.Version,
            status.System.OSDescription,
            status.System.ProcessArchitecture,
            status.System.FrameworkDescription,
            status.Status,
            status.CheckedAt,
            status.NativeBundle,
            status.Runtime,
            status.Diagnostics,
            status);
    }

    public RuntimeStatusResponse GetRuntimeStatus()
    {
        var configuration = configurationStore.EnsureConfiguration();
        var paths = basePaths.WithConfiguration(configuration.Configuration);
        var directories = EnsureDirectories(paths);
        var databaseInitializer = new LocalDatabaseInitializer(paths);
        var database = databaseInitializer.EnsureDatabase();
        var apiKeys = database.Status == "error"
            ? new ApiKeyStoreState("error", 0, "API key store is unavailable because SQLite initialization failed.", Array.Empty<ApiKeyRecord>())
            : new ApiKeyStore(databaseInitializer).GetState();
        var disk = GetDiskState(paths.DataDirectory);
        var proxy = GetProxyState();
        var port = GetPortState(ResolveServiceUrls(configuration.Configuration));
        var nativeBundle = nativeBundleProbe.Probe(paths.RuntimeDirectory);
        var runtime = GetRuntimeDiagnostic(nativeBundle);
        var diagnostics = BuildDiagnostics(configuration, directories, database, apiKeys, disk, proxy, port, nativeBundle, runtime);
        var resolvedPathConfiguration = paths.ToPathConfiguration();

        return new RuntimeStatusResponse(
            ResolveOverallStatus(diagnostics),
            DateTimeOffset.UtcNow,
            Defaults.Version,
            GetSystemSnapshot(),
            resolvedPathConfiguration,
            configuration,
            directories,
            database,
            apiKeys,
            disk,
            proxy,
            port,
            nativeBundle,
            runtime,
            diagnostics);
    }

    private string ResolveServiceUrls(LocalConfiguration configuration)
    {
        return string.IsNullOrWhiteSpace(serverOptions.Urls)
            ? configuration.Server.Urls
            : serverOptions.Urls;
    }

    private RuntimeDiagnostic GetRuntimeDiagnostic(NativeBundleProbeResult nativeBundle)
    {
        var llama = nativeBundle.Components.FirstOrDefault(static component =>
            string.Equals(component.Id, "llama", StringComparison.OrdinalIgnoreCase));
        if (llama is null || !string.Equals(llama.Status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return new RuntimeDiagnostic(
                "error",
                "native_runtime_unavailable",
                llama?.Message ?? "The llama.cpp native runtime component is not available.",
                null,
                [
                    "Run tomur native prepare to extract or repair the managed runtime bundle.",
                    "Run tomur doctor to inspect native runtime status."
                ]);
        }

        var snapshot = inferenceService?.GetSnapshot();
        if (snapshot is null)
        {
            return new RuntimeDiagnostic(
                "available",
                "runtime_ready",
                "Local llama.cpp runtime is prepared. No model session is loaded yet.",
                null,
                [
                    "Send a chat, completion or embedding request with a visible local GGUF model to load the first session.",
                    "Use tomur ps to inspect visible model files."
                ]);
        }

        if (!snapshot.Loaded)
        {
            return new RuntimeDiagnostic(
                "available",
                "runtime_ready",
                "Local llama.cpp runtime is prepared. No model session is loaded yet.",
                null,
                [
                    "Send a chat, completion or embedding request with a visible local GGUF model to load the first session.",
                    "Use tomur ps to inspect visible model files."
                ]);
        }

        return new RuntimeDiagnostic(
            "ok",
            "runtime_loaded",
            $"Local llama.cpp {snapshot.Mode ?? "session"} session is loaded for model '{snapshot.ModelId}'.",
            snapshot.ModelId,
            [
                $"Loaded at: {snapshot.LoadedAt:O}",
                $"Mode: {snapshot.Mode ?? "unknown"}",
                $"Requests handled: {snapshot.RequestCount}",
                $"Prompt tokens: {snapshot.PromptTokens}",
                $"Completion tokens: {snapshot.CompletionTokens}"
            ]);
    }

    private static SystemSnapshot GetSystemSnapshot()
    {
        return new SystemSnapshot(
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            GetCpuName(),
            GetTotalMemoryBytes());
    }

    private static IReadOnlyList<DirectoryState> EnsureDirectories(DataPaths paths)
    {
        return
        [
            EnsureDirectory("data", paths.DataDirectory),
            EnsureDirectory("config", paths.ConfigDirectory),
            EnsureDirectory("runtime", paths.RuntimeDirectory),
            EnsureDirectory("models", paths.ModelsDirectory),
            EnsureDirectory("logs", paths.LogsDirectory)
        ];
    }

    private static DirectoryState EnsureDirectory(string name, string path)
    {
        try
        {
            var existed = Directory.Exists(path);
            Directory.CreateDirectory(path);

            return new DirectoryState(
                name,
                path,
                existed ? "ok" : "created",
                existed ? "Directory is available." : "Directory was created.");
        }
        catch (IOException exception)
        {
            return new DirectoryState(
                name,
                path,
                "error",
                $"Directory could not be created: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return new DirectoryState(
                name,
                path,
                "error",
                $"Directory could not be accessed: {exception.Message}");
        }
    }

    private static DiskState GetDiskState(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return new DiskState(path, string.Empty, null, null, "warning", "Disk root could not be resolved.");
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return new DiskState(path, drive.Name, null, null, "warning", "Disk is not ready.");
            }

            var status = drive.AvailableFreeSpace < LowDiskWarningBytes ? "warning" : "ok";
            var message = status == "ok"
                ? "Disk has enough free space for initial local state."
                : "Disk has less than 10 GiB available; model downloads may fail.";

            return new DiskState(path, drive.Name, drive.AvailableFreeSpace, drive.TotalSize, status, message);
        }
        catch (IOException exception)
        {
            return new DiskState(path, string.Empty, null, null, "error", $"Disk could not be inspected: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return new DiskState(path, string.Empty, null, null, "error", $"Disk could not be accessed: {exception.Message}");
        }
    }

    private static ProxyState GetProxyState()
    {
        var httpProxy = FirstEnvironmentValue("HTTP_PROXY", "http_proxy");
        var httpsProxy = FirstEnvironmentValue("HTTPS_PROXY", "https_proxy");
        var noProxy = FirstEnvironmentValue("NO_PROXY", "no_proxy");

        var hasProxy = !string.IsNullOrWhiteSpace(httpProxy) || !string.IsNullOrWhiteSpace(httpsProxy);
        return new ProxyState(
            "ok",
            httpProxy,
            httpsProxy,
            noProxy,
            hasProxy
                ? "Proxy environment variables are configured."
                : "No HTTP proxy environment variables are configured.");
    }

    private static PortState GetPortState(string configuredUrls)
    {
        var urls = configuredUrls
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .DefaultIfEmpty(Defaults.DefaultHttpUrl)
            .ToArray();

        foreach (var url in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Port <= 0)
            {
                return new PortState(url, string.Empty, null, "error", "Configured service URL could not be parsed.");
            }
        }

        var firstUri = new Uri(urls[0]);
        return new PortState(urls[0], firstUri.Host, firstUri.Port, "ok", "Configured service URLs are parseable.");
    }

    private static List<DiagnosticItem> BuildDiagnostics(
        ConfigurationState configuration,
        IReadOnlyList<DirectoryState> directories,
        LocalDatabaseState database,
        ApiKeyStoreState apiKeys,
        DiskState disk,
        ProxyState proxy,
        PortState port,
        NativeBundleProbeResult nativeBundle,
        RuntimeDiagnostic runtime)
    {
        var diagnostics = new List<DiagnosticItem>
        {
            ToDiagnostic(
                "configuration",
                configuration.Status,
                configuration.Status == "error" ? "error" : configuration.HasWarning ? "warning" : "ok",
                configuration.Message,
                configuration.Path,
                configuration.RecoveredPath is null ? [] : [$"Review recovered file: {configuration.RecoveredPath}"])
        };

        foreach (var directory in directories)
        {
            diagnostics.Add(ToDiagnostic(
                $"directory:{directory.Name}",
                directory.Status,
                directory.Status == "error" ? "error" : "ok",
                directory.Message,
                directory.Path,
                directory.Status == "error" ? ["Check filesystem permissions for this path."] : []));
        }

        diagnostics.Add(ToDiagnostic(
            "database",
            database.Status,
            database.Status == "error" ? "error" : "ok",
            database.Message,
            database.Path,
            database.Status == "error" ? ["Move the damaged database aside or choose another data directory."] : []));

        diagnostics.Add(ToDiagnostic(
            "api_keys",
            apiKeys.Status,
            apiKeys.Status == "error" ? "error" : apiKeys.Status == "warning" ? "warning" : "ok",
            apiKeys.Message,
            apiKeys.ActiveKeyCount.ToString(CultureInfo.InvariantCulture),
            apiKeys.Status == "warning" ? ["Create a local API key before exposing compatibility APIs beyond trusted localhost use."] : []));

        diagnostics.Add(ToDiagnostic(
            "disk",
            disk.Status,
            disk.Status == "error" ? "error" : disk.Status == "warning" ? "warning" : "ok",
            disk.Message,
            disk.AvailableBytes?.ToString(CultureInfo.InvariantCulture),
            disk.Status == "warning" ? ["Free disk space before downloading large local models."] : []));

        diagnostics.Add(ToDiagnostic(
            "proxy",
            proxy.Status,
            proxy.Status == "warning" ? "warning" : "ok",
            proxy.Message,
            proxy.HttpsProxy ?? proxy.HttpProxy,
            proxy.Status == "warning" ? ["Set HTTP_PROXY or HTTPS_PROXY if your network requires a proxy."] : []));

        diagnostics.Add(ToDiagnostic(
            "port",
            port.Status,
            port.Status == "error" ? "error" : port.Status == "warning" ? "warning" : "ok",
            port.Message,
            port.Url,
            port.Status != "ok" ? ["Start tomur serve with --urls <url> to choose another port."] : []));

        diagnostics.Add(ToDiagnostic(
            "native_bundle",
            nativeBundle.Status,
            nativeBundle.Status == "error" ? "warning" : nativeBundle.Status == "warning" ? "warning" : "ok",
            nativeBundle.Message,
            nativeBundle.RuntimeRoot,
            nativeBundle.Status == "ok" ? [] : ["Run tomur native prepare to extract or repair the versioned runtime bundle."]));

        diagnostics.Add(ToDiagnostic(
            "runtime",
            runtime.Status,
            runtime.Status == "error" ? "error" : "ok",
            runtime.Message,
            runtime.Code,
            runtime.Actions));

        return diagnostics;
    }

    private static DiagnosticItem ToDiagnostic(
        string name,
        string status,
        string severity,
        string message,
        string? value,
        IReadOnlyList<string> actions)
    {
        return new DiagnosticItem(name, status, severity, message, value, actions);
    }

    private static string ResolveOverallStatus(IReadOnlyList<DiagnosticItem> diagnostics)
    {
        if (diagnostics.Any(static diagnostic => diagnostic.Severity == "error"))
        {
            return "error";
        }

        if (diagnostics.Any(static diagnostic => diagnostic.Severity == "warning"))
        {
            return "warning";
        }

        return "ok";
    }

    private static string? FirstEnvironmentValue(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? GetCpuName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        }

        if (File.Exists("/proc/cpuinfo"))
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))
                {
                    var separatorIndex = line.IndexOf(':', StringComparison.Ordinal);
                    return separatorIndex >= 0 ? line[(separatorIndex + 1)..].Trim() : line;
                }
            }
        }

        return null;
    }

    private static ulong? GetTotalMemoryBytes()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return TryGetWindowsTotalMemoryBytes();
        }

        if (File.Exists("/proc/meminfo"))
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && ulong.TryParse(parts[1], out var kib))
                {
                    return kib * 1024UL;
                }
            }
        }

        return null;
    }

    private static ulong? TryGetWindowsTotalMemoryBytes()
    {
        var status = new MemoryStatusEx();
        status.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();

        return GlobalMemoryStatusEx(ref status)
            ? status.ullTotalPhys
            : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
