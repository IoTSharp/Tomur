# Third-Party Notices

Tomur source code owned by IoTSharp contributors is licensed under the Apache License 2.0. This file identifies the main third-party boundaries in the repository. Third-party code, libraries, tools, and model assets remain under their respective licenses; the Tomur license does not replace or relicense those terms.

## Native Components

| Component | Upstream | License |
| --- | --- | --- |
| llama.cpp and ggml | <https://github.com/ggml-org/llama.cpp> | MIT |
| whisper.cpp | <https://github.com/ggml-org/whisper.cpp> | MIT |
| stable-diffusion.cpp | <https://github.com/leejet/stable-diffusion.cpp> | MIT |
| PaddleOCR | <https://github.com/PaddlePaddle/PaddleOCR> | Apache-2.0 |

The corresponding Git submodules retain their upstream copyright and license files. Tomur-specific bridge code outside those submodules is covered by the Tomur Apache-2.0 license unless a file states otherwise.

## Managed And Web Dependencies

Tomur directly uses .NET packages from Microsoft and OpenTelemetry under MIT or Apache-2.0 terms. The Web workspace directly uses Ant Design X, Ant Design, React, React DOM, and Vite under MIT terms; Lucide React under ISC terms; and TypeScript under Apache-2.0 terms.

Resolved transitive dependencies remain under their own licenses. A release distributor must audit the dependency graph used for that release and preserve all copyright, attribution, license, and NOTICE files required by those dependencies.

## Optional Accelerator Runtimes

CUDA, cuBLAS, Intel oneAPI/SYCL, OpenVINO, Vulkan loaders, GPU drivers, and other vendor runtime files are not relicensed under Apache-2.0. Their redistribution and use are governed by the applicable vendor or upstream terms. A Tomur release may include such files only when their redistribution terms have been verified and their required notices accompany the release.

## Model Assets

Model weights are not part of the Tomur source-code license and are not embedded in the Tomur executable. Catalog entries may download model files into the local Tomur data directory. Each model, tokenizer, VAE, codec, adapter, and sidecar remains governed by its upstream model card and license.

Some catalog assets have non-commercial or otherwise restricted terms, including assets identified as CC-BY-NC-4.0. Other entries require the user or distributor to review the upstream terms before commercial use or redistribution. Tomur's Apache-2.0 license does not grant rights to use those assets.

## Acknowledgements

Tomur's pure C# GLM / MoE provider design was inspired by the ideas and engineering exploration in [JustVugg/colibri](https://github.com/JustVugg/colibri), including its pure C MoE runtime, disk-streamed routed experts, and resident/cache memory hierarchy. We thank JustVugg for publishing this work. The referenced upstream project is licensed under Apache-2.0 and is not bundled as a Tomur submodule or runtime dependency.

## Release Requirement

This repository-level summary is not an artifact-specific software bill of materials. Before publishing a source archive, binary package, container image, or native runtime bundle, maintainers must inspect the exact included files and resolved dependency versions, preserve all required upstream notices, and exclude components whose redistribution terms have not been verified.
