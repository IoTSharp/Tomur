#include "ggml-backend.h"
#include "llama.h"
#include "mtmd.h"
#include "mtmd-helper.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <dlfcn.h>
#include <limits.h>
#include <unistd.h>
#endif

#if defined(_WIN32)
#define TOMUR_VLM_EXPORT extern "C" __declspec(dllexport)
#else
#define TOMUR_VLM_EXPORT extern "C" __attribute__((visibility("default")))
#endif

struct tomur_llama_vlm_image {
    const uint8_t * data;
    size_t size;
    const char * media_type;
    const char * detail;
};

struct tomur_llama_vlm_request {
    const char * model_path;
    const char * mmproj_path;
    const char * prompt_utf8;
    const tomur_llama_vlm_image * images;
    size_t image_count;
    int32_t context_size;
    int32_t batch_size;
    int32_t threads;
    int32_t gpu_layers;
    int32_t max_output_tokens;
    float temperature;
    float top_p;
    int32_t top_k;
    int32_t penalty_last_tokens;
    float repeat_penalty;
    float frequency_penalty;
    float presence_penalty;
    int32_t seed;
    const char ** stop_sequences;
    size_t stop_sequence_count;
    bool use_gpu;
    bool flash_attention;
    bool warmup;
};

struct tomur_llama_vlm_result {
    int32_t status_code;
    char * text_utf8;
    char * diagnostics_json;
    int64_t elapsed_ms;
    char * error_utf8;
};

