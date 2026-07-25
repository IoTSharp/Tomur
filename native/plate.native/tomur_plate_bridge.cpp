#include "tomur_plate.h"

#include "hyper_lpr_sdk.h"
#include <opencv2/core.hpp>
#include <opencv2/imgcodecs.hpp>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <iomanip>
#include <limits>
#include <locale>
#include <memory>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

namespace {

namespace fs = std::filesystem;

/* Tomur 仅发布 64 位 RID，编译期固定跨托管边界的结果结构布局。 */
static_assert(sizeof(void *) == 8U, "tomur-plate requires a 64-bit target ABI");
static_assert(sizeof(tomur_plate_result) == 32U, "tomur_plate_result ABI size changed");
static_assert(offsetof(tomur_plate_result, status_code) == 0U, "status_code ABI offset changed");
static_assert(offsetof(tomur_plate_result, json_utf8) == 8U, "json_utf8 ABI offset changed");
static_assert(offsetof(tomur_plate_result, elapsed_ms) == 16U, "elapsed_ms ABI offset changed");
static_assert(offsetof(tomur_plate_result, error_utf8) == 24U, "error_utf8 ABI offset changed");

constexpr size_t k_max_encoded_image_bytes = 64U * 1024U * 1024U;
constexpr size_t k_max_decoded_pixels = 50U * 1000U * 1000U;
constexpr const char * k_empty_results_json = "{\"results\":[]}";
constexpr const char * k_required_model_files[] = {
    "b320_backbone_h.mnn",
    "b320_header_h.mnn",
    "b640x_backbone_h.mnn",
    "b640x_head_h.mnn",
    "litemodel_cls_96xh.mnn",
    "rpv3_mdict_160h.mnn"
};

/* 编码图片头中声明的尺寸，用于完整解码前的内存边界检查。 */
struct encoded_image_dimensions {
    uint64_t width = 0U;
    uint64_t height = 0U;
};

/* 从受边界保护的位置读取大端 16 位整数。 */
uint16_t read_be16(const uint8_t * data) noexcept {
    return static_cast<uint16_t>(
        (static_cast<uint16_t>(data[0]) << 8U) |
        static_cast<uint16_t>(data[1]));
}

/* 从受边界保护的位置读取大端 32 位整数。 */
uint32_t read_be32(const uint8_t * data) noexcept {
    return (static_cast<uint32_t>(data[0]) << 24U) |
        (static_cast<uint32_t>(data[1]) << 16U) |
        (static_cast<uint32_t>(data[2]) << 8U) |
        static_cast<uint32_t>(data[3]);
}

/* 从受边界保护的位置读取小端 16 位整数。 */
uint16_t read_le16(const uint8_t * data) noexcept {
    return static_cast<uint16_t>(
        static_cast<uint16_t>(data[0]) |
        (static_cast<uint16_t>(data[1]) << 8U));
}

/* 从受边界保护的位置读取小端 24 位整数。 */
uint32_t read_le24(const uint8_t * data) noexcept {
    return static_cast<uint32_t>(data[0]) |
        (static_cast<uint32_t>(data[1]) << 8U) |
        (static_cast<uint32_t>(data[2]) << 16U);
}

/* 从受边界保护的位置读取小端 32 位整数。 */
uint32_t read_le32(const uint8_t * data) noexcept {
    return static_cast<uint32_t>(data[0]) |
        (static_cast<uint32_t>(data[1]) << 8U) |
        (static_cast<uint32_t>(data[2]) << 16U) |
        (static_cast<uint32_t>(data[3]) << 24U);
}

/* 比较图片头中的四个 ASCII 字节。 */
bool matches_ascii(
    const uint8_t * data,
    size_t length,
    size_t offset,
    char first,
    char second,
    char third,
    char fourth) noexcept {
    return length >= 4U && offset <= length - 4U &&
        data[offset] == static_cast<uint8_t>(first) &&
        data[offset + 1U] == static_cast<uint8_t>(second) &&
        data[offset + 2U] == static_cast<uint8_t>(third) &&
        data[offset + 3U] == static_cast<uint8_t>(fourth);
}

/* 只接受 OpenCV int 尺寸能够表达的正整数宽高。 */
bool assign_dimensions(
    uint64_t width,
    uint64_t height,
    encoded_image_dimensions & dimensions) noexcept {
    if (width == 0U || height == 0U ||
        width > static_cast<uint64_t>(std::numeric_limits<int>::max()) ||
        height > static_cast<uint64_t>(std::numeric_limits<int>::max())) {
        return false;
    }

    dimensions.width = width;
    dimensions.height = height;
    return true;
}

/* 保留 WebP 画布或帧中像素数最大的尺寸，防止小画布头掩盖大帧。 */
bool keep_largest_dimensions(
    uint64_t width,
    uint64_t height,
    encoded_image_dimensions & dimensions) noexcept {
    encoded_image_dimensions candidate;
    if (!assign_dimensions(width, height, candidate)) {
        return false;
    }

    if (candidate.width * candidate.height > dimensions.width * dimensions.height) {
        dimensions = candidate;
    }
    return true;
}

/* 判断 JPEG 标记是否携带图像帧尺寸。 */
bool is_jpeg_start_of_frame(uint8_t marker) noexcept {
    switch (marker) {
        case 0xc0U:
        case 0xc1U:
        case 0xc2U:
        case 0xc3U:
        case 0xc5U:
        case 0xc6U:
        case 0xc7U:
        case 0xc9U:
        case 0xcaU:
        case 0xcbU:
        case 0xcdU:
        case 0xceU:
        case 0xcfU:
            return true;
        default:
            return false;
    }
}

/* 遍历 JPEG 段并从首个有效 SOF 标记读取宽高。 */
bool try_read_jpeg_dimensions(
    const uint8_t * data,
    size_t length,
    encoded_image_dimensions & dimensions) noexcept {
    if (length < 4U || data[0] != 0xffU || data[1] != 0xd8U) {
        return false;
    }

    size_t offset = 2U;
    while (offset < length) {
        if (data[offset] != 0xffU) {
            return false;
        }

        while (offset < length && data[offset] == 0xffU) {
            ++offset;
        }
        if (offset >= length) {
            return false;
        }

        const auto marker = data[offset++];
        if (marker == 0x00U || marker == 0xd8U || marker == 0xd9U || marker == 0x01U ||
            (marker >= 0xd0U && marker <= 0xd7U)) {
            continue;
        }
        if (offset > length || length - offset < 2U) {
            return false;
        }

        const auto segment_length = static_cast<size_t>(read_be16(data + offset));
        if (segment_length < 2U || segment_length > length - offset) {
            return false;
        }
        if (is_jpeg_start_of_frame(marker)) {
            if (segment_length < 7U) {
                return false;
            }
            return assign_dimensions(
                read_be16(data + offset + 5U),
                read_be16(data + offset + 3U),
                dimensions);
        }
        if (marker == 0xdaU) {
            return false;
        }

        offset += segment_length;
    }

    return false;
}

/* 从 PNG 固定 IHDR 位置读取大端宽高。 */
bool try_read_png_dimensions(
    const uint8_t * data,
    size_t length,
    encoded_image_dimensions & dimensions) noexcept {
    if (length < 24U ||
        data[0] != 0x89U || data[1] != 0x50U || data[2] != 0x4eU || data[3] != 0x47U ||
        data[4] != 0x0dU || data[5] != 0x0aU || data[6] != 0x1aU || data[7] != 0x0aU ||
        read_be32(data + 8U) != 13U ||
        !matches_ascii(data, length, 12U, 'I', 'H', 'D', 'R')) {
        return false;
    }

    return assign_dimensions(read_be32(data + 16U), read_be32(data + 20U), dimensions);
}

/* 读取 WebP 的 VP8X、VP8L 或 VP8 帧头尺寸。 */
bool try_read_webp_dimensions(
    const uint8_t * data,
    size_t length,
    encoded_image_dimensions & dimensions) noexcept {
    if (length < 20U ||
        !matches_ascii(data, length, 0U, 'R', 'I', 'F', 'F') ||
        !matches_ascii(data, length, 8U, 'W', 'E', 'B', 'P')) {
        return false;
    }

    const auto declared_end = static_cast<uint64_t>(read_le32(data + 4U)) + 8U;
    if (declared_end != length || declared_end < 20U) {
        return false;
    }

    const auto end = static_cast<size_t>(declared_end);
    size_t offset = 12U;
    bool found = false;
    while (offset <= end - 8U) {
        const auto chunk_size = static_cast<size_t>(read_le32(data + offset + 4U));
        const auto payload_offset = offset + 8U;
        if (chunk_size > end - payload_offset) {
            return false;
        }

        if (chunk_size >= 10U && matches_ascii(data, length, offset, 'V', 'P', '8', 'X')) {
            found = keep_largest_dimensions(
                1U + read_le24(data + payload_offset + 4U),
                1U + read_le24(data + payload_offset + 7U),
                dimensions) || found;
        } else if (chunk_size >= 5U &&
            matches_ascii(data, length, offset, 'V', 'P', '8', 'L') &&
            data[payload_offset] == 0x2fU) {
            const auto width = 1U + static_cast<uint32_t>(data[payload_offset + 1U]) +
                (static_cast<uint32_t>(data[payload_offset + 2U] & 0x3fU) << 8U);
            const auto height = 1U +
                (static_cast<uint32_t>(data[payload_offset + 2U] & 0xc0U) >> 6U) +
                (static_cast<uint32_t>(data[payload_offset + 3U]) << 2U) +
                (static_cast<uint32_t>(data[payload_offset + 4U] & 0x0fU) << 10U);
            found = keep_largest_dimensions(width, height, dimensions) || found;
        } else if (chunk_size >= 10U &&
            matches_ascii(data, length, offset, 'V', 'P', '8', ' ') &&
            data[payload_offset + 3U] == 0x9dU &&
            data[payload_offset + 4U] == 0x01U &&
            data[payload_offset + 5U] == 0x2aU) {
            found = keep_largest_dimensions(
                read_le16(data + payload_offset + 6U) & 0x3fffU,
                read_le16(data + payload_offset + 8U) & 0x3fffU,
                dimensions) || found;
        }

        const auto padding = chunk_size & 1U;
        if (chunk_size > end - payload_offset - padding) {
            return false;
        }
        offset = payload_offset + chunk_size + padding;
    }

    return found && offset == end;
}

/* 读取 BMP CORE 或 INFO 头中的有符号宽高。 */
bool try_read_bmp_dimensions(
    const uint8_t * data,
    size_t length,
    encoded_image_dimensions & dimensions) noexcept {
    if (length < 26U || data[0] != static_cast<uint8_t>('B') || data[1] != static_cast<uint8_t>('M')) {
        return false;
    }

    const auto dib_size = read_le32(data + 14U);
    if (dib_size == 12U) {
        return assign_dimensions(read_le16(data + 18U), read_le16(data + 20U), dimensions);
    }
    if (dib_size < 40U) {
        return false;
    }

    const auto raw_width = static_cast<uint64_t>(read_le32(data + 18U));
    const auto raw_height = static_cast<uint64_t>(read_le32(data + 22U));
    const auto signed_width = raw_width <= static_cast<uint64_t>(std::numeric_limits<int32_t>::max())
        ? static_cast<int64_t>(raw_width)
        : static_cast<int64_t>(raw_width) - (1LL << 32U);
    const auto signed_height = raw_height <= static_cast<uint64_t>(std::numeric_limits<int32_t>::max())
        ? static_cast<int64_t>(raw_height)
        : static_cast<int64_t>(raw_height) - (1LL << 32U);
    if (signed_width <= 0 || signed_height == 0 || signed_height == std::numeric_limits<int32_t>::min()) {
        return false;
    }

    const auto height = signed_height < 0
        ? -static_cast<int64_t>(signed_height)
        : static_cast<int64_t>(signed_height);
    return assign_dimensions(
        static_cast<uint64_t>(signed_width),
        static_cast<uint64_t>(height),
        dimensions);
}

/* 仅接受能够在完整解码前可靠读取尺寸的抓拍图片格式。 */
bool try_read_encoded_dimensions(
    const uint8_t * data,
    size_t length,
    encoded_image_dimensions & dimensions) noexcept {
    return try_read_jpeg_dimensions(data, length, dimensions) ||
        try_read_png_dimensions(data, length, dimensions) ||
        try_read_webp_dimensions(data, length, dimensions) ||
        try_read_bmp_dimensions(data, length, dimensions);
}

/* 使用上游释放函数托管识别上下文，避免异常路径泄漏模型资源。 */
struct context_deleter {
    /* 释放可为空的 HyperLPR3 Context。 */
    void operator()(HLPR_Context * context) const noexcept {
        if (context != nullptr) {
            HLPR_ReleaseContext(context);
        }
    }
};

/* 使用上游释放函数托管单次图片缓冲。 */
struct buffer_deleter {
    /* 释放可为空的 HyperLPR3 图片缓冲。 */
    void operator()(HLPR_DataBuffer * buffer) const noexcept {
        if (buffer != nullptr) {
            HLPR_ReleaseDataBuffer(buffer);
        }
    }
};

using context_handle = std::unique_ptr<HLPR_Context, context_deleter>;
using buffer_handle = std::unique_ptr<HLPR_DataBuffer, buffer_deleter>;

/* HyperLPR3 Context 会复用内部结果缓冲，因此用同一把锁保护初始化与推理。 */
struct recognizer_cache {
    std::mutex mutex;
    context_handle context;
    std::string model_dir;
    int max_results = 0;
    float min_confidence = -1.0F;
};

/* 返回进程内唯一识别器缓存，减少后台逐图回填时的模型重复加载。 */
recognizer_cache & get_recognizer_cache() {
    static recognizer_cache cache;
    return cache;
}

/* 计算从调用开始到当前时刻的毫秒数。 */
int64_t elapsed_ms(const std::chrono::steady_clock::time_point & started_at) {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - started_at).count();
}

