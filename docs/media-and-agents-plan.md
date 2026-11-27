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

Plain version: AI Portal learns to talk to cloud providers as well as local and remote-PC models. A lead model can
ask other models for help, visibly and within limits. The Host learns a new engine type for video. A "director"
uses all of this to turn an idea into a finished video, with the user approving each shot.

Terms: the **Host** is Portal AI Host (runs local models, shares them over an OpenAI-compatible API). **AI Portal** is
the separate chat and documents app that uses the Host.

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

In `catalogue/catalogue-gen.json` today (signed on its own, `available: false` until the engine lands): `wan2.1-t2v-1.3b`
(three pinned parts: diffusion model, VAE, text encoder) and the image model `sd-1.5`.

To add after measurement (no verified single-file builds pinned yet, so not in the catalogue):

- Wan 2.2 TI2V 5B (Apache-2.0): about 12 GB graphics memory with CPU offload, 24 GB recommended.
- LTX-Video 2B distilled (OpenRAIL-M): about 8 GB with CPU offload, 12 GB recommended.
- Wan 2.1 T2V 14B, high-end (Apache-2.0): about 16 GB with CPU offload, 24 GB recommended.
- Wan 2.2 T2V A14B, high-end (Apache-2.0): about 24 GB with CPU offload, 48 GB recommended.

The figures are estimates until measured on the engine.

Each catalogue entry records files, quantisation, minimum RAM and VRAM, whether image-to-video is supported, maximum
frames, licence and a speed note per hardware class. Image-to-video support is required for the chaining in section
5.2; a model without it can still make single clips.

### 4.4 Engine as built (phase 4)
- **One process per job, not a resident server.** The host runs the stable-diffusion.cpp command-line program
  (`engine\sdcpp\<flavour>\sd-cli.exe`, or `sd.exe` on older releases) once per job, in its own Job object, at
  below-normal priority. Reasons: a job lasts seconds to hours, so reloading weights (seconds) costs little; ending the
  process gives back every byte of VRAM and RAM, so a chat model can use the card between jobs; cancel is a kill of
  the job's own process tree; a driver fault ends one job, not a shared server. The runtime's server mode is newer,
  its HTTP API changes between releases, and it reports neither step progress nor cancellation the host could rely
  on. "Loaded" for this engine therefore means: binary present, model files checked, memory plan reserved.
- **Image and video share one engine kind and one queue** (`generation` in the fair queue): on one GPU two jobs
  would only fight over VRAM. The job API is in [api-v1.md](api-v1.md), "Image and video jobs".
- **Memory plan.** The generation planner tries, in order: everything on the card; text encoders on the processor
  (`--clip-on-cpu`); encoders and VAE on the processor (`--vae-on-cpu`); weights in RAM streamed to the card
  (`--offload-to-cpu`); processor only (pictures, or video when the owner chose processor-only). VAE tiling is on
  for video and for pictures above one megapixel, flash attention for video. The share of offloaded weights assumed
  on the card at once (25 %) and the compute-buffer estimates are guesses until `scripts\measure-gen.ps1` has been
  run on the owner's PC.
- **Flags.** Every sd.cpp flag lives in one table (`src\Portal.AiHost.Engine\SdCppArgs.cs`). Not yet checked against
  the pinned release: the run modes `img_gen` and `vid_gen`, `--high-noise-diffusion-model`, `--clip_vision`,
  `--flow-shift`, `--video-frames`, `--fps`, `--offload-to-cpu`, `--diffusion-fa` and the preview flags (sent only when
  switched on). Check them with `sd-cli --help` after the first fetch.
- **Catalogue.** An image or video entry may carry `generation` (default size, steps, cfg, sampler, flow shift,
  frames, fps, `image_to_video`, `max_side`, `compute_bytes`) and a `role` per file (`model`, `diffusion`,
  `high_noise`, `clip_l`, `clip_g`, `t5xxl`, `vae`, `clip_vision`). Without them the roles are guessed from file
  names and the defaults come from the model family. Supported families: SD 1.5, SD 2, SDXL, SD3 and FLUX for
  pictures; Wan for video. LTX-Video is not supported by this runtime.
- **Fetch.** `scripts\fetch-sdcpp.ps1` installs a pinned release into `engine\sdcpp\vulkan` (and an optional
  processor build) after a SHA-256 check; `engine\sdcpp.lock.json` holds the tag and hashes, which are placeholders
  until the owner approves the download. The MSIX build includes the engine only when it is present.

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
8. **Audio:** user-supplied music and voice only at first, or add generated speech and music in a later phase?
9. **Remote video:** may paired laptops run the director against the home PC's video engine in the first release, or
   later?
10. **Price tables:** bundled and updated with app releases, or fetched from a small file the app checks (needs
    approval for the network call)?
