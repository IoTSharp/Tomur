#include "llama.h"

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <sstream>
#include <string>
#include <vector>

#if defined(_WIN32)
#define TOMUR_TTS_EXPORT extern "C" __declspec(dllexport)
#else
#define TOMUR_TTS_EXPORT extern "C" __attribute__((visibility("default")))
#endif

struct tomur_tts_request {
    const char * text_utf8;
    const char * acoustic_model_path;
    const char * voice_model_path;
    const char * speaker_prompt_utf8;
    int32_t sample_rate;
    int32_t threads;
    int32_t gpu_layers;
};

struct tomur_tts_result {
    int32_t status_code;
    const int16_t * pcm;
    size_t pcm_length;
    int32_t sample_rate;
    char * diagnostics_json;
    char * error_utf8;
};

namespace {

char * duplicate_string(const std::string & value) {
    char * copy = static_cast<char *>(std::malloc(value.size() + 1));
    if (copy == nullptr) {
        return nullptr;
    }

    std::memcpy(copy, value.c_str(), value.size() + 1);
    return copy;
}

std::string json_escape(const std::string & value) {
    std::ostringstream output;
    for (unsigned char ch : value) {
        switch (ch) {
            case '\\': output << "\\\\"; break;
            case '"': output << "\\\""; break;
            case '\b': output << "\\b"; break;
            case '\f': output << "\\f"; break;
            case '\n': output << "\\n"; break;
            case '\r': output << "\\r"; break;
            case '\t': output << "\\t"; break;
            default:
                if (ch < 0x20) {
                    constexpr char hex[] = "0123456789abcdef";
                    output << "\\u00" << hex[(ch >> 4) & 0xF] << hex[ch & 0xF];
                } else {
                    output << static_cast<char>(ch);
                }
                break;
        }
    }

    return output.str();
}

std::string to_json_array(const std::vector<std::string> & values) {
    std::ostringstream output;
    output << "[";
    for (size_t index = 0; index < values.size(); index++) {
        if (index > 0) {
            output << ",";
        }

        output << "\"" << json_escape(values[index]) << "\"";
    }

    output << "]";
    return output.str();
}

tomur_tts_result * make_result(
    int32_t status_code,
    int32_t sample_rate,
    const std::vector<std::string> & diagnostics,
    const std::string & error) {
    auto * result = static_cast<tomur_tts_result *>(std::calloc(1, sizeof(tomur_tts_result)));
    if (result == nullptr) {
        return nullptr;
    }

    result->status_code = status_code;
    result->pcm = nullptr;
    result->pcm_length = 0;
    result->sample_rate = sample_rate;
    result->diagnostics_json = duplicate_string(to_json_array(diagnostics));
    result->error_utf8 = duplicate_string(error);
    return result;
}

} // namespace

TOMUR_TTS_EXPORT const char * tomur_tts_bridge_version(void) {
    return "tomur-tts/llama.cpp-tools-tts/1";
}

TOMUR_TTS_EXPORT const char * tomur_tts_runtime_info(void) {
    return llama_print_system_info();
}

TOMUR_TTS_EXPORT tomur_tts_result * tomur_tts_synthesize_to_pcm(
    const tomur_tts_request * request) {
    std::vector<std::string> diagnostics {
        "tts-runtime: llama.cpp-tools-tts",
        "tts-native-bridge: tomur-tts",
        "tts-status: abi-ready"
    };

    if (request == nullptr) {
        return make_result(1, 0, diagnostics, "request is required");
    }

    if (request->text_utf8 == nullptr || request->text_utf8[0] == '\0') {
        return make_result(1, request->sample_rate, diagnostics, "text_utf8 is required");
    }

    if (request->acoustic_model_path == nullptr || request->acoustic_model_path[0] == '\0') {
        return make_result(1, request->sample_rate, diagnostics, "acoustic_model_path is required");
    }

    if (request->voice_model_path == nullptr || request->voice_model_path[0] == '\0') {
        return make_result(1, request->sample_rate, diagnostics, "voice_model_path is required");
    }

    diagnostics.push_back("llama-supports-mmap: " + std::string(llama_supports_mmap() ? "true" : "false"));
    diagnostics.push_back("llama-supports-gpu-offload: " + std::string(llama_supports_gpu_offload() ? "true" : "false"));
    diagnostics.push_back("tts-synthesis: pending-llama-tools-tts-adapter");

    return make_result(
        2,
        request->sample_rate > 0 ? request->sample_rate : 24000,
        diagnostics,
        "llama.cpp GGUF TTS ABI is present, but synthesis is reserved for the R8 TTS adapter.");
}

TOMUR_TTS_EXPORT void tomur_tts_result_free(tomur_tts_result * result) {
    if (result == nullptr) {
        return;
    }

    std::free(const_cast<int16_t *>(result->pcm));
    std::free(result->diagnostics_json);
    std::free(result->error_utf8);
    std::free(result);
}
