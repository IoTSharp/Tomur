using System.Security.Cryptography;
using System.Text.Json;

namespace Tomur.Providers;

public sealed record ManagedProviderReleaseManifest(
    int SchemaVersion,
    int ContractVersion,
    IReadOnlyList<ManagedProviderReleaseEntry> Providers)
{
    public const string FileName = "providers.manifest.json";

    public static bool TryRead(string path, out ManagedProviderReleaseManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema_version", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) ||
                schemaVersion != 1)
            {
                error = "Managed provider release manifest schema_version must be 1.";
                return false;
            }

            if (!root.TryGetProperty("contract", out var contract) ||
                contract.ValueKind != JsonValueKind.Object ||
                !contract.TryGetProperty("assembly", out var assembly) ||
                assembly.ValueKind != JsonValueKind.String ||
                !string.Equals(assembly.GetString(), ModelProviderContract.AssemblyName, StringComparison.Ordinal) ||
                !contract.TryGetProperty("version", out var contractVersion) ||
                contractVersion.ValueKind != JsonValueKind.String ||
                !int.TryParse(contractVersion.GetString(), out var parsedContractVersion) ||
                parsedContractVersion != ModelProviderContract.Version ||
                !contract.TryGetProperty("assembly_version", out var contractAssemblyVersion) ||
                contractAssemblyVersion.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    contractAssemblyVersion.GetString(),
                    ModelProviderContract.AssemblyVersion?.ToString(),
                    StringComparison.Ordinal))
            {
                error = "Managed provider release manifest contract is incompatible with this Tomur build.";
                return false;
            }

            if (!root.TryGetProperty("providers", out var providers) ||
                providers.ValueKind != JsonValueKind.Array)
            {
                error = "Managed provider release manifest must contain a providers array.";
                return false;
            }

            var entries = new List<ManagedProviderReleaseEntry>();
            foreach (var provider in providers.EnumerateArray())
            {
                if (provider.ValueKind != JsonValueKind.Object ||
                    !TryGetRequiredString(provider, "id", out var id) ||
                    !TryGetRequiredString(provider, "assembly", out var fileName) ||
                    !TryGetRequiredString(provider, "version", out var version) ||
                    !TryGetRequiredString(provider, "sha256", out var sha256) ||
                    Path.GetFileName(fileName) != fileName ||
                    !fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    sha256.Length != 64 ||
                    !sha256.All(static character => Uri.IsHexDigit(character)))
                {
                    error = "Managed provider release manifest contains an invalid provider entry.";
                    return false;
                }

                entries.Add(new ManagedProviderReleaseEntry(id, fileName, version, sha256));
            }

            if (entries.Count == 0 ||
                entries.Select(static entry => entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count ||
                entries.Select(static entry => entry.Assembly).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            {
                error = "Managed provider release manifest must contain unique provider IDs and assemblies.";
                return false;
            }

            manifest = new ManagedProviderReleaseManifest(1, parsedContractVersion, entries);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            error = exception.Message;
            return false;
        }
    }

    public bool TryVerify(
        string directory,
        string fileName,
        out ManagedProviderReleaseEntry? entry,
        out string? error)
    {
        entry = null;
        error = null;
        entry = Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Assembly, fileName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            error = $"Provider '{fileName}' is not listed in the release manifest.";
            return false;
        }

        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            error = $"Provider release asset is missing: {fileName}.";
            return false;
        }

        using var stream = File.OpenRead(path);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Provider checksum mismatch for '{fileName}'.";
            return false;
        }

        return true;
    }

    private static bool TryGetRequiredString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }
}

public sealed record ManagedProviderReleaseEntry(
    string Id,
    string Assembly,
    string Version,
    string Sha256);
