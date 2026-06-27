#include <stdint.h>

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

SD_API sd_ctx_t* tomur_sd_create_ctx(const tomur_sd_ctx_probe_params_t* probe_params) {
    if (probe_params == NULL) {
        return NULL;
    }

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
    native_params.backend                  = probe_params->backend;
    native_params.params_backend           = probe_params->params_backend;

    if (probe_params->n_threads > 0) {
        native_params.n_threads = probe_params->n_threads;
    }

    return new_sd_ctx(&native_params);
}

SD_API void tomur_sd_free_ctx(sd_ctx_t* sd_ctx) {
    free_sd_ctx(sd_ctx);
}
