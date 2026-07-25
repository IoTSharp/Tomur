# syntax=docker/dockerfile:1.7

ARG NODE_IMAGE=node:26
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime-deps:10.0
ARG NATIVE_BUILD_IMAGE=ubuntu:24.04

# Web 资源在构建机架构上生成，产物与最终 CPU 架构无关。
FROM --platform=$BUILDPLATFORM ${NODE_IMAGE} AS web-build
WORKDIR /src
COPY web/package.json ./web/package.json
RUN --mount=type=cache,target=/root/.npm \
    cd web && npm install --no-audit --no-fund --no-package-lock
COPY web/ ./web/
RUN cd web && npm run build

# 原生依赖在构建平台交叉编译，Compose 的 platform 会把 CPU 架构传入 TARGETARCH。
FROM --platform=$BUILDPLATFORM ${NATIVE_BUILD_IMAGE} AS native-base
ARG TARGETARCH
ARG NATIVE_BUILD_JOBS=8
ARG CPU_ARM64_ARCH=armv8-a
ENV DEBIAN_FRONTEND=noninteractive
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt/lists,sharing=locked \
    apt-get update && \
    apt-get install -y --no-install-recommends \
      build-essential \
      ca-certificates \
      cmake \
      gcc-aarch64-linux-gnu \
      gcc-x86-64-linux-gnu \
      git \
      g++-aarch64-linux-gnu \
      g++-x86-64-linux-gnu \
      ninja-build \
      pkg-config

# 所有 CMake 项目共用同一个目标工具链，避免任一依赖回退到构建机架构。
RUN case "$TARGETARCH" in \
      arm64) processor=aarch64; cc=aarch64-linux-gnu-gcc; cxx=aarch64-linux-gnu-g++; initial_flags="-march=$CPU_ARM64_ARCH" ;; \
      amd64) processor=x86_64; cc=x86_64-linux-gnu-gcc; cxx=x86_64-linux-gnu-g++; initial_flags= ;; \
      *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    { \
      echo 'set(CMAKE_SYSTEM_NAME Linux)'; \
      echo "set(CMAKE_SYSTEM_PROCESSOR $processor)"; \
      echo "set(CMAKE_C_COMPILER $cc)"; \
      echo "set(CMAKE_CXX_COMPILER $cxx)"; \
      echo "set(CMAKE_C_FLAGS_INIT \"$initial_flags\")"; \
      echo "set(CMAKE_CXX_FLAGS_INIT \"$initial_flags\")"; \
    } > /opt/tomur-toolchain.cmake

# llama.cpp 仅启用 CPU backend；ARM64 优化参数不会传给 AMD64 构建。
FROM native-base AS llama-build
WORKDIR /src/native
COPY native/llama.cpp/ ./llama.cpp/
COPY native/llama.native/ ./llama.native/
RUN case "$TARGETARCH" in \
      arm64) rid=linux-arm64; machine=AArch64; arm_option="-DGGML_CPU_ARM_ARCH=$CPU_ARM64_ARCH" ;; \
      amd64) rid=linux-x64; machine='Advanced Micro Devices X86-64'; arm_option= ;; \
      *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    cmake \
      -S /src/native/llama.native \
      -B /build/llama \
      -G Ninja \
      -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_TOOLCHAIN_FILE=/opt/tomur-toolchain.cmake \
      -DCMAKE_INSTALL_PREFIX=/out/native \
      -DGGML_NATIVE=OFF \
      -DGGML_OPENMP=ON \
      $arm_option && \
    cmake --build /build/llama --parallel "$NATIVE_BUILD_JOBS" && \
    cmake --install /build/llama --strip && \
    runtime_root="/out/native/runtimes/$rid/native" && \
    for library in llama ggml ggml-base ggml-cpu tomur-llama-mtmd tomur-llama-vlm; do \
      library_path="$runtime_root/lib$library.so"; \
      test -s "$library_path"; \
      readelf -h "$library_path" | grep -q "Machine:.*$machine"; \
      readelf -d "$library_path" >/dev/null; \
    done && \
    for symbol in tomur_llama_vlm_generate tomur_llama_vlm_result_free tomur_llama_vlm_bridge_version; do \
      readelf -Ws "$runtime_root/libtomur-llama-vlm.so" | grep -q "$symbol"; \
    done && \
    mkdir -p /out/runtime-deps && \
    case "$TARGETARCH" in arm64) cc=aarch64-linux-gnu-gcc ;; amd64) cc=x86_64-linux-gnu-gcc ;; esac && \
    libgomp_path="$("$cc" -print-file-name=libgomp.so.1)" && \
    test -s "$libgomp_path" && \
    cp --dereference "$libgomp_path" /out/runtime-deps/libgomp.so.1

