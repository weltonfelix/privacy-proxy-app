// Command-line check of the four pp_qwen calls, meant to run on the Quest through adb.
// Usage: pp_qwen_test <model.gguf> <mmproj.gguf> <crop.png> [options]
//   --prompt TEXT         instruction (default: the stage 4 short-category prompt)
//   --repeat N            describe the same crop N times with one load (default 1)
//   --expect TEXT         exit with code 3 if any finished run returns other text
//   --cancel-after-ms MS  call pp_qwen_cancel from another thread MS ms into each run

#include "pp_qwen.h"

#define STB_IMAGE_STATIC
#define STB_IMAGE_IMPLEMENTATION
#include "stb/stb_image.h"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>

static const char * k_default_prompt =
    "Name the main object or assembly using only a broad, generic category. "
    "Return one short noun phrase. No explanation, brands, model names, codes, or function.";

// Resident memory (KiB) of this process, from /proc/self/status.
static long rss_kib(const char * key = "VmRSS:") {
    FILE * f = std::fopen("/proc/self/status", "r");
    if (!f) {
        return -1;
    }
    char line[256];
    long value = -1;
    while (std::fgets(line, sizeof(line), f)) {
        if (std::strncmp(line, key, std::strlen(key)) == 0) {
            value = std::atol(line + std::strlen(key));
            break;
        }
    }
    std::fclose(f);
    return value;
}

static const char * status_name(int code) {
    switch (code) {
        case PP_QWEN_OK:        return "ok";
        case PP_QWEN_CANCELLED: return "cancelado";
        default:                return "erro";
    }
}

int main(int argc, char ** argv) {
    if (argc < 4) {
        std::fprintf(stderr, "usage: %s <model.gguf> <mmproj.gguf> <crop.png> [--prompt TEXT] [--repeat N] "
                             "[--expect TEXT] [--cancel-after-ms MS]\n", argv[0]);
        return 2;
    }
    const char * model  = argv[1];
    const char * mmproj = argv[2];
    const char * image  = argv[3];
    std::string prompt  = k_default_prompt;
    const char * expect = nullptr;
    int repeat          = 1;
    int cancel_ms       = -1;
    for (int i = 4; i + 1 < argc; i += 2) {
        if (!std::strcmp(argv[i], "--prompt")) {
            prompt = argv[i + 1];
        } else if (!std::strcmp(argv[i], "--repeat")) {
            repeat = std::atoi(argv[i + 1]);
        } else if (!std::strcmp(argv[i], "--expect")) {
            expect = argv[i + 1];
        } else if (!std::strcmp(argv[i], "--cancel-after-ms")) {
            cancel_ms = std::atoi(argv[i + 1]);
        } else {
            std::fprintf(stderr, "unknown option: %s\n", argv[i]);
            return 2;
        }
    }

    // same decoding as mtmd_helper_bitmap_init_from_file: stb_image forced to RGB
    int w = 0, h = 0, n = 0;
    unsigned char * rgb = stbi_load(image, &w, &h, &n, 3);
    if (!rgb) {
        std::fprintf(stderr, "could not read image: %s\n", image);
        return 2;
    }

    std::printf("[mem] start: VmRSS=%ld KiB\n", rss_kib());
    auto t0 = std::chrono::steady_clock::now();
    if (pp_qwen_load(model, mmproj) != PP_QWEN_OK) {
        std::fprintf(stderr, "load failed: %s\n", pp_qwen_last_error());
        stbi_image_free(rgb);
        return 1;
    }
    double load_s = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
    std::printf("[load] %.3f s, VmRSS=%ld KiB\n", load_s, rss_kib());

    int exit_code = 0;
    char text[4096];
    for (int run = 1; run <= repeat; run++) {
        std::thread canceller;
        if (cancel_ms >= 0) {
            canceller = std::thread([cancel_ms] {
                std::this_thread::sleep_for(std::chrono::milliseconds(cancel_ms));
                pp_qwen_cancel();
            });
        }
        double seconds = 0;
        const int res = pp_qwen_describe(rgb, w, h, prompt.c_str(), text, sizeof(text), &seconds);
        if (canceller.joinable()) {
            canceller.join();
        }
        std::printf("[run %d/%d] estado=%s tempo=%.3f s VmRSS=%ld KiB texto=\"%s\"\n",
                    run, repeat, status_name(res), seconds, rss_kib(), text);
        if (res == PP_QWEN_ERROR) {
            std::fprintf(stderr, "describe failed: %s\n", pp_qwen_last_error());
            exit_code = 1;
            break;
        }
        if (res == PP_QWEN_OK && expect && std::strcmp(text, expect) != 0) {
            std::fprintf(stderr, "text differs from expected \"%s\"\n", expect);
            exit_code = 3;
        }
    }

    pp_qwen_free();
    std::printf("[free] VmRSS=%ld KiB, peak VmHWM=%ld KiB\n", rss_kib(), rss_kib("VmHWM:"));
    stbi_image_free(rgb);
    return exit_code;
}
