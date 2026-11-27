# Hardware backends, upscaling and video quality: design

Status: design, not built. Date: 2026-11-27. Related: [media-and-agents-plan.md](media-and-agents-plan.md) (sections 4,
6, 10), [api-v1.md](api-v1.md) ("Image and video jobs", "MP4 output, last picture and joins").

Speed and size figures are **estimates** from published figures for similar hardware unless marked "measured".
`scripts\measure-gen.ps1` (and a new `scripts\measure-upscale.ps1`) replace them with real numbers.

## 1. Summary

- **Backends:** llama.cpp and stable-diffusion.cpp get NVIDIA CUDA builds next to Vulkan and CPU. Auto order is
  CUDA, Vulkan, CPU; Settings can override it per engine.
- **On-demand flavours:** the MSIX keeps today's Vulkan and CPU builds. CUDA builds (about 0.6-1 GB with the CUDA
  runtime) download only on an NVIDIA PC after the user agrees, pinned by SHA-256 in a manifest inside the signed
  package (no catalogue re-sign for engines). A new card is found by a fingerprint change and offered its flavour.
- **Upscaling:** routes `/host/image/upscales` and `/host/video/upscales`, tiers Fast, Balanced, Best. Fast and
  Balanced run Real-ESRGAN-class models in a small Host helper on ONNX Runtime (DirectML on any DX12 card, CPU
  everywhere). Best (SeedVR2, then FlashVSR) is an optional CUDA-only add-on for cards with 12 GB or more.
- **Video upscale** works in segments (ffmpeg decode, upscale, encode, join) and resumes from the last finished one.
- **MP4 quality:** encoders this PC really runs (probed), presets Fast/Balanced/Best, custom quality or bitrate.

## 2. Hardware detection and backend choice

### 2.1 What the Host detects
| Fact | Source | Notes |
|---|---|---|
| Adapters, vendor id, LUID, dedicated VRAM, driver string | DXGI (`Dxgi.cs`, `WindowsHardwareProbe`) | Exists today. Vendor ids: 0x10DE NVIDIA, 0x1002 AMD, 0x8086 Intel. |
| NVIDIA driver version, CUDA driver version, compute capability | NVML (`nvml.dll`, installed with the NVIDIA driver in System32), P/Invoke | New. No download. Missing NVML means "no usable CUDA". |
| Vulkan device and free memory | `llama-server --list-devices` on the Vulkan build | Exists (`RuntimeProbe.ProbeVulkanAsync`). |
| CUDA device and free memory | `llama-server --list-devices` on the CUDA build | Same parser; prefix `CUDA0:` instead of `Vulkan0:`. |
| Smart App Control state | Registry `HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy` | Affects downloaded unsigned DLLs (2.5). |

The probe runs off the UI thread at start, when the hardware fingerprint changes, and on "Check again" in Settings.
Results are cached per fingerprint plus driver version.

### 2.2 Automatic choice
The selector returns an ordered list of candidates per engine; the first one that loads wins.