/* 复制 UTF-8 字符串，并确保结果可由标准 free 释放。 */
char * duplicate_string(const std::string & value) noexcept {
    auto * copy = static_cast<char *>(std::malloc(value.size() + 1U));
    if (copy == nullptr) {
        return nullptr;
    }

    std::memcpy(copy, value.c_str(), value.size() + 1U);
    return copy;
}

/* 构造跨 ABI 返回对象；任一字符串分配失败时返回空指针。 */
tomur_plate_result * make_result(
    int32_t status_code,
    const std::string & json,
    int64_t duration_ms,
    const std::string & error) noexcept {
    auto * result = static_cast<tomur_plate_result *>(std::calloc(1U, sizeof(tomur_plate_result)));
    if (result == nullptr) {
        return nullptr;
    }

    auto * json_copy = duplicate_string(json);
    auto * error_copy = duplicate_string(error);
    if (json_copy == nullptr || error_copy == nullptr) {
        std::free(json_copy);
        std::free(error_copy);
        std::free(result);
        return nullptr;
    }

    result->status_code = status_code;
    result->json_utf8 = json_copy;
    result->elapsed_ms = duration_ms;
    result->error_utf8 = error_copy;
    return result;
}

/* 对 JSON 字符串执行最小且完整的控制字符转义，原样保留合法 UTF-8 字节。 */
std::string json_escape(const std::string & value) {
    std::ostringstream output;
    for (const unsigned char character : value) {
        switch (character) {
            case '\\': output << "\\\\"; break;
            case '"': output << "\\\""; break;
            case '\b': output << "\\b"; break;
            case '\f': output << "\\f"; break;
            case '\n': output << "\\n"; break;
            case '\r': output << "\\r"; break;
            case '\t': output << "\\t"; break;
            default:
                if (character < 0x20U) {
                    constexpr char hex[] = "0123456789abcdef";
                    output << "\\u00" << hex[(character >> 4U) & 0x0FU] << hex[character & 0x0FU];
                } else {
                    output << static_cast<char>(character);
                }
                break;
        }
    }

    return output.str();
}

