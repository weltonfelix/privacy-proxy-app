cmake_minimum_required(VERSION 3.14...3.28)
project(pp_qwen C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POLICY_DEFAULT_CMP0077 NEW)

# llama.cpp is used from an existing clone (not vendored, not modified). Default: the clone
# next to this repository, as in the project folder layout.
set(LLAMA_CPP_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../llama.cpp" CACHE PATH "llama.cpp source checkout")
set(PP_QWEN_LLAMA_REV "8642541" CACHE STRING "llama.cpp revision this code was validated with")

find_package(Git QUIET)
if (GIT_FOUND)
    execute_process(COMMAND ${GIT_EXECUTABLE} -C "${LLAMA_CPP_DIR}" rev-parse --short=7 HEAD
                    OUTPUT_VARIABLE LLAMA_CPP_HEAD OUTPUT_STRIP_TRAILING_WHITESPACE ERROR_QUIET)
    if (NOT LLAMA_CPP_HEAD STREQUAL PP_QWEN_LLAMA_REV)
        message(FATAL_ERROR "llama.cpp at ${LLAMA_CPP_DIR} is at '${LLAMA_CPP_HEAD}', expected ${PP_QWEN_LLAMA_REV}. "
                            "Run: git -C \"${LLAMA_CPP_DIR}\" checkout ${PP_QWEN_LLAMA_REV}")
    endif()
endif()

# Same switches as the stage 4 llama-mtmd-cli Android build; static libs folded into one .so.
set(BUILD_SHARED_LIBS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(GGML_NATIVE OFF)
set(GGML_OPENMP OFF)
set(GGML_LLAMAFILE OFF)
set(LLAMA_OPENSSL OFF)
set(LLAMA_BUILD_COMMON ON)
set(LLAMA_BUILD_MTMD ON)
set(LLAMA_BUILD_TOOLS OFF)
set(LLAMA_BUILD_TESTS OFF)
set(LLAMA_BUILD_EXAMPLES OFF)
set(LLAMA_BUILD_SERVER OFF)
set(LLAMA_BUILD_APP OFF)
add_subdirectory("${LLAMA_CPP_DIR}" llama.cpp EXCLUDE_FROM_ALL)

add_library(pp_qwen SHARED src/pp_qwen.cpp)
target_include_directories(pp_qwen PUBLIC include)
target_link_libraries(pp_qwen PRIVATE llama-common mtmd llama ggml)
# export only the pp_qwen_* functions, not the symbols of the static llama.cpp libraries
target_link_options(pp_qwen PRIVATE -Wl,--exclude-libs,ALL)
set_target_properties(pp_qwen PROPERTIES CXX_VISIBILITY_PRESET hidden)

# stripped copy (~9 MB instead of ~160 MB) that Unity packs into the APK
set(PP_QWEN_UNITY_PLUGIN_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../Assets/PrivacyProxy/Plugins/Android/arm64-v8a"
    CACHE PATH "where the stripped libpp_qwen.so is copied for Unity")
add_custom_command(TARGET pp_qwen POST_BUILD
    COMMAND ${CMAKE_STRIP} --strip-unneeded -o "${PP_QWEN_UNITY_PLUGIN_DIR}/libpp_qwen.so" $<TARGET_FILE:pp_qwen>
    COMMENT "Copying stripped libpp_qwen.so to ${PP_QWEN_UNITY_PLUGIN_DIR}")

add_executable(pp_qwen_test test/pp_qwen_test.cpp)
target_link_libraries(pp_qwen_test PRIVATE pp_qwen vendor::stb)