# OpenCV、MNN、HyperLPR3 和 Tomur bridge 均从当前 Tomur 子模块构建。
FROM native-base AS plate-compile
WORKDIR /src/native
COPY native/opencv/ ./opencv/
COPY native/mnn/ ./mnn/
COPY native/hyperlpr3/ ./hyperlpr3/
COPY native/plate.native/ ./plate.native/

# OpenCV 只构建图片解码和 HyperLPR3 必需模块，格式库静态并入 OpenCV 动态库。
RUN cmake \
      -S /src/native/opencv \
      -B /build/opencv \
      -G Ninja \
      -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_TOOLCHAIN_FILE=/opt/tomur-toolchain.cmake \
      -DCMAKE_INSTALL_PREFIX=/deps/opencv \
      -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON \
      -DCMAKE_INSTALL_RPATH=\$ORIGIN \
      -DBUILD_LIST=core,imgproc,imgcodecs \
      -DBUILD_SHARED_LIBS=ON \
      -DBUILD_opencv_apps=OFF \
      -DBUILD_opencv_js=OFF \
      -DBUILD_opencv_java=OFF \
      -DBUILD_opencv_python2=OFF \
      -DBUILD_opencv_python3=OFF \
      -DBUILD_EXAMPLES=OFF \
      -DBUILD_TESTS=OFF \
      -DBUILD_PERF_TESTS=OFF \
      -DBUILD_DOCS=OFF \
      -DOPENCV_FORCE_3RDPARTY_BUILD=ON \
      -DBUILD_ZLIB=ON \
      -DBUILD_JPEG=ON \
      -DBUILD_PNG=ON \
      -DBUILD_WEBP=ON \
      -DWITH_JPEG=ON \
      -DWITH_PNG=ON \
      -DWITH_WEBP=ON \
      -DWITH_OPENCL=OFF \
      -DWITH_IPP=OFF \
      -DWITH_ITT=OFF \
      -DWITH_TBB=OFF \
      -DWITH_EIGEN=OFF \
      -DWITH_FFMPEG=OFF \
      -DWITH_GSTREAMER=OFF \
      -DWITH_GTK=OFF && \
    cmake --build /build/opencv --parallel "$NATIVE_BUILD_JOBS" && \
    cmake --install /build/opencv --strip

# 兼容 GCC 13 严格 ANSI 头文件规则和新版 GNU 汇编器的 AArch64 寄存器语法。
COPY deploy/patches/mnn-2.2.0-gcc13.patch /tmp/mnn-2.2.0-gcc13.patch
RUN git -C /src/native/mnn apply --check /tmp/mnn-2.2.0-gcc13.patch && \
    git -C /src/native/mnn apply /tmp/mnn-2.2.0-gcc13.patch

# HyperLPR3 保留 FetchContent target 关系，但 MNN 强制解析到本地固定子模块。
RUN case "$TARGETARCH" in \
      arm64) mnn_arm82=ON ;; \
      amd64) mnn_arm82=OFF ;; \
      *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    cmake \
      -S /src/native/hyperlpr3 \
      -B /build/hyperlpr3 \
      -G Ninja \
      -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_TOOLCHAIN_FILE=/opt/tomur-toolchain.cmake \
      -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
      -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON \
      -DCMAKE_INSTALL_RPATH=\$ORIGIN \
      -DOpenCV_DIR=/deps/opencv/lib/cmake/opencv4 \
      -DLINUX_FETCH_MNN=ON \
      -DFETCHCONTENT_SOURCE_DIR_MNN=/src/native/mnn \
      -DMNN_BUILD_SHARED_LIBS=OFF \
      -DMNN_SEP_BUILD=OFF \
      -DMNN_BUILD_TOOLS=OFF \
      -DMNN_BUILD_TEST=OFF \
      -DMNN_BUILD_BENCHMARK=OFF \
      -DMNN_BUILD_DEMO=OFF \
      -DMNN_BUILD_CONVERTER=OFF \
      -DMNN_BUILD_PROTOBUFFER=OFF \
      -DMNN_ARM82="$mnn_arm82" \
      -DBUILD_SHARE=ON \
      -DBUILD_SAMPLES=OFF \
      -DBUILD_TEST=OFF && \
    cmake --build /build/hyperlpr3 --target hyperlpr3 --parallel "$NATIVE_BUILD_JOBS" && \
    mkdir -p /deps/hyperlpr3/include /deps/hyperlpr3/lib && \
    cp /src/native/hyperlpr3/cpp/c_api/hyper_lpr_sdk.h /deps/hyperlpr3/include/ && \
    hyperlpr_library="$(find /build/hyperlpr3 -name libhyperlpr3.so -type f | head -n 1)" && \
    test -s "$hyperlpr_library" && \
    cp "$hyperlpr_library" /deps/hyperlpr3/lib/libhyperlpr3.so