/* 只清理上游结果两端的 ASCII 空白，不改动车牌内部字符。 */
std::string trim_ascii(const std::string & value) {
    const auto first = value.find_first_not_of(" \t\r\n");
    if (first == std::string::npos) {
        return {};
    }

    const auto last = value.find_last_not_of(" \t\r\n");
    return value.substr(first, last - first + 1U);
}

/* 安全读取上游固定长度车牌缓冲，避免异常数据缺少结尾零字节。 */
std::string read_plate_number(const HLPR_PlateResult & plate) {
    size_t length = 0U;
    while (length < sizeof(plate.code) && plate.code[length] != '\0') {
        ++length;
    }

    return trim_ascii(std::string(plate.code, length));
}

/* 将 HyperLPR3 类型转换为稳定英文枚举值。 */
const char * plate_type_name(HLPR_PlateType type) noexcept {
    switch (type) {
        case PLATE_TYPE_BLUE: return "blue";
        case PLATE_TYPE_YELLOW_SINGLE: return "yellow_single";
        case PLATE_TYPE_WHILE_SINGLE: return "white_single";
        case PLATE_TYPE_GREEN: return "green";
        case PLATE_TYPE_BLACK_HK_MACAO: return "black_hk_macao";
        case PLATE_TYPE_HK_SINGLE: return "hong_kong_single";
        case PLATE_TYPE_HK_DOUBLE: return "hong_kong_double";
        case PLATE_TYPE_MACAO_SINGLE: return "macao_single";
        case PLATE_TYPE_MACAO_DOUBLE: return "macao_double";
        case PLATE_TYPE_YELLOW_DOUBLE: return "yellow_double";
        default: return "unknown";
    }
}

