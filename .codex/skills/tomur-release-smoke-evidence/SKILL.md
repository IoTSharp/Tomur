---
name: tomur-release-smoke-evidence
description: Create, run, or document Tomur release and smoke evidence for Native AOT/self-contained publishing, service shape smoke, API/Web/native runtime regression, Intel GPU/NPU smoke, and reproducible validation records. Use when asked to verify, smoke test, publish, record release evidence, update R12/R16 evidence docs, or prepare Tomur validation; obey Tomur's rule that build/test/server commands run only when the user explicitly requests validation.
---

# Tomur Release Smoke Evidence

## Core Rule

Read the repository `AGENTS.md` before any Tomur work. In the default collaboration mode, do not run `dotnet build`, `dotnet test`, `dotnet run`, `dotnet publish`, frontend scripts, browser automation, service commands, or local servers unless the user explicitly asks for validation, smoke testing, publishing, or execution.

If validation is not explicitly requested, prepare the smoke plan, inspect source files, or update documentation only.

## Workflow

1. Identify the target evidence lane:
   - R12 release: Native AOT, self-contained single-file, native bundle, service shape, publish package structure.
   - R13 Web: Chat-first workbench, Settings/status drawers, API capability map, static hosting.
   - R14 accelerator: CUDA13, Vulkan, SYCL, OpenVINO, Intel GPU/NPU, CPU fallback.
   - R16 regression: public API, native prepare, model visibility, Web static hosting, service/tray smoke.
2. Read the relevant docs before acting:
   - `ROADMAP.md`
   - `CHANGELOG.md`
   - `docs/r12-aot-release-audit.md`
   - `docs/r12-release-package-structure.md`
   - `docs/r12-service-smoke.md`
   - `docs/r12-native-bundle-inventory.md`
   - `docs/r10-r11-smoke-maintenance.md`
   - `docs/r8-smoke-report.md` when real multimodal evidence is relevant.
3. Check the working tree with `git status --short`; do not overwrite unrelated user changes.
4. Use isolated data directories for execution evidence, such as `.tmp/<lane>-smoke-data`; never use the user's real Tomur data directory for smoke.
5. Capture both success and failure as evidence. A clear structured failure is useful Tomur evidence and must not be hidden.

## Execution Guidance

When the user explicitly asks to validate, choose the smallest command set that proves the claim.

For source-level checks, prefer:

```powershell
dotnet build app/Tomur.csproj
```

For release evidence, prefer publish artifacts over `dotnet run`:

```powershell
dotnet publish app/Tomur.csproj -c Release -r win-x64 -p:PublishProfile=native-aot-audit
dotnet publish app/Tomur.csproj -c Release -r win-x64 -p:PublishProfile=self-contained-single-file
```

For minimum release smoke, cover:

```powershell
.\tomur.exe --help
.\tomur.exe doctor --data-dir .tmp\r12-release-data
.\tomur.exe native prepare --data-dir .tmp\r12-release-data
.\tomur.exe serve --data-dir .tmp\r12-release-data --urls http://127.0.0.1:5149
```

HTTP smoke should usually include:

```powershell
Invoke-RestMethod http://127.0.0.1:5149/health
Invoke-RestMethod http://127.0.0.1:5149/api/version
Invoke-RestMethod http://127.0.0.1:5149/v1/models
Invoke-RestMethod http://127.0.0.1:5149/api/runtime/native
Invoke-RestMethod http://127.0.0.1:5149/api/runtime/status
```

Stop any process started for smoke before finishing the turn.

## Evidence Quality

Record the following whenever possible:

1. Command line and working directory.
2. OS, RID, architecture, and Tomur version/commit.
3. Data directory used.
4. Native bundle source and managed runtime path.
5. Endpoint responses or summarized important fields.
6. Model package, backend, selected accelerator, GPU layers, token usage, and context size for real inference smoke.
7. Logs or artifact paths for failures, WAV/PNG/text outputs, and service stdout/stderr.

Do not claim a runtime, platform, backend, or model is verified unless the evidence was actually produced. Use "not run", "diagnosed", "backend visible", "device enumerable", or "real inference passed" precisely.

## Documentation Updates

Update only the files whose responsibility matches the evidence:

- `CHANGELOG.md`: completed work and verified results.
- `ROADMAP.md`: remaining work, acceptance criteria, current phase status.
- `README.md` / `README.en.md`: concise public status and user-facing boundary.
- `docs/*`: command details, smoke matrices, logs, package structure, native inventory.

Keep Tomur terminology consistent with `AGENTS.md`: local-first, single process, no fake inference, no external platform migration story, no multi-tenant/server-governance framing.
