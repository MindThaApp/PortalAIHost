# Cloud models, model agents and video generation: product and technical plan

Status: draft for owner decisions. Every speed, size and time figure below is an **estimate to be measured** on the
owner's PC before it is promised in the UI or the Store listing.

## 1. Summary

| # | Feature | Lands in | Size |
|---|---|---|---|
| 1 | Cloud models (own API keys) | AI Portal | Medium |
| 2 | Pick who you talk to, models that consult each other | AI Portal | Medium |
| 3 | Video generation engine and models | Host, then AI Portal | Large |
| 4 | Director workflow (shot list, chaining, stitching) | AI Portal | Large |
| 5 | Models page by category | Host (in progress on another branch) | Small |
| 6 | Voice cloning and text to speech (sections 10 and 11) | Host, then Voice Portal | Medium |

Plain version: AI Portal learns to talk to cloud providers as well as local and remote-PC models. A lead model can
ask other models for help, visibly and within limits. The Host learns a new engine type for video. A "director"
uses all of this to turn an idea into a finished video, with the user approving each shot.

Terms: the **Host** is Portal AI Host (runs local models, shares them over an OpenAI-compatible API). **AI Portal** is
the separate chat and documents app that uses the Host. Since 27/11 the apps are split (section 10): the Host is the
engine, AI Portal does chat and agents, Voice Portal does voice, and a new Studio Portal does images, video and the
director.

## 2. Cloud models in AI Portal (not in the Host)

The Host stays local and private. Cloud access lives only in AI Portal, so no key ever reaches the Host, a paired
laptop or the relay.

### 2.1 What the user sees
- Settings > Models > Cloud accounts: one card per provider (Claude by Anthropic, OpenAI, Gemini), each with a key
  box, "Test key", "Remove key" and a plain note on what is sent.
- Cloud models appear in the same model picker as local models and remote-PC models, grouped by where they run
  (This PC, Other PC, Cloud) with a small badge. Cloud rows show a price hint.
- Nothing is selectable until a key is added and tested.

### 2.2 Keys
- Stored with DPAPI (current user scope), in a file written atomically. Never logged, never in crash reports, never
  in exports or backups, never shown again after saving (only the last four characters).
- Sent only to the provider's own endpoint over TLS. A fixed allow-list of three host names; no redirects followed to
  other hosts. A unit test asserts that requests with a key go nowhere else.
- Keys are per Windows user and per install. They are not synced to other PCs.

### 2.3 Making cost visible
| Where | What is shown |
|---|---|
| Model picker | Price per million input and output tokens, or "free (this PC)" |
| Before a long job | Estimate range ("about 0.20 to 0.60 USD") and a confirm step above a user-set threshold |
| In the chat | Tokens and cost on each cloud reply (details line), running total for the conversation |
| Settings > Usage | Today, this month, per provider and per model; a monthly cap with a hard stop |
| Hand-offs and video | Each cloud call made by a lead model or the director is itemised |

- Prices come from a small bundled table with a "last checked" date and a manual override. Cost shown is always an
  estimate computed from token counts the provider returns; the provider's own bill is authoritative.
- The default monthly cap is low (for example 5 USD) and must be raised by the user.
- Provider differences (tool-call format, streaming, image input) are hidden behind one `IChatBackend` interface with
  three cloud adapters plus the existing Host adapter.

## 3. Pick who you talk to, and models that consult each other

### 3.1 The idea
The user chooses a **lead model** for the conversation. The lead may call other models as tools: "ask the local
model", "ask Claude", "ask the vision model". Each hand-off is a normal tool call, so any lead that supports tool
calling can do it. The tool list is built from the models the user has enabled for this conversation.

### 3.2 Visible hand-offs
- A hand-off appears in the chat as a collapsible card: who asked whom, the question, the answer, tokens, time, cost.
- The user can expand it, copy it, or hide cards in Settings (they stay in the history either way).
- A "Consulting..." state with a Stop button shows while a helper works.

