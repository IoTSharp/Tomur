using System.Runtime.InteropServices;
using Tomur.Config;

namespace Tomur.Runtime;

public sealed class RuntimeDiagnosticsProvider
{
    public RuntimeDiagnostic GetRuntimeUnavailable(string? model)
    {
        return new RuntimeDiagnostic(
            "unavailable",
            "runtime_not_configured",
            "Local model runtime is not configured yet. Tomur R1 exposes the API contract without generating model output.",
            string.IsNullOrWhiteSpace(model) ? null : model,
            [
                "Run tomur doctor to inspect the local runtime status.",
                "Configure native runtime and model assets in a later Tomur milestone.",
                "Do not treat this response as model inference output."
            ]);
    }

    public DoctorReport GetDoctorReport()
    {
        return new DoctorReport(
            Defaults.Version,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            GetRuntimeUnavailable(null));
    }
}
