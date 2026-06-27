#include "whisper.h"

WHISPER_API int tomur_whisper_full_with_params(
    struct whisper_context * ctx,
    struct whisper_full_params * params,
    const char * language,
    bool detect_language,
    bool translate,
    const float * samples,
    int n_samples) {
    if (ctx == NULL || params == NULL || samples == NULL || n_samples < 0) {
        return -1;
    }

    struct whisper_full_params configured = *params;
    const char * effective_language = language;

    if (effective_language != NULL && effective_language[0] == '\0') {
        effective_language = NULL;
    }

    configured.translate = translate;
    configured.detect_language = detect_language;

    if (detect_language) {
        configured.language = NULL;
    } else if (effective_language != NULL) {
        configured.language = effective_language;
    }

    return whisper_full(ctx, configured, samples, n_samples);
}