# R8 Multimodal Smoke Report

Date: 2026-06-28

This report records the first real-model R8 smoke run for the standalone Tomur app. It is intentionally evidence-oriented: successful endpoint wiring and successful model execution are listed separately from native/runtime blockers.

## Environment

- App: `Tomur/app/Tomur.csproj`
- Data directory: `D:\source\Camel.NET\.tomur-r8-smoke`
- Model directory: `D:\source\Camel.NET\local-models`
- Base URL: `http://127.0.0.1:5138`
- Result artifacts: `D:\source\Camel.NET\.tomur-r8-smoke\results`
- Logs: `D:\source\Camel.NET\.tomur-r8-smoke\logs`

## Models

- Text/VLM smoke: `ggml-org/SmolVLM-500M-Instruct-GGUF`, `SmolVLM-500M-Instruct-Q8_0.gguf` with `mmproj-SmolVLM-500M-Instruct-Q8_0.gguf`
- Embeddings: `ggml-org/embeddinggemma-300M-GGUF`, `embeddinggemma-300M-Q8_0.gguf`
- ASR: `ggerganov/whisper.cpp`, `ggml-large-v3-turbo-q5_0.bin`
- TTS: `OuteAI/OuteTTS-0.2-500M-GGUF`, `OuteTTS-0.2-500M-Q4_K_M.gguf` with WavTokenizer sidecar
- Image generation: `unsloth/FLUX.2-klein-4B-GGUF`, `flux-2-klein-4b-Q4_K_M.gguf` with FLUX.2 VAE and Qwen3 4B text encoder sidecar

SmolVLM is an optional low-memory smoke package. The default VLM catalog package remains Qwen3-VL 4B.

## Build And Runtime Checks

- `dotnet build Tomur/app/Tomur.csproj`: passed.
- `cmake --build --preset windows-x64 --target install` under `Tomur/native/stable-diffusion.native`: passed after resetting the stale `build/windows-x64` intermediate directory.
- `GET /health`: 200, 54 ms.
- `GET /v1/models`: 200, 7 ms.
- `GET /api/runtime/multimodal`: 200, 154 ms, all R8 backends reported `ready`.
- `GET /api/models/installed`: 200, 3 ms.

## Endpoint Smoke Matrix

| Area | Endpoint | Model | Status | Time | Evidence |
| --- | --- | --- | --- | --- | --- |
| Chat | `POST /v1/chat/completions` | `smolvlm-500m-instruct-q8-0` | 200 | 6441 ms | `chat-completions.json` |
| Embeddings | `POST /v1/embeddings` | `embeddinggemma-300m-q8-0` | 200 | 1903 ms | vector count 1, dimensions 768 |
| ASR | `POST /v1/audio/transcriptions` | `whisper-large-v3-turbo-q5-0` | 200 | 23185 ms | JFK sample transcript matched expected text |
| TTS | `POST /v1/audio/speech` | `outetts-0.2-500m-q4-k-m` | 503 | 486 ms | native ABI reached, synthesis still pending |
| VLM | `POST /v1/chat/completions` with image | `smolvlm-500m-instruct-q8-0` | 200 | 5322 ms | response contained `OK 42` |
| OCR | `POST /api/ocr/analyze` | `smolvlm-500m-instruct-q8-0` | 200 | 3933 ms | response text `OK42` |
| Image generation | `POST /v1/images/generations` | `flux-2-klein-4b-q4-k-m` | process crash | 14108 ms retest | native assert in stable-diffusion.cpp |

## Findings

1. Whisper ASR is now a real managed/native execution path. The smoke input was `Tomur/native/whisper.cpp/samples/jfk.wav`, and the endpoint returned:
   `And so, my fellow Americans, ask not what your country can do for you, ask what you can do for your country.`
2. VLM and OCR both executed through native model paths against a generated `OK 42` image.
3. `/v1/audio/speech` reaches `tomur-tts`, validates the OuteTTS and WavTokenizer bundle, and returns a structured OpenAI-style error. It does not synthesize audio yet because `Tomur/native/tts.native/tts_bridge.cpp` still returns `tts-synthesis: pending-llama-tools-tts-adapter`.
4. `/v1/images/generations` reaches stable-diffusion.cpp but crashes the Tomur process with:
   `D:\source\Camel.NET\Tomur\native\stable-diffusion.cpp\src\conditioning/conditioner.hpp:1671: GGML_ASSERT(!hidden_states.empty()) failed`
5. The stable-diffusion native bridge now forwards `backend` and `params_backend` to the current stable-diffusion.cpp C API, but the FLUX.2 klein smoke still fails with the same conditioner assert after rebuilding the Windows CPU runtime.

## Current R8 Verdict

R8 is partially smoke-validated, not complete.

Passing real-model paths:

- Health/model/runtime inventory
- OpenAI chat success response
- OpenAI embeddings success response
- OpenAI audio transcription success response
- VLM image chat success response
- OCR analyze success response

Blocking paths:

- OpenAI audio speech: native TTS bridge is still an ABI-ready diagnostic stub.
- OpenAI image generation: FLUX.2 klein path crashes inside stable-diffusion.cpp conditioner setup.
