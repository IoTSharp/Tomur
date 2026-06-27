cmake_minimum_required(VERSION 3.21)
project(stable_diffusion_native VERSION 1.0 LANGUAGES C CXX)

set(SD_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SD_WEBP OFF CACHE BOOL "" FORCE)
set(SD_USE_SYSTEM_WEBP OFF CACHE BOOL "" FORCE)
set(SD_WEBM OFF CACHE BOOL "" FORCE)
set(SD_USE_SYSTEM_WEBM OFF CACHE BOOL "" FORCE)
set(SD_BUILD_SHARED_LIBS ON CACHE BOOL "" FORCE)
set(SD_BUILD_SHARED_GGML_LIB OFF CACHE BOOL "" FORCE)
option(TOMUR_SD_USE_LLAMA_SHARED_GGML "Use llama.native-exported ggml package" ON)
if(TOMUR_SD_USE_LLAMA_SHARED_GGML)
    set(SD_USE_SYSTEM_GGML ON CACHE BOOL "" FORCE)
    list(PREPEND CMAKE_PREFIX_PATH "${CMAKE_CURRENT_SOURCE_DIR}/..")
else()
    set(SD_USE_SYSTEM_GGML OFF CACHE BOOL "" FORCE)
endif()
set(STABLE_DIFFUSION_NATIVE_BACKEND "cpu" CACHE STRING "Tomur stable-diffusion backend flavor")
string(TOLOWER "${STABLE_DIFFUSION_NATIVE_BACKEND}" STABLE_DIFFUSION_NATIVE_BACKEND)

if(WIN32)
    set(STABLE_DIFFUSION_NATIVE_RID "win-x64")
    if(CMAKE_GENERATOR_PLATFORM STREQUAL "ARM64")
        set(STABLE_DIFFUSION_NATIVE_RID "win-arm64")
    endif()
elseif(APPLE)
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "arm64|aarch64")
        set(STABLE_DIFFUSION_NATIVE_RID "osx-arm64")
    else()
        set(STABLE_DIFFUSION_NATIVE_RID "osx-x64")
    endif()
else()
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "aarch64|arm64")
        set(STABLE_DIFFUSION_NATIVE_RID "linux-arm64")
    else()
        set(STABLE_DIFFUSION_NATIVE_RID "linux-x64")
    endif()
endif()

set(STABLE_DIFFUSION_NATIVE_DESTINATION
    "runtimes/${STABLE_DIFFUSION_NATIVE_RID}/native/stable-diffusion/${STABLE_DIFFUSION_NATIVE_BACKEND}")

set(CMAKE_INSTALL_BINDIR     "${STABLE_DIFFUSION_NATIVE_DESTINATION}"            CACHE PATH "" FORCE)
set(CMAKE_INSTALL_LIBDIR     "${STABLE_DIFFUSION_NATIVE_DESTINATION}"            CACHE PATH "" FORCE)
set(CMAKE_INSTALL_INCLUDEDIR "${STABLE_DIFFUSION_NATIVE_DESTINATION}/include"    CACHE PATH "" FORCE)

if(APPLE)
    set(CMAKE_INSTALL_RPATH "@loader_path;@loader_path/../..")
elseif(UNIX)
    set(CMAKE_INSTALL_RPATH "$ORIGIN;$ORIGIN/../..")
endif()

add_subdirectory(
    ${CMAKE_CURRENT_SOURCE_DIR}/../stable-diffusion.cpp
    ${CMAKE_CURRENT_BINARY_DIR}/stable-diffusion.cpp
)

if(NOT TARGET stable-diffusion)
    message(FATAL_ERROR "stable-diffusion target was not produced by native/stable-diffusion.cpp")
endif()

target_sources(stable-diffusion PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/stable_diffusion_bridge.c
    ${CMAKE_CURRENT_SOURCE_DIR}/stable_diffusion_image_bridge.cpp)

if(MSVC)
    target_compile_options(stable-diffusion PRIVATE /bigobj)
endif()

if(TOMUR_SD_USE_LLAMA_SHARED_GGML)
    target_compile_definitions(stable-diffusion PRIVATE GGML_MAX_NAME=128)
    target_include_directories(stable-diffusion PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../include)

    function(tomur_link_sd_shared_backend target_name imported_target)
        if(TARGET ${imported_target})
            target_link_libraries(${target_name} PRIVATE ${imported_target})
        endif()
    endfunction()

    tomur_link_sd_shared_backend(stable-diffusion ggml::ggml-base)
    tomur_link_sd_shared_backend(stable-diffusion ggml::ggml-cpu)

    if(SD_CUDA)
        tomur_link_sd_shared_backend(stable-diffusion ggml::ggml-cuda)
    endif()

    if(SD_VULKAN)
        tomur_link_sd_shared_backend(stable-diffusion ggml::ggml-vulkan)
    endif()

    if(SD_OPENCL)
        tomur_link_sd_shared_backend(stable-diffusion ggml::ggml-opencl)
    endif()

    if(SD_SYCL)
        tomur_link_sd_shared_backend(stable-diffusion ggml::ggml-sycl)
    endif()
endif()

include(GNUInstallDirs)

function(tomur_sd_force_isolated_install target_name)
    if(NOT TARGET ${target_name})
        return()
    endif()

    if(WIN32)
        install(TARGETS ${target_name}
            RUNTIME DESTINATION ${STABLE_DIFFUSION_NATIVE_DESTINATION}
            LIBRARY DESTINATION ${STABLE_DIFFUSION_NATIVE_DESTINATION}
        )
    else()
        install(TARGETS ${target_name}
            LIBRARY DESTINATION ${STABLE_DIFFUSION_NATIVE_DESTINATION}
        )
    endif()
endfunction()

tomur_sd_force_isolated_install(stable-diffusion)
