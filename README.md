# Privacy Proxy

Privacy Proxy is a mixed reality app for the Meta Quest 3. It lets you ask an external AI
(OpenAI) a question about your workbench **without showing it a sensitive object**.

You point at the object you want to hide. The headset then, **on its own, without internet**:

1. cuts the object out of the photo with **EdgeSAM** (an AI that finds the outline of an object);
2. writes a short, generic description of it with **Qwen3-VL 2B** (a small AI that reads images),
   for example "electronic circuit board";
3. covers the object with a gray card that shows only that description.

Only this protected photo (or the original, if you allow it) is sent to OpenAI, together with
your question. You still see the real object on the headset.

Academic project, CIn/UFPE.

---

## How the app works (the 5 screens)

| Screen | What you do |
|---|---|
| 1. Question | Pick a question with the `<` `>` buttons, or tap it to type your own. Press **Capturar** to take a photo. |
| 2. Selection | Mark two opposite corners of a rectangle around the sensitive object, then **Confirmar seleção**. |
| 3. Description | The app draws the object's outline and **Gerar descrição** creates the short description locally. |
| 4. Review | Compare what you see with what will be shared. Choose **Manter versão protegida** (recommended) or **Permitir foto original**. |
| 5. Answer | The chosen photo and your question go to OpenAI and the answer appears. **Nova foto** starts again. |

On the headset you aim with the **right controller** (cyan cursor) and click with the **trigger**.
To move the panel, aim at the bar under it, hold the trigger and move.

The app's text is in Portuguese.

---

## What you need

| Item | Version / note |
|---|---|
| Meta Quest 3 or 3S | Horizon OS v74 or newer, with **developer mode** on |
| USB-C cable | to install the app and copy files |
| Unity Hub + **Unity 6000.0.66f2** | with the **Android Build Support** module (includes the SDK, NDK and `adb`) |
| Free disk space | about 15 GB (Unity cache + models) |
| OpenAI API key | only for screen 5; everything else works without internet |

Unity packages (installed automatically when the project opens): Meta MR Utility Kit 85.0.0,
Unity Inference Engine 2.2.1, OpenXR 1.15.1. See `Packages/manifest.json`.

---

## Installation

### 1. Open the project in Unity

1. In Unity Hub, click **Add > Add project from disk** and choose this folder.
2. Open it with Unity **6000.0.66f2**. The first opening takes a while (Unity downloads the
   packages and builds its cache in `Library/`).
3. If Unity asks, accept the Meta / OpenXR suggestions. You can also use
   **Meta > Tools > Project Setup Tool** and click **Fix All**.
4. Open the scene **`Assets/PrivacyProxy/Scenes/PrivacyProxy.unity`**. It is the only scene.

### 2. Prepare the Quest 3

1. Turn on developer mode for the headset in the Meta Horizon phone app
   (Devices > your headset > Developer mode).
2. Connect the headset to the computer with the USB cable and, inside the headset, accept
   **Allow USB debugging**.

### 3. Build and install the app

1. In Unity, open **File > Build Profiles**, choose **Android** and click **Switch Platform**
   (only the first time).
2. Check that the scene list has only `PrivacyProxy`.
3. Choose your Quest under **Run Device** and click **Build And Run**. Save the APK anywhere
   (for example a `Builds/` folder).
4. When the app opens on the headset, **allow camera access** (headset camera permission).

The app id is `br.ufpe.cin.privacyproxy`.

### 4. Models

There are two AI models:

**EdgeSAM (outline of the object): already included.** The ONNX files are in
`Assets/PrivacyProxy/Models/` (`edge_sam_3x_encoder.onnx` and `edge_sam_3x_decoder.onnx`) and go
inside the APK. Nothing to do.

**Qwen3-VL 2B (description): download separately.** These files are too big for the APK
(about 1.5 GB), so they are copied to the headset once:

