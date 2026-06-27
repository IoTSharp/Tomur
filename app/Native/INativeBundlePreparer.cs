namespace Tomur.Native;

public interface INativeBundlePreparer
{
    NativeBundlePrepareResult Prepare();

    NativeBundlePrepareResult Prepare(string runtimeDirectory);
}
