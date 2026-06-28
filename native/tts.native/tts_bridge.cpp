#include "llama.h"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <fstream>
#include <iomanip>
#include <limits>
#include <map>
#include <memory>
#include <mutex>
#include <regex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#define JSON_ASSERT GGML_ASSERT
#include <nlohmann/json.hpp>

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

using json = nlohmann::ordered_json;

enum outetts_version {
    OUTETTS_V0_2,
    OUTETTS_V0_3,
};

constexpr int k_default_sample_rate = 24000;
constexpr int k_audio_token_min = 151672;
constexpr int k_audio_token_max = 155772;
constexpr int k_context_size = 8192;
constexpr int k_batch_size = 8192;
constexpr int k_max_predict = 4096;
constexpr float k_pi = 3.14159265358979323846f;

static std::once_flag backend_init_once;

struct tts_audio {
    std::vector<int16_t> pcm;
    int sample_rate = k_default_sample_rate;
    int prompt_tokens = 0;
    int generated_tokens = 0;
    int audio_codes = 0;
};

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

tomur_tts_result * make_audio_result(
    int32_t sample_rate,
    const std::vector<int16_t> & pcm,
    const std::vector<std::string> & diagnostics) {
    auto * result = static_cast<tomur_tts_result *>(std::calloc(1, sizeof(tomur_tts_result)));
    if (result == nullptr) {
        return nullptr;
    }

    int16_t * pcm_copy = nullptr;
    if (!pcm.empty()) {
        pcm_copy = static_cast<int16_t *>(std::malloc(pcm.size() * sizeof(int16_t)));
        if (pcm_copy == nullptr) {
            std::free(result);
            return nullptr;
        }

        std::memcpy(pcm_copy, pcm.data(), pcm.size() * sizeof(int16_t));
    }

    result->status_code = 0;
    result->pcm = pcm_copy;
    result->pcm_length = pcm.size();
    result->sample_rate = sample_rate;
    result->diagnostics_json = duplicate_string(to_json_array(diagnostics));
    result->error_utf8 = duplicate_string("");
    return result;
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

void ensure_backend_initialized() {
    std::call_once(backend_init_once, []() {
        llama_backend_init();
    });
}

std::string read_string(const char * value) {
    return value == nullptr ? std::string() : std::string(value);
}

std::vector<llama_token> tokenize(
    const llama_vocab * vocab,
    const std::string & text,
    bool add_special,
    bool parse_special) {
    const auto text_length = static_cast<int32_t>(std::min<size_t>(
        text.size(),
        static_cast<size_t>(std::numeric_limits<int32_t>::max())));
    std::vector<llama_token> tokens(std::max<int32_t>(16, text_length + 32));
    auto count = llama_tokenize(vocab, text.c_str(), text_length, tokens.data(), static_cast<int32_t>(tokens.size()), add_special, parse_special);
    if (count < 0) {
        tokens.resize(static_cast<size_t>(-count));
        count = llama_tokenize(vocab, text.c_str(), text_length, tokens.data(), static_cast<int32_t>(tokens.size()), add_special, parse_special);
    }

    if (count <= 0) {
        return {};
    }

    tokens.resize(static_cast<size_t>(count));
    return tokens;
}

void batch_clear(llama_batch & batch) {
    batch.n_tokens = 0;
}

void batch_add(llama_batch & batch, llama_token token, llama_pos pos, llama_seq_id seq_id, bool logits) {
    if (batch.n_tokens < 0) {
        throw std::runtime_error("llama batch is invalid");
    }

    batch.token[batch.n_tokens] = token;
    batch.pos[batch.n_tokens] = pos;
    batch.n_seq_id[batch.n_tokens] = 1;
    batch.seq_id[batch.n_tokens][0] = seq_id;
    batch.logits[batch.n_tokens] = logits ? 1 : 0;
    batch.n_tokens++;
}

struct batch_handle {
    llama_batch batch;

    batch_handle(int32_t tokens, int32_t embeddings, int32_t seq_max)
        : batch(llama_batch_init(tokens, embeddings, seq_max)) {
    }

    batch_handle(const batch_handle &) = delete;
    batch_handle & operator=(const batch_handle &) = delete;

    ~batch_handle() {
        llama_batch_free(batch);
    }
};

struct model_deleter {
    void operator()(llama_model * model) const {
        if (model != nullptr) {
            llama_model_free(model);
        }
    }
};

struct context_deleter {
    void operator()(llama_context * context) const {
        if (context != nullptr) {
            llama_free(context);
        }
    }
};

struct sampler_deleter {
    void operator()(llama_sampler * sampler) const {
        if (sampler != nullptr) {
            llama_sampler_free(sampler);
        }
    }
};

using model_ptr = std::unique_ptr<llama_model, model_deleter>;
using context_ptr = std::unique_ptr<llama_context, context_deleter>;
using sampler_ptr = std::unique_ptr<llama_sampler, sampler_deleter>;

static void fill_hann_window(int length, bool periodic, float * output) {
    int offset = periodic ? 0 : -1;
    for (int i = 0; i < length; i++) {
        output[i] = 0.5f * (1.0f - std::cos((2.0f * k_pi * i) / (length + offset)));
    }
}

static void twiddle(float * real, float * imag, int k, int n) {
    float angle = 2.0f * k_pi * k / n;
    *real = std::cos(angle);
    *imag = std::sin(angle);
}

static void irfft(int n, const float * input_complex, float * output_real) {
    int bins = n / 2 + 1;

    std::vector<float> real_input(bins);
    std::vector<float> imag_input(bins);
    for (int i = 0; i < bins; ++i) {
        real_input[i] = input_complex[2 * i];
        imag_input[i] = input_complex[2 * i + 1];
    }

    std::vector<float> real_output(n);
    std::vector<float> imag_output(n);
    for (int k = 0; k < n; ++k) {
        for (int m = 0; m < bins; ++m) {
            float twiddle_real;
            float twiddle_imag;
            twiddle(&twiddle_real, &twiddle_imag, k * m, n);
            real_output[k] += real_input[m] * twiddle_real - imag_input[m] * twiddle_imag;
            imag_output[k] += real_input[m] * twiddle_imag + imag_input[m] * twiddle_real;
        }
    }

    for (int i = 0; i < n; ++i) {
        output_real[i] = real_output[i] / bins;
    }
}

static void fold(
    const std::vector<float> & data,
    int64_t n_out,
    int64_t n_win,
    int64_t n_hop,
    int64_t n_pad,
    std::vector<float> & output) {
    int64_t width = n_out;
    output.resize(width, 0.0f);

    int64_t column_index = 0;
    for (int64_t column = 0; column < width; ++column) {
        int64_t start = column * n_hop - n_pad;
        int64_t end = start + n_win;

        for (int64_t position = start; position < end; ++position) {
            if (position >= 0 && position < n_out && column_index < static_cast<int64_t>(data.size())) {
                output[position] += data[static_cast<size_t>(column_index)];
            }
            column_index++;
        }
    }

    output.resize(static_cast<size_t>(n_out - 2 * n_pad));
}

static std::vector<float> embeddings_to_audio(
    const float * embeddings,
    int n_codes,
    int n_embd,
    int n_threads) {
    if (embeddings == nullptr || n_codes <= 0 || n_embd <= 0 || n_embd % 2 != 0) {
        throw std::runtime_error("WavTokenizer did not return a valid embedding matrix");
    }

    const int n_fft = 1280;
    const int n_hop = 320;
    const int n_win = 1280;
    const int n_pad = (n_win - n_hop) / 2;
    const int n_out = (n_codes - 1) * n_hop + n_win;

    std::vector<float> hann(n_fft);
    fill_hann_window(static_cast<int>(hann.size()), true, hann.data());

    int n_spec = n_embd * n_codes;
    std::vector<float> transposed(n_spec);
    std::vector<float> complex_spec(n_spec);
    std::vector<float> stft(n_spec);

    for (int code = 0; code < n_codes; ++code) {
        for (int embd = 0; embd < n_embd; ++embd) {
            transposed[embd * n_codes + code] = embeddings[code * n_embd + embd];
        }
    }

    for (int embd = 0; embd < n_embd / 2; ++embd) {
        for (int code = 0; code < n_codes; ++code) {
            float mag = std::exp(transposed[embd * n_codes + code]);
            float phase = transposed[(embd + n_embd / 2) * n_codes + code];
            mag = std::min(mag, 1e2f);
            complex_spec[2 * (embd * n_codes + code) + 0] = mag * std::cos(phase);
            complex_spec[2 * (embd * n_codes + code) + 1] = mag * std::sin(phase);
        }
    }

    for (int code = 0; code < n_codes; ++code) {
        for (int embd = 0; embd < n_embd / 2; ++embd) {
            stft[code * n_embd + 2 * embd + 0] = complex_spec[2 * (embd * n_codes + code) + 0];
            stft[code * n_embd + 2 * embd + 1] = complex_spec[2 * (embd * n_codes + code) + 1];
        }
    }

    std::vector<float> result(static_cast<size_t>(n_codes * n_fft));
    std::vector<float> envelope_columns(static_cast<size_t>(n_codes * n_fft));
    int worker_count = std::max(1, n_threads);
    std::vector<std::thread> workers(static_cast<size_t>(worker_count));
    for (int worker = 0; worker < worker_count; ++worker) {
        workers[static_cast<size_t>(worker)] = std::thread([&, worker]() {
            for (int code = worker; code < n_codes; code += worker_count) {
                irfft(n_fft, stft.data() + code * n_embd, result.data() + code * n_fft);
                for (int index = 0; index < n_fft; ++index) {
                    result[static_cast<size_t>(code * n_fft + index)] *= hann[static_cast<size_t>(index)];
                    envelope_columns[static_cast<size_t>(code * n_fft + index)] = hann[static_cast<size_t>(index)] * hann[static_cast<size_t>(index)];
                }
            }
        });
    }

    for (auto & worker : workers) {
        worker.join();
    }

    std::vector<float> audio;
    std::vector<float> envelope;
    fold(result, n_out, n_win, n_hop, n_pad, audio);
    fold(envelope_columns, n_out, n_win, n_hop, n_pad, envelope);

    for (size_t i = 0; i < audio.size(); ++i) {
        if (std::abs(envelope[i]) > 1e-12f) {
            audio[i] /= envelope[i];
        } else {
            audio[i] = 0.0f;
        }
    }

    return audio;
}

static std::vector<int16_t> floats_to_pcm16(std::vector<float> audio) {
    const auto silence = std::min<size_t>(audio.size(), k_default_sample_rate / 4);
    for (size_t i = 0; i < silence; ++i) {
        audio[i] = 0.0f;
    }

    std::vector<int16_t> pcm(audio.size());
    for (size_t i = 0; i < audio.size(); ++i) {
        auto sample = std::clamp(audio[i] * 32767.0f, -32768.0f, 32767.0f);
        pcm[i] = static_cast<int16_t>(sample);
    }

    return pcm;
}

static const std::map<int, std::string> ones = {
    {0, "zero"}, {1, "one"}, {2, "two"}, {3, "three"}, {4, "four"},
    {5, "five"}, {6, "six"}, {7, "seven"}, {8, "eight"}, {9, "nine"},
    {10, "ten"}, {11, "eleven"}, {12, "twelve"}, {13, "thirteen"}, {14, "fourteen"},
    {15, "fifteen"}, {16, "sixteen"}, {17, "seventeen"}, {18, "eighteen"}, {19, "nineteen"}
};

static const std::map<int, std::string> tens = {
    {2, "twenty"}, {3, "thirty"}, {4, "forty"}, {5, "fifty"},
    {6, "sixty"}, {7, "seventy"}, {8, "eighty"}, {9, "ninety"}
};

static std::string convert_less_than_thousand(int number) {
    std::string result;
    if (number >= 100) {
        result += ones.at(number / 100) + " hundred ";
        number %= 100;
    }

    if (number >= 20) {
        result += tens.at(number / 10);
        if (number % 10 > 0) {
            result += "-" + ones.at(number % 10);
        }
    } else if (number > 0) {
        result += ones.at(number);
    }

    return result;
}

static std::string number_to_words(const std::string & number_text) {
    try {
        size_t decimal_pos = number_text.find('.');
        std::string integer_part = number_text.substr(0, decimal_pos);
        int number = std::stoi(integer_part);
        std::string result;

        if (number == 0) {
            result = "zero";
        } else {
            if (number >= 1000000000) {
                result += convert_less_than_thousand(number / 1000000000) + " billion ";
                number %= 1000000000;
            }

            if (number >= 1000000) {
                result += convert_less_than_thousand(number / 1000000) + " million ";
                number %= 1000000;
            }

            if (number >= 1000) {
                result += convert_less_than_thousand(number / 1000) + " thousand ";
                number %= 1000;
            }

            if (number > 0) {
                result += convert_less_than_thousand(number);
            }
        }

        if (decimal_pos != std::string::npos) {
            result += " point";
            std::string decimal_part = number_text.substr(decimal_pos + 1);
            for (char digit : decimal_part) {
                result += " " + ones.at(digit - '0');
            }
        }

        return result;
    } catch (const std::exception &) {
        return " ";
    }
}

static std::string replace_numbers_with_words(const std::string & input_text) {
    std::regex number_pattern(R"(\d+(\.\d+)?)");
    std::string result;
    auto iterator = std::sregex_iterator(input_text.begin(), input_text.end(), number_pattern);
    auto end = std::sregex_iterator();

    size_t last_position = 0;
    for (auto current = iterator; current != end; ++current) {
        const auto & match = *current;
        result.append(input_text, last_position, static_cast<size_t>(match.position()) - last_position);
        result.append(number_to_words(match.str()));
        last_position = static_cast<size_t>(match.position() + match.length());
    }

    result.append(input_text, last_position);
    return result;
}

static std::string process_text(const std::string & text, outetts_version tts_version) {
    std::string processed = replace_numbers_with_words(text);
    std::transform(processed.begin(), processed.end(), processed.begin(), [](unsigned char value) {
        return static_cast<char>(std::tolower(value));
    });

    processed = std::regex_replace(processed, std::regex(R"([-_/,\.\\])"), " ");
    processed = std::regex_replace(processed, std::regex(R"([^a-z\s])"), "");
    processed = std::regex_replace(processed, std::regex(R"(\s+)"), " ");
    processed = std::regex_replace(processed, std::regex(R"(^\s+|\s+$)"), "");

    const std::string separator = tts_version == OUTETTS_V0_3 ? "<|space|>" : "<|text_sep|>";
    processed = std::regex_replace(processed, std::regex(R"(\s)"), separator);
    return processed;
}

static std::vector<llama_token> prepare_guide_tokens(
    const llama_vocab * vocab,
    const std::string & text,
    outetts_version tts_version) {
    const std::string delimiter = tts_version == OUTETTS_V0_3 ? "<|space|>" : "<|text_sep|>";

    std::vector<llama_token> result;
    auto newline = tokenize(vocab, "\n", false, true);
    if (!newline.empty()) {
        result.push_back(newline[0]);
    }

    size_t start = 0;
    size_t end = text.find(delimiter);
    while (end != std::string::npos) {
        auto tokenized = tokenize(vocab, text.substr(start, end - start), false, true);
        if (!tokenized.empty()) {
            result.push_back(tokenized[0]);
        }

        start = end + delimiter.length();
        end = text.find(delimiter, start);
    }

    auto tokenized = tokenize(vocab, text.substr(start), false, true);
    if (!tokenized.empty()) {
        result.push_back(tokenized[0]);
    }

    return result;
}

static json speaker_from_file(const std::string & speaker_file) {
    std::ifstream file(speaker_file);
    if (!file) {
        throw std::runtime_error("speaker file could not be opened: " + speaker_file);
    }

    return json::parse(file);
}

static outetts_version get_tts_version(llama_model * model, const json & speaker = json::object()) {
    if (speaker.contains("version")) {
        auto version = speaker["version"].get<std::string>();
        if (version == "0.2") {
            return OUTETTS_V0_2;
        }
        if (version == "0.3") {
            return OUTETTS_V0_3;
        }
        throw std::runtime_error("unsupported speaker version: " + version);
    }

    const char * chat_template = llama_model_chat_template(model, nullptr);
    if (chat_template != nullptr && std::string(chat_template) == "outetts-0.3") {
        return OUTETTS_V0_3;
    }

    return OUTETTS_V0_2;
}

static std::string audio_text_from_speaker(const json & speaker, outetts_version tts_version) {
    std::string audio_text = "<|text_start|>";
    const std::string separator = tts_version == OUTETTS_V0_3 ? "<|space|>" : "<|text_sep|>";
    for (const auto & word : speaker["words"]) {
        audio_text += word["word"].get<std::string>() + separator;
    }

    return audio_text;
}

static std::string audio_data_from_speaker(const json & speaker, outetts_version tts_version) {
    std::string audio_data = "<|audio_start|>\n";
    const std::string code_start = tts_version == OUTETTS_V0_3 ? "" : "<|code_start|>";
    const std::string code_end = tts_version == OUTETTS_V0_3 ? "<|space|>" : "<|code_end|>";

    for (const auto & word : speaker["words"]) {
        std::ostringstream entry;
        entry << word["word"].get<std::string>() << "<|t_" << std::fixed << std::setprecision(2)
              << word["duration"].get<double>() << "|>" << code_start;

        for (int code : word["codes"].get<std::vector<int>>()) {
            entry << "<|" << code << "|>";
        }

        entry << code_end << "\n";
        audio_data += entry.str();
    }

    return audio_data;
}

static void load_default_speaker(outetts_version tts_version, std::string & audio_text, std::string & audio_data) {
    audio_text = "<|text_start|>the<|text_sep|>overall<|text_sep|>package<|text_sep|>from<|text_sep|>just<|text_sep|>two<|text_sep|>people<|text_sep|>is<|text_sep|>pretty<|text_sep|>remarkable<|text_sep|>sure<|text_sep|>i<|text_sep|>have<|text_sep|>some<|text_sep|>critiques<|text_sep|>about<|text_sep|>some<|text_sep|>of<|text_sep|>the<|text_sep|>gameplay<|text_sep|>aspects<|text_sep|>but<|text_sep|>its<|text_sep|>still<|text_sep|>really<|text_sep|>enjoyable<|text_sep|>and<|text_sep|>it<|text_sep|>looks<|text_sep|>lovely<|text_sep|>";
    audio_data = R"(<|audio_start|>
the<|t_0.08|><|code_start|><|257|><|740|><|636|><|913|><|788|><|1703|><|code_end|>
overall<|t_0.36|><|code_start|><|127|><|201|><|191|><|774|><|700|><|532|><|1056|><|557|><|798|><|298|><|1741|><|747|><|1662|><|1617|><|1702|><|1527|><|368|><|1588|><|1049|><|1008|><|1625|><|747|><|1576|><|728|><|1019|><|1696|><|1765|><|code_end|>
package<|t_0.56|><|code_start|><|935|><|584|><|1319|><|627|><|1016|><|1491|><|1344|><|1117|><|1526|><|1040|><|239|><|1435|><|951|><|498|><|723|><|1180|><|535|><|789|><|1649|><|1637|><|78|><|465|><|1668|><|901|><|595|><|1675|><|117|><|1009|><|1667|><|320|><|840|><|79|><|507|><|1762|><|1508|><|1228|><|1768|><|802|><|1450|><|1457|><|232|><|639|><|code_end|>
from<|t_0.19|><|code_start|><|604|><|782|><|1682|><|872|><|1532|><|1600|><|1036|><|1761|><|647|><|1554|><|1371|><|653|><|1595|><|950|><|code_end|>
just<|t_0.25|><|code_start|><|1782|><|1670|><|317|><|786|><|1748|><|631|><|599|><|1155|><|1364|><|1524|><|36|><|1591|><|889|><|1535|><|541|><|440|><|1532|><|50|><|870|><|code_end|>
two<|t_0.24|><|code_start|><|1681|><|1510|><|673|><|799|><|805|><|1342|><|330|><|519|><|62|><|640|><|1138|><|565|><|1552|><|1497|><|1552|><|572|><|1715|><|1732|><|code_end|>
people<|t_0.39|><|code_start|><|593|><|274|><|136|><|740|><|691|><|633|><|1484|><|1061|><|1138|><|1485|><|344|><|428|><|397|><|1562|><|645|><|917|><|1035|><|1449|><|1669|><|487|><|442|><|1484|><|1329|><|1832|><|1704|><|600|><|761|><|653|><|269|><|code_end|>
is<|t_0.16|><|code_start|><|566|><|583|><|1755|><|646|><|1337|><|709|><|802|><|1008|><|485|><|1583|><|652|><|10|><|code_end|>
pretty<|t_0.32|><|code_start|><|1818|><|1747|><|692|><|733|><|1010|><|534|><|406|><|1697|><|1053|><|1521|><|1355|><|1274|><|816|><|1398|><|211|><|1218|><|817|><|1472|><|1703|><|686|><|13|><|822|><|445|><|1068|><|code_end|>
remarkable<|t_0.68|><|code_start|><|230|><|1048|><|1705|><|355|><|706|><|1149|><|1535|><|1787|><|1356|><|1396|><|835|><|1583|><|486|><|1249|><|286|><|937|><|1076|><|1150|><|614|><|42|><|1058|><|705|><|681|><|798|><|934|><|490|><|514|><|1399|><|572|><|1446|><|1703|><|1346|><|1040|><|1426|><|1304|><|664|><|171|><|1530|><|625|><|64|><|1708|><|1830|><|1030|><|443|><|1509|><|1063|><|1605|><|1785|><|721|><|1440|><|923|><|code_end|>
sure<|t_0.36|><|code_start|><|792|><|1780|><|923|><|1640|><|265|><|261|><|1525|><|567|><|1491|><|1250|><|1730|><|362|><|919|><|1766|><|543|><|1|><|333|><|113|><|970|><|252|><|1606|><|133|><|302|><|1810|><|1046|><|1190|><|1675|><|code_end|>
i<|t_0.08|><|code_start|><|123|><|439|><|1074|><|705|><|1799|><|637|><|code_end|>
have<|t_0.16|><|code_start|><|1509|><|599|><|518|><|1170|><|552|><|1029|><|1267|><|864|><|419|><|143|><|1061|><|0|><|code_end|>
some<|t_0.16|><|code_start|><|619|><|400|><|1270|><|62|><|1370|><|1832|><|917|><|1661|><|167|><|269|><|1366|><|1508|><|code_end|>
critiques<|t_0.60|><|code_start|><|559|><|584|><|1163|><|1129|><|1313|><|1728|><|721|><|1146|><|1093|><|577|><|928|><|27|><|630|><|1080|><|1346|><|1337|><|320|><|1382|><|1175|><|1682|><|1556|><|990|><|1683|><|860|><|1721|><|110|><|786|><|376|><|1085|><|756|><|1523|><|234|><|1334|><|1506|><|1578|><|659|><|612|><|1108|><|1466|><|1647|><|308|><|1470|><|746|><|556|><|1061|><|code_end|>
about<|t_0.29|><|code_start|><|26|><|1649|><|545|><|1367|><|1263|><|1728|><|450|><|859|><|1434|><|497|><|1220|><|1285|><|179|><|755|><|1154|><|779|><|179|><|1229|><|1213|><|922|><|1774|><|1408|><|code_end|>
some<|t_0.23|><|code_start|><|986|><|28|><|1649|><|778|><|858|><|1519|><|1|><|18|><|26|><|1042|><|1174|><|1309|><|1499|><|1712|><|1692|><|1516|><|1574|><|code_end|>
of<|t_0.07|><|code_start|><|197|><|716|><|1039|><|1662|><|64|><|code_end|>
the<|t_0.08|><|code_start|><|1811|><|1568|><|569|><|886|><|1025|><|1374|><|code_end|>
gameplay<|t_0.48|><|code_start|><|1269|><|1092|><|933|><|1362|><|1762|><|1700|><|1675|><|215|><|781|><|1086|><|461|><|838|><|1022|><|759|><|649|><|1416|><|1004|><|551|><|909|><|787|><|343|><|830|><|1391|><|1040|><|1622|><|1779|><|1360|><|1231|><|1187|><|1317|><|76|><|997|><|989|><|978|><|737|><|189|><|code_end|>
aspects<|t_0.56|><|code_start|><|1423|><|797|><|1316|><|1222|><|147|><|719|><|1347|><|386|><|1390|><|1558|><|154|><|440|><|634|><|592|><|1097|><|1718|><|712|><|763|><|1118|><|1721|><|1311|><|868|><|580|><|362|><|1435|><|868|><|247|><|221|><|886|><|1145|><|1274|><|1284|><|457|><|1043|><|1459|><|1818|><|62|><|599|><|1035|><|62|><|1649|><|778|><|code_end|>
but<|t_0.20|><|code_start|><|780|><|1825|><|1681|><|1007|><|861|><|710|><|702|><|939|><|1669|><|1491|><|613|><|1739|><|823|><|1469|><|648|><|code_end|>
its<|t_0.09|><|code_start|><|92|><|688|><|1623|><|962|><|1670|><|527|><|599|><|code_end|>
still<|t_0.27|><|code_start|><|636|><|10|><|1217|><|344|><|713|><|957|><|823|><|154|><|1649|><|1286|><|508|><|214|><|1760|><|1250|><|456|><|1352|><|1368|><|921|><|615|><|5|><|code_end|>
really<|t_0.36|><|code_start|><|55|><|420|><|1008|><|1659|><|27|><|644|><|1266|><|617|><|761|><|1712|><|109|><|1465|><|1587|><|503|><|1541|><|619|><|197|><|1019|><|817|><|269|><|377|><|362|><|1381|><|507|><|1488|><|4|><|1695|><|code_end|>
enjoyable<|t_0.49|><|code_start|><|678|><|501|><|864|><|319|><|288|><|1472|><|1341|><|686|><|562|><|1463|><|619|><|1563|><|471|><|911|><|730|><|1811|><|1006|><|520|><|861|><|1274|><|125|><|1431|><|638|><|621|><|153|><|876|><|1770|><|437|><|987|><|1653|><|1109|><|898|><|1285|><|80|><|593|><|1709|><|843|><|code_end|>
and<|t_0.15|><|code_start|><|1285|><|987|><|303|><|1037|><|730|><|1164|><|502|><|120|><|1737|><|1655|><|1318|><|code_end|>
it<|t_0.09|><|code_start|><|848|><|1366|><|395|><|1601|><|1513|><|593|><|1302|><|code_end|>
looks<|t_0.27|><|code_start|><|1281|><|1266|><|1755|><|572|><|248|><|1751|><|1257|><|695|><|1380|><|457|><|659|><|585|><|1315|><|1105|><|1776|><|736|><|24|><|736|><|654|><|1027|><|code_end|>
lovely<|t_0.56|><|code_start|><|634|><|596|><|1766|><|1556|><|1306|><|1285|><|1481|><|1721|><|1123|><|438|><|1246|><|1251|><|795|><|659|><|1381|><|1658|><|217|><|1772|><|562|><|952|><|107|><|1129|><|1112|><|467|><|550|><|1079|><|840|><|1615|><|1469|><|1380|><|168|><|917|><|836|><|1827|><|437|><|583|><|67|><|595|><|1087|><|1646|><|1493|><|1677|><|code_end|>)";

    if (tts_version == OUTETTS_V0_3) {
        audio_text = std::regex_replace(audio_text, std::regex(R"(<\|text_sep\|>)"), "<|space|>");
        audio_data = std::regex_replace(audio_data, std::regex(R"(<\|code_start\|>)"), "");
        audio_data = std::regex_replace(audio_data, std::regex(R"(<\|code_end\|>)"), "<|space|>");
    }
}

