cmake_minimum_required(VERSION 3.21)
project(tomur_tts_native VERSION 1.0 LANGUAGES C CXX)

set(TTS_NATIVE_BACKEND "cpu" CACHE STRING "Tomur TTS backend flavor")
string(TOLOWER "${TTS_NATIVE_BACKEND}" TTS_NATIVE_BACKEND)

if(WIN32)
    set(TTS_NATIVE_RID "win-x64")
    if(CMAKE_GENERATOR_PLATFORM STREQUAL "ARM64")
        set(TTS_NATIVE_RID "win-arm64")
    endif()
elseif(APPLE)
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "arm64|aarch64")
        set(TTS_NATIVE_RID "osx-arm64")
    else()
        set(TTS_NATIVE_RID "osx-x64")
    endif()
else()
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "aarch64|arm64")
        set(TTS_NATIVE_RID "linux-arm64")
    else()
        set(TTS_NATIVE_RID "linux-x64")
    endif()
endif()

set(TTS_NATIVE_DESTINATION "runtimes/${TTS_NATIVE_RID}/native/tts/${TTS_NATIVE_BACKEND}")

set(CMAKE_INSTALL_BINDIR     "${TTS_NATIVE_DESTINATION}"            CACHE PATH "" FORCE)
set(CMAKE_INSTALL_LIBDIR     "${TTS_NATIVE_DESTINATION}"            CACHE PATH "" FORCE)
set(CMAKE_INSTALL_INCLUDEDIR "${TTS_NATIVE_DESTINATION}/include"    CACHE PATH "" FORCE)

if(APPLE)
    set(CMAKE_INSTALL_RPATH "@loader_path;@loader_path/../..")
elseif(UNIX)
    set(CMAKE_INSTALL_RPATH "$ORIGIN;$ORIGIN/../..")
endif()

include(GNUInstallDirs)

list(PREPEND CMAKE_PREFIX_PATH "${CMAKE_CURRENT_SOURCE_DIR}/..")

find_package(llama CONFIG REQUIRED)
find_package(Threads REQUIRED)

add_library(tomur-tts SHARED tts_bridge.cpp)
target_compile_features(tomur-tts PRIVATE cxx_std_17)
target_include_directories(tomur-tts PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/../llama.cpp/include"
    "${CMAKE_CURRENT_SOURCE_DIR}/../llama.cpp/vendor")
target_link_libraries(tomur-tts PRIVATE llama Threads::Threads)
set_target_properties(tomur-tts PROPERTIES
    OUTPUT_NAME "tomur-tts"
    POSITION_INDEPENDENT_CODE ON)

if(MSVC)
    target_compile_options(tomur-tts PRIVATE /bigobj)
endif()

if(WIN32)
    install(TARGETS tomur-tts
        RUNTIME DESTINATION ${TTS_NATIVE_DESTINATION}
        LIBRARY DESTINATION ${TTS_NATIVE_DESTINATION})
else()
    install(TARGETS tomur-tts
        LIBRARY DESTINATION ${TTS_NATIVE_DESTINATION})
endif()
