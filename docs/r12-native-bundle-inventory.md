# R12 Native Bundle 随包清单

记录时间：2026-07-03

本文记录 R12 发布包中的 native bundle 资产边界、当前 RID 覆盖、checksum 证据和缺口。清单只描述当前仓库中真实存在的 `native/runtimes` 文件，不把尚未补齐的 macOS 资产写成已完成。

## 当前范围

1. bundle manifest：`native/bundle.manifest.json`
2. bundle id：`tomur.native.r8.cuda13`
3. bundle version：`0.8.0-cuda13`
4. runtime root：`native/runtimes/{rid}/native`
5. publish 行为：`Tomur.csproj` 会把 `native/runtimes/**` 作为单文件外部内容复制到发布目录，并保持 `ExcludeFromSingleFile=true`。
6. prepare 行为：`tomur native prepare` / `POST /api/runtime/native/prepare` 会从发布目录的 `native/runtimes/{rid}/native` 复制到 `<data>/runtime/<bundle-id>/<version>/runtimes/{rid}/native`。
7. checksum 行为：`NativeBundlePreparer` 会计算源文件 SHA256；`NativeBundleProbe` 会用 manifest 中的 `sha256` 字段或源 runtime 文件动态 hash 作为 expected checksum，并在目标 runtime 文件缺失、损坏或 stale 时返回诊断。
8. Linux `.so` alias 在当前工作区表现为 symlink，`Length` 可能显示为 0；`Get-FileHash` 会跟随 symlink 计算目标版本化库的 SHA256。

## RID 覆盖

| RID | 当前状态 | 文件数 | 说明 |
| --- | --- | ---: | --- |
| `win-x64` | present | 64 | CPU 与 CUDA13 变体均有随包文件；包含 CUDA 13 runtime 动态库。 |
| `linux-x64` | present | 33 | 当前为 CPU 资产；包含 Linux `.so` 版本化库和零字节 alias。 |
| `osx-x64` | missing | 0 | 需要后续补齐 native runtime bundle。 |
| `osx-arm64` | missing | 0 | 需要后续补齐 native runtime bundle。 |

## Manifest 组件

| Component | Runtime path | Required libraries | Optional / variant libraries |
| --- | --- | --- | --- |
| `llama` | `.` | `llama`, `ggml`, `ggml-base`, `ggml-cpu` | CPU microarch libraries, `ggml-cuda`, CUDA 13 libraries, `ggml-cann`, `ggml-metal`, `ggml-vulkan`, `ggml-sycl`, `ggml-openvino`, `ggml-opencl`, `tomur-llama-mtmd`, `tomur-llama-vlm` |
| `whisper` | `whisper/cpu`, `whisper/cuda13` | `whisper` | CUDA13 variant requires `ggml-cuda`; shared dependencies are `llama`, `ggml`, `ggml-base`, `ggml-cpu` |
| `stable-diffusion` | `stable-diffusion/cpu`, `stable-diffusion/cuda13` | `stable-diffusion` | CUDA13 variant requires `ggml-cuda`; shared dependencies are `ggml`, `ggml-base`, `ggml-cpu` |
| `ocr` | `ocr/cpu`, `ocr/cuda13` | `tomur-ocr`, `tomur-mtmd` | CUDA13 variant requires `ggml-cuda`; shared dependencies are `llama`, `ggml`, `ggml-base`, `ggml-cpu` |
| `tts` | `tts/cpu`, `tts/cuda13` | `tomur-tts` | CUDA13 variant requires `ggml-cuda`; shared dependencies are `llama`, `ggml`, `ggml-base`, `ggml-cpu` |

## Checksum Ledger

生成命令：

```powershell
$root = Resolve-Path 'native\runtimes'
Get-ChildItem -Path $root -Recurse -File |
  Sort-Object FullName |
  ForEach-Object {
    $relative = $_.FullName.Substring($root.Path.Length + 1).Replace('\','/')
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [PSCustomObject]@{ RelativePath = $relative; Size = $_.Length; Sha256 = $hash }
  }
```