static sampler_ptr create_sampler() {
    auto params = llama_sampler_chain_default_params();
    sampler_ptr sampler(llama_sampler_chain_init(params));
    if (!sampler) {
        throw std::runtime_error("llama sampler chain initialization failed");
    }

    llama_sampler_chain_add(sampler.get(), llama_sampler_init_top_k(4));
    llama_sampler_chain_add(sampler.get(), llama_sampler_init_dist(LLAMA_DEFAULT_SEED));
    return sampler;
}

static llama_token sample_token(llama_context * context, llama_sampler * sampler) {
    const llama_model * model = llama_get_model(context);
    const llama_vocab * vocab = llama_model_get_vocab(model);
    const int n_vocab = llama_vocab_n_tokens(vocab);
    const float * logits = llama_get_logits_ith(context, -1);
    if (logits == nullptr || n_vocab <= 0) {
        throw std::runtime_error("llama logits are unavailable for TTS sampling");
    }

    std::vector<llama_token_data> candidates(static_cast<size_t>(n_vocab));
    for (llama_token token = 0; token < n_vocab; token++) {
        candidates[static_cast<size_t>(token)] = llama_token_data { token, logits[token], 0.0f };
    }

    llama_token_data_array candidate_array {
        candidates.data(),
        candidates.size(),
        -1,
        false
    };
    llama_sampler_apply(sampler, &candidate_array);
    if (candidate_array.selected < 0 || static_cast<size_t>(candidate_array.selected) >= candidate_array.size) {
        throw std::runtime_error("llama sampler did not select a TTS token");
    }

    return candidate_array.data[candidate_array.selected].id;
}

