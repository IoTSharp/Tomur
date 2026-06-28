#include <cstdlib>
#include <cstdint>
#include <algorithm>
#include <cctype>
#include <cmath>
#include <string_view>

#define STB_IMAGE_WRITE_IMPLEMENTATION
#include "../stable-diffusion.cpp/thirdparty/stb_image_write.h"

#include "stable-diffusion.h"

static int32_t tomur_count_debug_tokens(const char* text) {
    if (text == NULL) {
        return 0;
    }

    std::string_view view(text);
    bool in_token = false;
    int32_t count = 0;

    for (char ch : view) {
        if (std::isspace(static_cast<unsigned char>(ch))) {
            in_token = false;
            continue;
        }

        if (!in_token) {
            in_token = true;
            count++;
        }
    }

    return count;
}

typedef struct tomur_sd_image_generation_params_t {
    const char* prompt;
    const char* negative_prompt;
    const char* sample_method;
    const char* scheduler;
    int32_t width;
    int32_t height;
    int32_t sample_steps;
    float cfg_scale;
    float distilled_guidance;
    float flow_shift;
    int64_t seed;
} tomur_sd_image_generation_params_t;

typedef struct tomur_sd_encoded_image_t {
    uint8_t* data;
    int32_t length;
} tomur_sd_encoded_image_t;

extern "C" {

SD_API bool tomur_sd_debug_token_split_count(const char* text,
                                                int32_t* token_count) {
    if (text == NULL || token_count == NULL) {
        return false;
    }

    *token_count = tomur_count_debug_tokens(text);
    return true;
}

SD_API bool tomur_sd_generate_png(sd_ctx_t* sd_ctx,
                                     const tomur_sd_image_generation_params_t* generation_params,
                                     tomur_sd_encoded_image_t* encoded_image) {
    if (sd_ctx == NULL || generation_params == NULL || encoded_image == NULL || generation_params->prompt == NULL) {
        return false;
    }

    encoded_image->data = NULL;
    encoded_image->length = 0;

    sd_img_gen_params_t native_params;
    sd_img_gen_params_init(&native_params);

    native_params.prompt = generation_params->prompt;
    native_params.negative_prompt = generation_params->negative_prompt;
    native_params.batch_count = 1;

    if (generation_params->sample_method != NULL && generation_params->sample_method[0] != '\0') {
        auto sample_method = str_to_sample_method(generation_params->sample_method);
        if (sample_method != SAMPLE_METHOD_COUNT) {
            native_params.sample_params.sample_method = sample_method;
        }
    }

    if (generation_params->scheduler != NULL && generation_params->scheduler[0] != '\0') {
        auto scheduler = str_to_scheduler(generation_params->scheduler);
        if (scheduler != SCHEDULER_COUNT) {
            native_params.sample_params.scheduler = scheduler;
        }
    }

    if (generation_params->width > 0) {
        native_params.width = generation_params->width;
    }

    if (generation_params->height > 0) {
        native_params.height = generation_params->height;
    }

    if (generation_params->sample_steps > 0) {
        native_params.sample_params.sample_steps = generation_params->sample_steps;
    }

    if (generation_params->cfg_scale > 0.0f) {
        native_params.sample_params.guidance.txt_cfg = generation_params->cfg_scale;
    }

    if (std::isfinite(generation_params->distilled_guidance) && generation_params->distilled_guidance > 0.0f) {
        native_params.sample_params.guidance.distilled_guidance = generation_params->distilled_guidance;
    }

    if (std::isfinite(generation_params->flow_shift)) {
        native_params.sample_params.flow_shift = generation_params->flow_shift;
    }

    if (native_params.sample_params.sample_method == SAMPLE_METHOD_COUNT) {
        native_params.sample_params.sample_method = sd_get_default_sample_method(sd_ctx);
    }

    if (native_params.sample_params.scheduler == SCHEDULER_COUNT) {
        native_params.sample_params.scheduler = sd_get_default_scheduler(sd_ctx, native_params.sample_params.sample_method);
    }

    native_params.seed = generation_params->seed;

    sd_image_t* images = generate_image(sd_ctx, &native_params);
    if (images == NULL) {
        return false;
    }

    bool success = false;
    if (images[0].data != NULL && images[0].width > 0 && images[0].height > 0 && images[0].channel > 0) {
        int encoded_length = 0;
        unsigned char* encoded_bytes = stbi_write_png_to_mem(
            images[0].data,
            static_cast<int>(images[0].width * images[0].channel),
            static_cast<int>(images[0].width),
            static_cast<int>(images[0].height),
            static_cast<int>(images[0].channel),
            &encoded_length,
            NULL);

        if (encoded_bytes != NULL && encoded_length > 0) {
            encoded_image->data = encoded_bytes;
            encoded_image->length = encoded_length;
            success = true;
        } else if (encoded_bytes != NULL) {
            free(encoded_bytes);
        }
    }

    free_sd_images(images, native_params.batch_count);
    return success;
}

SD_API void tomur_sd_free_buffer(void* buffer) {
    free(buffer);
}

}
