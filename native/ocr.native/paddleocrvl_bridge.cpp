#include "ggml-backend.h"
#include "llama.h"
#include "mtmd.h"
#include "mtmd-helper.h"

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string>
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
#define TOMUR_OCR_EXPORT extern "C" __declspec(dllexport)
#else
#define TOMUR_OCR_EXPORT extern "C" __attribute__((visibility("default")))
#endif

struct tomur_paddleocrvl_result {
    int status_code;
    char * text;
    double confidence;
    char * diagnostics_json;
    int64_t elapsed_ms;
    char * error;
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
                    output << "\\u";
                    constexpr char hex[] = "0123456789abcdef";
                    output << "00" << hex[(ch >> 4) & 0xF] << hex[ch & 0xF];
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

tomur_paddleocrvl_result * make_result(
    int status_code,
    const std::string & text,
    double confidence,
    const std::vector<std::string> & diagnostics,
    int64_t elapsed_ms,
    const std::string & error) {
    auto * result = static_cast<tomur_paddleocrvl_result *>(std::calloc(1, sizeof(tomur_paddleocrvl_result)));
    if (result == nullptr) {
        return nullptr;
    }

    result->status_code = status_code;
    result->text = duplicate_string(text);
    result->confidence = confidence;
    result->diagnostics_json = duplicate_string(to_json_array(diagnostics));
    result->elapsed_ms = elapsed_ms;
    result->error = duplicate_string(error);
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

std::string apply_chat_template(const llama_model * model, const std::string & user_prompt) {
    const char * tmpl = llama_model_chat_template(model, nullptr);
    if (tmpl == nullptr || tmpl[0] == '\0') {
        return user_prompt;
    }

    llama_chat_message message {
        "user",
        user_prompt.c_str()
    };

    int32_t required = llama_chat_apply_template(tmpl, &message, 1, true, nullptr, 0);
    if (required <= 0) {
        return user_prompt;
    }

    std::string formatted(static_cast<size_t>(required), '\0');
    int32_t written = llama_chat_apply_template(tmpl, &message, 1, true, formatted.data(), required);
    if (written > required) {
        formatted.assign(static_cast<size_t>(written), '\0');
        written = llama_chat_apply_template(tmpl, &message, 1, true, formatted.data(), written);
    }

    if (written <= 0) {
        return user_prompt;
    }

    formatted.resize(static_cast<size_t>(written));
    return formatted;
}

std::string build_user_prompt(const char * prompt, const char * language) {
    std::string content = prompt != nullptr && prompt[0] != '\0'
        ? prompt
        : "Extract all text from this document image. Preserve tables as Markdown. Return only recognized content.";

    if (language != nullptr && language[0] != '\0') {
        std::string lang = language;
        if (lang != "auto") {
            content += "\nLanguage hint: ";
            content += lang;
            content += ".";
        }
    }

    const char * marker = mtmd_default_marker();
    if (content.find(marker) == std::string::npos) {
        content = std::string(marker) + "\n" + content;
    }

    return content;
}

#if defined(_WIN32)
std::string current_library_directory() {
    HMODULE module = nullptr;
    if (!GetModuleHandleExA(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCSTR>(&current_library_directory),
            &module)) {
        return "";
    }

    char path[MAX_PATH];
    DWORD length = GetModuleFileNameA(module, path, MAX_PATH);
    if (length == 0 || length == MAX_PATH) {
        return "";
    }

    std::string full_path(path, length);
    const auto slash = full_path.find_last_of("\\/");
    return slash == std::string::npos ? "" : full_path.substr(0, slash);
}
#else
std::string current_library_directory() {
    Dl_info info {};
    if (dladdr(reinterpret_cast<void *>(&current_library_directory), &info) == 0 || info.dli_fname == nullptr) {
        return "";
    }

    char resolved[PATH_MAX];
    const char * path = realpath(info.dli_fname, resolved) != nullptr ? resolved : info.dli_fname;
    std::string full_path(path);
    const auto slash = full_path.find_last_of('/');
    return slash == std::string::npos ? "" : full_path.substr(0, slash);
}
#endif

std::string parent_directory(const std::string & path) {
    const auto slash = path.find_last_of("\\/");
    return slash == std::string::npos ? "" : path.substr(0, slash);
}

void initialize_backend_once(std::vector<std::string> & diagnostics) {
    std::call_once(g_backend_once, [] {
        llama_backend_init();
    });

    std::string backend_dir = current_library_directory();
    if (!backend_dir.empty()) {
        std::string native_root = parent_directory(parent_directory(backend_dir));
        if (!native_root.empty()) {
            ggml_backend_load_all_from_path(native_root.c_str());
            diagnostics.push_back("ggml-backends-path: " + native_root);
        }
    }
}

llama_sampler * create_sampler(float temperature, float top_p, int seed) {
    llama_sampler_chain_params sampler_params = llama_sampler_chain_default_params();
    llama_sampler * sampler = llama_sampler_chain_init(sampler_params);
    if (sampler == nullptr) {
        return nullptr;
    }

    if (temperature <= 0.0f) {
        llama_sampler_chain_add(sampler, llama_sampler_init_greedy());
        return sampler;
    }

    llama_sampler_chain_add(sampler, llama_sampler_init_top_k(40));
    llama_sampler_chain_add(sampler, llama_sampler_init_top_p(top_p <= 0.0f ? 0.9f : top_p, 1));
    llama_sampler_chain_add(sampler, llama_sampler_init_temp(temperature));
    llama_sampler_chain_add(sampler, llama_sampler_init_dist(seed < 0 ? LLAMA_DEFAULT_SEED : static_cast<uint32_t>(seed)));
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
    llama_context * context,
    const llama_vocab * vocab,
    int max_output_tokens,
    float temperature,
    float top_p,
    int seed,
    llama_pos & n_past,
    std::vector<std::string> & diagnostics,
    int & status_code) {
    llama_sampler * sampler = create_sampler(temperature, top_p, seed);
    if (sampler == nullptr) {
        status_code = 7;
        return "";
    }

    llama_batch batch = llama_batch_init(1, 0, 1);
    std::string output;

    for (int i = 0; i < max_output_tokens; i++) {
        llama_token token = llama_sampler_sample(sampler, context, -1);
        if (llama_vocab_is_eog(vocab, token)) {
            break;
        }

        llama_sampler_accept(sampler, token);
        output += read_token_piece(vocab, token);

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

TOMUR_OCR_EXPORT tomur_paddleocrvl_result * tomur_paddleocrvl_recognize_image(
    const char * model_path,
    const char * mmproj_path,
    const unsigned char * image_data,
    size_t image_length,
    const char * prompt,
    const char * language,
    int context_size,
    int batch_size,
    int threads,
    int gpu_layers,
    int max_output_tokens,
    float temperature,
    float top_p,
    int seed,
    bool use_gpu,
    bool flash_attention,
    bool warmup) {
    const int64_t started = now_ms();
    std::vector<std::string> diagnostics;

    try {
        if (model_path == nullptr || model_path[0] == '\0') {
            return make_result(1, "", 0.0, diagnostics, now_ms() - started, "model_path is required");
        }

        if (mmproj_path == nullptr || mmproj_path[0] == '\0') {
            return make_result(1, "", 0.0, diagnostics, now_ms() - started, "mmproj_path is required");
        }

        if (image_data == nullptr || image_length == 0) {
            return make_result(1, "", 0.0, diagnostics, now_ms() - started, "image_data is required");
        }

        initialize_backend_once(diagnostics);

        llama_model_params model_params = llama_model_default_params();
        model_params.n_gpu_layers = gpu_layers;
        llama_model * model = llama_model_load_from_file(model_path, model_params);
        if (model == nullptr) {
            diagnostics.push_back("model-load: failed");
            return make_result(2, "", 0.0, diagnostics, now_ms() - started, "failed to load PaddleOCR-VL model");
        }

        llama_context_params context_params = llama_context_default_params();
        context_params.n_ctx = static_cast<uint32_t>(std::max(1024, context_size));
        context_params.n_batch = static_cast<uint32_t>(std::max(32, batch_size));
        context_params.n_ubatch = static_cast<uint32_t>(std::max(32, std::min(batch_size, 1024)));
        context_params.n_seq_max = 1;
        context_params.n_threads = std::max(1, threads);
        context_params.n_threads_batch = std::max(1, threads);
        context_params.flash_attn_type = flash_attention ? LLAMA_FLASH_ATTN_TYPE_ENABLED : LLAMA_FLASH_ATTN_TYPE_AUTO;

        llama_context * context = llama_init_from_model(model, context_params);
        if (context == nullptr) {
            llama_model_free(model);
            diagnostics.push_back("context-init: failed");
            return make_result(2, "", 0.0, diagnostics, now_ms() - started, "failed to create llama context");
        }

        mtmd_context_params mtmd_params = mtmd_context_params_default();
        mtmd_params.use_gpu = use_gpu;
        mtmd_params.print_timings = false;
        mtmd_params.n_threads = std::max(1, threads);
        mtmd_params.flash_attn_type = flash_attention ? LLAMA_FLASH_ATTN_TYPE_ENABLED : LLAMA_FLASH_ATTN_TYPE_AUTO;
        mtmd_params.warmup = warmup;

        mtmd_context * mtmd_context = mtmd_init_from_file(mmproj_path, model, mtmd_params);
        if (mtmd_context == nullptr) {
            llama_free(context);
            llama_model_free(model);
            diagnostics.push_back("mmproj-load: failed");
            return make_result(3, "", 0.0, diagnostics, now_ms() - started, "failed to load PaddleOCR-VL mmproj");
        }

        mtmd_helper_bitmap_wrapper bitmap_wrapper = mtmd_helper_bitmap_init_from_buf(
            mtmd_context,
            image_data,
            image_length,
            false);
        mtmd_bitmap * bitmap = bitmap_wrapper.bitmap;
        if (bitmap == nullptr) {
            mtmd_free(mtmd_context);
            llama_free(context);
            llama_model_free(model);
            diagnostics.push_back("image-decode: failed");
            return make_result(4, "", 0.0, diagnostics, now_ms() - started, "failed to decode image bytes");
        }

        std::string user_prompt = build_user_prompt(prompt, language);
        std::string formatted_prompt = apply_chat_template(model, user_prompt);
        mtmd_input_text input_text {
            formatted_prompt.c_str(),
            true,
            true
        };
        const mtmd_bitmap * bitmaps[] = { bitmap };
        mtmd_input_chunks * chunks = mtmd_input_chunks_init();
        int32_t tokenize_result = mtmd_tokenize(mtmd_context, chunks, &input_text, bitmaps, 1);
        if (tokenize_result != 0) {
            mtmd_input_chunks_free(chunks);
            mtmd_bitmap_free(bitmap);
            mtmd_free(mtmd_context);
            llama_free(context);
            llama_model_free(model);
            diagnostics.push_back("tokenize-result: " + std::to_string(tokenize_result));
            return make_result(5, "", 0.0, diagnostics, now_ms() - started, "failed to tokenize OCR prompt and image");
        }

        llama_pos n_past = 0;
        int32_t eval_result = mtmd_helper_eval_chunks(
            mtmd_context,
            context,
            chunks,
            n_past,
            0,
            std::max(32, batch_size),
            true,
            &n_past);
        if (eval_result != 0) {
            mtmd_input_chunks_free(chunks);
            mtmd_bitmap_free(bitmap);
            mtmd_free(mtmd_context);
            llama_free(context);
            llama_model_free(model);
            diagnostics.push_back("eval-result: " + std::to_string(eval_result));
            return make_result(6, "", 0.0, diagnostics, now_ms() - started, "failed to evaluate OCR prompt and image");
        }

        int generation_status = 0;
        std::string text = generate_text(
            context,
            llama_model_get_vocab(model),
            std::max(16, max_output_tokens),
            temperature,
            top_p,
            seed,
            n_past,
            diagnostics,
            generation_status);

        mtmd_input_chunks_free(chunks);
        mtmd_bitmap_free(bitmap);
        mtmd_free(mtmd_context);
        llama_free(context);
        llama_model_free(model);

        diagnostics.push_back("confidence: not-provided-by-generative-ocr");
        if (generation_status != 0) {
            return make_result(generation_status, text, 0.0, diagnostics, now_ms() - started, "generation failed");
        }

        if (text.empty()) {
            return make_result(8, "", 0.0, diagnostics, now_ms() - started, "PaddleOCR-VL returned no text");
        }

        return make_result(0, text, 0.0, diagnostics, now_ms() - started, "");
    } catch (const std::exception & exception) {
        diagnostics.push_back("exception: " + std::string(exception.what()));
        return make_result(99, "", 0.0, diagnostics, now_ms() - started, exception.what());
    } catch (...) {
        diagnostics.push_back("exception: unknown");
        return make_result(99, "", 0.0, diagnostics, now_ms() - started, "unknown native OCR exception");
    }
}

TOMUR_OCR_EXPORT void tomur_paddleocrvl_result_free(tomur_paddleocrvl_result * result) {
    if (result == nullptr) {
        return;
    }

    std::free(result->text);
    std::free(result->diagnostics_json);
    std::free(result->error);
    std::free(result);
}
