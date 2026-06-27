cmake_minimum_required(VERSION 3.21)
project(tomur_ocr_native VERSION 1.0 LANGUAGES C CXX)

set(OCR_NATIVE_BACKEND "cpu" CACHE STRING "Tomur OCR backend flavor")
string(TOLOWER "${OCR_NATIVE_BACKEND}" OCR_NATIVE_BACKEND)

if(WIN32)
    set(OCR_NATIVE_RID "win-x64")
    if(CMAKE_GENERATOR_PLATFORM STREQUAL "ARM64")
        set(OCR_NATIVE_RID "win-arm64")
    endif()
elseif(APPLE)
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "arm64|aarch64")
        set(OCR_NATIVE_RID "osx-arm64")
    else()
        set(OCR_NATIVE_RID "osx-x64")
    endif()
else()
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "aarch64|arm64")
        set(OCR_NATIVE_RID "linux-arm64")
    else()
        set(OCR_NATIVE_RID "linux-x64")
    endif()
endif()

set(OCR_NATIVE_DESTINATION "runtimes/${OCR_NATIVE_RID}/native/ocr/${OCR_NATIVE_BACKEND}")

set(CMAKE_INSTALL_BINDIR     "${OCR_NATIVE_DESTINATION}"            CACHE PATH "" FORCE)
set(CMAKE_INSTALL_LIBDIR     "${OCR_NATIVE_DESTINATION}"            CACHE PATH "" FORCE)
set(CMAKE_INSTALL_INCLUDEDIR "${OCR_NATIVE_DESTINATION}/include"    CACHE PATH "" FORCE)

if(APPLE)
    set(CMAKE_INSTALL_RPATH "@loader_path;@loader_path/../..")
elseif(UNIX)
    set(CMAKE_INSTALL_RPATH "$ORIGIN;$ORIGIN/../..")
endif()

include(GNUInstallDirs)

set(LLAMA_CPP_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../llama.cpp" CACHE PATH "llama.cpp source directory")
list(PREPEND CMAKE_PREFIX_PATH "${CMAKE_CURRENT_SOURCE_DIR}/..")

find_package(llama CONFIG REQUIRED)
find_package(Threads REQUIRED)

set(MTMD_DIR "${LLAMA_CPP_DIR}/tools/mtmd")
file(GLOB MTMD_MODEL_SOURCES CONFIGURE_DEPENDS "${MTMD_DIR}/models/*.cpp")
set(MTMD_SOURCES
    "${MTMD_DIR}/mtmd.cpp"
    "${MTMD_DIR}/mtmd-audio.cpp"
    "${MTMD_DIR}/mtmd-image.cpp"
    "${MTMD_DIR}/mtmd-helper.cpp"
    "${MTMD_DIR}/clip.cpp"
    ${MTMD_MODEL_SOURCES})

add_library(tomur_mtmd SHARED ${MTMD_SOURCES})
target_compile_features(tomur_mtmd PRIVATE cxx_std_17)
target_compile_definitions(tomur_mtmd PRIVATE LLAMA_BUILD)
target_compile_definitions(tomur_mtmd PUBLIC LLAMA_SHARED)
target_include_directories(tomur_mtmd
    PUBLIC
        "${MTMD_DIR}"
    PRIVATE
        "${LLAMA_CPP_DIR}"
        "${LLAMA_CPP_DIR}/include"
        "${LLAMA_CPP_DIR}/ggml/include"
        "${LLAMA_CPP_DIR}/ggml/src"
        "${LLAMA_CPP_DIR}/vendor")
target_link_libraries(tomur_mtmd PUBLIC llama)
target_link_libraries(tomur_mtmd PRIVATE Threads::Threads)
set_target_properties(tomur_mtmd PROPERTIES POSITION_INDEPENDENT_CODE ON)
set_target_properties(tomur_mtmd PROPERTIES
    OUTPUT_NAME "tomur-mtmd")

if(NOT MSVC)
    target_compile_options(tomur_mtmd PRIVATE -Wno-cast-qual)
endif()

add_library(tomur-ocr SHARED paddleocrvl_bridge.cpp)
target_compile_features(tomur-ocr PRIVATE cxx_std_17)
target_include_directories(tomur-ocr
    PRIVATE
        "${MTMD_DIR}"
        "${LLAMA_CPP_DIR}/include"
        "${LLAMA_CPP_DIR}/ggml/include")
target_link_libraries(tomur-ocr PRIVATE tomur_mtmd llama ${CMAKE_DL_LIBS})
set_target_properties(tomur-ocr PROPERTIES
    OUTPUT_NAME "tomur-ocr"
    POSITION_INDEPENDENT_CODE ON)

if(MSVC)
    target_compile_options(tomur_mtmd PRIVATE /bigobj)
    target_compile_options(tomur-ocr PRIVATE /bigobj)
endif()

if(WIN32)
    install(TARGETS tomur_mtmd tomur-ocr
        RUNTIME DESTINATION ${OCR_NATIVE_DESTINATION}
        LIBRARY DESTINATION ${OCR_NATIVE_DESTINATION})
else()
    install(TARGETS tomur_mtmd tomur-ocr
        LIBRARY DESTINATION ${OCR_NATIVE_DESTINATION})
endif()
