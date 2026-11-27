# Third-party notices: voice

The voice feature (speech from text and voice cloning) uses the components below. The helper `portal-voice-server.exe`
ships in `engine\voice\`; the model files are downloaded to the host's `voice-models` folder.

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
