// Follows tools/mtmd/mtmd-cli.cpp (llama.cpp 8642541) single-turn path step by step, so
// the output matches `llama-mtmd-cli --temp 0 -n 96 -c 4096 --image <crop> -p <prompt>`.

#include "pp_qwen.h"

#include "arg.h"
#include "chat.h"
#include "common.h"
#include "llama.h"
#include "log.h"
#include "mtmd-helper.h"
#include "mtmd.h"
#include "sampling.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <clocale>
#include <cstring>
#include <memory>
#include <sched.h>
#include <string>
#include <vector>

namespace {

struct qwen_state {
    common_params              params;
    common_init_result_ptr     llama_init;
    mtmd::context_ptr          ctx_vision;
    common_chat_templates_ptr  tmpls;
    common_sampler           * smpl = nullptr;
    llama_model              * model = nullptr;
    llama_context            * lctx = nullptr;
    const llama_vocab        * vocab = nullptr;

    ~qwen_state() { common_sampler_free(smpl); }
};

std::unique_ptr<qwen_state> g_state;
std::atomic<bool>           g_cancel{false};
std::atomic<bool>           g_running{false};
thread_local std::string    g_error;

int fail(const std::string & msg) {
    g_error = msg;
    LOG_ERR("pp_qwen: %s\n", msg.c_str());
    return PP_QWEN_ERROR;
}

int ok() {
    g_error.clear();
    return PP_QWEN_OK;
}

// Prompt + image -> KV cache, same as eval_message() in mtmd-cli.cpp.
int eval_prompt(qwen_state & s, mtmd_bitmap * bitmap, const std::string & prompt, llama_pos & n_past) {
    common_chat_msg msg;
    msg.role    = "user";
    msg.content = mtmd_default_marker() + prompt;
    const std::string formatted = common_chat_format_single(s.tmpls.get(), {}, msg, true, s.params.use_jinja);

    // split on the media marker and interleave text and image parts
    const std::string marker = mtmd_default_marker();
    const size_t pos = formatted.find(marker);
    if (pos == std::string::npos || formatted.find(marker, pos + marker.size()) != std::string::npos) {
        return fail("the instruction must not contain the media marker " + marker);
    }
    const std::string before = formatted.substr(0, pos);
    const std::string after  = formatted.substr(pos + marker.size());
    mtmd_input_text texts[2] = {
        {before.data(), before.size(), /* add_special */ false, /* parse_special */ true},
        {after.data(),  after.size(),  /* add_special */ false, /* parse_special */ true},
    };
    const mtmd_input_part parts[3] = {{&texts[0], nullptr}, {nullptr, bitmap}, {&texts[1], nullptr}};
    const mtmd_input_part * parts_ptr[3] = {&parts[0], &parts[1], &parts[2]};

    mtmd::input_chunks chunks(mtmd_input_chunks_init());
    // mbatch must not outlive the chunks it references
    mtmd::batch_ptr mbatch;
    if (mtmd_tokenize_from_parts(s.ctx_vision.get(), chunks.ptr.get(), parts_ptr, 3, /* add_bos */ true) != 0) {
        return fail("could not tokenize the prompt with the image");
    }

    const size_t n_chunks = mtmd_input_chunks_size(chunks.ptr.get());
    for (size_t i = 0; i < n_chunks; i++) {
        auto chunk = mtmd_input_chunks_get(chunks.ptr.get(), i);
        llama_pos new_n_past = n_past;
        if (mtmd_input_chunk_get_type(chunk) == MTMD_INPUT_CHUNK_TYPE_TEXT) {
            if (mtmd_helper_eval_chunk_single(s.ctx_vision.get(), s.lctx, chunk, n_past, 0, s.params.n_batch,
                                              i == n_chunks - 1, &new_n_past) != 0) {
                return g_cancel ? PP_QWEN_CANCELLED : fail("could not evaluate the prompt text");
            }
        } else {
            // one image only, so each media chunk gets its own batch
            mbatch.reset(mtmd_batch_init(s.ctx_vision.get()));
            if (mtmd_batch_add_chunk(mbatch.get(), chunk) != 0 || mtmd_batch_encode(mbatch.get()) != 0) {
                return g_cancel ? PP_QWEN_CANCELLED : fail("could not encode the image");
            }
            float * embd = mtmd_batch_get_output_embd(mbatch.get(), chunk);
            if (!embd || mtmd_helper_decode_image_chunk(s.ctx_vision.get(), s.lctx, chunk, embd, n_past, 0,
                                                        s.params.n_batch, &new_n_past, nullptr, nullptr) != 0) {
                return g_cancel ? PP_QWEN_CANCELLED : fail("could not decode the image");
            }
        }
        n_past = new_n_past;
        if (g_cancel) {
            return PP_QWEN_CANCELLED;
        }
    }
    return PP_QWEN_OK;
}

} // namespace