/* 映射项目设备协议颜色码；无法从类型确认颜色时返回未确定 9。 */
const char * plate_color_code(HLPR_PlateType type) noexcept {
    switch (type) {
        case PLATE_TYPE_BLUE: return "0";
        case PLATE_TYPE_YELLOW_SINGLE:
        case PLATE_TYPE_YELLOW_DOUBLE: return "1";
        case PLATE_TYPE_BLACK_HK_MACAO: return "2";
        case PLATE_TYPE_WHILE_SINGLE: return "3";
        case PLATE_TYPE_GREEN: return "11";
        default: return "9";
    }
}

/* 把浮点坐标转换为受 int32 范围保护的整数坐标。 */
int32_t coordinate_to_int(float value) noexcept {
    if (!std::isfinite(value)) {
        return 0;
    }

    const auto rounded = std::llround(static_cast<double>(value));
    return static_cast<int32_t>(std::clamp<long long>(
        rounded,
        std::numeric_limits<int32_t>::min(),
        std::numeric_limits<int32_t>::max()));
}

/* 检查完整 r2_mobile 配置的六个模型文件，并返回规范化目录。 */
bool validate_model_directory(
    const char * model_dir_utf8,
    std::string & normalized_model_dir,
    std::string & error) {
    try {
        const fs::path model_dir = fs::absolute(fs::u8path(model_dir_utf8)).lexically_normal();
        if (!fs::is_directory(model_dir)) {
            error = "HyperLPR3 model directory was not found: " + model_dir.u8string();
            return false;
        }

        std::vector<std::string> missing;
        for (const auto * file_name : k_required_model_files) {
            const auto model_file = model_dir / fs::u8path(file_name);
            if (!fs::is_regular_file(model_file) || fs::file_size(model_file) == 0U) {
                missing.emplace_back(file_name);
            }
        }

        if (!missing.empty()) {
            std::ostringstream message;
            message << "HyperLPR3 model directory is incomplete; missing or empty:";
            for (const auto & file_name : missing) {
                message << " " << file_name;
            }
            error = message.str();
            return false;
        }

        normalized_model_dir = model_dir.u8string();
        return true;
    } catch (const fs::filesystem_error & exception) {
        error = "HyperLPR3 model directory could not be inspected: " + std::string(exception.what());
        return false;
    }
}

