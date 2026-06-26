using System.Text.Json.Serialization;

namespace Tomur.Storage;

public sealed record ApiKeyRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("prefix")] string Prefix,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("last_used_at")] DateTimeOffset? LastUsedAt);
