# Third-party notices

The app uses the components below. Voice features (speech from text and voice cloning) and image and video generation
each use the components listed under their headings. Model files are downloaded by the user to the host's model folders;
none is bundled with the app. The helper `portal-voice-server.exe` ships in `engine\voice\`.

## sherpa-onnx

Speech runtime used by the voice helper (org.k2fsa.sherpa.onnx 2.0.3, with ONNX Runtime).
Licence: Apache License 2.0. Copyright the k2-fsa authors. https://github.com/k2-fsa/sherpa-onnx
ONNX Runtime: MIT licence, Copyright Microsoft Corporation.

## ONNX Runtime with DirectML (picture upscaling)

Model runtime used by the upscale helper `portal-upscale.exe`, which ships in `engine\upscale\` (Microsoft.ML.OnnxRuntime.DirectML 1.23.0, with DirectML).
Licence: MIT, Copyright Microsoft Corporation. https://github.com/microsoft/onnxruntime
The upscale models are downloaded by the user to the host's model folders; none is bundled.

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

## FFmpeg

Video and audio tools (ffmpeg.exe, ffprobe.exe and the FFmpeg shared libraries in engine\ffmpeg) used for MP4 output,
last-frame pictures, joining clips and reading MP3 voice samples. Credit: the FFmpeg developers, https://ffmpeg.org .
Licence: GNU Lesser General Public License version 3 or later (LGPL-3.0-or-later); the build is configured with
--enable-version3 and without GPL-only or non-free components (no libx264). The licence text ships as
engine\ffmpeg\LICENSE.txt. The libraries are shared (DLLs next to the programs), so they can be replaced by a build
of the same FFmpeg version.
Build: BtbN/FFmpeg-Builds, release autobuild-2026-11-27-12-42 (ffmpeg-n8.1.3-41-ga8029b6528-win64-lgpl-shared-8.1.zip),
https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-11-27-12-42 .
Source code of this exact build: FFmpeg release n8.1.3, https://github.com/FFmpeg/FFmpeg/tree/n8.1.3 (the build adds
the commits up to a8029b6528, listed in the FFmpeg repository history), with the build recipe and patches in
https://github.com/BtbN/FFmpeg-Builds . H.264 video is encoded with the Windows Media Foundation encoder that Windows
provides (h264_mf), not with a bundled encoder; AAC audio uses FFmpeg's own encoder.
