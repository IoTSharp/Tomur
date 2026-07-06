---
name: tomur-native-runtime-backend
description: Add, audit, or repair Tomur native runtime backend support across CMake presets, native bundle manifests, C# P/Invoke/loading/probe code, hardware acceleration selection, runtime diagnostics, doctor output, Web Runtime UI, and documentation. Use for llama.cpp, Whisper, OCR, stable-diffusion.cpp, TTS, CUDA, Vulkan, SYCL, OpenVINO, Intel GPU/NPU, CPU fallback, native prepare, checksum, and backend visibility work.
---

# Tomur Native Runtime Backend

## Core Rule

Read the repository `AGENTS.md` before changing Tomur. Keep the single `Tomur.csproj` shape, do not add external service processes, and do not expose native backend internals as user prerequisites. Do not run build/test/server/native commands unless the user explicitly requests validation.

## Backend Change Map

For any native backend change, inspect the relevant existing pattern before editing:

1. Native build boundary:
   - `native/README.md`
   - `native/bundle.manifest.json`
   - `native/<component>.native/CMakeLists.txt`
   - `native/<component>.native/CMakePresets.json`
2. C# native bundle and loading:
   - `app/Native/NativeBuildPlanner.cs`
   - `app/Native/NativeBundleManifest.cs`
   - `app/Native/NativeBundleProbe.cs`
   - `app/Native/NativeBundlePreparer.cs`
   - `app/Native/NativeLibraryResolver.cs`
   - `app/Native/NativeLibraryLoader.cs`
3. Runtime selection and diagnostics:
   - `app/Hardware/RuntimeBackendCatalog.cs`
   - `app/Hardware/LlamaBackendDeviceCatalog.cs`
   - `app/Hardware/HardwareAccelerationService.cs`
   - `app/Inference/LlamaBackendInitializer.cs`
   - `app/Runtime/RuntimeDiagnosticsProvider.cs`
   - `app/Cli/DoctorCommand.cs`
4. Configuration and serialization:
   - `app/Config/LocalConfiguration.cs`
   - `app/Config/Defaults.cs`
   - `app/Config/ConfigurationStore.cs`
   - `app/Serialization/AppJsonSerializerContext.cs`
5. Web Runtime surface:
   - `web/src/types.ts`
   - `web/src/components/settings/RuntimeSettingsPanel.tsx`
   - related Settings/status components.

## Required Behavior

Preserve these boundaries:

1. Missing optional accelerator libraries must not block CPU runtime.
2. Missing or damaged required libraries must return structured diagnostics through `tomur doctor`, runtime API, and UI.
3. Backend visible, device enumerable, selected accelerator, and real inference passed are separate states.
4. Tomur must never fabricate inference output when models, native runtime, context, or backend compatibility are missing.
5. ggml-related native assets must remain isolated and diagnosable.
6. Model weights, SQLite, logs, user files, and generated artifacts must not be packed into the executable.
7. AOT/trimming issues must be fixed specifically; do not add blanket suppressions.

## Implementation Checklist

When adding or changing a backend:

1. Extend CMake presets and install rules only for the relevant native component.
2. Update `native/bundle.manifest.json` with variant, required/optional libraries, diagnostics, and repair action text.
3. Update native build CLI planning so `tomur native build --backend <name>` reaches the intended preset.
4. Update probe/prepare behavior so source runtime and managed runtime checks report checksum, stale, missing, repaired, copied, aliased, or unchanged states clearly.
5. Add configuration fields only when the user needs persistent local preferences; keep names short and not product-prefixed.
6. Set controlled environment variables immediately before backend initialization, and keep CPU fallback explicit.
7. Report selection and fallback reasons in `/api/runtime/status`, `tomur doctor`, and Web Runtime UI.
8. Register new DTO/config shapes in `AppJsonSerializerContext` when needed for Native AOT.
9. Update README/ROADMAP/CHANGELOG only after the code surface exists; keep unverified real inference as pending smoke.

## Validation Guidance

If validation is explicitly requested, choose the narrowest useful checks:

- Build or native build for compile/link changes.
- `tomur native prepare` for bundle materialization.
- `tomur doctor` and `/api/runtime/status` for diagnostics.
- `/v1/chat/completions` real-model smoke only when model assets and hardware are available.

Use an isolated `--data-dir` for smoke and record both success and diagnostic failure evidence.
