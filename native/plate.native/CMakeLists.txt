cmake_minimum_required(VERSION 3.21)
project(tomur_plate_native VERSION 1.0 LANGUAGES CXX)

set(TOMUR_HYPERLPR3_ROOT "" CACHE PATH "HyperLPR3 install root containing include and lib/bin")
set(TOMUR_HYPERLPR3_INCLUDE_DIR "" CACHE PATH "Directory containing hyper_lpr_sdk.h")
set(TOMUR_HYPERLPR3_LIBRARY "" CACHE FILEPATH "HyperLPR3 link library")
set(TOMUR_HYPERLPR3_RUNTIME_LIBRARY "" CACHE FILEPATH "HyperLPR3 runtime library copied into the Tomur bundle")
set(TOMUR_MNN_RUNTIME_LIBRARY "" CACHE FILEPATH "Optional dynamic MNN runtime copied into the Tomur bundle")
set(TOMUR_PLATE_RUNTIME_DEPENDENCIES "" CACHE STRING "Optional semicolon-separated OpenCV/MNN runtime files copied into the Tomur bundle")

# 交叉编译必须从显式目标依赖目录解析，不能静默链接构建机的 x64 头文件或库。
if(CMAKE_CROSSCOMPILING)
    if(NOT TOMUR_HYPERLPR3_ROOT AND
       (NOT TOMUR_HYPERLPR3_INCLUDE_DIR OR NOT TOMUR_HYPERLPR3_LIBRARY))
        message(FATAL_ERROR
            "Cross-compiling tomur-plate requires TOMUR_HYPERLPR3_ROOT or both TOMUR_HYPERLPR3_INCLUDE_DIR and TOMUR_HYPERLPR3_LIBRARY for the target architecture.")
    endif()
    if(NOT OpenCV_DIR)
        message(FATAL_ERROR
            "Cross-compiling tomur-plate requires OpenCV_DIR to point to the target-architecture OpenCV CMake package.")
    endif()
endif()

if(WIN32)
    set(TOMUR_PLATE_RID "win-x64")
    if(CMAKE_GENERATOR_PLATFORM STREQUAL "ARM64")
        set(TOMUR_PLATE_RID "win-arm64")
    endif()
elseif(APPLE)
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "arm64|aarch64")
        set(TOMUR_PLATE_RID "osx-arm64")
    else()
        set(TOMUR_PLATE_RID "osx-x64")
    endif()
else()
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "aarch64|arm64")
        set(TOMUR_PLATE_RID "linux-arm64")
    else()
        set(TOMUR_PLATE_RID "linux-x64")
    endif()
endif()

set(TOMUR_PLATE_DESTINATION "runtimes/${TOMUR_PLATE_RID}/native/plate/cpu")

if(NOT TOMUR_HYPERLPR3_INCLUDE_DIR)
    unset(TOMUR_HYPERLPR3_INCLUDE_DIR CACHE)
    if(CMAKE_CROSSCOMPILING)
        find_path(TOMUR_HYPERLPR3_INCLUDE_DIR
            NAMES hyper_lpr_sdk.h
            HINTS
                "${TOMUR_HYPERLPR3_ROOT}/include"
                "${TOMUR_HYPERLPR3_ROOT}/cpp/c_api"
            NO_DEFAULT_PATH)
    else()
        find_path(TOMUR_HYPERLPR3_INCLUDE_DIR
            NAMES hyper_lpr_sdk.h
            HINTS
                "${TOMUR_HYPERLPR3_ROOT}/include"
                "${TOMUR_HYPERLPR3_ROOT}/cpp/c_api")
    endif()
endif()

if(NOT TOMUR_HYPERLPR3_LIBRARY)
    unset(TOMUR_HYPERLPR3_LIBRARY CACHE)
    if(CMAKE_CROSSCOMPILING)
        find_library(TOMUR_HYPERLPR3_LIBRARY
            NAMES hyperlpr3
            HINTS
                "${TOMUR_HYPERLPR3_ROOT}/lib"
                "${TOMUR_HYPERLPR3_ROOT}/lib64"
                "${TOMUR_HYPERLPR3_ROOT}/bin"
            NO_DEFAULT_PATH)
    else()
        find_library(TOMUR_HYPERLPR3_LIBRARY
            NAMES hyperlpr3
            HINTS
                "${TOMUR_HYPERLPR3_ROOT}/lib"
                "${TOMUR_HYPERLPR3_ROOT}/lib64"
                "${TOMUR_HYPERLPR3_ROOT}/bin")
    endif()
endif()

if(NOT TOMUR_HYPERLPR3_INCLUDE_DIR OR NOT EXISTS "${TOMUR_HYPERLPR3_INCLUDE_DIR}/hyper_lpr_sdk.h")
    message(FATAL_ERROR
        "HyperLPR3 header was not found. Set TOMUR_HYPERLPR3_ROOT or TOMUR_HYPERLPR3_INCLUDE_DIR to a source-built HyperLPR3 installation.")
endif()