| Hardware | llama.cpp | stable-diffusion.cpp | Upscaler helper |
|---|---|---|---|
| NVIDIA, driver new enough, compute capability 6.1+ | CUDA, then Vulkan, then CPU | CUDA, then Vulkan, then CPU | DirectML, then CPU |
| NVIDIA, CUDA flavour not installed | Vulkan (offer CUDA download), then CPU | Vulkan, then CPU | DirectML, then CPU |
| AMD (owner's RX 6600) | Vulkan, then CPU | Vulkan, then CPU | DirectML, then CPU |
| Intel Arc or iGPU | Vulkan, then CPU (iGPU under 2 GB budget: CPU) | Vulkan, then CPU | DirectML, then CPU |
| No GPU, or arm64 | CPU (`cpu-arm64` on arm64) | CPU | CPU |

- Several cards: the card with the most dedicated VRAM, as today. CUDA device order can differ from DXGI order, so
  the Host matches by name and memory from `--list-devices` and passes `--device CUDA<n>`.
- Not planned: AMD HIP builds (the RX 6600, gfx1032, is not in AMD's Windows support list) and Intel SYCL builds
  (large oneAPI runtime, little gain over Vulkan on Arc).

### 2.3 Settings override
`BackendPreference` (Auto, Gpu, Cpu) becomes per engine with explicit values: Auto, NVIDIA CUDA, Vulkan, Processor.
A choice that is not installed or not usable is shown disabled with the reason in its tooltip. A forced choice that
fails to load falls back as in 2.4 and shows an InfoBar; it never silently changes the saved setting.

### 2.4 Fallback when a build fails
- Generalise today's `VulkanMarkedBad` to a per-flavour mark keyed by `(flavour, engine tag, driver version)`.
- A load failure or a crash during load marks the flavour bad and retries the next candidate once.
- Known failure texts map to plain messages: missing DLL (exit 0xC0000135) means "engine files incomplete, download
  again"; "CUDA driver version is insufficient" means "update the NVIDIA driver to <min> or later".
- A mark clears when the driver version or the pinned engine changes, or on "Try again" in Settings.
- sd.cpp runs one process per job, so a CUDA failure there fails that job's start, marks the flavour bad and the job
  is retried once on Vulkan with `plan.backend` showing what really ran.

### 2.5 Driver and security checks
- The manifest records a minimum NVIDIA driver per CUDA flavour (from NVIDIA's CUDA compatibility table at pin
  time; indicative: CUDA 12.x builds need driver 528 or later, 12.8 builds about 570, CUDA 13 builds about 580).
  An older driver hides the CUDA choice and the tooltip names the version needed.
- CUDA 13 drops Maxwell, Pascal and Volta. A CUDA 12.x build (12.8 or later, so Blackwell RTX 50 is covered) is the
  one line pinned first; compute capability below 6.1 gets Vulkan.
- Downloaded engine DLLs are unsigned. With Smart App Control on, Windows may block them. The Host detects this,
  marks CUDA "blocked by Windows" and keeps Vulkan, and says so in the tooltip. Package-signed engines are not affected.

## 3. Engine flavours and downloads

### 3.1 What ships where
| Flavour | Where | Size, zip (unpacked) | Licence |
|---|---|---|---|
| llama.cpp Vulkan, CPU, CPU arm64 (b12600) | MSIX, as today | 39 MB, 25 MB, 13 MB (measured, lock file) | MIT |
| sd.cpp Vulkan (master-1142-908965d) | MSIX when fetched, as today | 31 MB (measured) | MIT |
| ffmpeg LGPL shared (n8.1.3) | MSIX, as today | 82 MB (measured) | LGPL-3.0-or-later |
| Upscale helper + ONNX Runtime DirectML | MSIX (our own code) | about 25-35 MB | MIT (ONNX Runtime), DirectML redistributable licence |
| llama.cpp CUDA 12.x | Download on demand | about 150-250 MB (400-600 MB) | MIT |
| sd.cpp CUDA 12.x | Download on demand | about 100-300 MB (300-700 MB) | MIT |
| CUDA runtime (cudart, cuBLAS, cuBLASLt) for 12.x | Download on demand, shared by both engines | about 350-450 MB (about 700 MB) | NVIDIA CUDA EULA, Attachment A |
| Best-tier add-on (Python, PyTorch CUDA, SeedVR2 code) | Download on demand, optional | about 3-5 GB | Python PSF, PyTorch BSD-3, SeedVR2 Apache-2.0 |
| Upscaler models | Catalogue download | 5 MB to 67 MB each; SeedVR2 3B about 6-7 GB | per model (section 4) |

The Store accepts packages far larger than this, but every user downloads the base package. Keeping CUDA out of it
saves about 0.6-1 GB for the AMD, Intel and CPU users who cannot use it.

### 3.2 Pins and the flavour manifest
- Each flavour gets a lock entry in `engine\*.lock.json` (tag, asset, SHA-256, size), fetched by the existing
  `scripts\fetch-*.ps1` for development, as today.
- New: `build-msix.ps1` writes `engine\flavours.json` into the package from the lock files. Per flavour: id
  (`llama-cuda12`, `sd-cuda12`, `cudart12`, `upscale-best`), URL, SHA-256, size, unpacked size, expected file list
  with per-file SHA-256, minimum driver, compute capability range, licence id and notice file, and `requires`
  (`llama-cuda12` requires `cudart12`).
- The manifest is trusted because it is inside the signed package. Engine pins therefore change with an app update,
  not a catalogue re-sign. Only upscaler **models** use the catalogue (3.4).
- Upstream deletes old release assets after a while (see the ffmpeg lock note). Keep a copy of each pinned zip with
  the release artefacts, and optionally list it as a second URL (open decision 1).

### 3.3 Download and install
1. Ask first (size and licence link shown); check free disk space (zip plus unpacked plus 10 %).
2. Download with the model downloader's resumable, incrementally hashed `.part` file, from the pinned URLs only.
3. Hash mismatch: delete the Host's own `.part`, report, stop. Never extract an unverified zip.
4. Extract into `engines\<flavour>\<tag>-<sha8>.staging\` (a folder the Host creates, with a `.portal-engine`
   marker). Reject zip entries with `..`, absolute paths or links. Check every file against the file list.
5. Rename the staging folder to `engines\<flavour>\<tag>-<sha8>\` in one step, then probe it (2.1).
6. An older version folder is removed only after the new one probed fine and no job uses it.

`engines\` lives in the Host's data folder (packaged LocalState). A full-trust packaged app may start executables from
there. Children get the shared CUDA runtime folder prepended to their `PATH`, so cudart is stored once.

### 3.4 Catalogue impact (owner re-sign)
Upscaler models need catalogue entries. To leave `catalogue.json` and `catalogue-gen.json` untouched, they go in a
new signed file `catalogue\catalogue-upscale.json` with engine `portal-upscale` (ONNX models) or
`portal-upscale-best` (SeedVR2, FlashVSR). Older hosts ignore unknown engines, as the schema already says. The schema
gains `kind: "upscale"`, `upscale: { arch, scale, domain: photo|anime|video, tile, temporal }`. The owner signs the new
file with `scripts\sign-catalogue.ps1`. Until then the entries stay `available: false`.

### 3.5 CUDA runtime licence
NVIDIA's CUDA EULA lets an application redistribute the files listed in Attachment A (including cudart, cuBLAS and
cuBLASLt) in binary form, unmodified, as part of an application that adds real functionality, with NVIDIA's notice
included and end-user terms at least as protective as NVIDIA's. The Host shows the notice before the first CUDA
download and lists it in the third-party notices. This is a summary, not legal advice: the owner confirms the terms
(open decision 2). The llama.cpp and sd.cpp CUDA binaries themselves are MIT.

## 4. Upscaling engines compared

### 4.1 Runtimes for Fast and Balanced
| Option | Hardware | Quality range | Maintenance | Size | Licence | Verdict |
|---|---|---|---|---|---|---|
| ONNX Runtime + DirectML, in a Host helper exe | Any DX12 card (AMD, NVIDIA, Intel) and CPU | Any ONNX super-resolution model (compact, RRDB, SwinIR, SPAN) | Active; DirectML is in maintenance but stable | about 25-35 MB | MIT | **Chosen** |
| realesrgan-ncnn-vulkan | Vulkan only, no CPU path | Real-ESRGAN models only | Last release 2022 | about 5 MB plus models | BSD-3-Clause | Not chosen: no CPU, unmaintained |
| sd.cpp built-in ESRGAN (`--upscale-model`) | Same as sd.cpp (Vulkan, CUDA, CPU) | ESRGAN RRDB family only | Active | none extra | MIT | Not chosen as the main path; a stand-alone upscale mode must be checked in `sd-cli --help` of the pin. Useful as "upscale after generate" later |

Later options: the ONNX Runtime CUDA provider (needs cuDNN, about 1 GB more) only if DirectML on NVIDIA measures too
slow; Windows ML (vendor providers installed by Windows 11 24H2+) when the minimum Windows version allows.

The helper is a separate process per job (`engine\upscale\portal-upscale.exe`), like sd-cli: a driver fault ends one
job, and ending it frees all VRAM. It reads raw RGB frames on stdin and writes them on stdout, with a small JSON
control line per frame, so ffmpeg can feed it through pipes. Tiles overlap by 16-32 px and are blended, so seams do
not move between frames.

### 4.2 Models (licences checked against the paid-app rule)
| Tier | Model | Arch, scale | Size | Licence | Use |
|---|---|---|---|---|---|
| Fast | realesr-general-x4v3 | SRVGG compact, x4 (denoise strength mix) | about 5 MB | BSD-3-Clause | Photos and video, least flicker |
| Fast | realesr-animevideov3 | SRVGG compact, x2-x4 | about 2-5 MB | BSD-3-Clause | Animation and illustration video |
| Balanced | RealESRGAN_x4plus | RRDB, x4 | about 67 MB | BSD-3-Clause | Photos, stills, sharp video |
| Balanced | RealESRGAN_x4plus_anime_6B | RRDB 6-block, x4 | about 18 MB | BSD-3-Clause | Illustration stills |
| Balanced (option) | SwinIR real-world x4 | Transformer | about 50-140 MB | Apache-2.0 | Higher still-image quality, slower |
| Best | SeedVR2 3B (7B on 24 GB+) | One-step diffusion video restoration | about 6-7 GB (3B, fp16) | Apache-2.0 (verify weights card at pin) | Video and stills, temporally consistent |
| Best | FlashVSR | One-step streaming diffusion VSR on Wan 2.1 1.3B | about 3-6 GB | Verify at pin (Wan 2.1 base is Apache-2.0) | Faster Best on 12-16 GB |

**Excluded** because their terms forbid or restrict commercial use: STAR (built on CogVideoX weights with restrictive
terms), Upscale-A-Video (S-Lab License 1.0, non-commercial), CodeFormer face restoration (S-Lab, non-commercial),
4x-UltraSharp and other OpenModelDB models under CC BY-NC(-SA), and any model whose card is silent on licence.
Real-ESRGAN weights are BSD-3-Clause; their training sets have research-oriented terms, which is a known grey area
the owner should accept or reject (open decision 5).

ONNX files: the catalogue pins either a published ONNX export or one the owner converts and publishes in a release
of a public repo (open decision 5). Each file's SHA-256 is pinned like any model.

### 4.3 Speed estimates (per output frame at 1920x1080, upscaling from 480p-540p)
| Hardware | Fast (compact) | Balanced (RRDB x4) | Best (SeedVR2 3B) | Best (FlashVSR) |
|---|---|---|---|---|
| CPU, Ryzen 9 9900X, 12 threads | 0.5-1.5 s | 8-20 s | not offered | not offered |
| AMD RX 6600 8 GB, DirectML | 0.05-0.15 s | 0.6-1.5 s | not offered (no CUDA) | not offered |
| NVIDIA RTX 3060 12 GB | 0.04-0.1 s | 0.4-1 s | 5-15 s, block swap, tight | 1-3 s |
| NVIDIA RTX 4070-4080 12-16 GB | 0.02-0.06 s | 0.2-0.5 s | 3-8 s | 0.5-1.5 s |
| NVIDIA RTX 4090 / 5090 24-32 GB | 0.01-0.03 s | 0.1-0.3 s | 1-3 s (7B: 3-6 s) | 0.1-0.4 s |

Example: a 10 s clip at 24 fps is 240 frames. Fast on the 9900X: about 2-6 minutes. Balanced on the RX 6600: about
3-6 minutes. Balanced on CPU: about 30-80 minutes. Stills: one 1 MP image to 4 MP takes under 1 s on a GPU in Fast,
about 2-10 s on CPU in Balanced.

### 4.4 Tier visibility
- Before a job, the Host estimates time from the per-machine rate (a 2-second benchmark tile the first time a model
  runs, then learned from real jobs) and shows a range.
- A tier is offered when its estimate for the request is under a limit: video at most 20x clip length (Settings,
  adjustable), stills always. Otherwise it is listed with `too_slow` and clients hide it.
- Best is offered only with a working CUDA flavour, 12 GB VRAM or more (24 GB for the 7B model) and 32 GB RAM or
  more; otherwise `needs_hardware`. Fast on CPU is always offered.

### 4.5 Temporal flicker
- Frame-by-frame models are deterministic (no noise), and the tile grid is fixed per clip, which removes most
  shimmer. Fast uses the video-trained compact models.
- "Steady" (on by default for video) adds a light ffmpeg post-filter on the upscaled frames (`deflicker` plus mild
  temporal `hqdn3d`), applied during segment encode, so no extra pass.
- Best-tier models are temporally consistent by design. SeedVR2 runs in overlapping windows (batches of 4n+1
  frames, overlap blended).

## 5. API additions

All routes need a token, follow api-v1 rules (errors, fair queue, per-app ownership: another app's job is
`404 not_found`) and are additive; older hosts leave the new capability fields out.

### 5.1 Uploads (inputs bigger than a JSON body)
| Route | What it does |
|---|---|
| `POST /host/uploads` | Raw body (`Content-Type` image/png, image/jpeg, image/webp, video/mp4, video/quicktime, video/webm, video/x-matroska). Stored in the Host's `uploads\` folder under a new unique name. `201` with `{ "id": "upl_...", "object": "upload", "bytes", "content_type", "expires_at" }`. |
| `GET /host/uploads/{id}` | The upload's record (no content). |
| `DELETE /host/uploads/{id}` | Deletes this app's upload (the Host's copy only). |

Limits: `limits.max_upload_bytes` (default 8 GiB on this PC; 0 for paired devices in v1, open decision 6), at most
4 open uploads per app, expiry 24 h after last use. Each upload is checked with ffprobe in a child process (time
limit, Job object) before any job uses it: `400 unsupported_media` when the container or codec is not readable.

### 5.2 Upscale jobs
| Route | What it does |
|---|---|
| `POST /host/image/upscales`, `POST /host/video/upscales` | Checks and queues. `202` with the job (`object: "generation.upscale"`) and `Location`. |
| `POST /host/{kind}/upscales/estimate` | Same body; returns `{ "tier", "model", "plan", "estimate", "available", "reason" }` without queueing. |
| `GET /host/{kind}/upscales`, `GET /host/{kind}/upscales/{id}` | This app's jobs, newest first; one job. |
| `DELETE /host/{kind}/upscales/{id}` | Cancels; answers once ended. |
| `POST /host/video/upscales/{id}/resume` | Resumes a `paused` job from its last finished segment. |
| `GET /host/{kind}/upscales/{id}/result`, `.../preview` | The file (Range requests supported for video); latest upscaled frame as PNG. |

Request (every field but `source` optional):
```json
{ "source": { "upload": "upl_..." }, "tier": "balanced", "model": "realesrgan-x4plus",
  "scale": 2, "target_height": 1080, "backend": "auto", "steady": true, "denoise": 0.5,
  "range": { "start_seconds": 0, "end_seconds": 12.5 }, "audio": "copy",
  "encode": { "codec": "h264", "encoder": "auto", "preset": "balanced", "rate": { "mode": "quality", "value": 70 } },
  "output": { "format": "mp4" } }
```
- `source`: exactly one of `upload` (an upload id), `job` (a finished image or video job of this app), `join` (a
  finished join), or for images only `data_base64` (within `max_request_bytes`). Paths, folders and URLs are
  `400 invalid_request`.
- `tier`: `fast`, `balanced` (default), `best`. `model` overrides the tier's default with an installed upscaler.
- `scale` 2, 3 or 4, or `target_height` (720-4320; width follows the aspect ratio). Models run at their native scale
  and the result is resized with ffmpeg `lanczos` to the target.
- `backend`: `auto`, `gpu` or `cpu`, limited by the PC owner's Settings.
- Video only: `steady`, `range`, `audio` (`copy` default, `none`, `aac`), `encode` (section 6). Image only:
  `output.format` `png` (default), `jpeg` or `webp`, `output.quality` 1-100.
- Limits: video at most 4320 lines out, 30 minutes in, 120 fps (`400 unsupported_parameter`, `param`).

Job (adds to the generation job fields):
```json
{ "id": "ups_9b2e...", "object": "generation.upscale", "kind": "video", "tier": "balanced",
  "model": "realesrgan-x4plus", "state": "running", "stage": "upscaling", "progress": 0.31,
  "frames_done": 74, "frames_total": 240, "segments_done": 1, "segments_total": 5,
  "estimate": { "seconds_low": 180, "seconds_high": 360, "measured": true },
  "plan": { "backend": "directml", "device": "AMD Radeon RX 6600", "gpu_bytes": 1342177280, "cpu_bytes": 805306368, "tile": 512 } }
```
- `state` adds `paused` (gaming mode, ease-off, or the Host restarted mid-job), with `paused_reason`.
- `stage`: `probing`, `upscaling`, `encoding`, `joining`, `saving`.
- `result`: `url`, `content_type`, `file_name`, `bytes`, `width`, `height`, and for video `duration_seconds`,
  `encoder`, `fps`.
- Errors: `503 engine_missing`, `404 model_not_installed`, `503 insufficient_memory`, `507 insufficient_disk`
  (`details.needed_bytes`), `409 tier_unavailable` (`details.reason`), `429 queue_full`.

### 5.3 Capability flags
`/host/capabilities` and `/host/status` gain `upscale` (`image`, `video`, `backends`, `tiers` such as
`{ "fast": "ready", "balanced": "ready", "best": "needs_hardware" }`), `hardware` (`gpus` with `name`, `vendor`,
`vram_bytes`, `driver`; `backends` per engine, e.g. `{ "chat": "vulkan", "generation": "vulkan", "upscale":
"directml" }`; `available_flavours`, `downloadable_flavours`) and `limits.max_upload_bytes`.
Tier values (open set): `ready`, `no_model`, `no_engine`, `needs_hardware`, `needs_download`, `too_slow`, `disabled`.
Endpoints listed: `uploads`, `image.upscales`, `video.upscales`, `media.encoders`. Paired devices see `hardware`
without the driver string.

### 5.4 Scheduling and memory
- One GPU job at a time: an upscale planned on the GPU takes the `generation` slot (shared with image and video jobs).
- A CPU-planned upscale runs in the processor lane with joins (one at a time, thread cap from Settings,
  below-normal priority), so it never blocks the GPU queue.
- Memory plan: weights plus tile activations (helper reports its peak after the benchmark tile); the plan is
  reserved like generation jobs, chat and embedding engines included. Best tier needs block swap and checks RAM too.
- Gaming mode and ease-off pause a video job at the next segment boundary (at most one segment of work is lost).

### 5.5 Video pipeline and resume
1. Probe the source; normalise variable frame rate to constant at the source's average rate.
2. Split into segments of about 2-4 s (whole frames). For each segment: ffmpeg decodes the frame range to raw RGB on a
   pipe, the helper upscales, a second ffmpeg encodes the segment (closed GOP, chosen encoder) into the job's work
   folder. A state file (AtomicFile) records each finished segment and its SHA-256.
3. After the last segment: concat with `-c copy`, add the source's audio (copy, or AAC when the container needs it),
   write the result into `generated\upscaled\` under a new unique name.
4. Resume: on restart or `resume`, segments whose files match the state file are kept; the first missing one is
   redone. Unfinished jobs persist in `jobs\upscale\` (unlike generation jobs, which end with the Host).

## 6. ffmpeg quality settings API

### 6.1 Encoder probe
`ffmpeg -encoders` only shows what is compiled in. The Host also runs a 0.1 s test encode per hardware encoder
(`-f lavfi -i color=s=256x256:d=0.1 -c:v <enc> -f null -`, 5 s limit) off the UI thread, cached per hardware
fingerprint and driver. Example: on the RX 6600, `h264_amf` and `hevc_amf` pass and `av1_amf` fails (AV1 encode needs
RDNA3), so it is not offered. NVENC AV1 needs RTX 40 or later; QSV AV1 needs Arc or newer Intel chips.

`GET /host/media/encoders` lists `data` entries (`id`, `codec`, `hardware`, `vendor`, `rate_modes`, `ten_bit`,
`max_height`) and `auto` per codec, e.g. `{ "h264": "h264_amf", "hevc": "hevc_amf", "av1": "libsvtav1" }`.

### 6.2 The `encode` object
Used by video jobs with `container: "mp4"`, joins and video upscales. Omitted: Settings defaults (Auto, H.264,
Balanced).
- `codec`: `h264` (default, plays everywhere), `hevc`, `av1`, `vp9`.
- `encoder`: `auto` or an id from 6.1; an encoder not in the probed list is `400 unsupported_parameter`.
- `preset`: `fast`, `balanced`, `best`.
- `rate`: `{ "mode": "quality", "value": 1-100 }` (mapped to the encoder's CRF/CQ/QP range, higher is better) or
  `{ "mode": "bitrate", "kbps": 12000, "max_kbps": 16000 }`. Omitted: the preset's quality value.
- `ten_bit` (hevc, av1 only, when the encoder supports it), `keyframe_seconds` (default 2).
- `container`: `mp4` (default) or `webm` for vp9 and av1.
- The job's `result.encoder` reports what really ran. If a hardware encoder fails mid-job, the segment is redone with
  the next encoder in the auto order and the job records `encoder_fallback: true`.

### 6.3 Preset mapping (starting values; tuned by a measured test clip)
| Encoder | Fast | Balanced | Best | Quality knob |
|---|---|---|---|---|
| `h264_nvenc` / `hevc_nvenc` / `av1_nvenc` | `-preset p2` | `-preset p5 -tune hq` | `-preset p7 -tune hq -multipass fullres` | `-rc vbr -cq` 28/23/19 (av1 34/30/26) |
| `h264_amf` / `hevc_amf` / `av1_amf` | `-quality speed` | `-quality balanced` | `-quality quality` | `-rc cqp -qp_i/-qp_p` 28/24/20 |
| `h264_qsv` / `hevc_qsv` | `-preset veryfast` | `-preset medium` | `-preset veryslow` | `-global_quality` 28/24/20 |
| `h264_mf` / `hevc_mf` | `-scenario`, `-hw_encoding 1` | same | same | `-rate_control quality -quality` 60/75/88 (today: 80) |
| `libsvtav1` | `-preset 10` | `-preset 7` | `-preset 4` | `-crf` 38/32/26 |
| `libaom-av1` | `-cpu-used 8` | `-cpu-used 6` | `-cpu-used 4` | `-crf` 36/30/26 `-b:v 0 -row-mt 1` |
| `librav1e` | `-speed 10` | `-speed 6` | `-speed 4` | `-qp` 120/90/70 |
| `libvpx-vp9` | `-deadline realtime -cpu-used 8` | `-deadline good -cpu-used 2` | `-deadline good -cpu-used 1` | `-crf` 38/32/28 `-b:v 0 -row-mt 1` |

Auto order: the card's hardware encoder for the codec, then `*_mf` (H.264, HEVC), then software (AV1: `libsvtav1`;
VP9: `libvpx-vp9`). Software AV1 at Best on CPU can be slower than real time; the estimate says so. HEVC files may
need the HEVC extension to play on some Windows PCs; the codec tooltip says so. All flags stay in one table in
`FfmpegArgs.cs`, checked against `ffmpeg -h encoder=<id>` of the pinned build.

## 7. Data safety
- The Host never writes to a user's folder. Inputs are uploads or job ids; outputs go to Host-owned folders
  (`generated\upscaled\`, `uploads\`, `jobs\upscale\`, `engines\`) under new unique names, checked absent before the
  child starts (as `SdCppEngine` does). Clients saving a result use `name (2).mp4`-style names, never the original.
- Deletes touch only the Host's own files: a job's work folder on cancel or failure, expired uploads, unverified
  `.part` files, staging folders, old engine versions after a new one works. Recursive deletes only in folders with
  the Host's marker file. These features never delete model files. State files are written with AtomicFile.
- Downloads are verified (zip SHA-256, then per-file SHA-256) before anything from them runs.
- Untrusted media is parsed only by ffmpeg/ffprobe children in a Job object with time and memory limits.
- Crash leftovers follow the generation rule; unknown files are kept under `recovered\` with unique names.

## 8. Settings UI
Buttons, menus and lists show an icon and a 1-3 word label. Explanations go in the tooltip and
`AutomationProperties.HelpText`. Settings cards may carry their usual one-line card description. Strings go in
`Strings/en-US/Resources.resw`. Defaults stay adjustable.

- **Hardware** card: detected cards (name, memory, driver), "Check again"; per engine Auto, NVIDIA CUDA, Vulkan,
  Processor (disabled items say why in the tooltip).
- **Engine downloads** list: per flavour "Download", "Cancel" or "Remove"; size, licence and minimum driver in the
  tooltip; the NVIDIA notice opens before the first CUDA download. New card: InfoBar with "Download" and "Not now".
- **Media** card: Encoder (Auto plus probed list), Codec, Preset, Quality slider, Bitrate box (only in bitrate mode).
- **Upscaling** card: default tier, "Steady video", "Allow processor", slow-job limit.
- Smoothness: probes, downloads and benchmarks run off the UI thread and stop when the page closes; placeholders
  fade in without moving the layout; progress updates are coalesced (at most 4 per second).

## 9. Build plan (Sonnet-sized steps)
| # | Step | Depends on | Owner action |
|---|---|---|---|
| 1 | NVML probe (driver, CUDA version, compute capability); `hardware` capability block; tests with stub | - | - |
| 2 | Flavour manifest format, `build-msix.ps1` writes `engine\flavours.json`; flavour store (folders, markers, file-list check) | - | - |
| 3 | Flavour downloader (resumable, hash-checked, staging extract, atomic rename, disk check), tests with a local zip | 2 | - |
| 4 | Candidate-list backend selector replacing `RuntimeProbe.Select`; per-flavour bad marks; CUDA `--list-devices` parser; Settings combos | 1, 2 | - |
| 5 | Pin llama.cpp CUDA 12.x, sd.cpp CUDA 12.x and the shared cudart; notices file; `fetch-*.ps1` support | 2 | Accept NVIDIA EULA terms (decision 2) |
| 6 | sd.cpp CUDA flavour in `SdCppEngine` and the generation planner; retry on Vulkan | 4, 5 | - |
| 7 | Fingerprint change re-probe, "NVIDIA card found" InfoBar, Engine downloads list in Settings | 3, 4 | - |
| 8 | ffmpeg encoder probe, `GET /host/media/encoders`, `encode` object on mp4 jobs and joins, preset table; then the Media card | - | - |
| 9 | Smart App Control detection and the "blocked by Windows" path | 4 | - |
| 10 | `/host/uploads` with limits, expiry, ffprobe check | - | - |
| 11 | `portal-upscale.exe` helper (ONNX Runtime DirectML and CPU, tiling, pipe protocol, peak-memory report) | - | - |
| 12 | `catalogue-upscale.json` with schema additions, ONNX files pinned | 11 | Re-sign catalogue; decide ONNX hosting (decision 5) |
| 13 | Image upscale jobs and estimate route | 10, 11, 12 | - |
| 14 | Video upscale pipeline: segments, state file, resume, Steady filter, audio, concat | 8, 13 | - |
| 15 | Scheduling: GPU slot vs processor lane, gaming-mode pause, memory plan | 14 | - |
| 16 | Per-machine benchmark, learned rates, tier visibility, `scripts\measure-upscale.ps1` | 13 | Run the measure script on the owner's PC |
| 17 | Client library methods, api-v1.md sections, Media Studio hand-off notes | 13-16 | - |
| 18 | CUDA test pass (load, fallback, driver too old, card swap) | 6, 7 | Needs an NVIDIA card (buy, borrow, or a rented Windows GPU machine) |
| 19 | Best tier spike: SeedVR2 3B and FlashVSR in a Python add-on on a 12-24 GB NVIDIA card; measure, then decide | 18 | Needs a 12 GB+ NVIDIA card; decision 4 |

Steps 1, 2, 8, 10 and 11 can start in parallel. Steps 1-17 can be built and tested on the owner's AMD PC with stub
CUDA output; only 18 and 19 need NVIDIA hardware.

## 10. Decisions for the owner

**Decided 2026-11-27 (owner):** 1. Upstream first, a copy in a release of the owner's public repo as the second URL.
2. NVIDIA CUDA redistribution terms accepted. 3. CUDA 12.8 line first; CUDA 13 later. 4. Build the Best-tier add-on now,
at lower priority, and steps 18-19 are built for NVIDIA users even without a test card here (stub and unit tests now,
live test when a card is available). 5. Real-ESRGAN accepted; ONNX files hosted in a release of the owner's public repo.
6. Uploads local only in v1. 7. H.264 by default. 8. Never hide a tier for being slow: show a warning with the time
estimate when a job is estimated above 10x clip length, and a stronger warning above 100x.

The original questions:
1. **CUDA download source:** upstream GitHub releases (pinned), a copy in a release of the owner's public repo, or
   both (recommended: upstream first, owner copy as second URL). A Store optional package is possible but heavy.
2. **NVIDIA CUDA EULA:** accept the redistribution terms in 3.5 after review?
3. **CUDA lines:** 12.x only (recommended first), or also a CUDA 13 build later for newest cards?
4. **Best tier:** build the optional Python add-on (3-5 GB, CUDA only, 12 GB+) after the spike, or wait for a native
   runtime and ship Fast and Balanced only?
5. **Upscaler weights:** accept Real-ESRGAN (BSD-3 weights, research-term training data)? Host ONNX conversions in
   the owner's public repo, or pin third-party exports?
6. **Uploads from paired devices:** local only in v1 (recommended), or allow large uploads over the relay?
7. **Default codec:** H.264 everywhere (recommended), or HEVC/AV1 when the hardware encodes it?
8. **Slow-job limit:** hide video tiers above 20x clip length by default?