| Relative path | Size | SHA256 |
| --- | ---: | --- |
| `linux-x64/native/libggml.so` | 0 | `68e127feb6812bfec4a0a342087ccb6135ebedaa1211f90d124cd257aa3cfe97` |
| `linux-x64/native/libggml.so.0` | 0 | `68e127feb6812bfec4a0a342087ccb6135ebedaa1211f90d124cd257aa3cfe97` |
| `linux-x64/native/libggml.so.0.15.3` | 56376 | `68e127feb6812bfec4a0a342087ccb6135ebedaa1211f90d124cd257aa3cfe97` |
| `linux-x64/native/libggml-base.so` | 0 | `2803f4777529388c8390e421c12ded64d3f0dcb76d039b618813dee3430d7f05` |
| `linux-x64/native/libggml-base.so.0` | 0 | `2803f4777529388c8390e421c12ded64d3f0dcb76d039b618813dee3430d7f05` |
| `linux-x64/native/libggml-base.so.0.15.3` | 913472 | `2803f4777529388c8390e421c12ded64d3f0dcb76d039b618813dee3430d7f05` |
| `linux-x64/native/libggml-cpu.so` | 0 | `e9cee94e160e486bcb476f2d3e1949be990dc64bc12d7f8ed6d488020a8737b4` |
| `linux-x64/native/libggml-cpu.so.0` | 0 | `e9cee94e160e486bcb476f2d3e1949be990dc64bc12d7f8ed6d488020a8737b4` |
| `linux-x64/native/libggml-cpu.so.0.15.3` | 1118752 | `e9cee94e160e486bcb476f2d3e1949be990dc64bc12d7f8ed6d488020a8737b4` |
| `linux-x64/native/libllama.so` | 0 | `c7d13bb4c98927a8d8d2fecbb4986ab4d3f4972564497ed7bade5f59349e6f95` |
| `linux-x64/native/libllama.so.0` | 0 | `c7d13bb4c98927a8d8d2fecbb4986ab4d3f4972564497ed7bade5f59349e6f95` |
| `linux-x64/native/libllama.so.0.0.1` | 3871904 | `c7d13bb4c98927a8d8d2fecbb4986ab4d3f4972564497ed7bade5f59349e6f95` |
| `linux-x64/native/libtomur-llama-mtmd.so` | 1405176 | `1cc7c87d04784a8299ed39e1367ed8acf24616be66b69ded847a05538f2fdaa2` |
| `linux-x64/native/libtomur-llama-vlm.so` | 70592 | `86bdb11d66a9d90f1c0392f989983ee373be73e6126c7f64c6887e323d0db9b9` |
| `linux-x64/native/ocr/cpu/libtomur-mtmd.so` | 1405176 | `80a4fee29c9c9bfdb27ce7cd025e3c4cb28d86e3e3754f14ac5c5ffc9cb5c8bc` |
| `linux-x64/native/ocr/cpu/libtomur-ocr.so` | 56904 | `3b417d7b4c50aa8983c46caea97ac7c3b83e0ec85730eeea057b7c2cebbebdf8` |
| `linux-x64/native/stable-diffusion/cpu/include/stable-diffusion.h` | 14921 | `b90d4626981b973d798113742cf78c59743f1a082d9d46f2a71aba0e07da90d5` |
| `linux-x64/native/stable-diffusion/cpu/libstable-diffusion.so` | 54668400 | `0a2975cea8b6f907ba50e2f7239e09d1df8dc8087bb71e040f71053a3cc21b77` |
| `linux-x64/native/tts/cpu/libtomur-tts.so` | 31392 | `e11d3f8410d073bf63dbc1144e76a193dadd7833865e3d2ecb07e9da50af90d5` |
| `linux-x64/native/whisper/cpu/cmake/parakeet/parakeet-config.cmake` | 1923 | `28288a3af42b909315cbf69f3289e0b6e475afb6187c61d9fb5400a495e0320a` |
| `linux-x64/native/whisper/cpu/cmake/parakeet/parakeet-version.cmake` | 2756 | `91dc5ae391972b1095f6225e5c64992a1ee87e46d293213f6949ef07bd186571` |
| `linux-x64/native/whisper/cpu/cmake/whisper/whisper-config.cmake` | 1905 | `c3f104947948b249a835cff17502171c5203678e87dfeba33c27d4f9a1d3f88d` |
| `linux-x64/native/whisper/cpu/cmake/whisper/whisper-version.cmake` | 2756 | `91dc5ae391972b1095f6225e5c64992a1ee87e46d293213f6949ef07bd186571` |
| `linux-x64/native/whisper/cpu/include/parakeet.h` | 16133 | `ae7c60bad18ba795eb96670e050616b1e917841443e0a46038a9ef52d5bf0053` |
| `linux-x64/native/whisper/cpu/include/whisper.h` | 35640 | `6c1c70a5d4b74556f4253e51a13874ad013513b0ae62e779c0e30ffde3dc30ba` |
| `linux-x64/native/whisper/cpu/libparakeet.so` | 0 | `d1fa721e8c97d8762dbbb48d1a836e6169b45fd240d6e8942f552f7475e3e73e` |
| `linux-x64/native/whisper/cpu/libparakeet.so.1` | 0 | `d1fa721e8c97d8762dbbb48d1a836e6169b45fd240d6e8942f552f7475e3e73e` |
| `linux-x64/native/whisper/cpu/libparakeet.so.1.9.1` | 183464 | `d1fa721e8c97d8762dbbb48d1a836e6169b45fd240d6e8942f552f7475e3e73e` |
| `linux-x64/native/whisper/cpu/libwhisper.so` | 0 | `939684b4bc4d9495b9a3ca14bbc5a14b141583e6207f14c8b75e8728611e5f05` |
| `linux-x64/native/whisper/cpu/libwhisper.so.1` | 0 | `939684b4bc4d9495b9a3ca14bbc5a14b141583e6207f14c8b75e8728611e5f05` |
| `linux-x64/native/whisper/cpu/libwhisper.so.1.9.1` | 626560 | `939684b4bc4d9495b9a3ca14bbc5a14b141583e6207f14c8b75e8728611e5f05` |
| `linux-x64/native/whisper/cpu/pkgconfig/parakeet.pc` | 399 | `fea25f47e66241c10e189d65a823f8454ed3008ef448a5af67d5b87cc94392b7` |
| `linux-x64/native/whisper/cpu/pkgconfig/whisper.pc` | 397 | `fb73339b4a3d3251f877df6c97261b1751ee1ce5e01f6ae473465a8a46e6816d` |
| `win-x64/native/cublas64_13.dll` | 51870320 | `f1d500d0cd892f5b8c6b6cdbffd82d0c55d5f5427215668e7ceb55aeeccc1b63` |
| `win-x64/native/cublasLt64_13.dll` | 460301424 | `b592cd016d7673e9cb97716a22b27c4010ee635377a3ba28f37070a9bdb76a68` |
| `win-x64/native/cudart64_13.dll` | 551024 | `b00ca6f53699120da815bf3e06e2e4285fae2f201235b883dcbb50eec51e2a2a` |
| `win-x64/native/ggml.dll` | 67584 | `e96875e44d9d1ef38c602b4d89c25ef0bd91f37ac9095282e3860962a9525e93` |
| `win-x64/native/ggml.lib` | 5288 | `fb89638f851a826fe55c8c176c03ae9d6514c05ea47b23b2cb628024a2d98be1` |
| `win-x64/native/ggml-base.dll` | 657920 | `7bf00b6bcf1bfde1ff6e36be2978c9f4cbbf5223165c0d8a0292456643d6a235` |
| `win-x64/native/ggml-base.lib` | 149108 | `b4d449dbf7dc4747202fdf6c167bd44c5d27eb2a3fa982adf69ed47e65af3e62` |
| `win-x64/native/ggml-cpu.dll` | 905216 | `f93052cd2a387512c8b8f4f346690edb5b6fa4d125df1eda53293a7c4366b36e` |
| `win-x64/native/ggml-cpu.lib` | 15532 | `472ec94b1e279d261b02397d19498fe4a405e1a7f24b1720a57791572364002c` |
| `win-x64/native/ggml-cpu-alderlake.dll` | 906752 | `17fb0a904eb63dd9eb94a14972652a480ac5e89e42d099b187052602e42c0dd8` |
| `win-x64/native/ggml-cpu-cannonlake.dll` | 1019904 | `d8f93302d2df5041724d44b170dab979a5183eb4ca9e3212bb0020aa77ac67cb` |
| `win-x64/native/ggml-cpu-cascadelake.dll` | 1017344 | `8fabd6bd5fbe6118bc3669e7d8b38f6a7ae39ed4738db122dae76510f9c26691` |
| `win-x64/native/ggml-cpu-haswell.dll` | 907776 | `d0f43b52ff4059b730c5413ca7af0c5a8768facafe617f3c9b6dbb0b1636b274` |
| `win-x64/native/ggml-cpu-icelake.dll` | 1017344 | `31f85d627cc9d7aaa716d2646693e462aa1cbf3208c04d4238aa2db3c60cc47c` |
| `win-x64/native/ggml-cpu-sandybridge.dll` | 867840 | `268186db226026577f1d057147714b1507761f1b7e107d4dd8fe62d239849f4e` |
| `win-x64/native/ggml-cpu-skylakex.dll` | 1019904 | `0ea1f7e2a3edab49cf92e5d3ff5dfbe6bc4ba07af99f478f9b114395d2c1e319` |
| `win-x64/native/ggml-cpu-sse42.dll` | 765952 | `cf1f48bea2c278767d0161427eeaf94c6e9c0a59e86cf3f2e4eab65a190e327a` |
| `win-x64/native/ggml-cpu-x64.dll` | 768000 | `cd64e43b4c7e7626910c2a04a61868905303a9b1cf3ad8b16c10f84c83d04af9` |
| `win-x64/native/ggml-cuda.dll` | 54169088 | `f6f3905398b169b429df0ced2f8a197248597f86425356f5b8e71cf0943856aa` |
| `win-x64/native/llama.dll` | 2232320 | `5709a8fa00adc139594e13fbf02cfbc2ed0a4b82128335e78d8d2b638bde495a` |
| `win-x64/native/llama.lib` | 65242 | `728bf32ec2ad188e086ad0c511325e78da5670f8ba747de90454f1fe1aff998d` |
| `win-x64/native/ocr/cpu/tomur-mtmd.dll` | 978432 | `0f58f24973476925fc32d5305ecb8ab29fe442111d4bd387e51666005538ab7c` |
| `win-x64/native/ocr/cpu/tomur-ocr.dll` | 65024 | `51d49653ab1b094ed5406bec86b9501c26f4587662e1a59958a9bfe9fd9d4a05` |
| `win-x64/native/ocr/cuda13/tomur-mtmd.dll` | 978432 | `30c43f468af3eb1f67d89fbf7496b016e1aff9b0f030454ff1b16eaa9bc4cb06` |
| `win-x64/native/ocr/cuda13/tomur-ocr.dll` | 65024 | `7ec57d5b0d4fd39505feedcffb4b0febc8f7a84e168232588e72fdd3e7ed9715` |
| `win-x64/native/stable-diffusion/cpu/include/stable-diffusion.h` | 14921 | `b90d4626981b973d798113742cf78c59743f1a082d9d46f2a71aba0e07da90d5` |
| `win-x64/native/stable-diffusion/cpu/stable-diffusion.dll` | 52036096 | `ead19eaa2634d99d3794eb4caf6d98e35cbb16876dead89a4235d72134c8c6c8` |
| `win-x64/native/stable-diffusion/cpu/stable-diffusion.lib` | 14004 | `88488800496e680fdc6b94ec0bb794b7a7046d34f967e600677f23acfd0e2297` |
| `win-x64/native/stable-diffusion/cuda13/cmake/stable-diffusion/stable-diffusion-config.cmake` | 2258 | `6de9b778ec08affd05d783f1727b48b5f9393c59ca378c1636eeb158440d66a6` |
| `win-x64/native/stable-diffusion/cuda13/cmake/stable-diffusion/stable-diffusion-version.cmake` | 2866 | `f6f5fd73654a33a9ea1f062798d557de22cc1f3a1c446164d1cadb14aa2394a1` |
| `win-x64/native/stable-diffusion/cuda13/include/stable-diffusion.h` | 15169 | `7f8d52ccdd6caeed26f1ef84b2e725ead403f20ed0e0d264fea567e061504dcc` |
| `win-x64/native/stable-diffusion/cuda13/pkgconfig/stable-diffusion.pc` | 496 | `ee21122d9c2023b3e1c40f46bb424e5f624ecc63a645ae6eaefc9418f0bdea8e` |
| `win-x64/native/stable-diffusion/cuda13/stable-diffusion.dll` | 52079104 | `2a7471a0d1bab2feeed6b17c100f653703da2a88a71283041f006b38a3e32e6a` |
| `win-x64/native/stable-diffusion/cuda13/stable-diffusion.lib` | 14004 | `88488800496e680fdc6b94ec0bb794b7a7046d34f967e600677f23acfd0e2297` |
| `win-x64/native/tomur-llama-mtmd.dll` | 978944 | `196407ffd9d5e7423b25a4234c73e14879f4b0dba2ff49321acfd35c43cf1277` |
| `win-x64/native/tomur-llama-mtmd.lib` | 22160 | `fb99e22a1736ff1332808d96e72f13bb920e749d6df3f16bde9b05f6891b4210` |
| `win-x64/native/tomur-llama-vlm.dll` | 69632 | `9068f72bf19218ed3d739e8f1ab3a5fe159d5c8bcbbc3c25ad2c43ec5351adec` |
| `win-x64/native/tomur-llama-vlm.lib` | 2510 | `0fe13fa03fd64aec0abc4154b7fa2b3606479582c5a077fee9ccb4fe155462b4` |
| `win-x64/native/tts/cpu/tomur-tts.dll` | 31232 | `3f21d44368b1917c443493873b2dd250a02448729efeda8b6d65cdbbb66b9cc0` |
| `win-x64/native/tts/cuda13/tomur-tts.dll` | 262144 | `00e5ecb69f44dfdf937d0b2a258b047294bc2cfc63cf77831523720750bf012e` |
| `win-x64/native/whisper/cpu/cmake/parakeet/parakeet-config.cmake` | 1971 | `3099af9c718e6c38645a30a1490fbd7c5343a11e91c0a4151504de4dc98ba6e4` |
| `win-x64/native/whisper/cpu/cmake/parakeet/parakeet-version.cmake` | 2821 | `1233802497f5a3ff5c80cb424ef9f5ec8c7eb3f06a68c5ca5c2c64ae089b8e65` |
| `win-x64/native/whisper/cpu/cmake/whisper/whisper-config.cmake` | 1953 | `fce9667e50da67e8caa114cc3c2d6c037bd3c7f6f4389470be729ed388a40557` |
| `win-x64/native/whisper/cpu/cmake/whisper/whisper-version.cmake` | 2821 | `1233802497f5a3ff5c80cb424ef9f5ec8c7eb3f06a68c5ca5c2c64ae089b8e65` |
| `win-x64/native/whisper/cpu/include/parakeet.h` | 16133 | `ae7c60bad18ba795eb96670e050616b1e917841443e0a46038a9ef52d5bf0053` |
| `win-x64/native/whisper/cpu/include/whisper.h` | 35640 | `6c1c70a5d4b74556f4253e51a13874ad013513b0ae62e779c0e30ffde3dc30ba` |
| `win-x64/native/whisper/cpu/parakeet.dll` | 527360 | `3697b6d46ba0801b3ced2186a77a3d2d9c7b327fd3ada21fc96179f0142d7fb1` |
| `win-x64/native/whisper/cpu/parakeet.lib` | 1770406 | `5c9fc2ef534391112082a2ee33344e61e0afda2b2db8ed94532b5ff515eaa66a` |
| `win-x64/native/whisper/cpu/pkgconfig/parakeet.pc` | 393 | `d08b8012d2008ca93b8b3f45c4fee9b2b7a4e99010ff3ad048b15182601956e4` |
| `win-x64/native/whisper/cpu/pkgconfig/whisper.pc` | 391 | `bfbebc7378c291d974682a8f283339084f9d746af9076e7c92e25e97f67ffbc4` |
| `win-x64/native/whisper/cpu/whisper.dll` | 1363456 | `b33324999c0001698bd5be0d5cd5e9dd500c738b5bc4a64f540f4009b107b575` |
| `win-x64/native/whisper/cpu/whisper.lib` | 4651218 | `5df8035a1df27a966c1755b275a46c1bb6c377aab22842e332e75f415915fb9e` |
| `win-x64/native/whisper/cuda13/cmake/parakeet/parakeet-config.cmake` | 1981 | `7e3a0ff8baa6f2fd3f7145ef68facfdf66946487baa60ee8fa7406c303b692f0` |
| `win-x64/native/whisper/cuda13/cmake/parakeet/parakeet-version.cmake` | 2821 | `1233802497f5a3ff5c80cb424ef9f5ec8c7eb3f06a68c5ca5c2c64ae089b8e65` |
| `win-x64/native/whisper/cuda13/cmake/whisper/whisper-config.cmake` | 1963 | `85a104210202453c5834f8a8a2e7fb9d88859dc0660d888b87dc7fc811c4795b` |
| `win-x64/native/whisper/cuda13/cmake/whisper/whisper-version.cmake` | 2821 | `1233802497f5a3ff5c80cb424ef9f5ec8c7eb3f06a68c5ca5c2c64ae089b8e65` |
| `win-x64/native/whisper/cuda13/include/parakeet.h` | 16133 | `ae7c60bad18ba795eb96670e050616b1e917841443e0a46038a9ef52d5bf0053` |
| `win-x64/native/whisper/cuda13/include/whisper.h` | 35640 | `6c1c70a5d4b74556f4253e51a13874ad013513b0ae62e779c0e30ffde3dc30ba` |
| `win-x64/native/whisper/cuda13/parakeet.dll` | 527360 | `a46fd0367f091b073aefe558002c1711f169919ef31ff40b8d8a22382913182b` |
| `win-x64/native/whisper/cuda13/parakeet.lib` | 1770406 | `5c9fc2ef534391112082a2ee33344e61e0afda2b2db8ed94532b5ff515eaa66a` |
| `win-x64/native/whisper/cuda13/pkgconfig/parakeet.pc` | 399 | `03aa11ba07ad3cf508f8c6edd36c81c53e1727455067124f1c521a3bcfac17fe` |
| `win-x64/native/whisper/cuda13/pkgconfig/whisper.pc` | 397 | `a52270d4444d5abbf35f64bd6fd51eb0e64ea079d36bce869888b2f8dd445a81` |
| `win-x64/native/whisper/cuda13/whisper.dll` | 1363456 | `3cf224c6002ddc4b87c7b1da5c272249bd94b25d9071438ad72624e0ffe24c00` |
| `win-x64/native/whisper/cuda13/whisper.lib` | 4651218 | `5df8035a1df27a966c1755b275a46c1bb6c377aab22842e332e75f415915fb9e` |

## 后续补齐

1. 补齐 `native/runtimes/osx-x64/native` 和 `native/runtimes/osx-arm64/native`。
2. 为 Linux x64 补齐 CUDA13 变体，或在发布说明中明确 Linux 当前只随包 CPU native runtime。
3. 发布前重新生成本文件的 checksum ledger，并确认发布目录中的 `native/runtimes/{rid}/native` 与本清单一致。
