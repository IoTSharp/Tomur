#ifndef TOMUR_PLATE_H
#define TOMUR_PLATE_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#if defined(TOMUR_PLATE_BUILD)
#define TOMUR_PLATE_API __declspec(dllexport)
#else
#define TOMUR_PLATE_API __declspec(dllimport)
#endif
#else
#define TOMUR_PLATE_API __attribute__((visibility("default")))
#endif

#if defined(__cplusplus)
#define TOMUR_PLATE_NOEXCEPT noexcept
extern "C" {
#else
#define TOMUR_PLATE_NOEXCEPT
#endif

typedef enum tomur_plate_status_code {
    TOMUR_PLATE_STATUS_OK = 0,
    TOMUR_PLATE_STATUS_INVALID_ARGUMENT = 1,
    TOMUR_PLATE_STATUS_MODEL_UNAVAILABLE = 2,
    TOMUR_PLATE_STATUS_IMAGE_DECODE_FAILED = 3,
    TOMUR_PLATE_STATUS_RUNTIME_INITIALIZATION_FAILED = 4,
    TOMUR_PLATE_STATUS_RECOGNITION_FAILED = 5,
    TOMUR_PLATE_STATUS_INTERNAL_ERROR = 6
} tomur_plate_status_code;

/* 返回对象及两个 UTF-8 字符串均由原生层分配，调用方必须统一交给 free 函数释放。 */
typedef struct tomur_plate_result {
    int32_t status_code;
    const char * json_utf8;
    int64_t elapsed_ms;
    const char * error_utf8;
} tomur_plate_result;

/* 从 JPEG/PNG 等 OpenCV 支持的编码图片中识别车牌，并返回内部 results JSON。 */
TOMUR_PLATE_API tomur_plate_result * tomur_plate_recognize_image(
    const char * model_dir_utf8,
    const uint8_t * image_data,
    size_t image_length,
    int max_results,
    float min_confidence) TOMUR_PLATE_NOEXCEPT;

/* 释放 recognize 返回的完整结果对象；传入空指针是安全的。 */
TOMUR_PLATE_API void tomur_plate_result_free(
    tomur_plate_result * result) TOMUR_PLATE_NOEXCEPT;

#if defined(__cplusplus)
}
#endif

#endif
