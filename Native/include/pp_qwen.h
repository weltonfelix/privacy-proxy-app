// Plain C API around llama.cpp + libmtmd for Qwen3-VL on Quest 3 (CPU only).
// Meant to be called from Unity through P/Invoke. One model at a time.
// pp_qwen_load, pp_qwen_describe and pp_qwen_free must run one at a time (never at the
// same time), from any thread; pp_qwen_cancel can be called from any thread at any time.
#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define PP_QWEN_API __attribute__((visibility("default")))

// Every function returns one of these codes.
enum {
    PP_QWEN_OK        = 0,
    PP_QWEN_ERROR     = 1, // see pp_qwen_last_error()
    PP_QWEN_CANCELLED = 2, // only pp_qwen_describe: stopped by pp_qwen_cancel()
};

// Loads the language model (GGUF) and its vision projector (mmproj GGUF).
PP_QWEN_API int pp_qwen_load(const char * model_path, const char * mmproj_path);

// Describes an RGB image (3 bytes per pixel, row by row, no padding) with the given
// instruction. Same settings as the stage 4 CLI test: greedy (temp 0), at most 96 new
// tokens, context 4096.
// out_text receives the generated text (NUL-terminated; partial on cancel or error, cut to fit);
// out_text_size is the buffer size in bytes. out_seconds receives the wall time of the
// call (may be NULL). Returns PP_QWEN_OK, PP_QWEN_ERROR or PP_QWEN_CANCELLED.
PP_QWEN_API int pp_qwen_describe(const uint8_t * rgb, int width, int height, const char * prompt,
                                 char * out_text, int out_text_size, double * out_seconds);

// Asks the running pp_qwen_describe to stop. The language model stops within its current
// compute step; the vision encoder cannot be interrupted, so a cancel during image encoding
// takes effect when it ends. Has no effect if nothing is running.
PP_QWEN_API int pp_qwen_cancel(void);

// Frees the model, the vision projector and all buffers. Safe to call twice.
PP_QWEN_API int pp_qwen_free(void);

// Human-readable message for the last PP_QWEN_ERROR (empty string after success).
// Kept per thread: read it on the thread that got the error, before its next call.
PP_QWEN_API const char * pp_qwen_last_error(void);

#ifdef __cplusplus
}
#endif