/* 解码受大小限制的编码图片，统一输出 HyperLPR3 所需的 BGR 三通道矩阵。 */
bool decode_image(
    const uint8_t * image_data,
    size_t image_length,
    cv::Mat & decoded,
    std::string & error) {
    if (image_length > k_max_encoded_image_bytes) {
        error = "Encoded image exceeds the 64 MiB native limit.";
        return false;
    }

    if (image_length > static_cast<size_t>(std::numeric_limits<int>::max())) {
        error = "Encoded image is too large for OpenCV decoding.";
        return false;
    }

    encoded_image_dimensions dimensions;
    if (!try_read_encoded_dimensions(image_data, image_length, dimensions)) {
        error = "Image header is not a supported JPEG, PNG, WebP or BMP image.";
        return false;
    }
    if (dimensions.width > k_max_decoded_pixels / dimensions.height) {
        error = "Decoded image exceeds the 50 megapixel native limit.";
        return false;
    }

    try {
        const cv::Mat encoded(
            1,
            static_cast<int>(image_length),
            CV_8UC1,
            const_cast<uint8_t *>(image_data));
        decoded = cv::imdecode(encoded, cv::IMREAD_COLOR);
    } catch (const cv::Exception & exception) {
        error = "OpenCV image decoding failed: " + std::string(exception.what());
        return false;
    }
    if (decoded.empty() || decoded.cols <= 0 || decoded.rows <= 0 || decoded.channels() != 3) {
        error = "Image bytes are not a supported, non-empty JPEG, PNG, WebP or BMP image.";
        return false;
    }
    if (decoded.total() > k_max_decoded_pixels) {
        decoded.release();
        error = "Decoded image exceeds the 50 megapixel native limit.";
        return false;
    }

    if (!decoded.isContinuous()) {
        decoded = decoded.clone();
    }
    return true;
}

