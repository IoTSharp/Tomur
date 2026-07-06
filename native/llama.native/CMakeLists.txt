cmake_minimum_required(VERSION 3.21)
project(llama_native VERSION 1.0 LANGUAGES C CXX)

set(TOMUR_GGML_MAX_NAME "128" CACHE STRING "Tomur ggml tensor name capacity")
add_compile_definitions(GGML_MAX_NAME=${TOMUR_GGML_MAX_NAME})

if(APPLE)
    set(CMAKE_INSTALL_RPATH "@loader_path")
elseif(UNIX)
    set(CMAKE_INSTALL_RPATH "$ORIGIN")
endif()

# Build llama.cpp as a shared library and suppress everything else
set(BUILD_SHARED_LIBS ON CACHE BOOL "Build shared libraries" FORCE)
set(LLAMA_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_TOOLS OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_SERVER OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_COMMON OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_WEBUI OFF CACHE BOOL "" FORCE)
set(LLAMA_TOOLS_INSTALL OFF CACHE BOOL "" FORCE)

add_subdirectory(
    ${CMAKE_CURRENT_SOURCE_DIR}/../llama.cpp
    ${CMAKE_CURRENT_BINARY_DIR}/llama.cpp
)

find_package(Threads REQUIRED)

set(LLAMA_CPP_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../llama.cpp")
set(MTMD_DIR "${LLAMA_CPP_DIR}/tools/mtmd")
file(GLOB MTMD_MODEL_SOURCES CONFIGURE_DEPENDS "${MTMD_DIR}/models/*.cpp")
set(MTMD_SOURCES
    "${MTMD_DIR}/mtmd.cpp"
    "${MTMD_DIR}/mtmd-audio.cpp"
    "${MTMD_DIR}/mtmd-image.cpp"
    "${MTMD_DIR}/mtmd-helper.cpp"
    "${MTMD_DIR}/clip.cpp"
    ${MTMD_MODEL_SOURCES})

add_library(tomur_llama_mtmd SHARED ${MTMD_SOURCES})
target_compile_features(tomur_llama_mtmd PRIVATE cxx_std_17)
target_compile_definitions(tomur_llama_mtmd PRIVATE LLAMA_BUILD)
target_compile_definitions(tomur_llama_mtmd PUBLIC LLAMA_SHARED)
target_include_directories(tomur_llama_mtmd
    PUBLIC
        "${MTMD_DIR}"
    PRIVATE
        "${LLAMA_CPP_DIR}"
        "${LLAMA_CPP_DIR}/include"
        "${LLAMA_CPP_DIR}/ggml/include"
        "${LLAMA_CPP_DIR}/ggml/src"
        "${LLAMA_CPP_DIR}/vendor")
target_link_libraries(tomur_llama_mtmd PUBLIC llama)
target_link_libraries(tomur_llama_mtmd PRIVATE Threads::Threads)
set_target_properties(tomur_llama_mtmd PROPERTIES
    OUTPUT_NAME "tomur-llama-mtmd"
    POSITION_INDEPENDENT_CODE ON)

if(NOT MSVC)
    target_compile_options(tomur_llama_mtmd PRIVATE -Wno-cast-qual)
endif()

add_library(tomur-llama-vlm SHARED llama_vlm_bridge.cpp)
target_compile_features(tomur-llama-vlm PRIVATE cxx_std_17)
target_include_directories(tomur-llama-vlm
    PRIVATE
        "${MTMD_DIR}"
        "${LLAMA_CPP_DIR}/include"
        "${LLAMA_CPP_DIR}/ggml/include")
target_link_libraries(tomur-llama-vlm PRIVATE tomur_llama_mtmd llama ${CMAKE_DL_LIBS})
set_target_properties(tomur-llama-vlm PROPERTIES
    OUTPUT_NAME "tomur-llama-vlm"
    POSITION_INDEPENDENT_CODE ON)

if(MSVC)
    target_compile_options(tomur_llama_mtmd PRIVATE /bigobj)
    target_compile_options(tomur-llama-vlm PRIVATE /bigobj)
endif()

# Install the shared library to CMAKE_INSTALL_PREFIX/runtimes/<rid>/native/
include(GNUInstallDirs)
if(WIN32)
    set(LLAMA_NATIVE_RID "win-x64")
    if(CMAKE_GENERATOR_PLATFORM STREQUAL "ARM64")
        set(LLAMA_NATIVE_RID "win-arm64")
    endif()
elseif(APPLE)
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "arm64|aarch64")
        set(LLAMA_NATIVE_RID "osx-arm64")
    else()
        set(LLAMA_NATIVE_RID "osx-x64")
    endif()
else()
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "aarch64|arm64")
        set(LLAMA_NATIVE_RID "linux-arm64")
    else()
        set(LLAMA_NATIVE_RID "linux-x64")
    endif()
endif()

set(LLAMA_NATIVE_DESTINATION "runtimes/${LLAMA_NATIVE_RID}/native")

function(tomur_llama_install_if_target target_name)
    if(NOT TARGET ${target_name})
        return()
    endif()

    if(WIN32)
        install(TARGETS ${target_name}
            RUNTIME DESTINATION ${LLAMA_NATIVE_DESTINATION}
            LIBRARY DESTINATION ${LLAMA_NATIVE_DESTINATION}
            ARCHIVE DESTINATION ${LLAMA_NATIVE_DESTINATION}
        )
    else()
        install(TARGETS ${target_name}
            LIBRARY DESTINATION ${LLAMA_NATIVE_DESTINATION}
            ARCHIVE DESTINATION ${LLAMA_NATIVE_DESTINATION}
        )
    endif()
endfunction()

tomur_llama_install_if_target(llama)
tomur_llama_install_if_target(ggml)
tomur_llama_install_if_target(ggml-base)
tomur_llama_install_if_target(ggml-cpu)
tomur_llama_install_if_target(ggml-cuda)
tomur_llama_install_if_target(ggml-vulkan)
tomur_llama_install_if_target(ggml-openvino)
tomur_llama_install_if_target(ggml-sycl)
tomur_llama_install_if_target(tomur_llama_mtmd)
tomur_llama_install_if_target(tomur-llama-vlm)