1. Download these two files from Hugging Face,
   [Qwen/Qwen3-VL-2B-Instruct-GGUF](https://huggingface.co/Qwen/Qwen3-VL-2B-Instruct-GGUF)
   (tested revision `52d6c8ffea26cc873ac5ad116f8631268d7eb503`):
   - `Qwen3VL-2B-Instruct-Q4_K_M.gguf` (1.1 GB)
   - `mmproj-Qwen3VL-2B-Instruct-Q8_0.gguf` (445 MB)
2. Put both in the **`QwenModels/`** folder at the root of this project.
3. Install the app and open it once on the headset (it creates its own folder), then close it.
4. With the headset on the cable, run in the Terminal, from this project folder:

   ```sh
   tools/copy-models.sh
   ```

   (If the files are somewhere else: `tools/copy-models.sh /path/to/folder`.)
5. Open the app. On the first launch it checks the files (about 25 s). After that it is fast.

The app checks the size and fingerprint (SHA-256) of each file against
`Assets/PrivacyProxy/Resources/qwen_models.json`. If a file is missing or wrong, it shows a message.

Qwen runs through [llama.cpp](https://github.com/ggml-org/llama.cpp), compiled into a small
library that is already in the project (`Assets/PrivacyProxy/Plugins/Android/arm64-v8a/libpp_qwen.so`).
You do not need to compile anything. To rebuild it, see [`Native/README.md`](Native/README.md).

### 5. OpenAI key (screen 5 only)

The key is **never** stored in the project or in the APK. It is a text file copied to the headset.

1. Create a key at [platform.openai.com/api-keys](https://platform.openai.com/api-keys).
2. Save it in a file with only the key on one line, for example
   `~/.config/privacy-proxy/openai_key.txt`.
3. With the app installed and opened once, and the headset on the cable, run:

   ```sh
   tools/copy-openai-key.sh
   ```

   (Or `tools/copy-openai-key.sh /path/to/openai_key.txt`.) The script never prints the key.

Without the key, screens 1 to 4 work normally and screen 5 shows an error.

The model and limits are in `Assets/PrivacyProxy/Resources/openai_config.json`
(default `gpt-5.4-mini`, low reasoning effort, 60 s timeout, 400 output tokens).
Do not put the key in that file.

---

## Running in the Unity Editor (without a headset)

Press **Play** in the `PrivacyProxy` scene. The headset camera and Qwen do not exist on the
computer, so the Editor uses:

- an example photo (`Assets/PrivacyProxy/Textures/source.png`) instead of the camera;
- an example mask (`Textures/lower_assembly.png`) instead of EdgeSAM;
- a fake Qwen that waits 15 s and returns a fixed description;
- on screen 5, OpenAI if the key is in the `OPENAI_API_KEY` environment variable, otherwise
  an example answer.

Use the mouse to click the buttons. In the Editor, each capture is saved in `Saida/` at the
project root.

### Simulating errors

The `PrivacyProxyFlow` object in the scene has a **Privacy Proxy Error Simulator** component.
Its checkboxes (camera permission denied, empty mask, Qwen failure, missing model files) let
you see the app's error messages. Leave them all off for normal use.

### Automated tests

Open **Window > General > Test Runner**, tab **EditMode**, and click **Run All**. The tests are
in `Assets/PrivacyProxy/Editor/Tests/` and do not need a headset or internet.

---

## Logs on the headset

The app writes timing logs to
`/sdcard/Android/data/br.ufpe.cin.privacyproxy/files/logs/`:

- `flow-screen-times.jsonl`: time spent on each screen;
- `etapas.jsonl`: time of each step (EdgeSAM, Qwen, OpenAI).

Each capture (photo, mask, sent image, answer) is saved in the same app folder, under `Saida/`.
Copy them with `adb pull /sdcard/Android/data/br.ufpe.cin.privacyproxy/files/ .`

---

## Project structure

```
Assets/PrivacyProxy/
  Scenes/PrivacyProxy.unity      the app (only scene)
  Scripts/App/                   the 5 screens, error panel, controller pointer
  Scripts/Segmentation/          EdgeSAM (object outline)
  Scripts/Description/           Qwen (local description) and model file check
  Scripts/Image/                 crop, outline and protected image with the gray card
  Scripts/ExternalAi/            OpenAI request (screen 5)
  Scripts/Logging/               timing logs and capture folders
  Models/                        EdgeSAM ONNX files
  Plugins/Android/arm64-v8a/     libpp_qwen.so (Qwen through llama.cpp)
  Prefabs/                       headset camera access (from Meta's samples)
  Resources/                     questions, OpenAI settings, Qwen file list, example answer
  Textures/, TestData/           example images for the Editor and the tests
  Editor/Tests/                  automated tests
Native/                          C++ source of libpp_qwen.so (optional)
tools/                           scripts to copy the models and the key to the headset
QwenModels/                      put the Qwen .gguf files here (not shared)
```

## Privacy rules kept by the app

- EdgeSAM and Qwen run only on the headset.
- Only one image leaves the device: the one reviewed on screen 4 (protected by default),
  sent to OpenAI with the question. Crops, masks and the unreviewed photo are never sent.
- The description is only a broad category: no brands, text, codes or invented details.
- The key and the image are never written to the logs.

## Troubleshooting

- **Black panel or no camera image:** allow the camera permission for Privacy Proxy in the
  headset settings and use a real headset. The Meta XR Simulator does not support
  the passthrough camera.
- **"adb not found":** use the one that comes with Unity, or pass it:
  `ADB=/path/to/adb tools/copy-models.sh`.
- **Message about missing or wrong model files:** run `tools/copy-models.sh` again.
- **Slow description sometimes (20-30 s):** probably the headset getting hot. Let it cool down.

## License

The team's own code (everything in `Assets/PrivacyProxy/`, `Native/` and `tools/`) is not currently released under an open-source license.

This project started from Meta's Unity Passthrough Camera API Samples. The material that comes from Meta (Meta SDK settings, the camera access prefab and other supporting files) is under the Oculus License in [`LICENSE.txt`](LICENSE.txt), which applies only to that material.
