using System.Globalization;
using System.Runtime.InteropServices;
using Tomur.Config;
using Tomur.Storage;

namespace Tomur.Runtime;

public sealed class RuntimeDiagnosticsProvider
{
    private const long LowDiskWarningBytes = 10L * 1024L * 1024L * 1024L;

    private readonly ConfigurationStore configurationStore;
    private readonly DataPaths basePaths;
    private readonly ServerOptions serverOptions;

    public RuntimeDiagnosticsProvider(
        ConfigurationStore configurationStore,
        DataPaths basePaths,
        ServerOptions? serverOptions = null)
    {
        this.configurationStore = configurationStore;
        this.basePaths = basePaths;
        this.serverOptions = serverOptions ?? new ServerOptions();
    }

    public RuntimeDiagnostic GetRuntimeUnavailable(string? model)
    {
        return new RuntimeDiagnostic(
            "unavailable",
            "runtime_not_configured",
            "Local model runtime is not configured yet. The API contract is available but model inference is not connected.",
            string.IsNullOrWhiteSpace(model) ? null : model,
            [
                "Run tomur doctor to inspect the local runtime status.",
                "Prepare native runtime and model assets in the matching Tomur milestone.",
                "Do not treat this response as model inference output."
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
        var runtime = GetRuntimeUnavailable(null);
        var diagnostics = BuildDiagnostics(configuration, directories, database, apiKeys, disk, proxy, port, runtime);
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
            runtime,
            diagnostics);
    }

    private string ResolveServiceUrls(LocalConfiguration configuration)
    {
        return string.IsNullOrWhiteSpace(serverOptions.Urls)
            ? configuration.Server.Urls
            : serverOptions.Urls;
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
            "runtime",
            runtime.Status,
            "warning",
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
