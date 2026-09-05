#include "whisper.h"
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstring>
#include <cstdio>
#include <filesystem>
#include <memory>
#include <stdexcept>
#include <string>

#if defined(_WIN32)
#define RT_EXPORT extern "C" __declspec(dllexport)
#else
#define RT_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {
struct speech_session {
    whisper_context * asr = nullptr;
    whisper_vad_context * vad = nullptr;
    std::atomic<bool> cancelled{false};
    std::chrono::steady_clock::time_point deadline;
    ~speech_session() {
        if (asr) whisper_free(asr);
        if (vad) whisper_vad_free(vad);
    }
};
bool abort_asr(void * opaque) {
    auto * session = static_cast<speech_session *>(opaque);
    return session->cancelled.load() || std::chrono::steady_clock::now() >= session->deadline;
}

struct bounded_reader {
    std::unique_ptr<FILE, decltype(&std::fclose)> file{nullptr, std::fclose};
    std::chrono::steady_clock::time_point deadline;
    int (*cancelled)(void *);
    void * user;
    bool stopped() const { return std::chrono::steady_clock::now() >= deadline || cancelled(user) != 0; }
    bounded_reader(const char * path, int (*cancelled)(void *), void * user)
        : deadline(std::chrono::steady_clock::now() + std::chrono::seconds(60)), cancelled(cancelled), user(user) {
#if defined(_WIN32)
        FILE * opened = nullptr;
        _wfopen_s(&opened, std::filesystem::u8path(path).c_str(), L"rb");
        file.reset(opened);
#else
        file.reset(std::fopen(path, "rb"));
#endif
    }
    whisper_model_loader loader() {
        return {this,
            [](void * opaque, void * output, size_t size) -> size_t {
                auto * reader = static_cast<bounded_reader *>(opaque);
                constexpr size_t chunk_size = 1024 * 1024;
                constexpr size_t max_read = 512 * chunk_size;
                if (size > max_read) throw std::runtime_error("Realtime model read exceeds its size budget");
                if (reader->stopped()) throw std::runtime_error("Realtime model load cancelled or timed out");
                size_t total = 0;
                for (size_t chunk = 0; chunk < 512 && total < size; ++chunk) {
                    if (reader->stopped()) throw std::runtime_error("Realtime model load cancelled or timed out");
                    const size_t requested = std::min(chunk_size, size - total);
                    const size_t received = std::fread(static_cast<char *>(output) + total, 1, requested, reader->file.get());
                    total += received;
                    if (received != requested) {
                        // Whisper probes three scalar fields before consulting EOF.
                        if (total == 0 && size == sizeof(int32_t) && std::feof(reader->file.get())) {
                            std::memset(output, 0, size);
                            return 0;
                        }
                        throw std::runtime_error("Realtime model file is truncated or unreadable");
                    }
                }
                return total;
            },
            [](void * opaque) -> bool {
                auto * reader = static_cast<bounded_reader *>(opaque);
                return reader->stopped() || std::feof(reader->file.get());
            },
            [](void *) {}};
    }
};
}

RT_EXPORT int tomur_realtime_speech_abi() { return 1; }

RT_EXPORT void * tomur_realtime_speech_create(const char * model, const char * vad, int threads,
    int (*cancelled)(void *), void * user) {
    if (!model || !vad || !cancelled || threads < 1 || threads > 32) return nullptr;
    try {
        auto session = std::make_unique<speech_session>();
        auto params = whisper_context_default_params();
        params.use_gpu = false;
        bounded_reader model_reader(model, cancelled, user);
        if (!model_reader.file || model_reader.stopped()) return nullptr;
        auto model_loader = model_reader.loader();
        session->asr = whisper_init_with_params(&model_loader, params);
        if (!session->asr || model_reader.stopped()) return nullptr;
        auto vad_params = whisper_vad_default_context_params();
        vad_params.n_threads = threads;
        vad_params.use_gpu = false;
        bounded_reader vad_reader(vad, cancelled, user);
        if (!vad_reader.file || vad_reader.stopped()) return nullptr;
        auto vad_loader = vad_reader.loader();
        session->vad = whisper_vad_init_with_params(&vad_loader, vad_params);
        if (!session->vad || vad_reader.stopped()) return nullptr;
        return session.release();
    } catch (...) { return nullptr; }
}

// Exactly 512 samples: Silero's recurrent window must not be padded on each
// 320-sample transport packet. The host keeps the remainder across packets.
RT_EXPORT float tomur_realtime_vad_process(void * opaque, const float * samples, int count) {
    auto * session = static_cast<speech_session *>(opaque);
    if (!session || !samples || count != 512) return -1;
    try {
        if (!whisper_vad_detect_speech_no_reset(session->vad, samples, count)) return -1;
        auto * probs = whisper_vad_probs(session->vad);
        return probs && whisper_vad_n_probs(session->vad) > 0 ? probs[0] : -1;
    } catch (...) { return -1; }
}

RT_EXPORT void tomur_realtime_vad_reset(void * opaque) {
    if (opaque) whisper_vad_reset_state(static_cast<speech_session *>(opaque)->vad);
}

RT_EXPORT void tomur_realtime_speech_reset(void * opaque) {
    if (opaque) static_cast<speech_session *>(opaque)->cancelled.store(false);
}

RT_EXPORT void tomur_realtime_speech_cancel(void * opaque) {
    if (opaque) static_cast<speech_session *>(opaque)->cancelled.store(true);
}

RT_EXPORT int tomur_realtime_transcribe(void * opaque, const float * samples, int count,
    const char * language, int threads, char * output, int capacity) {
    auto * session = static_cast<speech_session *>(opaque);
    if (!session || !samples || count < 1600 || count > 480000 || !output || capacity < 2) return -1;
    try {
        session->deadline = std::chrono::steady_clock::now() + std::chrono::seconds(30);
        auto params = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
        params.n_threads = std::clamp(threads, 1, 32);
        params.print_progress = false;
        params.print_realtime = false;
        params.print_timestamps = false;
        params.print_special = false;
        params.no_context = true;
        params.no_timestamps = true;
        params.language = language && language[0] ? language : "auto";
        params.detect_language = false;
        params.abort_callback = abort_asr;
        params.abort_callback_user_data = session;
        if (abort_asr(session) || whisper_full(session->asr, params, samples, count) != 0) return -2;
        if (abort_asr(session)) return -2;
        std::string text;
        const int segments = whisper_full_n_segments(session->asr);
        if (segments < 0 || segments > 4096) return -3;
        for (int i = 0; i < segments; ++i) {
            if (abort_asr(session)) return -2;
            const char * segment = whisper_full_get_segment_text(session->asr, i);
            if (segment) text += segment;
            if (text.size() >= static_cast<size_t>(capacity)) return -3;
        }
        std::memcpy(output, text.c_str(), text.size() + 1);
        return static_cast<int>(text.size());
    } catch (...) { return -4; }
}

RT_EXPORT void tomur_realtime_speech_destroy(void * opaque) {
    delete static_cast<speech_session *>(opaque);
}
