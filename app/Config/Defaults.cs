using System.Reflection;

namespace Tomur.Config;

public static class Defaults
{
    public const string ProductName = "Tomur";
    public const string DefaultHttpUrl = "http://127.0.0.1:5137";
    public const string DataDirectoryEnvironmentVariable = "TOMUR_DATA_DIR";
    public const int ConfigurationSchemaVersion = 1;
    public const int DatabaseSchemaVersion = 1;
    public const string ConfigDirectoryName = "config";
    public const string ConfigFileName = "tomur.json";
    public const string RuntimeDirectoryName = "runtime";
    public const string ModelsDirectoryName = "models";
    public const string LogsDirectoryName = "logs";
    public const string DatabaseFileName = "tomur.db";

    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var assembly = typeof(Defaults).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return string.IsNullOrWhiteSpace(informationalVersion)
            ? "0.1.0-r1"
            : informationalVersion;
    }
}
