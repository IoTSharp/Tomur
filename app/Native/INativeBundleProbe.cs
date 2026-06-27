namespace Tomur.Native;

public interface INativeBundleProbe
{
    NativeBundleProbeResult Probe();

    NativeBundleProbeResult Probe(string runtimeDirectory);
}