### 3.3 Limits (all adjustable in Settings)
| Limit | Default | Why |
|---|---|---|
| Depth of hand-offs (a helper asking another helper) | 1 | Stops loops; helpers get no consult tool by default |
| Consults per user message | 4 | Bounds runaway tool use |
| Same model, same question twice | Blocked | Catches loops |
| Cost per user message | 0.25 USD, then ask to continue | Cloud cost guard |
| Cost per conversation | 2 USD | Same |
| Cost per month | Cap from section 2.3 | Same |
| Time per consult | 120 s, cancellable | A stuck local model must not hang the chat |
| Cloud helpers | Ask once per conversation | The user knows when text leaves the PC |

When a limit is hit, the lead gets a clear tool result ("limit reached") and the user sees a banner.

### 3.4 Privacy
A consult to a cloud model sends only the question text the lead wrote, plus attachments the user ticked for that
consult. The card shows exactly what was sent. A conversation can be marked "local only", which removes cloud helpers
from the tool list.

### 3.5 Which models can lead
A lead must call tools reliably. The Models page marks each model with a lead rating, set by a short built-in tool
test (call a tool with a typed argument, pick between two tools, refuse when none fits) run once per model.

| Class | Lead? | Notes (estimates until tested) |
|---|---|---|
| Cloud frontier models | Yes | Best tool use; default choice for the director |
| Qwen3-30B-A3B class MoE, other recent 30B+ instruct models | Yes | Good tool calling with the Host's `--jinja` template support |
| Dense 14B instruct | Usable | Fine for one or two tools; more mistakes with many tools |
| 7-8B | Limited | Single-tool use only; shown as "helper, not lead" |
| 3-4B and below, embeddings, vision-only | No | Helpers only |

Helpers have no such restriction: any model can answer a question put to it.

## 4. Video generation

### 4.1 New engine type in the Host
The Host already has `IEngineAdapter` per engine kind, a supervised child process per role, a second model slot and
placement controls. Video becomes a third engine kind, `video`, supervised the same way (separate process, own
folder, ended to free memory). Its API is an asynchronous job API, not chat completions:

- `POST /host/video/jobs` (prompt, optional start image, size, frames, seed, steps) returns a job id.
- `GET /host/video/jobs/{id}` for progress, preview frame and result; `DELETE` cancels.
- Output is written to a Host-owned folder with unique names; nothing existing is overwritten.
- Jobs go through the same fair queue, one at a time per video engine, and respect the memory plan (section 6.4).

