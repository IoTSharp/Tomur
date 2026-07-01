cmake_minimum_required(VERSION 3.21)
project(whisper_native VERSION 1.0 LANGUAGES C CXX)

set(BUILD_SHARED_LIBS ON CACHE BOOL "Build shared libraries" FORCE)
set(WHISPER_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_SERVER OFF CACHE BOOL "" FORCE)
option(TOMUR_WHISPER_USE_LLAMA_SHARED_GGML "Use llama.native-exported ggml package" ON)
if(TOMUR_WHISPER_USE_LLAMA_SHARED_GGML)
    set(WHISPER_USE_SYSTEM_GGML ON CACHE BOOL "" FORCE)
    list(PREPEND CMAKE_PREFIX_PATH "${CMAKE_CURRENT_SOURCE_DIR}/..")
    find_package(ggml CONFIG REQUIRED)
    if(TARGET ggml::ggml)
        set_property(TARGET ggml::ggml APPEND PROPERTY
            INTERFACE_INCLUDE_DIRECTORIES "${CMAKE_CURRENT_SOURCE_DIR}/../include")
    endif()
    if(NOT TARGET ggml AND TARGET ggml::ggml)
        add_library(ggml ALIAS ggml::ggml)
    endif()
else()
    set(WHISPER_USE_SYSTEM_GGML OFF CACHE BOOL "" FORCE)
endif()
set(WHISPER_NATIVE_BACKEND "cpu" CACHE STRING "Tomur whisper backend flavor")

string(TOLOWER "${WHISPER_NATIVE_BACKEND}" WHISPER_NATIVE_BACKEND)

if(WIN32)
    set(WHISPER_NATIVE_RID "win-x64")
    if(CMAKE_GENERATOR_PLATFORM STREQUAL "ARM64")
        set(WHISPER_NATIVE_RID "win-arm64")
    endif()
elseif(APPLE)
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "arm64|aarch64")
        set(WHISPER_NATIVE_RID "osx-arm64")
    else()
        set(WHISPER_NATIVE_RID "osx-x64")
    endif()
else()
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "aarch64|arm64")
        set(WHISPER_NATIVE_RID "linux-arm64")
    else()
        set(WHISPER_NATIVE_RID "linux-x64")
    endif()
endif()

set(WHISPER_NATIVE_DESTINATION "runtimes/${WHISPER_NATIVE_RID}/native/whisper/${WHISPER_NATIVE_BACKEND}")

set(CMAKE_INSTALL_BINDIR     "${WHISPER_NATIVE_DESTINATION}"            CACHE PATH "" FORCE)
set(CMAKE_INSTALL_LIBDIR     "${WHISPER_NATIVE_DESTINATION}"            CACHE PATH "" FORCE)
set(CMAKE_INSTALL_INCLUDEDIR "${WHISPER_NATIVE_DESTINATION}/include"    CACHE PATH "" FORCE)

if(APPLE)
    set(CMAKE_INSTALL_RPATH "@loader_path;@loader_path/../..")
elseif(UNIX)
    set(CMAKE_INSTALL_RPATH "$ORIGIN;$ORIGIN/../..")
endif()

add_subdirectory(
    ${CMAKE_CURRENT_SOURCE_DIR}/../whisper.cpp
    ${CMAKE_CURRENT_BINARY_DIR}/whisper.cpp
)

target_sources(whisper PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/whisper_bridge.c)

function(tomur_link_whisper_shared_backend target_name imported_target)
    if(TARGET ${imported_target})
        target_link_libraries(${target_name} PRIVATE ${imported_target})
    endif()
endfunction()

if(TOMUR_WHISPER_USE_LLAMA_SHARED_GGML)
    tomur_link_whisper_shared_backend(whisper ggml::ggml-base)
    tomur_link_whisper_shared_backend(whisper ggml::ggml-cpu)

    if(WHISPER_NATIVE_BACKEND MATCHES "cuda")
        tomur_link_whisper_shared_backend(whisper ggml::ggml-cuda)
    endif()

    if(WHISPER_NATIVE_BACKEND MATCHES "vulkan")
        tomur_link_whisper_shared_backend(whisper ggml::ggml-vulkan)
    endif()

    if(WHISPER_NATIVE_BACKEND MATCHES "openvino|npu")
        tomur_link_whisper_shared_backend(whisper ggml::ggml-openvino)
    endif()
endif()

include(GNUInstallDirs)

function(tomur_whisper_force_isolated_install target_name)
    if(NOT TARGET ${target_name})
        return()
    endif()

    if(WIN32)
        install(TARGETS ${target_name}
            RUNTIME DESTINATION ${WHISPER_NATIVE_DESTINATION}
        )
    else()
        install(TARGETS ${target_name}
            LIBRARY DESTINATION ${WHISPER_NATIVE_DESTINATION}
        )
    endif()
endfunction()

tomur_whisper_force_isolated_install(whisper)
