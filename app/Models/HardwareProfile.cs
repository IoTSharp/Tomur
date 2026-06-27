using System.Runtime.InteropServices;

namespace Tomur.Models;

public sealed record HardwareProfile(
    string OSDescription,
    string ProcessArchitecture,
    int ProcessorCount,
    ulong? TotalMemoryBytes,
    string Tier,
    IReadOnlyList<string> Recommendations)
{
    public static HardwareProfile Detect()
    {
        var totalMemoryBytes = GetTotalMemoryBytes();
        var tier = ResolveTier(totalMemoryBytes);
        var recommendations = BuildRecommendations(totalMemoryBytes);

        return new HardwareProfile(
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            totalMemoryBytes,
            tier,
            recommendations);
    }

    private static string ResolveTier(ulong? totalMemoryBytes)
    {
        if (totalMemoryBytes is null)
        {
            return "unknown";
        }

        var gib = totalMemoryBytes.Value / 1024UL / 1024UL / 1024UL;
        if (gib < 12)
        {
            return "low-memory";
        }

        if (gib < 24)
        {
            return "standard";
        }

        return "large-memory";
    }

    private static IReadOnlyList<string> BuildRecommendations(ulong? totalMemoryBytes)
    {
        if (totalMemoryBytes is null)
        {
            return ["Memory size could not be detected; recommended downloads keep the default package set."];
        }

        var gib = totalMemoryBytes.Value / 1024UL / 1024UL / 1024UL;
        if (gib < 12)
        {
            return
            [
                "Low-memory profile detected; prefer qwen35-4b-q4km before pulling larger chat, VLM or image bundles.",
                "Download embeddings and reranker packages first if local file search is the priority."
            ];
        }

        if (gib < 24)
        {
            return
            [
                "Standard-memory profile detected; default Qwen3.5 9B and retrieval packages are reasonable.",
                "Pull image and VLM bundles only when there is enough disk space for sidecar assets."
            ];
        }

        return
        [
            "Large-memory profile detected; default packages fit better, and optional larger models can be evaluated later."
        ];
    }

    private static ulong? GetTotalMemoryBytes()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return TryGetWindowsTotalMemoryBytes();
        }

        if (File.Exists("/proc/meminfo"))
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && ulong.TryParse(parts[1], out var kib))
                {
                    return kib * 1024UL;
                }
            }
        }

        return null;
    }

    private static ulong? TryGetWindowsTotalMemoryBytes()
    {
        var status = new MemoryStatusEx();
        status.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();

        return GlobalMemoryStatusEx(ref status)
            ? status.ullTotalPhys
            : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