/* 按模型目录和识别参数创建上下文，失败时保留上一个可用缓存。 */
context_handle create_context(
    const std::string & model_dir,
    int max_results,
    float min_confidence,
    std::string & error) {
    HLPR_ContextConfiguration configuration {};
    configuration.models_path = const_cast<char *>(model_dir.c_str());
    configuration.max_num = max_results;
    configuration.threads = 1;
    configuration.use_half = false;
    configuration.box_conf_threshold = 0.30F;
    configuration.nms_threshold = 0.50F;
    configuration.rec_confidence_threshold = min_confidence;
    /* 完整抓拍图使用 640x640 检测器，降低远处或车侧小车牌的漏检率。 */
    configuration.det_level = DETECT_LEVEL_HIGH;

    context_handle context(HLPR_CreateContext(&configuration));
    if (!context) {
        error = "HyperLPR3 returned a null recognition context.";
        return {};
    }

    if (HLPR_ContextQueryStatus(context.get()) != HResultCode::Ok) {
        error = "HyperLPR3 failed to initialize. Verify the model files and target-architecture MNN/OpenCV runtime dependencies.";
        return {};
    }

    return context;
}

/* 获取参数匹配的缓存上下文，必要时原子替换为新上下文。 */
HLPR_Context * ensure_context(
    recognizer_cache & cache,
    const std::string & model_dir,
    int max_results,
    float min_confidence,
    std::string & error) {
    if (cache.context &&
        cache.model_dir == model_dir &&
        cache.max_results == max_results &&
        cache.min_confidence == min_confidence) {
        return cache.context.get();
    }

    auto replacement = create_context(model_dir, max_results, min_confidence, error);
    if (!replacement) {
        return nullptr;
    }

    cache.context = std::move(replacement);
    cache.model_dir = model_dir;
    cache.max_results = max_results;
    cache.min_confidence = min_confidence;
    return cache.context.get();
}

/* 将上游结果序列化为托管层约定的内部 results 对象。 */
std::string serialize_results(const HLPR_PlateResultList & results, int max_results) {
    std::ostringstream output;
    output.imbue(std::locale::classic());
    output << "{\"results\":[";

    size_t written = 0U;
    const auto available = static_cast<size_t>(results.plate_size);
    const auto limit = std::min(available, static_cast<size_t>(max_results));
    for (size_t index = 0U; index < limit; ++index) {
        const auto & plate = results.plates[index];
        const auto plate_number = read_plate_number(plate);
        if (plate_number.empty() ||
            !std::isfinite(plate.text_confidence) ||
            plate.text_confidence < 0.0F ||
            plate.text_confidence > 1.0F) {
            continue;
        }

        if (written++ > 0U) {
            output << ',';
        }

        const auto * color_code = plate_color_code(plate.type);
        const auto vehicle_id = plate_number + "_" + color_code;
        output << "{\"plate_number\":\"" << json_escape(plate_number)
               << "\",\"plate_type\":\"" << plate_type_name(plate.type)
               << "\",\"plate_color_code\":\"" << color_code
               << "\",\"vehicle_id\":\"" << json_escape(vehicle_id)
               << "\",\"recognition_confidence\":" << std::setprecision(7) << plate.text_confidence
               << ",\"detection_confidence\":null"
               << ",\"box\":[" << coordinate_to_int(plate.x1)
               << ',' << coordinate_to_int(plate.y1)
               << ',' << coordinate_to_int(plate.x2)
               << ',' << coordinate_to_int(plate.y2) << "]}";
    }

    output << "]}";
    return output.str();
}

} // namespace

