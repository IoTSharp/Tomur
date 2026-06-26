using System.Text.Json;
using Tomur.Serialization;

namespace Tomur.Config;

public sealed class ConfigurationStore
{
    private readonly DataPaths paths;
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        TypeInfoResolver = AppJsonSerializerContext.Default,
        WriteIndented = true
    };

    public ConfigurationStore(DataPaths paths)
    {
        this.paths = paths;
    }

    public ConfigurationState EnsureConfiguration()
    {
        try
        {
            Directory.CreateDirectory(paths.ConfigDirectory);

            if (!File.Exists(paths.ConfigPath))
            {
                var created = LocalConfiguration.CreateDefault(paths);
                WriteConfiguration(created);

                return new ConfigurationState(
                    "created",
                    paths.ConfigPath,
                    "Configuration file was created with default local settings.",
                    null,
                    created);
            }

            var configuration = ReadConfiguration();
            if (configuration.SchemaVersion == Defaults.ConfigurationSchemaVersion)
            {
                return new ConfigurationState(
                    "ok",
                    paths.ConfigPath,
                    "Configuration file is readable.",
                    null,
                    configuration);
            }

            var upgraded = configuration with
            {
                SchemaVersion = Defaults.ConfigurationSchemaVersion
            };
            WriteConfiguration(upgraded);

            return new ConfigurationState(
                "upgraded",
                paths.ConfigPath,
                "Configuration schema version was updated to the current version.",
                null,
                upgraded);
        }
        catch (JsonException)
        {
            return TryRecoverConfiguration("Configuration JSON is invalid.");
        }
        catch (NotSupportedException)
        {
            return TryRecoverConfiguration("Configuration JSON contains unsupported values.");
        }
        catch (IOException exception)
        {
            return new ConfigurationState(
                "error",
                paths.ConfigPath,
                $"Configuration file could not be read: {exception.Message}",
                null,
                LocalConfiguration.CreateDefault(paths));
        }
        catch (UnauthorizedAccessException exception)
        {
            return new ConfigurationState(
                "error",
                paths.ConfigPath,
                $"Configuration file could not be accessed: {exception.Message}",
                null,
                LocalConfiguration.CreateDefault(paths));
        }
    }

    private ConfigurationState TryRecoverConfiguration(string message)
    {
        try
        {
            return RecoverConfiguration(message);
        }
        catch (IOException exception)
        {
            return new ConfigurationState(
                "error",
                paths.ConfigPath,
                $"{message} The damaged file could not be moved aside: {exception.Message}",
                null,
                LocalConfiguration.CreateDefault(paths));
        }
        catch (UnauthorizedAccessException exception)
        {
            return new ConfigurationState(
                "error",
                paths.ConfigPath,
                $"{message} The damaged file could not be moved aside: {exception.Message}",
                null,
                LocalConfiguration.CreateDefault(paths));
        }
    }

    private ConfigurationState RecoverConfiguration(string message)
    {
        var recoveredPath = CreateRecoveryPath();
        File.Move(paths.ConfigPath, recoveredPath, overwrite: true);

        var configuration = LocalConfiguration.CreateDefault(paths);
        WriteConfiguration(configuration);

        return new ConfigurationState(
            "recovered",
            paths.ConfigPath,
            $"{message} The damaged file was moved aside and a default configuration was written.",
            recoveredPath,
            configuration);
    }

    private LocalConfiguration ReadConfiguration()
    {
        using var stream = File.OpenRead(paths.ConfigPath);
        var configuration = JsonSerializer.Deserialize(
            stream,
            AppJsonSerializerContext.Default.LocalConfiguration);

        if (configuration is null)
        {
            throw new JsonException("Configuration document is empty.");
        }

        ValidateConfiguration(configuration);
        return configuration;
    }

    private static void ValidateConfiguration(LocalConfiguration configuration)
    {
        if (configuration.Server is null)
        {
            throw new JsonException("Configuration is missing server settings.");
        }

        if (string.IsNullOrWhiteSpace(configuration.Server.Urls))
        {
            throw new JsonException("Configuration server URL is empty.");
        }

        if (configuration.Paths is null)
        {
            throw new JsonException("Configuration is missing path settings.");
        }

        if (string.IsNullOrWhiteSpace(configuration.Paths.ModelsDirectory) ||
            string.IsNullOrWhiteSpace(configuration.Paths.RuntimeDirectory) ||
            string.IsNullOrWhiteSpace(configuration.Paths.LogsDirectory) ||
            string.IsNullOrWhiteSpace(configuration.Paths.DatabasePath))
        {
            throw new JsonException("Configuration contains empty path settings.");
        }

        if (configuration.Runtime is null)
        {
            throw new JsonException("Configuration is missing runtime settings.");
        }
    }

    private void WriteConfiguration(LocalConfiguration configuration)
    {
        Directory.CreateDirectory(paths.ConfigDirectory);

        var json = JsonSerializer.Serialize(configuration, WriteOptions);

        File.WriteAllText(paths.ConfigPath, json);
    }

    private string CreateRecoveryPath()
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", null);
        return Path.Combine(
            paths.ConfigDirectory,
            $"{Defaults.ConfigFileName}.damaged-{timestamp}");
    }
}