static void decode_prompt(llama_context * context, const std::vector<llama_token> & prompt_tokens, int n_parallel) {
    const int parallel = std::max(1, n_parallel);
    const auto prompt_batch_size = prompt_tokens.size() * static_cast<size_t>(parallel);
    batch_handle prompt_batch(static_cast<int32_t>(std::max<size_t>(prompt_batch_size, static_cast<size_t>(parallel))), 0, parallel);
    for (size_t i = 0; i < prompt_tokens.size(); ++i) {
        for (int seq = 0; seq < parallel; ++seq) {
            batch_add(prompt_batch.batch, prompt_tokens[i], static_cast<llama_pos>(i), static_cast<llama_seq_id>(seq), false);
        }
    }

    if (prompt_batch.batch.n_tokens <= 0) {
        throw std::runtime_error("TTS prompt tokenization produced no tokens");
    }

    prompt_batch.batch.logits[prompt_batch.batch.n_tokens - 1] = 1;
    if (llama_decode(context, prompt_batch.batch) != 0) {
        throw std::runtime_error("llama_decode failed while evaluating the TTS prompt");
    }

    llama_synchronize(context);
}

static std::vector<llama_token> generate_codes(
    llama_context * context,
    const llama_vocab * vocab,
    llama_sampler * sampler,
    std::vector<llama_token> guide_tokens,
    int initial_position,
    int n_predict) {
    std::vector<llama_token> codes;
    batch_handle batch(1, 0, 1);
    int n_past = initial_position;
    int n_decode = 0;
    bool next_token_uses_guide_token = true;

    while (n_decode <= n_predict) {
        batch_clear(batch.batch);

        llama_token token = sample_token(context, sampler);
        if (!guide_tokens.empty() &&
            next_token_uses_guide_token &&
            !llama_vocab_is_control(vocab, token) &&
            !llama_vocab_is_eog(vocab, token)) {
            token = guide_tokens.front();
            guide_tokens.erase(guide_tokens.begin());
        }

        llama_sampler_accept(sampler, token);
        next_token_uses_guide_token = token == 198;
        codes.push_back(token);

        if (llama_vocab_is_eog(vocab, token) || n_decode == n_predict) {
            break;
        }

        batch_add(batch.batch, token, static_cast<llama_pos>(n_past), 0, true);
        n_decode++;
        n_past++;

        if (llama_decode(context, batch.batch) != 0) {
            throw std::runtime_error("llama_decode failed while generating TTS audio codes");
        }
    }

    return codes;
}

