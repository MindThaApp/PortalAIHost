# Third-party notices

The app uses the components below. Voice features (speech from text and voice cloning) and image and video generation
each use the components listed under their headings. Model files are downloaded by the user to the host's model folders;
none is bundled with the app. The helper `portal-voice-server.exe` ships in `engine\voice\`.

## sherpa-onnx

Speech runtime used by the voice helper (org.k2fsa.sherpa.onnx 2.0.3, with ONNX Runtime).
Licence: Apache License 2.0. Copyright the k2-fsa authors. https://github.com/k2-fsa/sherpa-onnx
ONNX Runtime: MIT licence, Copyright Microsoft Corporation.

## Kokoro-82M (built-in voices)

Text-to-speech model, multi-language v1.0 export for sherpa-onnx.
Licence: Apache License 2.0. https://huggingface.co/hexgrad/Kokoro-82M

## Pocket TTS (voice cloning)

Text-to-speech model by Kyutai. The model weights are licensed under Creative Commons Attribution 4.0 (CC-BY-4.0):
https://creativecommons.org/licenses/by/4.0/ . Credit: "Pocket TTS" by Kyutai, https://huggingface.co/kyutai/pocket-tts .
The ONNX export code is licensed under Apache License 2.0 (https://github.com/KevinAHM/pocket-tts-onnx-export).
The weights are used unchanged apart from the ONNX export. Use voice cloning only with the speaker's consent.

## stable-diffusion.cpp

Image and video generation runtime (sd-cli.exe, Vulkan and CPU builds), built from the leejet/stable-diffusion.cpp project.
Licence: MIT License. Copyright (c) 2023 leejet. https://github.com/leejet/stable-diffusion.cpp

## ggml

Tensor library used by stable-diffusion.cpp and the llama.cpp runtime.
Licence: MIT License. Copyright (c) 2023-2026 The ggml authors. https://github.com/ggml-org/ggml

## Wan 2.1 Text-to-Video 1.3B (video generation)

Text-to-video model by Alibaba Wan-AI. Downloaded by the user, not bundled. Credit: "Wan 2.1" by Alibaba Wan-AI,
https://huggingface.co/Wan-AI/Wan2.1-T2V-1.3B .
Licence: Apache License 2.0 (https://huggingface.co/Wan-AI/Wan2.1-T2V-1.3B/blob/main/LICENSE.txt).

## Stable Diffusion 1.5 (image generation)

Text-to-image model originally by Runway and CompVis. Downloaded by the user, not bundled.
Licence: CreativeML OpenRAIL-M (https://huggingface.co/spaces/CompVis/stable-diffusion-license). The licence's use
restrictions apply to the model and to what it generates, including its list of prohibited uses.

## llama.cpp

Chat and embedding runtime (llama-server.exe) used by the host.
Licence: MIT License. Copyright the llama.cpp authors. https://github.com/ggml-org/llama.cpp

## libwebp and libwebm

Image and video file libraries shipped with the stable-diffusion.cpp build (libwebp.dll, webm.dll).
Licence: BSD 3-Clause. Copyright (c) 2010, Google Inc. The full licence texts ship next to the files as libwebp.txt
and libwebm.txt. https://chromium.googlesource.com/webm/libwebp

## UMT5-XXL text encoder (used with Wan 2.1)

Text encoder by Google (umt5-xxl), in the GGUF conversion by city96. Downloaded by the user, not bundled.
Licence: Apache License 2.0. https://huggingface.co/google/umt5-xxl , https://huggingface.co/city96/umt5-xxl-encoder-gguf
