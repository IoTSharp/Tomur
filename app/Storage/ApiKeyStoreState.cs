using System.Text.Json.Serialization;

namespace Tomur.Storage;

public sealed record ApiKeyStoreState(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("active_key_count")] int ActiveKeyCount,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("keys")] IReadOnlyList<ApiKeyRecord> Keys);