# Tomur bridge 与同架构 HyperLPR3/OpenCV 链接，SONAME 和真实文件一并进入 plate/cpu。
RUN cmake \
      -S /src/native/plate.native \
      -B /build/plate \
      -G Ninja \
      -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_TOOLCHAIN_FILE=/opt/tomur-toolchain.cmake \
      -DCMAKE_INSTALL_PREFIX=/out/native \
      -DTOMUR_HYPERLPR3_ROOT=/deps/hyperlpr3 \
      -DOpenCV_DIR=/deps/opencv/lib/cmake/opencv4 && \
    cmake --build /build/plate --parallel "$NATIVE_BUILD_JOBS" && \
    cmake --install /build/plate --strip && \
    case "$TARGETARCH" in arm64) rid=linux-arm64 ;; amd64) rid=linux-x64 ;; esac && \
    plate_root="/out/native/runtimes/$rid/native/plate/cpu" && \
    cp -a /deps/opencv/lib/libopencv_core.so* "$plate_root/" && \
    cp -a /deps/opencv/lib/libopencv_imgproc.so* "$plate_root/" && \
    cp -a /deps/opencv/lib/libopencv_imgcodecs.so* "$plate_root/"

# 检查目标 ELF、公开 C ABI 和全部非系统 DT_NEEDED 依赖。
FROM plate-compile AS plate-build
RUN case "$TARGETARCH" in \
      arm64) rid=linux-arm64; machine=AArch64 ;; \
      amd64) rid=linux-x64; machine='Advanced Micro Devices X86-64' ;; \
    esac && \
    plate_root="/out/native/runtimes/$rid/native/plate/cpu" && \
    for library in tomur-plate hyperlpr3 opencv_core opencv_imgproc opencv_imgcodecs; do \
      library_path="$(find "$plate_root" -maxdepth 1 -name "lib$library.so*" | head -n 1)"; \
      if [ -z "$library_path" ] || [ ! -s "$library_path" ]; then \
        echo "缺少车牌识别运行库: lib$library.so" >&2; exit 1; \
      fi; \
      if ! readelf -h "$library_path" | grep -q "Machine:.*$machine"; then \
        echo "车牌识别运行库架构错误: $library_path" >&2; readelf -h "$library_path" >&2; exit 1; \
      fi; \
      if ! readelf -d "$library_path" >/dev/null; then \
        echo "无法读取车牌识别运行库动态段: $library_path" >&2; exit 1; \
      fi; \
    done && \
    for symbol in tomur_plate_recognize_image tomur_plate_result_free; do \
      if ! readelf -Ws "$plate_root/libtomur-plate.so" | grep -q "$symbol"; then \
        echo "Tomur 车牌 bridge 缺少公开符号: $symbol" >&2; exit 1; \
      fi; \
    done && \
    if readelf -d "$plate_root/libhyperlpr3.so" | grep -q 'Shared library: \[libMNN'; then \
      echo 'HyperLPR3 仍动态依赖 MNN，bundle 不完整。' >&2; exit 1; \
    fi && \
    missing_dependency=0 && \
    for object in $(find "$plate_root" -maxdepth 1 -type f -name '*.so*'); do \
      for needed in $(readelf -d "$object" | sed -n 's/.*Shared library: \[\(.*\)\]/\1/p'); do \
        case "$needed" in \
          libc.so.6|libm.so.6|libmvec.so.1|libstdc++.so.6|libgcc_s.so.1|libpthread.so.0|libdl.so.2|librt.so.1|libgomp.so.1|libatomic.so.1|ld-linux-*.so.*) ;; \
          *) if [ ! -e "$plate_root/$needed" ]; then \
               echo "车牌识别 bundle 缺少依赖: $object -> $needed" >&2; missing_dependency=1; \
             fi ;; \
        esac; \
      done; \
    done && \
    test "$missing_dependency" -eq 0

