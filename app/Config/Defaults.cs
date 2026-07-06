using System.Reflection;

namespace Tomur.Config;

public static class Defaults
{
    public const string ProductName = "Tomur";
    public const string WindowsServiceName = "Tomur";
    public const string LinuxServiceName = "tomur.service";
    public const string MacLaunchAgentLabel = "dev.tomur.service";
    public const string DefaultHttpUrl = "http://127.0.0.1:5137";
    public const string DataDirectoryEnvironmentVariable = "TOMUR_DATA_DIR";
    public const int ConfigurationSchemaVersion = 2;
    public const int DatabaseSchemaVersion = 3;
    public const string ConfigDirectoryName = "config";
    public const string ConfigFileName = "tomur.json";
    public const string RuntimeDirectoryName = "runtime";
    public const string ModelsDirectoryName = "models";
    public const string LogsDirectoryName = "logs";
    public const string DatabaseFileName = "tomur.db";
    public const string ModelInstallManifestFileName = "models.manifest.json";
    public const string DownloadCacheDirectoryName = "_downloads";

    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var assembly = typeof(Defaults).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return string.IsNullOrWhiteSpace(informationalVersion)
            ? "0.5.0-r5"
            : informationalVersion;
    }
}
