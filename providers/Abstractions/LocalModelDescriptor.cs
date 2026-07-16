namespace Tomur.Runtime;

public sealed record LocalModelDescriptor(
    string Id,
    string Name,
    string FileName,
    string RelativePath,
    string AbsolutePath,
    long SizeBytes,
    DateTime LastModifiedUtc,
    string Format,
    string Family,
    string QuantizationLevel,
    IReadOnlyList<string> Capabilities,
    string? PackageId = null,
    string? Status = null,
    string? License = null,
    string? LicenseNotice = null,
    bool IsVerified = false);
