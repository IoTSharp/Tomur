using System.Text.Json.Serialization;

namespace Tomur.Config;

public sealed record LocalConfiguration(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("server")] ServerConfiguration Server,
    [property: JsonPropertyName("paths")] PathConfiguration Paths,
    [property: JsonPropertyName("runtime")] RuntimeConfiguration Runtime)
{
    public static LocalConfiguration CreateDefault(DataPaths paths)
    {
        return new LocalConfiguration(
            Defaults.ConfigurationSchemaVersion,
            new ServerConfiguration(Defaults.DefaultHttpUrl),
            new PathConfiguration(
                paths.DataDirectory,
                paths.ModelsDirectory,
                paths.RuntimeDirectory,
                paths.LogsDirectory,
                paths.DatabasePath),
            new RuntimeConfiguration("not_configured"));
    }
}

public sealed record ServerConfiguration(
    [property: JsonPropertyName("urls")] string Urls);

public sealed record PathConfiguration(
    [property: JsonPropertyName("data_directory")] string DataDirectory,
    [property: JsonPropertyName("models_directory")] string ModelsDirectory,
    [property: JsonPropertyName("runtime_directory")] string RuntimeDirectory,
    [property: JsonPropertyName("logs_directory")] string LogsDirectory,
    [property: JsonPropertyName("database_path")] string DatabasePath);

public sealed record RuntimeConfiguration(
    [property: JsonPropertyName("default_backend")] string DefaultBackend);