static tts_audio synthesize(const tomur_tts_request & request, std::vector<std::string> & diagnostics) {
    ensure_backend_initialized();

    const std::string text = read_string(request.text_utf8);
    const std::string acoustic_model_path = read_string(request.acoustic_model_path);
    const std::string voice_model_path = read_string(request.voice_model_path);
    const std::string speaker_path = read_string(request.speaker_prompt_utf8);
    const int threads = std::max(1, request.threads);

    auto model_params = llama_model_default_params();
    model_params.n_gpu_layers = std::max(0, request.gpu_layers);

    model_ptr text_to_codes_model(llama_model_load_from_file(acoustic_model_path.c_str(), model_params));
    if (!text_to_codes_model) {
        throw std::runtime_error("failed to load OuteTTS acoustic GGUF model");
    }

    auto context_params = llama_context_default_params();
    context_params.n_ctx = k_context_size;
    context_params.n_batch = k_batch_size;
    context_params.n_ubatch = k_batch_size;
    context_params.n_seq_max = 1;
    context_params.n_threads = threads;
    context_params.n_threads_batch = threads;
    context_params.embeddings = false;
    context_params.pooling_type = LLAMA_POOLING_TYPE_NONE;
    context_params.offload_kqv = request.gpu_layers > 0;
    context_params.op_offload = request.gpu_layers > 0;

    context_ptr text_to_codes_context(llama_init_from_model(text_to_codes_model.get(), context_params));
    if (!text_to_codes_context) {
        throw std::runtime_error("failed to create OuteTTS acoustic model context");
    }

    const llama_vocab * vocab = llama_model_get_vocab(text_to_codes_model.get());
    if (vocab == nullptr) {
        throw std::runtime_error("OuteTTS acoustic model did not expose a vocabulary");
    }

    json speaker = json::object();
    if (!speaker_path.empty()) {
        speaker = speaker_from_file(speaker_path);
        diagnostics.push_back("speaker: custom-json");
    } else {
        diagnostics.push_back("speaker: default-en-male-1");
    }

    const auto tts_version = get_tts_version(text_to_codes_model.get(), speaker);
    std::string audio_text;
    std::string audio_data;
    if (!speaker.empty()) {
        audio_text = audio_text_from_speaker(speaker, tts_version);
        audio_data = audio_data_from_speaker(speaker, tts_version);
    } else {
        load_default_speaker(tts_version, audio_text, audio_data);
    }

    const std::string prompt_clean = process_text(text, tts_version);
    if (prompt_clean.empty()) {
        throw std::runtime_error("input text is empty after OuteTTS normalization");
    }

    std::vector<llama_token> guide_tokens = prepare_guide_tokens(vocab, prompt_clean, tts_version);
    std::vector<llama_token> prompt_tokens;
    auto append_tokens = [&](const std::string & segment, bool add_special, bool parse_special) {
        auto tokens = tokenize(vocab, segment, add_special, parse_special);
        prompt_tokens.insert(prompt_tokens.end(), tokens.begin(), tokens.end());
    };

    append_tokens("<|im_start|>\n", true, true);
    append_tokens(audio_text, false, true);
    append_tokens(prompt_clean, false, true);
    append_tokens("<|text_end|>\n", false, true);
    append_tokens(audio_data, false, true);

    if (prompt_tokens.empty()) {
        throw std::runtime_error("TTS prompt tokenization produced no tokens");
    }

    if (static_cast<int>(prompt_tokens.size()) >= k_context_size) {
        throw std::runtime_error("TTS prompt exceeds the llama.cpp context size");
    }

    auto sampler = create_sampler();
    decode_prompt(text_to_codes_context.get(), prompt_tokens, 1);
    auto codes = generate_codes(
        text_to_codes_context.get(),
        vocab,
        sampler.get(),
        std::move(guide_tokens),
        static_cast<int>(prompt_tokens.size()),
        k_max_predict);

    auto generated_tokens = static_cast<int>(codes.size());
    codes.erase(
        std::remove_if(
            codes.begin(),
            codes.end(),
            [](llama_token token) { return token < k_audio_token_min || token > k_audio_token_max; }),
        codes.end());

    if (codes.empty()) {
        throw std::runtime_error("OuteTTS did not generate any audio code tokens");
    }

    for (auto & token : codes) {
        token -= k_audio_token_min;
    }

    model_ptr codes_to_speech_model(llama_model_load_from_file(voice_model_path.c_str(), model_params));
    if (!codes_to_speech_model) {
        throw std::runtime_error("failed to load WavTokenizer GGUF model");
    }

    auto vocoder_context_params = llama_context_default_params();
    vocoder_context_params.n_ctx = std::max<int>(static_cast<int>(codes.size()), 512);
    vocoder_context_params.n_batch = std::max<int>(static_cast<int>(codes.size()), 512);
    vocoder_context_params.n_ubatch = vocoder_context_params.n_batch;
    vocoder_context_params.n_seq_max = 1;
    vocoder_context_params.n_threads = threads;
    vocoder_context_params.n_threads_batch = threads;
    vocoder_context_params.embeddings = true;
    vocoder_context_params.pooling_type = LLAMA_POOLING_TYPE_NONE;
    vocoder_context_params.offload_kqv = request.gpu_layers > 0;
    vocoder_context_params.op_offload = request.gpu_layers > 0;

    context_ptr codes_to_speech_context(llama_init_from_model(codes_to_speech_model.get(), vocoder_context_params));
    if (!codes_to_speech_context) {
        throw std::runtime_error("failed to create WavTokenizer context");
    }

    llama_set_embeddings(codes_to_speech_context.get(), true);
    batch_handle vocoder_batch(static_cast<int32_t>(codes.size()), 0, 1);
    for (size_t i = 0; i < codes.size(); ++i) {
        batch_add(vocoder_batch.batch, codes[i], static_cast<llama_pos>(i), 0, true);
    }

    if (llama_encode(codes_to_speech_context.get(), vocoder_batch.batch) != 0) {
        throw std::runtime_error("llama_encode failed while running WavTokenizer");
    }

    llama_synchronize(codes_to_speech_context.get());

    const int n_embd = llama_model_n_embd_out(codes_to_speech_model.get());
    const float * embeddings = llama_get_embeddings(codes_to_speech_context.get());
    auto audio = embeddings_to_audio(embeddings, static_cast<int>(codes.size()), n_embd, threads);

    return tts_audio {
        floats_to_pcm16(std::move(audio)),
        k_default_sample_rate,
        static_cast<int>(prompt_tokens.size()),
        generated_tokens,
        static_cast<int>(codes.size())
    };
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

    try {
        auto audio = synthesize(*request, diagnostics);
        diagnostics.push_back("tts-synthesis: llama.cpp-tools-tts");
        diagnostics.push_back("sample-rate: " + std::to_string(audio.sample_rate));
        diagnostics.push_back("prompt-tokens: " + std::to_string(audio.prompt_tokens));
        diagnostics.push_back("generated-tokens: " + std::to_string(audio.generated_tokens));
        diagnostics.push_back("audio-codes: " + std::to_string(audio.audio_codes));
        diagnostics.push_back("pcm-samples: " + std::to_string(audio.pcm.size()));
        return make_audio_result(audio.sample_rate, audio.pcm, diagnostics);
    } catch (const std::exception & exception) {
        diagnostics.push_back("tts-synthesis: failed");
        return make_result(
            2,
            request->sample_rate > 0 ? request->sample_rate : k_default_sample_rate,
            diagnostics,
            exception.what());
    } catch (...) {
        diagnostics.push_back("tts-synthesis: failed");
        return make_result(
            2,
            request->sample_rate > 0 ? request->sample_rate : k_default_sample_rate,
            diagnostics,
            "unknown llama.cpp GGUF TTS failure");
    }
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
