# Spec gaps: specs against main

Status as of main `b1a7ff9` (version 1.0.3). Open branch: `feat/localization` (French, Arabic, German strings).
In flight elsewhere: upscale jobs (build plan step 13). Status words: built, partly, missing, held (waits on the
owner or on hardware). Sizes: S (under an hour), M (a day or two), L (a week or more).

## Hardware backends and upscaling (hardware-backends-and-upscaling.md, section 9)

| # | Item | Status | Evidence | Size | Dependencies / owner action |
|---|---|---|---|---|---|
| 1 | NVML probe, `hardware` capability block | built | `98956c5`, `Engine/Hardware/NvmlProbe.cs` | - | - |
| 2 | Flavour manifest and flavour store | built | `48fe713` | - | - |
| 3 | Flavour downloader (resumable, hash-checked) | built | `c7114d2` | - | - |
| 4 | Candidate backend selector, per-flavour bad marks | built | `98956c5`, `Engine/Hardware/BackendSelector.cs` | - | Settings combos: check |
| 5 | CUDA 12 pins for llama.cpp, sd.cpp, cudart | partly | `48fe713`: pins are placeholders | S per pin | Real release URLs and hashes; EULA accepted 27/11 |
| 6 | sd.cpp CUDA flavour, retry on Vulkan | partly | selector covers it; no real CUDA build pinned | M | 5 |
| 7 | Re-probe on fingerprint change, "NVIDIA card found" InfoBar, Engine downloads list | partly | selector text in `BackendSelector.cs`; no Engine downloads list in Settings | M | 3, 4 |
| 8 | Encoder probe, `/host/media/encoders`, `encode` object | built | `87c4b10`, merge `d9e688f` | - | Media card in Settings: check |
| 9 | Smart App Control detection, "blocked by Windows" path | missing | no match in `src/` | M | 4 |
| 10 | `/host/uploads` with limits, expiry, ffprobe check | built | `1184bdc`, `5f64372` | - | - |
| 11 | `portal-upscale.exe` helper and host client | built | `7b32e61`, `src/Portal.AiHost.UpscaleHelper` | - | - |
| 11a | Upscaler wired into the app (Host routes, UI) | missing | client exists, no caller in App or Server | M | 12, 13 |
| 12 | `catalogue-upscale.json`, ONNX files pinned | held | no `catalogue/catalogue-upscale.json` | S draft | Owner re-signs catalogue; ONNX files in a public repo release |
| 13 | Image upscale jobs and estimate route | partly (in flight) | upscale-jobs work, not on main | M | 10, 11, 12 |
| 14 | Video upscale pipeline (segments, resume, audio, concat) | missing | - | L | 8, 13 |
| 15 | Scheduling: GPU slot vs processor lane, gaming pause | missing | - | M | 14 |
| 16 | Benchmark, learned rates, `scripts\measure-upscale.ps1` | missing | no script in `scripts/` | M (script alone S) | 13; owner runs the script |
| 17 | Client library methods, api-v1.md upscale sections | missing | - | M | 13-16 |
| 18 | CUDA test pass | held | - | M | NVIDIA card |
| 19 | Best tier add-on | partly | `abff6b4`, `cd64526`, `Engine/Upscale/BestTierHardware.cs` | L | 18; 12 GB+ NVIDIA card for the live test |

## Media, agents and voice (media-and-agents-plan.md)

| Item | Spec | Status | Evidence | Size | Dependencies / owner action |
|---|---|---|---|---|---|
| Models page by category | 7, phase 0 | built | merge `d0ef27e` | - | - |
| Cloud models, consults, hand-offs | 2, 3, phases 1-2 | not Host scope | AI Portal app | - | - |
| CPU director plus GPU engine presets, RAM budget refusal | 6.4, phase 3 | partly | `2a0e0cc`: processor-only director, reservation in memory budget; presets UI not found | M | - |
| Video engine kind, job API, MP4, joins | 4.4, phase 4 | built | `be967e0`, `6531d01` | - | - |
| sd.cpp in the Store package | 4.2 | held | `68bf328`: MSIX packaging optional; not in 1.0.3.0 | S to M | ffmpeg and sd.cpp licence review (decision 7) |
| Video panel and director | 5, phases 5-6 | not Host scope | AI Portal app | - | - |
| Wan 2.2 5B/14B with offload, estimates from runs | phase 7 | missing | - | M | phase 4 measured on the owner's PC |
| Voice helper, voice library, speech jobs | 11.4 | built | `50ac2b0`, merge `11b9a90`, `82ed3ae` | - | - |
| Voice pins | 11.4 | partly | `c9f6e85`: fetch script with placeholder pins | S | Spike approval (11.6 decision 1) |
| Open decisions | 9, 11.6 | held | - | - | Owner: ten media decisions, five voice decisions |

## API, remote and release text

| Item | Spec | Status | Evidence | Size | Dependencies / owner action |
|---|---|---|---|---|---|
| LAN pairing, authenticated requests, TLS pinning | remote-v1.md 1-9 | built | `Server/RemoteAccess.cs`, `Core/Remote/DeviceSessions.cs` | - | - |
| Relay host side and device connector | remote-v1.md 10 | partly | `Server/RelayBridge.cs`, `Core/Remote/Client/RelayConnector.cs`; relay admission is off | M | Owner: deploy the relay and turn admission on |
| What's new 1.0.3.0 | release text | partly (overclaims) | `b1a7ff9`: names MP4 video and the relay key, but sd.cpp is not in the Store package and admission is off | S | - |
| api-v1.md sections for uploads, encoders, upscale | api-v1.md | partly | uploads and encoders built; upscale part waits on 13-17 | S now, M later | 13 |
| UI strings in fr, ar, de | localization | partly (in flight) | branch `feat/localization` (`6e37b87`, `38471f5`, `5b758be`) | S to merge | Review RTL layout for Arabic |

Counts: S items 7, M items 12, L items 2.

## Can be built tonight in about 20 minutes

Each item touches its own files, so parallel agents can take one each.

1. **What's new 1.0.3.0 correction**: remove the MP4 video and relay key claims or mark them "coming later".
   Files: the What's new text source only (the What's new page strings and `docs/store` release notes), not
   `Resources.resw` keys that `feat/localization` also changes.
2. **Draft `catalogue-upscale.json`**: unsigned entries for the Real-ESRGAN ONNX files with the schema additions.
   Files: `catalogue/catalogue-upscale.json`, `catalogue/catalogue.schema.json`. Owner signs later.
3. **`scripts\measure-upscale.ps1`**: measurement script skeleton that calls the helper on a local test image and
   writes a result file. Files: `scripts/measure-upscale.ps1` only.
4. **api-v1.md: uploads and encoders sections**: document `/host/uploads` and `/host/media/encoders` as built.
   Files: `docs/api-v1.md` only.
5. **USER-GUIDE.md: honest feature list**: state that video needs the optional sd.cpp download and that the relay is
   not yet open. Files: `docs/USER-GUIDE.md` only.