### 4.2 Runtime options
| Option | How it works | Packaging and Store | Licence | AMD GPU on Windows | Verdict |
|---|---|---|---|---|---|
| stable-diffusion.cpp-style native runtime | One native exe, GGUF models, same style as llama.cpp; Vulkan and CPU | Easy: exe plus DLLs in `Engine\`, same pattern as llama-server | MIT | Works through Vulkan (no CUDA or ROCm) | **Recommended first** |
| Bundled ComfyUI (embedded Python) | Node-graph runtime, widest model support, block-swap nodes | Heavy: Python, PyTorch, hundreds of files, 3-10 GB; many unsigned binaries; hard in MSIX; brittle updates | GPL-3.0 for ComfyUI, plus many third-party node licences | Needs CUDA; ROCm and DirectML builds are limited on Windows and may not support RDNA2 cards | Optional power-user add-on, downloaded not bundled |
| Python with diffusers | Reference implementation, newest models first | Same Python weight as above; needs PyTorch | Apache-2.0 (diffusers); PyTorch BSD-style | Same CUDA/ROCm problem | Not for the Store build |
| Cloud video APIs | Provider generates | None locally | Provider terms | Not applicable | Out of scope for now; could extend section 2 later |

Reasoning:
- The owner's PC has an AMD GPU (RX 6600, 8 GB, Vulkan per the product spec), so a CUDA-only runtime is out. Vulkan is
  the common path and the native runtime already supports it.
- The native runtime fits the existing MSIX constraints (no capability beyond `runFullTrust`; engine children start
  from the package folder).
- The risk is model coverage and speed: the runtime must support each target model and quantisation. A spike checks
  this before any UI work (phase 4).
- Python-based runtimes stay a possible "advanced" engine, installed by the user into a folder the app creates, never
  inside the Store package. Whether to offer it is an owner decision (section 9).
- Licences of the runtime and of every model file must be confirmed before shipping, as the product spec already
  requires for the chat catalogue.

### 4.3 Video models
| Model | Size class | Role | Notes (estimates) |
|---|---|---|---|
| Wan 2.1 1.3B | Small | Default on 8 GB GPUs; text-to-video | About 480p; draft to good quality |
| LTX-Video | Small to medium | Fast; text and image to video | Quick previews; good for the review loop |
| Wan 2.2 5B | Medium | Better quality, image-to-video capable | Needs offload on 8 GB |
| Wan 14B class | Large | High-end quality | Heavy offload on 8 GB; slow; shown with a warning |

Each catalogue entry records files, quantisation, minimum RAM and VRAM, whether image-to-video is supported, maximum
frames, licence and a speed note per hardware class. Image-to-video support is required for the chaining in section
5.2; a model without it can still make single clips.

## 5. The director

### 5.1 What it is
A workflow in AI Portal driven by a lead model (the **director**). The user describes the video; the director plans
it, calls the video engine shot by shot, looks at the results and assembles the final file. It is the section 3
consult mechanism with video tools added.

### 5.2 Steps
1. **Brief:** the director asks short questions (length, style, aspect ratio, audio) only if needed.
2. **Shot list:** a table of shots (id, duration 5-10 s, prompt, camera, notes, whether it continues the previous
   shot). The user can edit, reorder, delete or add shots, and approve the list.
3. **Generate shot 1:** text-to-video, or image-to-video from a user image. A preview is shown; the user approves,
   regenerates (new seed or edited prompt) or skips.
4. **Chain:** the last frame of an approved clip becomes the start image of the next clip (image-to-video), so
   characters and scenes stay continuous. A shot marked as a cut starts fresh.
5. **Review:** the director looks at sample frames of each clip (with a vision-capable model) and flags problems
   (wrong subject, artefacts, no motion), proposing a prompt fix. The user decides; there is a retry limit and no
   silent regeneration beyond it.
6. **Stitch:** ffmpeg joins approved clips with chosen transitions (cut, crossfade) and optional music or voice-over
   (a user file, or Host speech models later), and writes an MP4 to a new, uniquely named file.
7. **Keep:** the project folder holds the shot list, every clip, every seed and the final file, so any shot can be redone.

### 5.3 User control
- An approval gate per shot is the default; "approve automatically" is a setting, off by default.
- Every generation step shows an estimated time and, for a cloud director, an estimated cost.
- Stop is always available and cancels the running job.

### 5.4 Director choices
| Director | Strengths | Weaknesses | Cost |
|---|---|---|---|
| Cloud model (Claude) | Best shot lists, prompt writing and review; reliable tool calls | Needs a key; text and sampled frames leave the PC | Small: a few thousand tokens per shot plus a few frame images; estimate under 1 USD for a short film |
| Local model | Free and private | Weaker prompt craft and frame review; needs a vision model for review | None |

The director's output is short, so local speed matters little (section 6.2). Frame review is the weak point locally;
a local director can skip it and rely on the user's approval.

### 5.5 ffmpeg
- Shipped as a separate exe in `Engine\ffmpeg\` or downloaded on first use. Used for last-frame extraction, joins,
  transitions, audio mixing and encoding.
- An LGPL build (no GPL-only encoders) is preferred for the Store; H.264 encoding may use the Windows media encoder
  instead. Licence and patent review is an open decision (section 9).
- Runs as a child process with a time limit and writes only into the project folder.

## 6. Worked example: Ryzen 9 9900X, 64 GB DDR5, 8 GB VRAM GPU

All numbers are rough estimates from published figures for similar hardware. None have been measured on this PC.

### 6.1 Hardware facts used
- 12 cores / 24 threads. Dual-channel DDR5 gives roughly 60-90 GB/s in practice. CPU token generation is limited by
  memory bandwidth: speed is about bandwidth divided by the bytes read per token.
- GPU: 8 GB VRAM (the spec lists an AMD RX 6600 through Vulkan). Video weights that do not fit stay in RAM and move to
  the GPU block by block (offload or block swap), which costs speed but works.
- 64 GB RAM is shared by everything; Windows and apps use roughly 6-10 GB.

### 6.2 Director on CPU and RAM
| Model | Size at Q4 | Active params per token | Estimated speed | Verdict |
|---|---|---|---|---|
| 30B-A3B class MoE (for example Qwen3-30B-A3B) | about 18 GB | about 3B | about 15-30 tokens/s | Best choice: large-model quality at small-model speed |
| Dense 14B instruct | about 9 GB | 14B | about 4-7 tokens/s | Works; slower and weaker than the MoE |
| Dense 7-8B instruct | about 5 GB | 7-8B | about 8-12 tokens/s | Fast but a weak lead (section 3.5) |

- A shot list is about 500-1500 tokens: roughly one minute at 15 tokens/s. Reading input (prompt processing) is
  slower on CPU, about 50-150 tokens/s, so a long history adds seconds to minutes. Keep the director's context short:
  send the shot list and the latest notes only.
- Total director time over a ten-shot film is a few minutes, small against clip generation, so the speed difference
  between these models hardly matters.

### 6.3 Video on the 8 GB GPU
| Model | Estimated weights | Fits in 8 GB? | Estimated time per 5 s clip |
|---|---|---|---|
| Wan 2.1 1.3B at 480p | about 3-6 GB | Yes, comfortably | about 3-8 minutes |
| LTX-Video at 480p | about 4-8 GB (quantised) | Yes | about 1-4 minutes |
| Wan 2.2 5B at 480p-720p | about 10-14 GB (quantised) | With offload | about 10-25 minutes |
| Wan 14B class at 480p | about 20-30 GB (quantised) | Heavy offload | about 30-90 minutes |
| Any model on CPU only | n/a | n/a | Hours per clip; not offered as a normal option |

Times assume a draft setting (about 20-30 steps) on a mid-range 8 GB card. A ten-shot film on the small models is
roughly an hour of unattended compute; with the 14B model it is a long overnight job. The UI shows ranges and learns
from the real timings recorded on each machine.

### 6.4 The split: director on CPU while video runs on the GPU
Both run at the same time because they use different resources. The Host's second model slot and placement controls
already allow a chat engine on CPU and RAM only (no GPU layers) beside another engine.

RAM budget (estimates):
| Use | RAM |
|---|---|
| Windows, Host, AI Portal, browser | 8-10 GB |
| Director: 30B-A3B Q4 weights plus context | 18-20 GB |
| Video: offloaded weights, 5B class | 10-14 GB |
| Video: offloaded weights, 14B class | 20-30 GB |
| Total with the 5B class | about 36-44 GB (fits in 64 GB) |
| Total with the 14B class | about 46-60 GB (tight; the planner warns and may unload the director while a clip renders) |

Rules for the placement planner:
- The director engine runs with zero GPU layers and a CPU thread cap (for example 6-8 of 24 threads), so video
  generation keeps enough CPU for its offload and decoding work.
- The video engine gets the GPU. If the two together exceed the RAM limit, the planner runs them in turn: the director
  is unloaded while a clip renders and reloaded for review (about 10-30 s from disk).
- The plan is shown before starting ("This will use about 40 GB of RAM and the GPU"). Nothing starts if it would
  exceed the user's memory limit.

## 7. Models page by category (separate branch)

Another branch implements this; in short: the Host's Models page groups models by category (Chat and writing,
Coding, Reasoning, Vision, Embeddings, Image generation, Video generation, Speech), with filters (installed, fits this
PC, size), search, and a "Show all" switch for entries that do not fit this PC. Video models are listed under Video
generation as "Coming soon", with expected size and requirements, until the video engine (phase 4) exists; they cannot
be downloaded before then. When the engine ships, each entry becomes a normal download through the existing resumable,
hash-checked downloader. This plan assumes a category field exists in the catalogue; see the model organisation
section of `product-spec.md` once that branch merges.

## 8. Roadmap

| Phase | App | Content | Depends on | Size |
|---|---|---|---|---|
| 0 | Host | Models page categories (separate branch) | Existing catalogue | Small (days) |
| 1 | AI Portal | Backend interface; Claude, OpenAI and Gemini adapters; key storage; picker; usage and cost view; caps | Nothing new | Medium (2-3 weeks) |
| 2 | AI Portal | Lead selection, consult tools, hand-off cards, limits, tool-calling test and lead rating | Phase 1 for cloud helpers; works with local helpers alone | Medium (2-3 weeks) |
| 3 | Host | Placement presets for "director on CPU, other engine on GPU"; RAM budget display and refusal | Existing second slot and placement controls | Small (about 1 week) |
| 4 | Host | Spike: native video runtime with Wan 1.3B and LTX on Vulkan, measured on the owner's PC; then the `video` engine kind, job API, catalogue entries | Phase 3; the spike decides the runtime | Large (4-6 weeks including the spike) |
| 5 | AI Portal | Video panel: single clip generation, preview, queue and cancel | Phase 4 | Medium (about 2 weeks) |
| 6 | AI Portal | Director: shot list editor, chaining, review, ffmpeg stitching, project folder | Phases 2 and 5 | Large (4-6 weeks) |
| 7 | Host and AI Portal | Wan 2.2 5B and 14B with offload; time estimates from recorded runs; optional advanced runtime add-on | Phase 4 | Medium |

Order notes:
- Phases 1 and 2 give value on their own and carry no video risk, so they go first.
- Phase 4 starts with the spike. If the native runtime cannot run a target model acceptably, the fallback is the
  advanced add-on (section 4.2) or a smaller model set, decided before phase 5.
- Phases 1 to 6 target the local Host first. Remote use of video jobs and consults over the existing remote path
  comes later; large clip files need the relay and streaming limits checked before remote video is promised.

## 9. Open decisions for the owner

1. **Runtime:** start with the native runtime and treat Python-based engines as an optional add-on, or skip the add-on
   to keep the Store package simple?
2. **Cloud providers at launch:** all three (Claude, OpenAI, Gemini), or Claude first and the others after?
3. **Default cost caps:** are 5 USD per month and 0.25 USD per message acceptable starting values?
4. **Cloud consults and privacy:** ask once per conversation (proposed), or always allow once a key is added?
5. **Director default:** cloud director when a key exists and local otherwise, or local first?
6. **Video quality bar:** is 480p draft quality acceptable for v1, with 720p only on stronger hardware?
7. **ffmpeg:** ship it in the Store package (licence and patent review needed) or download it on first use?
8. **Audio:** generated speech is decided (section 11). Is generated music wanted in a later phase?
9. **Remote video:** may paired laptops run the director against the home PC's video engine in the first release, or
   later?
10. **Price tables:** bundled and updated with app releases, or fetched from a small file the app checks (needs
    approval for the network call)?

## 10. App split (owner decision 27/11)

The apps are split so that each one has one job and the engines live in one place.

| App | Role | What it does |
|---|---|---|
| **Portal AI Host** | Engine | Runs and supervises every engine (chat, embeddings, voice, later image and video), owns the model store, the shared voice library, the job queues, memory budget, placement and access control. No end-user workflow of its own. |
| **AI Portal** | Chat and agents | Chat, documents, cloud models, the lead model and consults (sections 2 and 3). Its lead model can use the Host's other engines as tools. |
| **Voice Portal** | Voice | Dictation and transcription, plus voice cloning and text to speech: the screens for making a voice from a sample and reading text with it. |
| **Studio Portal** (working name) | Images, video and the director | Image and video generation panels and the director workflow (section 5). |

How they talk to each other:
- Every app is a client of the Host. Each one calls the Host's own API (`docs/api-v1.md`) with its own access token;
  the user approves each app once. Apps do not call each other and never touch each other's files.
- **Shared by the Host:** the model store, the queues, the memory budget, and the **voice library**. A voice made in
  Voice Portal appears in `GET /host/voices` for every approved app, so AI Portal or Studio Portal can read text with it
  without holding the sample. Only the Host stores samples; apps hold voice ids.
- **The Studio director** calls the Host's chat engine (shot list, prompts), image and video engines (shots), and voice
  engine (narration or dialogue lines from `POST /host/speech/jobs`), and joins the pieces with ffmpeg in its project
  folder. It asks the Host for every step, so the Host's budget and yield rules apply to the whole workflow.
- **AI Portal's lead model** can be given the Host engines as tools (read this aloud, make this image), through the same
  API, under the same hand-off and limit rules as model consults (section 3). Tool results are shown as visible cards.
- Paired laptops reach the same Host APIs over the existing remote path. Voice routes work for paired devices (they sign
  requests like any other call); a device can change only voices its own app made.
- What the Host guarantees to all of them: one queue per engine kind, a memory budget, cancel everywhere, a consent
  record for every cloned voice, and a "made with Portal" tag on generated audio.

## 11. Voice: cloning and text-to-speech

### 11.1 The feature

The user gives an MP3 or WAV of a voice (10 to 30 seconds). The Host makes a reusable voice from it. That voice then
reads any text and produces audio. All Portal apps use the same library through the Host. Built-in fixed voices cover
the case where nobody wants to clone anything.

Requirements for the runtime: runs natively on Windows without Python, on the CPU (an 8 GB AMD GPU is optional), and is
commercially usable (code **and** model weights).

### 11.2 Candidates

Facts below come from the projects' own pages (checked 27/11/2026). Speed and size figures marked "estimate" are mine
and have to be measured on the owner's PC before they reach any UI or Store text.

| Candidate | Licence (code / weights) | Quality | CPU speed | Packaging | Verdict |
|---|---|---|---|---|---|
| **sherpa-onnx + Pocket TTS** (Kyutai, 100M parameters; listed by sherpa-onnx as English voice cloning) | sherpa-onnx Apache-2.0 / weights CC-BY-4.0 (credit needed; the model card forbids cloning "without explicit and lawful consent") | Good for its size; cloning from a few seconds of audio, no transcript needed | Kyutai reports about 6x real time on 2 cores of a MacBook Air M4 and about 2.3 to 2.5x on a 4 vCPU x86 cloud VM; estimate 3 to 6x on a Ryzen 9 9900X | C/C++ API and ONNX runtime DLLs, Windows x64 and arm64; no ready TTS server, so Portal ships a small helper; model size estimate 150 to 250 MB | **Recommended for cloning** |
| **sherpa-onnx + Kokoro-82M** (and Piper/VITS for more fixed voices) | sherpa-onnx Apache-2.0 / Kokoro Apache-2.0; Piper voices differ per voice (some are GPL or non-commercial, so only vetted voices go in the catalogue) | Very good for fixed voices; many languages; no cloning | Estimate 5 to 15x real time on the 9900X (sherpa-onnx only publishes Raspberry Pi numbers); first chunk well under a second | Same runtime and helper as above; model about 80 to 330 MB depending on quantisation (estimate) | **Recommended for fast fixed voices** |
| sherpa-onnx + ZipVoice (zero-shot flow matching) | Code Apache-2.0 / **weights CC-BY-NC** (trained on Emilia) | Good, English and Chinese | Usable on CPU with the distilled int8 model; needs the reference text as well | Same runtime | **Rejected: weights not for commercial use** |
| llama.cpp `llama-tts` with OuteTTS | llama.cpp MIT / OuteTTS 0.2 and 0.3 weights non-commercial or share-alike (CC-BY-NC, CC-BY-SA depending on version); 1.0 1B mixes Llama 3.2 licence with CC-BY-NC-SA; 1.0 0.6B is reported as Apache-2.0 (not confirmed on the official card) | OK to good | Slow: a language model plus a vocoder pass, estimate under real time on CPU for the 1B | Reuses the existing engine, but stock `llama-tts` supports only OuteTTS 0.2/0.3; 1.0 needs a patched build and a DAC encoder, and the speaker profile is made with a Python tool | **Rejected for now** (licence and tooling). Revisit if the 0.6B Apache-2.0 model gets upstream support |
| Chatterbox (Resemble AI) via ONNX | MIT code and weights (also on the ONNX export) | Best of the open models in this list (claimed; not measured here), includes an optional Perth watermark | Unknown: 0.5B Llama-style language model plus decoder; estimate below real time to about 1x on a desktop CPU | ONNX export exists as four graphs, but the only reference code is Python; a C# or C++ port of tokenizer, sampling and audio front end would be needed; large files | **Later, as a quality tier** if the spike shows Pocket TTS is not good enough |
| OpenVoice v2 via ONNX | MIT | Converts the tone colour only; keeps the base voice's accent; sounds less like the person | Converter is light (about 150 MB graph), but the base voice (MeloTTS) is not | Converter and speaker encoder exist as ONNX; the MeloTTS front end still needs Python | **Rejected** (not Python-free, weaker cloning) |
| F5-TTS | Code MIT / **official weights CC-BY-NC** (Emilia) | Very good | Heavy for CPU | PyTorch | **Rejected: weights not for commercial use** |

### 11.3 Decision

- **Cloning:** sherpa-onnx with **Pocket TTS**. It is the only candidate that is native on Windows without Python,
  runs fast enough on a CPU, has commercial-friendly licences (CC-BY-4.0 asks for credit, which goes in the app's
  about page and the catalogue entry) and clones from a short sample without a transcript.
- **Fast fixed voices:** sherpa-onnx with **Kokoro-82M**, in the same helper process.
- **One helper for both.** sherpa-onnx has no TTS server, so Portal builds a small helper (`portal-voice-server.exe`) that
  links the sherpa-onnx C API and serves a loopback HTTP API. The Host supervises it like every other engine (random
  port, per-launch key, Job Object, health, restart backoff, idle unload). A separate process keeps a crash or a model
  fault out of the Host and lets the memory limit apply.
- The decision rests on facts from project pages, not on a run. Before any download is approved, the spike has to
  measure speed and quality and confirm three things I could not read directly: the exact sherpa-onnx flags and files
  for Pocket TTS (its documentation page returned 404), the languages it supports through sherpa-onnx (the project page
  lists English), and the licence text of the exact model files. If Pocket TTS is not good enough, Chatterbox is the
  next step up (11.2).
- The GPU stays optional: everything above runs on the CPU. A DirectML or Vulkan provider can be tried later for speed;
  the generation engines have the first claim on the 8 GB card (section 6).

### 11.4 What is built in the Host (branch `voice-engine`)

All of it works without any download; tests use a fake engine and a stub process.

- **Engine kind `voice-server`:** `VoiceServerAdapter` (argument list built from a table, loopback only, same checks as
  llama-server) and `VoiceEngineClient`, which starts the supervised helper on the first request and sends it one text
  piece at a time. The helper's contract: `GET /health`; `POST /v1/speech` with
  `{ "text", "speed", "format": "wav", "voice": { "kind": "builtin", "id" } | { "kind": "clone", "sample_path" } }`,
  answering a mono WAV. The helper only reads the sample path it is given.
- **Voice library** (`GET/POST /host/voices`, `PATCH/DELETE /host/voices/{id}`): the upload is base64 inside JSON (so it
  follows the same auth and size rules as every route; about 3 MB of audio, so mono or MP3 for long WAV files). The Host
  converts it to mono 24 kHz 16-bit and stores its own copy in `voices\vc_<id>\` (`sample.wav` and `voice.json`) under a
  new unique id. The original file is never kept or touched (it is never seen: only bytes arrive). **Consent is required**
  (`own_voice` or `permission`) and stored. A delete removes only that voice's two files and then its empty folder, after
  checking that the id has the exact shape the Host generates and that the folder is a direct child of the library root.
  A voice used by a queued or running job cannot be deleted.
- **MP3 samples** are read through ffmpeg over pipes (no temporary files). Until ffmpeg is bundled (open decision 7) the
  capability says `sample_formats: ["wav"]` and MP3 uploads are refused with a clear error.
- **Speech jobs** (`POST /host/speech/jobs`, `GET .../{id}`, `GET .../{id}/audio`, `DELETE .../{id}`): a small queue with
  one worker, 8 waiting jobs per app. The text is split at sentence ends and read piece by piece, so progress is real and a
  cancel works between pieces. The result is one WAV in the Host-owned `speech-output\` folder under a unique name; no
  file is overwritten or deleted by the Host. The job plumbing is self-contained on purpose and will move onto the shared
  generation queue when the image and video branch lands.
- **Tag:** every generated WAV ends with a `LIST/INFO` chunk (`ICMT = "Made with Portal"`, `ISFT = "Portal AI Host"`). This
  is metadata, not an inaudible watermark; it is lost if audio is re-encoded. An inaudible watermark would need a model
  (Chatterbox's Perth is one) and is left as an open decision.
- **Capabilities:** `voice: { cloning, speech, formats: ["wav"], sample_formats, ... }`. `cloning` and `speech` are false
  (and `formats` empty) while the helper or the models are missing. **MP3 output is not offered**; WAV only for now.
- **Auth:** all routes need a token, so this PC's apps and signed paired devices use them the same way. Jobs are private to
  the app that made them; the library is shared; a paired device can rename or delete only voices its own app made.
- **Client library:** `GetVoiceCapabilitiesAsync`, `ListVoicesAsync`, `CreateVoiceAsync`, `RenameVoiceAsync`,
  `DeleteVoiceAsync`, `StartSpeechAsync`, `GetSpeechJobAsync`, `CancelSpeechAsync`, `DownloadSpeechAsync`, `SpeakAsync`.
- **Fetch script:** `scripts/fetch-voice.ps1` reads `engine/voice.lock.json` (helper and model archives). Every tag, asset,
  hash and size is a placeholder, and the script refuses to download until real values are pinned.
- **Not done here:** the helper itself (a small program on the sherpa-onnx C API), the real model files, the catalogue
  entries, and the placement and memory-budget hooks of the generation engines (the voice helper is small enough to start
  without them; they join when the queues are unified).

Catalogue entries for the voice models are not added to `catalogue.json`. `catalogue-gen.json` does not exist yet, so the
entries are listed here for when it does. File names, sizes, hashes and commits are filled in when the spike pins the
exact files:

```json
[
  { "id": "pocket-tts-clone", "display_name": "Pocket TTS (voice cloning)", "engine": "voice-server", "kind": "voice",
    "licence": { "spdx": "CC-BY-4.0", "url": "https://huggingface.co/kyutai/pocket-tts", "commercial_ok": true },
    "notes": "Credit Kyutai. Voice cloning only with the speaker's consent. Folder: voice-models\\cloning." },
  { "id": "kokoro-82m-voices", "display_name": "Kokoro (built-in voices)", "engine": "voice-server", "kind": "voice",
    "licence": { "spdx": "Apache-2.0", "commercial_ok": true },
    "notes": "Fixed voices, no cloning. Folder: voice-models\\fixed." }
]
```

### 11.5 Safety and consent

- Every voice has a stored consent value; the Host refuses to create one without it. Apps must ask the user, in plain words,
  whose voice it is, and must not clone a voice without the speaker's permission (this is also the licence condition of the
  Pocket TTS weights).
- Generated audio carries the "Made with Portal" tag. Apps should say that a voice is generated wherever audio is shared.
- Voices and samples stay on the PC. The Host never uploads them, and the library is removable in one place.
- The API exposes no way to read a stored sample back, only to read text with a voice.

### 11.6 Open decisions for the owner

1. Approve the spike (a download of sherpa-onnx, Pocket TTS and Kokoro, and measuring on the 9900X) to confirm quality and
   speed before the helper is built.
2. Ship ffmpeg (for MP3 samples) in the Store package, or accept WAV samples only at first (open decision 7).
3. Offer MP3 output later through ffmpeg (the capability list is built for it), or stay WAV only.
4. Add an inaudible watermark (needs a model) on top of the metadata tag.
5. Pocket TTS covers English in sherpa-onnx today. Are other languages needed in the first release (Kokoro covers several for
   fixed voices)?
