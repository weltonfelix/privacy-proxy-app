# Native: Qwen library for Quest 3

`libpp_qwen.so` is a small C++ library that runs Qwen3-VL-2B (Q4_K_M) on the Quest 3 CPU
(arm64-v8a). It wraps llama.cpp and libmtmd (the part of llama.cpp that reads images).

**You do not need to build it.** A ready copy is already in
`Assets/PrivacyProxy/Plugins/Android/arm64-v8a/libpp_qwen.so`, and Unity puts it in the APK.
Only rebuild it if you change the C++ code.

## API

Plain C functions, declared in [`include/pp_qwen.h`](include/pp_qwen.h):

| Function | What it does |
|---|---|
| `pp_qwen_load(model, mmproj)` | Loads the model GGUF and the mmproj (vision part). |
| `pp_qwen_describe(rgb, width, height, prompt, text, size, &seconds)` | Describes an RGB image (3 bytes per pixel, top row first). |
| `pp_qwen_cancel()` | Asks the current description to stop. Safe from any thread. |
| `pp_qwen_free()` | Frees all model memory. |
| `pp_qwen_last_error()` | Readable message for the last error (per thread). |

Return codes: `0` ok, `1` error, `2` cancelled. Call `load`, `describe` and `free` one at a time.
Settings: temperature 0, at most 96 new tokens, context 4096.

The C# side is in `Assets/PrivacyProxy/Scripts/Description/` (`QwenNative`, `QwenClient`).
In the Unity Editor, `QwenNative` uses a fake Qwen that waits 15 s and returns a fixed answer.

## Rebuilding

You need:

- a clone of [llama.cpp](https://github.com/ggml-org/llama.cpp) at revision **`8642541`**
  (CMake stops with an error if the revision is different);
- the Android NDK that ships with Unity 6000.0.66f2 (r27c).

From this `Native/` folder:

```sh
NDK=/Applications/Unity/Hub/Editor/6000.0.66f2/PlaybackEngines/AndroidPlayer/NDK
cmake -B build-android \
  -DLLAMA_CPP_DIR=/path/to/llama.cpp \
  -DCMAKE_TOOLCHAIN_FILE=$NDK/build/cmake/android.toolchain.cmake \
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-32 \
  -DCMAKE_BUILD_TYPE=Release
cmake --build build-android -j 8
```

This writes the stripped library straight into `Assets/PrivacyProxy/Plugins/Android/arm64-v8a/`
and also builds a command-line test program, `build-android/pp_qwen_test`
(source in [`test/pp_qwen_test.cpp`](test/pp_qwen_test.cpp)).