# 独立导出原生资产，供发行流水线检查或复用。
FROM scratch AS native-export
COPY --from=llama-build /out/native/runtimes/ /native/runtimes/
COPY --from=plate-build /out/native/runtimes/ /native/runtimes/
COPY --from=llama-build /out/runtime-deps/ /runtime-deps/

# 托管宿主按目标架构选择 .NET RID，并在发布前注入原生 bundle。
FROM --platform=$BUILDPLATFORM ${DOTNET_SDK_IMAGE} AS app-build
ARG TARGETARCH
WORKDIR /src
COPY app/ ./app/
COPY providers/ ./providers/
COPY deploy/cpu.bundle.manifest.json ./native/bundle.manifest.json
COPY LICENSE ./LICENSE
COPY NOTICE ./NOTICE
COPY THIRD_PARTY_NOTICES.md ./THIRD_PARTY_NOTICES.md
COPY native/hyperlpr3/LICENSE ./licenses/hyperlpr3/LICENSE
COPY native/mnn/LICENSE ./licenses/mnn/LICENSE
COPY native/opencv/LICENSE ./licenses/opencv/LICENSE
COPY --from=llama-build /out/native/runtimes/ ./native/runtimes/
COPY --from=plate-build /out/native/runtimes/ ./native/runtimes/
COPY --from=web-build /src/app/wwwroot/ ./app/wwwroot/
RUN --mount=type=cache,target=/root/.nuget/packages \
    case "$TARGETARCH" in arm64) rid=linux-arm64 ;; amd64) rid=linux-x64 ;; \
      *) echo "Unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; esac && \
    dotnet publish app/Tomur.csproj \
      --configuration Release \
      --runtime "$rid" \
      --self-contained true \
      --output /out/tomur \
      -p:PublishSingleFile=false \
      -p:DebugType=None \
      -p:DebugSymbols=false && \
    test -s /out/tomur/native/bundle.manifest.json && \
    grep -q 'tomur.native.cpu.llama.plate' /out/tomur/native/bundle.manifest.json && \
    for library in llama ggml ggml-base ggml-cpu tomur-llama-mtmd tomur-llama-vlm; do \
      test -s "/out/tomur/native/runtimes/$rid/native/lib$library.so"; \
    done && \
    for library in tomur-plate hyperlpr3 opencv_core opencv_imgproc opencv_imgcodecs; do \
      find "/out/tomur/native/runtimes/$rid/native/plate/cpu" \
        -maxdepth 1 -name "lib$library.so*" -size +0c | grep -q .; \
    done

FROM --platform=$TARGETPLATFORM ${DOTNET_RUNTIME_IMAGE} AS runtime
ARG TARGETARCH
ARG TOMUR_REVISION=unknown
ARG LLAMA_REVISION=unknown
ARG HYPERLPR3_REVISION=unknown
ARG MNN_REVISION=unknown
ARG OPENCV_REVISION=unknown
ARG CPU_ARM64_ARCH=armv8-a
LABEL org.opencontainers.image.title="Tomur CPU" \
      org.opencontainers.image.source="https://github.com/IoTSharp/Tomur" \
      org.opencontainers.image.revision="$TOMUR_REVISION" \
      io.tomur.llama.revision="$LLAMA_REVISION" \
      io.tomur.hyperlpr3.revision="$HYPERLPR3_REVISION" \
      io.tomur.mnn.revision="$MNN_REVISION" \
      io.tomur.opencv.revision="$OPENCV_REVISION" \
      io.tomur.target.arch="$TARGETARCH" \
      io.tomur.arm64.cpu="$CPU_ARM64_ARCH"
COPY --from=llama-build /out/runtime-deps/libgomp.so.1 /usr/local/lib/libgomp.so.1
COPY --from=app-build /etc/ssl/certs/ca-certificates.crt /etc/ssl/certs/ca-certificates.crt
ENV ASPNETCORE_URLS=http://0.0.0.0:5137 \
    TOMUR_DATA_DIR=/data/tomur \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/data/tomur/bundle-cache \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_EnableDiagnostics=0 \
    LD_LIBRARY_PATH=/usr/local/lib
WORKDIR /app
COPY --from=app-build --chown=1654:1654 /out/tomur/ ./
USER 1654:1654
EXPOSE 5137
ENTRYPOINT ["./Tomur"]
CMD ["serve", "--urls", "http://0.0.0.0:5137", "--data-dir", "/data/tomur"]
