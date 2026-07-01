# R8 Multimodal Smoke Report

Date: 2026-07-02

This report records the R8 real-model smoke closure for the standalone Tomur app. It keeps the earlier blockers visible, but the current verdict is based on the latest full public-interface smoke run.

## Environment

- App: `app/Tomur.csproj`
- Data directory: `%LOCALAPPDATA%\Tomur`
- Base URL: `http://127.0.0.1:5140`
- Evidence directory: `docs/r8-smoke-evidence/2026-07-02`
- Service logs: `docs/r8-smoke-evidence/2026-07-02/serve.out.log`, `docs/r8-smoke-evidence/2026-07-02/serve.err.log`

## Models

- ASR: `whisper-large-v3-turbo-q5-0`
- TTS: `outetts-0.2-500m-q4-k-m` with WavTokenizer sidecar
- VLM / OCR smoke: `smolvlm-500m-instruct-q8-0` with `mmproj-SmolVLM-500M-Instruct-Q8_0.gguf`
- Image generation: `flux-2-klein-4b-q4-k-m` with FLUX.2 VAE and Qwen3 text encoder sidecars

SmolVLM is a low-memory smoke package for VLM/OCR verification. The default catalog vision package can still evolve independently.

## Build And Runtime Checks

- `dotnet build app/Tomur.csproj`: passed, 0 warnings, 0 errors.
- `native/llama.native` Windows x64 CPU and CUDA13 variants were rebuilt with `GGML_MAX_NAME=128`.
- `native/stable-diffusion.native` Windows x64 CUDA13 variant was rebuilt and installed.
- `tomur native prepare --data-dir=%LOCALAPPDATA%\Tomur`: repaired the managed runtime with the rebuilt ggml / llama DLLs.
- `tomur doctor --data-dir=%LOCALAPPDATA%\Tomur`: native bundle `ok`, CUDA accelerator detected.
- `GET /health`: 200.
- `GET /v1/models`: 200, visible R8 models included ASR, TTS, VLM/OCR and image generation.
- `GET /api/runtime/multimodal`: 200, all R8 backends reported `ready`.

## Endpoint Smoke Matrix

| Area | Endpoint | Model | Status | Time | Evidence |
| --- | --- | --- | --- | --- | --- |
| Runtime inventory | `GET /api/runtime/multimodal` | n/a | 200 | 1387 ms | `runtime-multimodal.response.json` |
| ASR | `POST /v1/audio/transcriptions` | `whisper-large-v3-turbo-q5-0` | 200 | 30016 ms | `audio-transcriptions.response.json` |
| TTS | `POST /v1/audio/speech` | `outetts-0.2-500m-q4-k-m` | 200 | 9574 ms | `audio-speech.wav`, 99244 bytes, `RIFF/WAVE` |
| VLM chat | `POST /v1/chat/completions` with image | `smolvlm-500m-instruct-q8-0` | 200 | 7264 ms | `chat-completions-image.response.json`, text `OK42.` |
| Vision API | `POST /api/vision/analyze` | `smolvlm-500m-instruct-q8-0` | 200 | 4852 ms | `vision-analyze.response.json`, text `OK42.` |
| OCR | `POST /api/ocr/analyze` | `smolvlm-500m-instruct-q8-0` | 200 | 8990 ms | `ocr-analyze.response.json`, text `OK42.` |
| Image generation | `POST /v1/images/generations` | `flux-2-klein-4b-q4-k-m` | 200 | 84566 ms | `images-generations.png`, 70659 bytes, PNG signature `89504E470D0A1A0A` |

## Findings

1. Whisper ASR executed through the native adapter against `native/whisper.cpp/samples/jfk.wav` and returned the JFK sample transcript.
2. TTS executed through the OuteTTS + WavTokenizer native route and returned a WAV payload with a valid `RIFF/WAVE` header.
3. VLM chat, `/api/vision/analyze` and `/api/ocr/analyze` executed against a generated `OK42` PNG and returned `OK42.`.
4. `/v1/images/generations` executed FLUX.2 klein through the isolated stable-diffusion.cpp image worker and returned a valid PNG from OpenAI-compatible `b64_json`.
5. The previous FLUX.2 blocker was traced to shared ggml tensor-name capacity mismatch: stable-diffusion.cpp required `GGML_MAX_NAME=128`, while the shared llama.native ggml runtime had been built with the smaller default. Rebuilding shared ggml / llama with `GGML_MAX_NAME=128`, then rebuilding stable-diffusion CUDA13 and preparing the managed runtime, closed the blocker.

## Current R8 Verdict

R8 is smoke-validated and complete for the current backend/API scope.

Completion evidence:

- Every public R8 endpoint has at least one real-model smoke result in `docs/r8-smoke-evidence/2026-07-02/smoke-summary.json`.
- Generated artifacts were checked for real binary signatures: `audio-speech.wav` has `RIFF/WAVE`, and `images-generations.png` has the PNG signature.
- Image generation remains isolated in a worker subprocess, so native assert or worker crash failures return structured diagnostics without taking down the main Tomur service.
- The R8 completion claim is limited to local backend adapters, public HTTP APIs, diagnostics and the existing Runtime UI diagnostic surface. It does not claim model-autonomous multimodal tool-calling, full attachment UX, file RAG, or release-grade cross-platform bundle validation.

Historical blocker evidence:

- `docs/r8-smoke-evidence/2026-07-01/images-generations-response.json` records the previous FLUX.2 worker crash.
- The previous diagnostic included truncated tensor name `post_attention_layernorm.weigh` and `GGML_ASSERT(!hidden_states.empty())`, which is consistent with the ggml name-capacity mismatch fixed before the 2026-07-02 pass.
