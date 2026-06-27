# Native Managed Boundary

This directory only contains the C# side of native runtime access: bundle probing,
library resolution, `NativeLibrary.Load`, future P/Invoke declarations, backend
detection, and managed adapters for upper layers.

C++ sources, CMake projects, compiled native libraries, and model weights do not
belong here. Native backend sources and packaging projects live under Tomur's
root-level `native/` directory. Runtime extraction and verification target the
Tomur-managed data/runtime directory.

R3 establishes `INativeBundleProbe`, `INativeLibraryResolver`, and
`INativeLibraryLoader` as the shared entry points for R4 runtime/API work. New
llama, Whisper, OCR, stable-diffusion, and GGUF TTS bindings should resolve
runtime paths through this boundary instead of rebuilding dynamic-library paths
inside each feature.