if(NOT TOMUR_HYPERLPR3_LIBRARY OR NOT EXISTS "${TOMUR_HYPERLPR3_LIBRARY}")
    message(FATAL_ERROR
        "HyperLPR3 link library was not found. Set TOMUR_HYPERLPR3_ROOT or TOMUR_HYPERLPR3_LIBRARY to a target-architecture build.")
endif()

if(UNIX AND TOMUR_HYPERLPR3_LIBRARY MATCHES "\\.a$")
    message(FATAL_ERROR
        "tomur-plate requires a shared HyperLPR3 library because the native bundle manifest probes libhyperlpr3 at runtime.")
endif()

if(CMAKE_CROSSCOMPILING)
    find_package(OpenCV 4.0 QUIET CONFIG COMPONENTS core imgcodecs
        PATHS "${OpenCV_DIR}"
        NO_DEFAULT_PATH)
else()
    find_package(OpenCV 4.0 QUIET COMPONENTS core imgcodecs)
endif()
if(NOT OpenCV_FOUND)
    message(FATAL_ERROR
        "OpenCV 4 with core and imgcodecs was not found. Set OpenCV_DIR to a target-architecture OpenCV CMake package.")
endif()

if(WIN32 AND NOT TOMUR_HYPERLPR3_RUNTIME_LIBRARY)
    unset(TOMUR_HYPERLPR3_RUNTIME_LIBRARY CACHE)
    find_file(TOMUR_HYPERLPR3_RUNTIME_LIBRARY
        NAMES hyperlpr3.dll
        HINTS
            "${TOMUR_HYPERLPR3_ROOT}/bin"
            "${TOMUR_HYPERLPR3_ROOT}/lib")
endif()

if(WIN32 AND (NOT TOMUR_HYPERLPR3_RUNTIME_LIBRARY OR NOT EXISTS "${TOMUR_HYPERLPR3_RUNTIME_LIBRARY}"))
    message(FATAL_ERROR
        "hyperlpr3.dll was not found. Set TOMUR_HYPERLPR3_RUNTIME_LIBRARY so the runtime bundle remains self-contained.")
endif()

add_library(tomur-plate SHARED
    tomur_plate_bridge.cpp
    tomur_plate.h)
target_compile_features(tomur-plate PRIVATE cxx_std_17)
target_compile_definitions(tomur-plate PRIVATE TOMUR_PLATE_BUILD)
target_include_directories(tomur-plate
    PRIVATE
        "${TOMUR_HYPERLPR3_INCLUDE_DIR}"
        ${OpenCV_INCLUDE_DIRS})
target_link_libraries(tomur-plate
    PRIVATE
        "${TOMUR_HYPERLPR3_LIBRARY}"
        ${OpenCV_LIBS})
set_target_properties(tomur-plate PROPERTIES
    OUTPUT_NAME "tomur-plate"
    POSITION_INDEPENDENT_CODE ON
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON)

if(APPLE)
    set_target_properties(tomur-plate PROPERTIES INSTALL_RPATH "@loader_path")
elseif(UNIX)
    set_target_properties(tomur-plate PROPERTIES INSTALL_RPATH "$ORIGIN")
endif()

include(GNUInstallDirs)

if(WIN32)
    install(TARGETS tomur-plate
        RUNTIME DESTINATION "${TOMUR_PLATE_DESTINATION}"
        LIBRARY DESTINATION "${TOMUR_PLATE_DESTINATION}")
    install(FILES "${TOMUR_HYPERLPR3_RUNTIME_LIBRARY}"
        DESTINATION "${TOMUR_PLATE_DESTINATION}")
else()
    install(TARGETS tomur-plate
        LIBRARY DESTINATION "${TOMUR_PLATE_DESTINATION}")
    install(FILES "${TOMUR_HYPERLPR3_LIBRARY}"
        DESTINATION "${TOMUR_PLATE_DESTINATION}")
endif()

if(TOMUR_MNN_RUNTIME_LIBRARY)
    if(NOT EXISTS "${TOMUR_MNN_RUNTIME_LIBRARY}")
        message(FATAL_ERROR "TOMUR_MNN_RUNTIME_LIBRARY does not exist: ${TOMUR_MNN_RUNTIME_LIBRARY}")
    endif()
    install(FILES "${TOMUR_MNN_RUNTIME_LIBRARY}"
        DESTINATION "${TOMUR_PLATE_DESTINATION}")
endif()

foreach(TOMUR_PLATE_DEPENDENCY IN LISTS TOMUR_PLATE_RUNTIME_DEPENDENCIES)
    if(NOT EXISTS "${TOMUR_PLATE_DEPENDENCY}")
        message(FATAL_ERROR "TOMUR_PLATE_RUNTIME_DEPENDENCIES contains a missing file: ${TOMUR_PLATE_DEPENDENCY}")
    endif()
    install(FILES "${TOMUR_PLATE_DEPENDENCY}"
        DESTINATION "${TOMUR_PLATE_DESTINATION}")
endforeach()

install(FILES tomur_plate.h
    DESTINATION "${TOMUR_PLATE_DESTINATION}/include")