extern "C" {

int pp_qwen_load(const char * model_path, const char * mmproj_path) {
    if (g_state) {
        return fail("model already loaded; call pp_qwen_free first");
    }
    if (!model_path || !mmproj_path || !*model_path || !*mmproj_path) {
        return fail("model_path and mmproj_path are required");
    }

    std::setlocale(LC_NUMERIC, "C");
    static bool common_ready = false;
    if (!common_ready) {
        ggml_time_init();
        common_init();
        mtmd_helper_log_set(common_log_default_callback, nullptr);
        ggml_backend_load_all();
        common_ready = true;
    }

    auto s = std::make_unique<qwen_state>();

    // Same flags as the stage 4 CLI test, parsed the same way, so every default matches.
    std::vector<std::string> args = {"pp_qwen", "-m", model_path, "--mmproj", mmproj_path,
                                     "--temp", "0", "-n", "96", "-c", "4096"};
    // One thread per core this process may use: Horizon OS pins immersive apps to 3 of the
    // 6 cores, and the llama.cpp default (6) makes the threads fight for them (~4x slower).
    cpu_set_t cpus;
    if (sched_getaffinity(0, sizeof(cpus), &cpus) == 0) {
        args.insert(args.end(), {"-t", std::to_string(CPU_COUNT(&cpus))});
    }
    std::vector<char *> argv;
    for (auto & a : args) {
        argv.push_back(a.data());
    }
    if (!common_params_parse((int) argv.size(), argv.data(), s->params, LLAMA_EXAMPLE_MTMD)) {
        return fail("invalid parameters");
    }

    s->llama_init = common_init_from_params(s->params);
    s->model = s->llama_init->model();
    s->lctx  = s->llama_init->context();
    if (!s->model || !s->lctx) {
        return fail(std::string("could not load model: ") + model_path);
    }
    s->vocab = llama_model_get_vocab(s->model);
    // stop llama_decode between graph nodes once pp_qwen_cancel is called, not only between tokens
    llama_set_abort_callback(s->lctx, [](void *) { return g_cancel.load(); }, nullptr);
    s->smpl  = common_sampler_init(s->model, s->params.sampling);
    if (!s->smpl) {
        return fail("could not create the sampler");
    }

    mtmd_context_params mparams = mtmd_context_params_default();
    mparams.use_gpu          = false;
    mparams.print_timings    = true;
    mparams.n_threads        = s->params.cpuparams.n_threads;
    mparams.flash_attn_type  = s->params.flash_attn_type;
    mparams.warmup           = s->params.warmup;
    mparams.image_min_tokens = s->params.image_min_tokens;
    mparams.image_max_tokens = s->params.image_max_tokens;
    {
        // non-causal models need the whole image in one ubatch (copied from mtmd-cli.cpp)
        const int n_ubatch = llama_n_ubatch(s->lctx);
        auto mem = mtmd_get_memory_usage(mmproj_path, mparams);
        if (mem.use_non_causal && mem.image_max_tokens > n_ubatch) {
            mparams.image_max_tokens = n_ubatch;
            mparams.image_min_tokens = std::min(mparams.image_min_tokens, n_ubatch);
        }
    }
    s->ctx_vision.reset(mtmd_init_from_file(mmproj_path, s->model, mparams));
    if (!s->ctx_vision) {
        return fail(std::string("could not load vision projector: ") + mmproj_path);
    }
    if (!mtmd_support_vision(s->ctx_vision.get())) {
        return fail("the mmproj file has no vision support");
    }
    if (!llama_model_chat_template(s->model, nullptr)) {
        return fail("the model has no chat template");
    }
    s->tmpls = common_chat_templates_init(s->model, s->params.chat_template);

    g_state = std::move(s);
    return ok();
}

int pp_qwen_describe(const uint8_t * rgb, int width, int height, const char * prompt,
                     char * out_text, int out_text_size, double * out_seconds) {
    const auto t0 = std::chrono::steady_clock::now();
    auto finish = [&](int code) {
        g_running = false;
        if (out_seconds) {
            *out_seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
        }
        return code;
    };

    if (out_text && out_text_size > 0) {
        out_text[0] = '\0';
    }
    // clear before marking running, so a cancel that sees g_running lands after the clear
    g_cancel  = false;
    g_running = true;

    if (!g_state) {
        return finish(fail("model not loaded; call pp_qwen_load first"));
    }
    if (!rgb || width <= 0 || height <= 0) {
        return finish(fail("image is empty or has an invalid size"));
    }
    if (!prompt) {
        return finish(fail("prompt is required"));
    }
    if (!out_text || out_text_size <= 0) {
        return finish(fail("out_text buffer is required"));
    }

    qwen_state & s = *g_state;

    // Start from an empty context every call, like a fresh CLI run.
    llama_memory_clear(llama_get_memory(s.lctx), true);
    common_sampler_reset(s.smpl);

    mtmd::bitmap bitmap(mtmd_bitmap_init((uint32_t) width, (uint32_t) height, rgb));
    if (!bitmap.ptr) {
        return finish(fail("could not create the image bitmap"));
    }

    llama_pos n_past = 0;
    const int prompt_res = eval_prompt(s, bitmap.ptr.get(), prompt, n_past);
    if (prompt_res == PP_QWEN_CANCELLED) {
        g_error.clear();
        return finish(PP_QWEN_CANCELLED);
    }
    if (prompt_res != PP_QWEN_OK) {
        return finish(PP_QWEN_ERROR);
    }

    // Token loop from generate_response() in mtmd-cli.cpp, plus the cancel check.
    common_batch batch(s.lctx);
    llama_tokens generated;
    bool cancelled     = false;
    bool decode_failed = false;
    for (int i = 0; i < s.params.n_predict; i++) {
        if (g_cancel) {
            cancelled = true;
            break;
        }
        const llama_token id = common_sampler_sample(s.smpl, s.lctx, -1);
        common_sampler_accept(s.smpl, id, true);
        if (llama_vocab_is_eog(s.vocab, id)) {
            break;
        }
        generated.push_back(id);

        batch.clear();
        batch.add(id, n_past++, 0, true);
        if (llama_process(s.lctx, LLAMA_PROCESS_TYPE_DECODE, batch.get())) {
            if (g_cancel) {
                cancelled = true;
                break;
            }
            decode_failed = true;
            break;
        }
    }

    // Always hand back what was generated, cut to fit the buffer on a UTF-8 boundary.
    const std::string text = common_detokenize(s.lctx, generated);
    size_t            n    = std::min(text.size(), (size_t) out_text_size - 1);
    while (n > 0 && n < text.size() && ((unsigned char) text[n] & 0xC0) == 0x80) {
        n--;
    }
    std::memcpy(out_text, text.data(), n);
    out_text[n] = '\0';

    if (decode_failed) {
        return finish(fail("failed to decode a token"));
    }
    if (cancelled) {
        g_error.clear();
        return finish(PP_QWEN_CANCELLED);
    }
    if (n < text.size()) {
        return finish(fail("out_text buffer too small (" + std::to_string(text.size() + 1) + " bytes needed)"));
    }
    return finish(ok());
}

int pp_qwen_cancel(void) {
    // shortcut: a cancel that reads g_running just as one describe ends can land on the next one; add a run id if that matters
    if (g_running) {
        g_cancel = true;
    }
    return ok();
}

int pp_qwen_free(void) {
    g_state.reset();
    return ok();
}

const char * pp_qwen_last_error(void) {
    return g_error.c_str();
}

} // extern "C"