namespace {

std::once_flag g_backend_once;

int64_t now_ms() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

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

tomur_llama_vlm_result * make_result(
    int32_t status_code,
    const std::string & text,
    const std::vector<std::string> & diagnostics,
    int64_t elapsed_ms,
    const std::string & error) {
    auto * result = static_cast<tomur_llama_vlm_result *>(std::calloc(1, sizeof(tomur_llama_vlm_result)));
    if (result == nullptr) {
        return nullptr;
    }

    result->status_code = status_code;
    result->text_utf8 = duplicate_string(text);
    result->diagnostics_json = duplicate_string(to_json_array(diagnostics));
    result->elapsed_ms = elapsed_ms;
    result->error_utf8 = duplicate_string(error);
    return result;
}

std::string trim(const std::string & value) {
    const auto begin = value.find_first_not_of(" \t\r\n");
    if (begin == std::string::npos) {
        return "";
    }

    const auto end = value.find_last_not_of(" \t\r\n");
    return value.substr(begin, end - begin + 1);
}

size_t count_occurrences(const std::string & value, const std::string & marker) {
    if (marker.empty()) {
        return 0;
    }

    size_t count = 0;
    size_t offset = 0;
    while ((offset = value.find(marker, offset)) != std::string::npos) {
        count++;
        offset += marker.size();
    }

    return count;
}

std::string ensure_media_markers(
    const std::string & prompt,
    size_t image_count,
    std::vector<std::string> & diagnostics) {
    const char * raw_marker = mtmd_default_marker();
    const std::string marker = raw_marker == nullptr ? "<__media__>" : raw_marker;
    const size_t existing = count_occurrences(prompt, marker);
    diagnostics.push_back("vlm-media-marker-count: " + std::to_string(existing));

    if (existing >= image_count) {
        return prompt;
    }

    std::string patched;
    const size_t missing = image_count - existing;
    for (size_t i = 0; i < missing; i++) {
        patched += marker;
        patched += "\n";
    }

    patched += prompt;
    diagnostics.push_back("vlm-media-marker-inserted: " + std::to_string(missing));
    return patched;
}

std::vector<std::string> read_stop_sequences(const tomur_llama_vlm_request * request) {
    std::vector<std::string> stops;
    if (request == nullptr || request->stop_sequences == nullptr) {
        return stops;
    }

    for (size_t i = 0; i < request->stop_sequence_count; i++) {
        const char * value = request->stop_sequences[i];
        if (value != nullptr && value[0] != '\0') {
            stops.emplace_back(value);
        }
    }

    return stops;
}

bool try_find_stop(const std::string & value, const std::vector<std::string> & stops, size_t & stop_index) {
    bool found = false;
    stop_index = std::string::npos;

    for (const auto & stop : stops) {
        if (stop.empty()) {
            continue;
        }

        const size_t index = value.find(stop);
        if (index != std::string::npos && (!found || index < stop_index)) {
            found = true;
            stop_index = index;
        }
    }

    return found;
}

std::string read_token_piece(const llama_vocab * vocab, llama_token token) {
    std::string buffer(256, '\0');
    int32_t written = llama_token_to_piece(vocab, token, buffer.data(), static_cast<int32_t>(buffer.size()), 0, false);
    if (written < 0) {
        buffer.assign(static_cast<size_t>(-written), '\0');
        written = llama_token_to_piece(vocab, token, buffer.data(), static_cast<int32_t>(buffer.size()), 0, false);
    }

    if (written <= 0) {
        return "";
    }

    buffer.resize(static_cast<size_t>(written));
    return buffer;
}

void initialize_backend_once(std::vector<std::string> & diagnostics) {
    std::call_once(g_backend_once, [] {
        llama_backend_init();
    });

#if defined(_WIN32)
    HMODULE module = nullptr;
    if (!GetModuleHandleExA(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCSTR>(&initialize_backend_once),
            &module)) {
        return;
    }

    char path[MAX_PATH];
    DWORD length = GetModuleFileNameA(module, path, MAX_PATH);
    if (length == 0 || length == MAX_PATH) {
        return;
    }

    std::string full_path(path, length);
    const auto slash = full_path.find_last_of("\\/");
    if (slash == std::string::npos) {
        return;
    }

    const std::string native_root = full_path.substr(0, slash);
#else
    Dl_info info {};
    if (dladdr(reinterpret_cast<void *>(&initialize_backend_once), &info) == 0 || info.dli_fname == nullptr) {
        return;
    }

    char resolved[PATH_MAX];
    const char * path = realpath(info.dli_fname, resolved) != nullptr ? resolved : info.dli_fname;
    std::string full_path(path);
    const auto slash = full_path.find_last_of('/');
    if (slash == std::string::npos) {
        return;
    }

    const std::string native_root = full_path.substr(0, slash);
#endif

    if (!native_root.empty()) {
        ggml_backend_load_all_from_path(native_root.c_str());
        diagnostics.push_back("ggml-backends-path: " + native_root);
    }
}

llama_sampler * create_sampler(const tomur_llama_vlm_request * request) {
    llama_sampler_chain_params sampler_params = llama_sampler_chain_default_params();
    llama_sampler * sampler = llama_sampler_chain_init(sampler_params);
    if (sampler == nullptr) {
        return nullptr;
    }

    const int32_t penalty_last_n = request->penalty_last_tokens < -1 ? -1 : request->penalty_last_tokens;
    if (penalty_last_n != 0 &&
        (std::abs(request->repeat_penalty - 1.0f) > 0.0001f ||
         std::abs(request->frequency_penalty) > 0.0001f ||
         std::abs(request->presence_penalty) > 0.0001f)) {
        llama_sampler_chain_add(
            sampler,
            llama_sampler_init_penalties(
                penalty_last_n,
                std::max(0.01f, request->repeat_penalty),
                request->frequency_penalty,
                request->presence_penalty));
    }

    if (request->temperature <= 0.0f) {
        llama_sampler_chain_add(sampler, llama_sampler_init_greedy());
        return sampler;
    }

    llama_sampler_chain_add(sampler, llama_sampler_init_top_k(std::max(1, request->top_k)));
    llama_sampler_chain_add(sampler, llama_sampler_init_top_p(std::clamp(request->top_p, 0.01f, 1.0f), 1));
    llama_sampler_chain_add(sampler, llama_sampler_init_temp(std::max(0.01f, request->temperature)));
    const uint32_t seed = request->seed < 0 ? LLAMA_DEFAULT_SEED : static_cast<uint32_t>(request->seed);
    llama_sampler_chain_add(sampler, llama_sampler_init_dist(seed));
    return sampler;
}

int decode_generated_token(
    llama_context * context,
    llama_batch & batch,
    llama_token token,
    llama_pos position) {
    batch.n_tokens = 1;
    batch.token[0] = token;
    batch.pos[0] = position;
    batch.n_seq_id[0] = 1;
    batch.seq_id[0][0] = 0;
    batch.logits[0] = true;
    return llama_decode(context, batch);
}

std::string generate_text(
    const tomur_llama_vlm_request * request,
    llama_context * context,
    const llama_vocab * vocab,
    llama_pos & n_past,
    std::vector<std::string> & diagnostics,
    int32_t & status_code) {
    llama_sampler * sampler = create_sampler(request);
    if (sampler == nullptr) {
        status_code = 7;
        diagnostics.push_back("sampler-init: failed");
        return "";
    }

    llama_batch batch = llama_batch_init(1, 0, 1);
    const auto stops = read_stop_sequences(request);
    std::string output;

    for (int32_t i = 0; i < std::max(1, request->max_output_tokens); i++) {
        llama_token token = llama_sampler_sample(sampler, context, -1);
        if (llama_vocab_is_eog(vocab, token)) {
            diagnostics.push_back("generation-stop: eog");
            break;
        }

        llama_sampler_accept(sampler, token);
        output += read_token_piece(vocab, token);

        size_t stop_index = std::string::npos;
        if (try_find_stop(output, stops, stop_index)) {
            output.resize(stop_index);
            diagnostics.push_back("generation-stop: stop-sequence");
            break;
        }

        if (decode_generated_token(context, batch, token, n_past++) != 0) {
            status_code = 7;
            diagnostics.push_back("generation-decode: failed");
            break;
        }
    }

    llama_batch_free(batch);
    llama_sampler_free(sampler);
    return trim(output);
}

} // namespace