tomur_plate_result * tomur_plate_recognize_image(
    const char * model_dir_utf8,
    const uint8_t * image_data,
    size_t image_length,
    int max_results,
    float min_confidence) noexcept {
    const auto started_at = std::chrono::steady_clock::now();
    try {
        if (model_dir_utf8 == nullptr || model_dir_utf8[0] == '\0') {
            return make_result(
                TOMUR_PLATE_STATUS_INVALID_ARGUMENT,
                k_empty_results_json,
                elapsed_ms(started_at),
                "model_dir_utf8 is required and must point to the HyperLPR3 r2_mobile directory.");
        }
        if (image_data == nullptr || image_length == 0U) {
            return make_result(
                TOMUR_PLATE_STATUS_INVALID_ARGUMENT,
                k_empty_results_json,
                elapsed_ms(started_at),
                "image_data is required and image_length must be greater than zero.");
        }
        if (max_results < 1 || max_results > 10) {
            return make_result(
                TOMUR_PLATE_STATUS_INVALID_ARGUMENT,
                k_empty_results_json,
                elapsed_ms(started_at),
                "max_results must be between 1 and 10.");
        }
        if (!std::isfinite(min_confidence) || min_confidence < 0.0F || min_confidence > 1.0F) {
            return make_result(
                TOMUR_PLATE_STATUS_INVALID_ARGUMENT,
                k_empty_results_json,
                elapsed_ms(started_at),
                "min_confidence must be a finite number between 0 and 1.");
        }

        std::string model_dir;
        std::string error;
        if (!validate_model_directory(model_dir_utf8, model_dir, error)) {
            return make_result(
                TOMUR_PLATE_STATUS_MODEL_UNAVAILABLE,
                k_empty_results_json,
                elapsed_ms(started_at),
                error);
        }

        cv::Mat image;
        if (!decode_image(image_data, image_length, image, error)) {
            return make_result(
                TOMUR_PLATE_STATUS_IMAGE_DECODE_FAILED,
                k_empty_results_json,
                elapsed_ms(started_at),
                error);
        }

        auto & cache = get_recognizer_cache();
        std::lock_guard<std::mutex> lock(cache.mutex);
        auto * context = ensure_context(cache, model_dir, max_results, min_confidence, error);
        if (context == nullptr) {
            return make_result(
                TOMUR_PLATE_STATUS_RUNTIME_INITIALIZATION_FAILED,
                k_empty_results_json,
                elapsed_ms(started_at),
                error);
        }

        HLPR_ImageData source {};
        source.data = image.ptr<uint8_t>(0);
        source.width = image.cols;
        source.height = image.rows;
        source.format = STREAM_BGR;
        source.rotation = CAMERA_ROTATION_0;
        buffer_handle buffer(HLPR_CreateDataBuffer(&source));
        if (!buffer) {
            return make_result(
                TOMUR_PLATE_STATUS_RECOGNITION_FAILED,
                k_empty_results_json,
                elapsed_ms(started_at),
                "HyperLPR3 returned a null image buffer.");
        }

        HLPR_PlateResultList results {};
        if (HLPR_ContextUpdateStream(context, buffer.get(), &results) != HResultCode::Ok) {
            return make_result(
                TOMUR_PLATE_STATUS_RECOGNITION_FAILED,
                k_empty_results_json,
                elapsed_ms(started_at),
                "HyperLPR3 failed while recognizing the decoded image.");
        }
        if (results.plate_size > 0U && results.plates == nullptr) {
            return make_result(
                TOMUR_PLATE_STATUS_RECOGNITION_FAILED,
                k_empty_results_json,
                elapsed_ms(started_at),
                "HyperLPR3 returned an invalid non-empty result list.");
        }

        return make_result(
            TOMUR_PLATE_STATUS_OK,
            serialize_results(results, max_results),
            elapsed_ms(started_at),
            "");
    } catch (const cv::Exception & exception) {
        return make_result(
            TOMUR_PLATE_STATUS_RECOGNITION_FAILED,
            k_empty_results_json,
            elapsed_ms(started_at),
            "OpenCV/HyperLPR3 recognition failed: " + std::string(exception.what()));
    } catch (const std::exception & exception) {
        return make_result(
            TOMUR_PLATE_STATUS_INTERNAL_ERROR,
            k_empty_results_json,
            elapsed_ms(started_at),
            "Native plate recognition failed: " + std::string(exception.what()));
    } catch (...) {
        return make_result(
            TOMUR_PLATE_STATUS_INTERNAL_ERROR,
            k_empty_results_json,
            elapsed_ms(started_at),
            "Native plate recognition failed with an unknown error.");
    }
}

void tomur_plate_result_free(tomur_plate_result * result) noexcept {
    if (result == nullptr) {
        return;
    }

    std::free(const_cast<char *>(result->json_utf8));
    std::free(const_cast<char *>(result->error_utf8));
    std::free(result);
}
