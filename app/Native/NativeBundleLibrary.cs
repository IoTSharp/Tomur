using System.Text.Json.Serialization;

namespace Tomur.Native;

public sealed record NativeBundleLibrary(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("required")] bool Required);