TOMUR_VLM_EXPORT const char * tomur_llama_vlm_bridge_version(void) {
    return "tomur-llama-vlm/mtmd/1";
}

TOMUR_VLM_EXPORT tomur_llama_vlm_result * tomur_llama_vlm_generate(
    const tomur_llama_vlm_request * request) {
    const int64_t started = now_ms();
    std::vector<std::string> diagnostics {
        "vlm-runtime: mtmd-enabled",
        "vlm-native-bridge: tomur-llama-vlm",
        "mtmd-api: mtmd_init_from_file|mtmd_helper_bitmap_init_from_buf|mtmd_tokenize|mtmd_helper_eval_chunks"
    };

    try {
        if (request == nullptr) {
            return make_result(1, "", diagnostics, now_ms() - started, "request is required");
        }

        if (request->model_path == nullptr || request->model_path[0] == '\0') {
            return make_result(1, "", diagnostics, now_ms() - started, "model_path is required");
        }

        if (request->mmproj_path == nullptr || request->mmproj_path[0] == '\0') {
            return make_result(1, "", diagnostics, now_ms() - started, "mmproj_path is required");
        }

        if (request->prompt_utf8 == nullptr || request->prompt_utf8[0] == '\0') {
            return make_result(1, "", diagnostics, now_ms() - started, "prompt is required");
        }

        if (request->images == nullptr || request->image_count == 0) {
            return make_result(1, "", diagnostics, now_ms() - started, "at least one image is required");
        }

        diagnostics.push_back("model-path: " + std::string(request->model_path));
        diagnostics.push_back("mmproj-path: " + std::string(request->mmproj_path));
        diagnostics.push_back("image-count: " + std::to_string(request->image_count));

        initialize_backend_once(diagnostics);

        llama_model_params model_params = llama_model_default_params();
        model_params.n_gpu_layers = std::max(0, request->gpu_layers);

        llama_model * model = llama_model_load_from_file(request->model_path, model_params);
        if (model == nullptr) {
            diagnostics.push_back("model-load: failed");
            return make_result(2, "", diagnostics, now_ms() - started, "failed to load llama model");
        }

        const int32_t effective_context = std::max(1024, request->context_size);
        const int32_t effective_batch = std::max(32, request->batch_size);
        const int32_t effective_threads = request->threads > 0
            ? request->threads
            : std::max(1, static_cast<int32_t>(std::thread::hardware_concurrency()));

        llama_context_params context_params = llama_context_default_params();
        context_params.n_ctx = static_cast<uint32_t>(effective_context);
        context_params.n_batch = static_cast<uint32_t>(effective_batch);
        context_params.n_ubatch = static_cast<uint32_t>(std::max(32, std::min(effective_batch, 1024)));
        context_params.n_seq_max = 1;
        context_params.n_threads = effective_threads;
        context_params.n_threads_batch = effective_threads;
        context_params.flash_attn_type = request->flash_attention ? LLAMA_FLASH_ATTN_TYPE_ENABLED : LLAMA_FLASH_ATTN_TYPE_AUTO;
        context_params.offload_kqv = request->gpu_layers > 0;
        context_params.op_offload = request->gpu_layers > 0;

        llama_context * context = llama_init_from_model(model, context_params);
        if (context == nullptr) {
            llama_model_free(model);
            diagnostics.push_back("context-init: failed");
            return make_result(2, "", diagnostics, now_ms() - started, "failed to create llama context");
        }

        mtmd_context_params mtmd_params = mtmd_context_params_default();
        mtmd_params.use_gpu = request->use_gpu;
        mtmd_params.print_timings = false;
        mtmd_params.n_threads = effective_threads;
        mtmd_params.flash_attn_type = request->flash_attention ? LLAMA_FLASH_ATTN_TYPE_ENABLED : LLAMA_FLASH_ATTN_TYPE_AUTO;
        mtmd_params.warmup = request->warmup;

        mtmd_context * mtmd_context = mtmd_init_from_file(request->mmproj_path, model, mtmd_params);
        if (mtmd_context == nullptr) {
            llama_free(context);
            llama_model_free(model);
            diagnostics.push_back("mmproj-load: failed");
            return make_result(3, "", diagnostics, now_ms() - started, "failed to load mtmd mmproj");
        }

        std::vector<mtmd_bitmap *> owned_bitmaps;
        std::vector<const mtmd_bitmap *> bitmap_refs;
        owned_bitmaps.reserve(request->image_count);
        bitmap_refs.reserve(request->image_count);

        for (size_t i = 0; i < request->image_count; i++) {
            const auto & image = request->images[i];
            if (image.data == nullptr || image.size == 0) {
                for (auto * bitmap : owned_bitmaps) {
                    mtmd_bitmap_free(bitmap);
                }
                mtmd_free(mtmd_context);
                llama_free(context);
                llama_model_free(model);
                diagnostics.push_back("image-decode: empty");
                return make_result(4, "", diagnostics, now_ms() - started, "image data is required");
            }

            mtmd_helper_bitmap_wrapper bitmap_wrapper = mtmd_helper_bitmap_init_from_buf(
                mtmd_context,
                image.data,
                image.size,
                false);
            mtmd_bitmap * bitmap = bitmap_wrapper.bitmap;
            if (bitmap == nullptr) {
                for (auto * item : owned_bitmaps) {
                    mtmd_bitmap_free(item);
                }
                mtmd_free(mtmd_context);
                llama_free(context);
                llama_model_free(model);
                diagnostics.push_back("image-decode: failed:" + std::to_string(i));
                return make_result(4, "", diagnostics, now_ms() - started, "failed to decode image bytes");
            }

            owned_bitmaps.push_back(bitmap);
            bitmap_refs.push_back(bitmap);
            diagnostics.push_back("image-decode: ok:" + std::to_string(i));
        }

        std::string prompt = ensure_media_markers(request->prompt_utf8, request->image_count, diagnostics);
        mtmd_input_text input_text {
            prompt.c_str(),
            true,
            true
        };

        mtmd_input_chunks * chunks = mtmd_input_chunks_init();
        if (chunks == nullptr) {
            for (auto * bitmap : owned_bitmaps) {
                mtmd_bitmap_free(bitmap);
            }
            mtmd_free(mtmd_context);
            llama_free(context);
            llama_model_free(model);
            diagnostics.push_back("input-chunks-init: failed");
            return make_result(5, "", diagnostics, now_ms() - started, "failed to allocate VLM input chunks");
        }

        int32_t tokenize_result = mtmd_tokenize(
            mtmd_context,
            chunks,
            &input_text,
            bitmap_refs.data(),
            bitmap_refs.size());
        if (tokenize_result != 0) {
            mtmd_input_chunks_free(chunks);
            for (auto * bitmap : owned_bitmaps) {
                mtmd_bitmap_free(bitmap);
            }
            mtmd_free(mtmd_context);
            llama_free(context);
            llama_model_free(model);
            diagnostics.push_back("tokenize-result: " + std::to_string(tokenize_result));
            return make_result(5, "", diagnostics, now_ms() - started, "failed to tokenize VLM prompt and image");
        }

        llama_pos n_past = 0;
        int32_t eval_result = mtmd_helper_eval_chunks(
            mtmd_context,
            context,
            chunks,
            n_past,
            0,
            effective_batch,
            true,
            &n_past);
        if (eval_result != 0) {
            mtmd_input_chunks_free(chunks);
            for (auto * bitmap : owned_bitmaps) {
                mtmd_bitmap_free(bitmap);
            }
            mtmd_free(mtmd_context);
            llama_free(context);
            llama_model_free(model);
            diagnostics.push_back("eval-result: " + std::to_string(eval_result));
            return make_result(6, "", diagnostics, now_ms() - started, "failed to evaluate VLM prompt and image");
        }

        int32_t generation_status = 0;
        std::string text = generate_text(
            request,
            context,
            llama_model_get_vocab(model),
            n_past,
            diagnostics,
            generation_status);

        mtmd_input_chunks_free(chunks);
        for (auto * bitmap : owned_bitmaps) {
            mtmd_bitmap_free(bitmap);
        }
        mtmd_free(mtmd_context);
        llama_free(context);
        llama_model_free(model);

        if (generation_status != 0) {
            return make_result(generation_status, text, diagnostics, now_ms() - started, "generation failed");
        }

        if (text.empty()) {
            return make_result(8, "", diagnostics, now_ms() - started, "VLM returned no text");
        }

        diagnostics.push_back("generation: succeeded");
        return make_result(0, text, diagnostics, now_ms() - started, "");
    } catch (const std::exception & exception) {
        diagnostics.push_back("exception: " + std::string(exception.what()));
        return make_result(99, "", diagnostics, now_ms() - started, exception.what());
    } catch (...) {
        diagnostics.push_back("exception: unknown");
        return make_result(99, "", diagnostics, now_ms() - started, "unknown native VLM exception");
    }
}

TOMUR_VLM_EXPORT void tomur_llama_vlm_result_free(tomur_llama_vlm_result * result) {
    if (result == nullptr) {
        return;
    }

    std::free(result->text_utf8);
    std::free(result->diagnostics_json);
    std::free(result->error_utf8);
    std::free(result);
}
