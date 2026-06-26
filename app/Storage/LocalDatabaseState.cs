using System.Text.Json.Serialization;

namespace Tomur.Storage;

public sealed record LocalDatabaseState(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("message")] string Message);
