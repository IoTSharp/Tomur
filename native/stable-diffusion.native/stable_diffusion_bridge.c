#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "stable-diffusion.h"

typedef struct tomur_sd_ctx_probe_params_t {
    const char* model_path;
    const char* diffusion_model_path;
    const char* clip_l_path;
    const char* clip_g_path;
    const char* t5xxl_path;
    const char* llm_path;
    const char* vae_path;
    int32_t n_threads;
    bool offload_params_to_cpu;
    bool enable_mmap;
    bool keep_clip_on_cpu;
    bool keep_vae_on_cpu;
    bool flash_attn;
    bool diffusion_flash_attn;
    bool vae_decode_only;
    bool free_params_immediately;
    const char* backend;
    const char* params_backend;
} tomur_sd_ctx_probe_params_t;

static const char* tomur_sd_log_level_name(enum sd_log_level_t level) {
    switch (level) {
        case SD_LOG_DEBUG:
            return "debug";
        case SD_LOG_INFO:
            return "info";
        case SD_LOG_WARN:
            return "warn";
        case SD_LOG_ERROR:
            return "error";
        default:
            return "unknown";
    }
}

static const char* tomur_sd_path_status(const char* path) {
    if (path == NULL) {
        return "null";
    }

    return path[0] == '\0' ? "empty" : "set";
}

static void tomur_sd_log_callback(enum sd_log_level_t level, const char* text, void* data) {
    (void)data;
    if (text == NULL) {
        return;
    }

    fprintf(stderr, "tomur_sd_upstream[%s]: %s", tomur_sd_log_level_name(level), text);
    size_t length = strlen(text);
    if (length == 0 || text[length - 1] != '\n') {
        fputc('\n', stderr);
    }
    fflush(stderr);
}

static bool tomur_sd_is_empty(const char* value) {
    return value == NULL || value[0] == '\0';
}

static const char* tomur_sd_resolve_backend(const tomur_sd_ctx_probe_params_t* probe_params) {
    if (!tomur_sd_is_empty(probe_params->backend)) {
        return probe_params->backend;
    }

    if (probe_params->keep_clip_on_cpu && probe_params->keep_vae_on_cpu) {
        return "te=cpu,vae=cpu";
    }

    if (probe_params->keep_clip_on_cpu) {
        return "te=cpu";
    }

    if (probe_params->keep_vae_on_cpu) {
        return "vae=cpu";
    }

    return probe_params->backend;
}

static const char* tomur_sd_resolve_params_backend(const tomur_sd_ctx_probe_params_t* probe_params) {
    if (!tomur_sd_is_empty(probe_params->params_backend)) {
        return probe_params->params_backend;
    }

    return probe_params->offload_params_to_cpu ? "*=cpu" : probe_params->params_backend;
}

SD_API sd_ctx_t* tomur_sd_create_ctx(const tomur_sd_ctx_probe_params_t* probe_params) {
    if (probe_params == NULL) {
        return NULL;
    }

    const char* effective_backend = tomur_sd_resolve_backend(probe_params);
    const char* effective_params_backend = tomur_sd_resolve_params_backend(probe_params);

    sd_set_log_callback(tomur_sd_log_callback, NULL);
    fprintf(stderr,
            "tomur_sd_bridge: create_ctx sd_version=%s sd_commit=%s model=%s diffusion=%s clip_l=%s clip_g=%s t5xxl=%s vae=%s llm=%s backend=%s params_backend=%s offload_cpu=%d keep_clip_cpu=%d keep_vae_cpu=%d mmap=%d fa=%d diffusion_fa=%d threads=%d\n",
            sd_version(),
            sd_commit(),
            tomur_sd_path_status(probe_params->model_path),
            tomur_sd_path_status(probe_params->diffusion_model_path),
            tomur_sd_path_status(probe_params->clip_l_path),
            tomur_sd_path_status(probe_params->clip_g_path),
            tomur_sd_path_status(probe_params->t5xxl_path),
            tomur_sd_path_status(probe_params->vae_path),
            tomur_sd_path_status(probe_params->llm_path),
            effective_backend == NULL ? "null" : effective_backend,
            effective_params_backend == NULL ? "null" : effective_params_backend,
            probe_params->offload_params_to_cpu ? 1 : 0,
            probe_params->keep_clip_on_cpu ? 1 : 0,
            probe_params->keep_vae_on_cpu ? 1 : 0,
            probe_params->enable_mmap ? 1 : 0,
            probe_params->flash_attn ? 1 : 0,
            probe_params->diffusion_flash_attn ? 1 : 0,
            probe_params->n_threads);
    fflush(stderr);

    sd_ctx_params_t native_params;
    sd_ctx_params_init(&native_params);

    native_params.model_path               = probe_params->model_path;
    native_params.diffusion_model_path     = probe_params->diffusion_model_path;
    native_params.clip_l_path              = probe_params->clip_l_path;
    native_params.clip_g_path              = probe_params->clip_g_path;
    native_params.t5xxl_path               = probe_params->t5xxl_path;
    native_params.llm_path                 = probe_params->llm_path;
    native_params.vae_path                 = probe_params->vae_path;
    native_params.enable_mmap              = probe_params->enable_mmap;
    native_params.flash_attn               = probe_params->flash_attn;
    native_params.diffusion_flash_attn     = probe_params->diffusion_flash_attn || probe_params->flash_attn;
    native_params.backend                  = effective_backend;
    native_params.params_backend           = effective_params_backend;

    if (probe_params->n_threads > 0) {
        native_params.n_threads = probe_params->n_threads;
    }

    sd_ctx_t* ctx = new_sd_ctx(&native_params);
    if (ctx == NULL) {
        fprintf(stderr, "tomur_sd_bridge: new_sd_ctx returned null\n");
        fflush(stderr);
        return NULL;
    }

    fprintf(stderr,
            "tomur_sd_bridge: create_ctx ok image_generation=%d video_generation=%d default_sampler=%s\n",
            sd_ctx_supports_image_generation(ctx) ? 1 : 0,
            sd_ctx_supports_video_generation(ctx) ? 1 : 0,
            sd_sample_method_name(sd_get_default_sample_method(ctx)));
    fflush(stderr);
    return ctx;
}

SD_API void tomur_sd_free_ctx(sd_ctx_t* sd_ctx) {
    free_sd_ctx(sd_ctx);
}
