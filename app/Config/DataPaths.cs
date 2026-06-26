using System.Runtime.InteropServices;

namespace Tomur.Config;

public sealed class DataPaths
{
    public DataPaths(PathOptions? options = null)
        : this(
            ResolveFullPath(
                options?.DataDirectory,
                ResolveDefaultDataDirectory()),
            null,
            null,
            null,
            null)
    {
    }

    private DataPaths(
        string dataDirectory,
        string? runtimeDirectory,
        string? modelsDirectory,
        string? logsDirectory,
        string? databasePath)
    {
        DataDirectory = dataDirectory;
        ConfigDirectory = Path.Combine(DataDirectory, Defaults.ConfigDirectoryName);
        RuntimeDirectory = ResolveFullPath(runtimeDirectory, Path.Combine(DataDirectory, Defaults.RuntimeDirectoryName), DataDirectory);
        ModelsDirectory = ResolveFullPath(modelsDirectory, Path.Combine(DataDirectory, Defaults.ModelsDirectoryName), DataDirectory);
        LogsDirectory = ResolveFullPath(logsDirectory, Path.Combine(DataDirectory, Defaults.LogsDirectoryName), DataDirectory);
        DatabasePath = ResolveFullPath(databasePath, Path.Combine(DataDirectory, Defaults.DatabaseFileName), DataDirectory);
        ConfigPath = Path.Combine(ConfigDirectory, Defaults.ConfigFileName);
    }

    public string DataDirectory { get; }
    public string ConfigDirectory { get; }
    public string RuntimeDirectory { get; }
    public string ModelsDirectory { get; }
    public string LogsDirectory { get; }
    public string DatabasePath { get; }
    public string ConfigPath { get; }

    public DataPaths WithConfiguration(LocalConfiguration configuration)
    {
        return new DataPaths(
            DataDirectory,
            configuration.Paths.RuntimeDirectory,
            configuration.Paths.ModelsDirectory,
            configuration.Paths.LogsDirectory,
            configuration.Paths.DatabasePath);
    }

    public PathConfiguration ToPathConfiguration()
    {
        return new PathConfiguration(
            DataDirectory,
            ModelsDirectory,
            RuntimeDirectory,
            LogsDirectory,
            DatabasePath);
    }

    public static string ResolveDefaultDataDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(Defaults.DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overridePath));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, Defaults.ProductName);
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, ".local", "share", "tomur");
        }

        return Path.Combine(AppContext.BaseDirectory, ".tomur");
    }

    private static string ResolveFullPath(string? configuredPath, string fallback)
    {
        return ResolveFullPath(configuredPath, fallback, null);
    }

    private static string ResolveFullPath(string? configuredPath, string fallback, string? baseDirectory)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? fallback
            : configuredPath;

        if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(baseDirectory))
        {
            path = Path.Combine(baseDirectory, path);
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }
}
