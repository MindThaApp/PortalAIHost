# Portal AI Host: phase 1 research (2026-11-20)

Sizes and licences below come from the Hugging Face (HF) API (`/api/models/<repo>?blobs=true`) and model cards, fetched 2026-11-20. Items marked **unverified** were not confirmed on a primary page and must be re-checked before shipping.

## 1. Model catalogue

All recommended models are **Apache 2.0 or MIT**. No Llama-licence or Gemma-terms models are in the default catalogue.

### 1.1 Recommendations

| Tier | Recommended (default) | HF repo and file | Q4_K_M size | Licence | Context |
|---|---|---|---|---|---|
| Large MoE | **Qwen3-30B-A3B-Instruct-2507** (30.5B total, 3.3B active; non-thinking only) | [unsloth/Qwen3-30B-A3B-Instruct-2507-GGUF](https://huggingface.co/unsloth/Qwen3-30B-A3B-Instruct-2507-GGUF), `Qwen3-30B-A3B-Instruct-2507-Q4_K_M.gguf` | 18,556,686,752 B (17.3 GiB) | Apache-2.0 ([LICENSE](https://huggingface.co/Qwen/Qwen3-30B-A3B-Instruct-2507/blob/main/LICENSE)) | 262,144 native |
| Large MoE (newer alternative) | Qwen3.6-35B-A3B (35B total, 3B active; thinking ON by default) | [unsloth/Qwen3.6-35B-A3B-GGUF](https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF), `Qwen3.6-35B-A3B-UD-Q4_K_M.gguf` | 22,134,528,992 B (20.6 GiB) | Apache-2.0 ([LICENSE](https://huggingface.co/Qwen/Qwen3.6-35B-A3B/blob/main/LICENSE)) | 262,144 native |
| Medium ~14B | **Qwen3-14B** (hybrid thinking, switchable) | [Qwen/Qwen3-14B-GGUF](https://huggingface.co/Qwen/Qwen3-14B-GGUF) (official), `Qwen3-14B-Q4_K_M.gguf` | 9,001,752,960 B (8.4 GiB) | Apache-2.0 | 32,768 native, 131,072 with YaRN (**unverified**, from memory of the card) |
| Medium (MIT alternative) | Phi-4 14B | [microsoft/phi-4-gguf](https://huggingface.co/microsoft/phi-4-gguf) (official). No Q4_K_M; use `phi-4-Q4_K.gguf` | 9,053,114,560 B | **MIT** | 16K (**unverified**) |
| Small 7-8B | **Qwen3-8B** | [Qwen/Qwen3-8B-GGUF](https://huggingface.co/Qwen/Qwen3-8B-GGUF) (official), `Qwen3-8B-Q4_K_M.gguf` | 5,027,783,488 B (4.7 GiB) | Apache-2.0 | 32,768 native, 131,072 YaRN (**unverified**) |
| Small (newer alternative) | Qwen3.5-9B | [unsloth/Qwen3.5-9B-GGUF](https://huggingface.co/unsloth/Qwen3.5-9B-GGUF), `Qwen3.5-9B-Q4_K_M.gguf` | 5,680,522,464 B (5.3 GiB) | Apache-2.0 | 262K reported (**unverified**) |
| Tiny 3-4B | **Qwen3-4B** | [Qwen/Qwen3-4B-GGUF](https://huggingface.co/Qwen/Qwen3-4B-GGUF) (official), `Qwen3-4B-Q4_K_M.gguf` | 2,497,280,256 B (2.3 GiB) | Apache-2.0 | 32,768 native, 131,072 YaRN (**unverified**) |
| Tiny (newer alternative) | Qwen3.5-4B | [unsloth/Qwen3.5-4B-GGUF](https://huggingface.co/unsloth/Qwen3.5-4B-GGUF), `Qwen3.5-4B-Q4_K_M.gguf` | 2,740,937,888 B (2.6 GiB) | Apache-2.0 | 262K reported (**unverified**) |
| Embeddings | **Qwen3-Embedding-0.6B** | [Qwen/Qwen3-Embedding-0.6B-GGUF](https://huggingface.co/Qwen/Qwen3-Embedding-0.6B-GGUF) (official). Only `Q8_0` (639,150,592 B) and `f16` (1,197,629,632 B) are published; no Q4 | 0.64 GB (Q8_0) | Apache-2.0 | 32K (**unverified**) |
| Embeddings (lighter) | nomic-embed-text-v1.5 | [nomic-ai/nomic-embed-text-v1.5-GGUF](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5-GGUF) (official). `Q4_K_M` is 84,106,624 B; `Q8_0` is 146,146,432 B | 84 MB / 146 MB | Apache-2.0 | 2,048 (8,192 with rope scaling) (**unverified**) |

Notes:
- **Newer Qwen generations exist.** Qwen3.5 (small dense 0.8B, 2B, 4B, 9B, 27B plus MoE) and Qwen3.6 (35B-A3B, 27B) are Apache-2.0 per their HF cards. I could not confirm that llama.cpp support and quality are mature, so the recommendation is the established Qwen3 line for the Large, Medium, Small and Tiny tiers, with Qwen3.5/3.6 as "re-test and upgrade" candidates. Qwen3.5 has no 14B, so Qwen3-14B stays as the Medium default.
- **Qwen3-30B-A3B-Instruct-2507** is non-thinking, which suits minutes and summaries. Qwen3.6-35B-A3B thinks by default; disable it with `chat_template_kwargs: {"enable_thinking": false}` (no `/nothink` soft switch). It is ~3.6 GB bigger, which matters on the 64 GB RAM + 8 GB GPU target only for speed.
- **Official vs third-party GGUF.** Qwen publishes official GGUFs for Qwen3-4B/8B/14B. For the 30B-A3B-Instruct-2507 and the 3.5/3.6 models I found no official `Qwen/...-GGUF` repo (the 2507 GGUF URL returned 401, which usually means the repo does not exist or is gated; **unverified**). The unsloth repos are a reputable third-party quantiser. They inherit the base licence (`license: apache-2.0`, `license_link` points to Qwen's LICENSE). Check that Qwen has not published an official 30B-A3B-2507 GGUF before pinning.
- **Phi-4 (MIT)** has the cleanest licence, but a 16K context and weaker multilingual quality. Offer it as an optional extra, not the default. The app promises 21 languages, which favours Qwen.
- **Embeddings.** The spec's "~100 MB" matches nomic (84 MB Q4_K_M, a small quality loss, and it needs `search_document:` / `search_query:` prefixes). Qwen3-Embedding-0.6B at 640 MB is more multilingual. Suggest nomic Q8_0 (146 MB) as the default and Qwen3-Embedding as the "better multilingual" option. Run the embedding model with llama.cpp `--embedding`.
- **Fit for the targets** (weights only; add KV cache and the OS):
  - 64 GB RAM + RX 6600 8 GB: Qwen3-30B-A3B Q4_K_M (17.3 GiB) fits in RAM with room to spare. The experts live in RAM and attention plus shared layers sit on the GPU.
  - M4 Pro 24 GB: Qwen3-14B Q4_K_M (8.4 GiB) is comfortable. The 30B-A3B (17.3 GiB) is possible but tight with macOS and other apps.
  - 16 GB RAM + 8 GB GPU: Qwen3-14B partly offloaded, or Qwen3-8B fully on the GPU.

### 1.2 Commercial use and redistribution

- **Apache 2.0** (Qwen, nomic) allows commercial use, modification and redistribution. If the app redistributes the weights: include a copy of the licence, and keep any NOTICE file and attribution. Downloading at runtime from HF puts these obligations on HF and the model owner, but we should still show the licence name and link in the model list ("Apache-2.0, Alibaba Cloud / Qwen team") and bundle a third-party notices page.
- **MIT** (Phi-4) needs only the copyright and permission notice kept.
- No extra field-of-use restrictions found on the Qwen3, Qwen3.5, Qwen3.6, Phi-4 or nomic cards beyond the licence itself. The Qwen3.6 card states Apache-2.0 only.
- **Avoided and flagged:** Llama 3.x (Llama Community Licence: 700M MAU clause, "Built with Llama" attribution, naming rules), Gemma (Gemma Terms of Use: use restrictions flow down to users), and Mistral models under the Mistral Research Licence. These are not in the catalogue.
- Model output carries no licence restriction under Apache-2.0 or MIT.

### 1.3 Download at runtime vs mirror

HF Terms of Service ([source](https://huggingface.co/terms-of-service)):
- Public repo content is licensed to every User ("perpetual, irrevocable, worldwide, royalty-free, non-exclusive license").
- Content under an open-source licence "is intended to remain under the terms of such license", and the Terms are not meant to override it. Downloading under Apache-2.0 / MIT is allowed.
- Downloading is "at your own risk". The page I fetched has no clause on automated access or bandwidth. The Supplemental Terms were not read, so **check the HF Content Policy and Supplemental Terms for automated-download rules** (**unverified**).

Recommendation:
1. **Download directly from HF** (`https://huggingface.co/<repo>/resolve/<commit-sha>/<file>`), with no mirror in v1. This costs nothing, needs no hosting, and keeps the app out of the redistribution business.
2. **Pin each model to an HF commit SHA** and ship the expected SHA-256 (from the HF LFS metadata) in the app's catalogue. Resumable download uses `Range` requests (HF supports them). This also protects against a repo author replacing a file.
3. **Keep the catalogue as a signed JSON file** that the app fetches, so repo moves or deletions can be fixed without a Store update.
4. **Plan a fallback mirror** (own storage or CDN) for the 3-4 default files only if HF rate limiting or a repo takedown becomes a problem. When mirroring, ship the licence text and NOTICE alongside each file. Mirroring 18 GB files costs egress, so defer it.
5. **User agent:** use a plain `PortalAIHost/<version>` string and no HF token.

## 2. llama.cpp licence and bundling

- Licence: **MIT**, "Copyright (c) 2023-2026 The ggml authors" ([LICENSE](https://github.com/ggml-org/llama.cpp/blob/master/LICENSE)). Fetched 2026-11-20.
- MIT allows use in closed-source commercial software, including Store distribution. The only condition is to include the copyright and permission notice "in all copies or substantial portions of the Software".
- What the Store app must do:
  - Ship a **THIRD-PARTY-NOTICES.txt** (also shown in an "About / Licences" page) containing the full MIT text for llama.cpp and ggml, and the licences of every bundled dependency.
  - Check what the chosen Vulkan build pulls in. Likely items: `cpp-httplib` (MIT), `nlohmann/json` (MIT), `stb` (public domain / MIT), the Vulkan SDK headers or loader (Apache-2.0, **unverified**), `miniaudio` (public domain / MIT-0, only if built). Generate the list from the vendored sources of the pinned release tag.
  - If we ship `llama-server.exe` as a child process, put the notice file beside it and in the MSIX. Linking the code in as a library needs the same notice.
  - Store listing: no AI-specific disclosure needed for licensing. Do not use the llama.cpp or ggml marks as the app's name or logo.
- No copyleft in the engine. Do not bundle a GPL component (for example some ffmpeg builds) into the same package.

## 3. Store name check: "Portal AI Host"

Searches run: exact phrase on the web; Microsoft Store and Mac App Store related queries (web search only; the Store itself cannot be searched from here, so the owner should run the in-Store search and Partner Center name reservation as the final check).

| Finding | Source | Conflict risk |
|---|---|---|
| No app or product called "Portal AI Host" found | web search | None found |
| "AI-Portal", a self-hosted AI platform (npx `create-ai-portal`) | [ai-portal-nine.vercel.app](https://ai-portal-nine.vercel.app) | Low (different name, web product) |
| "LLM Portal" (Mac App Store) | [apps.apple.com/app/id6759472256](https://apps.apple.com/app/id6759472256) | Low to medium: close concept, matters for the later Mac phase |
| "Portal Labs", an AI tools aggregator | [webcatalog.io/apps/portal-labs](https://webcatalog.io/apps/portal-labs) | Low |
| "Portal" (focus app, Mac App Store) and "Portal: Remote Play" | [9to5mac](https://9to5mac.com/2023/05/24/portal-now-has-a-mac-app/), [apps.apple.com](https://apps.apple.com/app/id6737148404) | Low |
| Meta "Portal" smart display; Tyk "AI Portal" | [Meta](https://developers.meta.com/vr/blog/build-apps-for-portal-with-ai/), [Tyk](https://tyk.io/docs/5.12/ai-management/ai-studio/ai-portal.md) | Low (the word "Portal" is generic) |

**Verdict:** no direct conflict found. "Portal" is a crowded word, so a trademark search (UK IPO / USPTO / EUIPO, classes 9 and 42) is advised before launch. Keeping the "Portal" family branding (Voice Portal, Files Portal) is a good reason to keep the name.

Fallback names:
1. **Portal Model Host**
2. **Portal Local AI** (or **Portal AI Server**)

## 4. Published speed numbers

No published benchmark was found for an RX 6600 8 GB running Qwen3-30B-A3B on llama.cpp Vulkan with partial offload. What exists:

| Data point | Result | Relevance | Source |
|---|---|---|---|
| willitrunai estimate, Qwen3-30B-A3B on RX 6600 8 GB | ~2.5 tok/s | Estimate, not a measurement, settings not stated; likely pessimistic for 64 GB DDR5 + `--n-cpu-moe` | [willitrunai.com](https://willitrunai.com/can-run/qwen-3-coder-30b-a3b-on-rx-6600-8gb) |
| llmrun.dev, RX 6600, Qwen3.5-9B Q4_K_M (fits in VRAM) | ~21.1 tok/s | Dense, fully on GPU: the speed expected for the Small tier | [llmrun.dev](https://llmrun.dev/gpu/rx-6600?models-page=2) |
| Radeon AI PRO R9700 32 GB, Vulkan, Qwen3-30B-A3B Q4_K_M | 183 tok/s decode, 3,033 tok/s prefill | Upper bound only (all on GPU, newer card) | [llama.cpp discussion 21043](https://github.com/ggml-org/llama.cpp/discussions/21043) |
| RX 7900 XTX 24 GB, same model, all on GPU | 57.64 tok/s | Not comparable (fits in VRAM) | same search results |
| 8 GB RTX 3070 Ti laptop, Qwen3.6-35B-A3B, `--n-cpu-moe` | ~4 to ~33 tok/s with the flag | Closest analogue (8 GB GPU, MoE, experts in RAM). Single unverified blog/article source, NVIDIA | [windowsforum.com](https://windowsforum.com/news/llama-cpp-n-cpu-moe-run-qwen3-35b-faster-on-8gb-rtx-laptop-gpus.449607/?amp=1) |
| 12 GB card, Qwen3-35B-A3B, `--n-cpu-moe` 8 to 30 | 17 to 34 tok/s | MoE offload scaling | [llmhardware.io guide](https://llmhardware.io/guides/moe-hybrid-gpu-cpu-offloading-guide) |

Takeaway:
- **Do not promise a figure.** Expect roughly 10 to 25 tok/s generation on the owner's PC with `-ngl 99 --n-cpu-moe N` (experts in DDR5, attention on the RX 6600). This is an inference from the analogues above, not a measurement. Prompt processing on Vulkan will be slower than on CUDA.
- **Phase 1 task:** run `llama-bench` on the owner's PC (`-ngl 99 --n-cpu-moe {20..48}`), record tokens/s, and bake the best N per VRAM size into the hardware check. Measure the Vulkan driver build against CPU-only too, since Vulkan on older AMD cards can lose to CPU for prompt processing.
- The app's "minutes in about N min" estimate must come from a measured speed test at first run, not from a table.

## 5. Open items for the owner

1. Confirm no official `Qwen/Qwen3-30B-A3B-Instruct-2507-GGUF` exists before pinning the unsloth repo.
2. Read the HF Supplemental Terms and Content Policy for automated downloads.
3. Run the in-Store name search and reserve the name in Partner Center; do a trademark check.
4. Run `llama-bench` on the RX 6600 PC.
5. Re-check context lengths marked unverified from each model's `config.json` when pinning commits.

## 6. Big-hardware tiers (optional purchases)

Sizes from the HF API (`?blobs=true`), 2026-11-20. Totals for split models are the sum of the parts (GB = 10^9 bytes). All Apache-2.0. Multi-part models need all parts in one folder; llama.cpp loads from the `-00001-of-0000N` file. Part 1 of unsloth's Qwen3.5 splits is a tiny 11 MB header file.

| Model | GGUF repo | Q3 | Q4_K_M | Q8_0 | Split? | Licence | Context |
|---|---|---|---|---|---|---|---|
| gpt-oss-20b (21B, 3.6B active) | [unsloth/gpt-oss-20b-GGUF](https://huggingface.co/unsloth/gpt-oss-20b-GGUF) | n/a | 11.6 GB | 12.1 GB | single | Apache-2.0 | 128K (**unverified**) |
| gpt-oss-120b (117B, 5.1B active) | [ggml-org/gpt-oss-120b-GGUF](https://huggingface.co/ggml-org/gpt-oss-120b-GGUF) | n/a | n/a (native MXFP4 only: **63.4 GB**) | n/a | single | Apache-2.0 ([card](https://huggingface.co/openai/gpt-oss-120b)); must use the harmony format | 128K (**unverified**; not stated on the card) |
| Qwen3-Next-80B-A3B-Instruct | [Qwen/Qwen3-Next-80B-A3B-Instruct-GGUF](https://huggingface.co/Qwen/Qwen3-Next-80B-A3B-Instruct-GGUF) | none | **48.4 GB** | 84.8 GB | single files (the 4-part set of ~160 GB is the unquantised conversion) | Apache-2.0 | 262K (**unverified**) |
| Qwen3.5-27B (dense) | [unsloth/Qwen3.5-27B-GGUF](https://huggingface.co/unsloth/Qwen3.5-27B-GGUF) | n/a | 16.7 GB | 28.6 GB | single | Apache-2.0 | 262K (**unverified**) |
| Qwen3.5-122B-A10B | [unsloth/Qwen3.5-122B-A10B-GGUF](https://huggingface.co/unsloth/Qwen3.5-122B-A10B-GGUF) | 56.4 GB (3 parts) | **76.5 GB** (3 parts) | 129.9 GB (4 parts) | yes | Apache-2.0 ([card](https://huggingface.co/Qwen/Qwen3.5-122B-A10B)) | 262,144 native; thinking ON by default |
| Qwen3-235B-A22B-Instruct-2507 | [unsloth/Qwen3-235B-A22B-Instruct-2507-GGUF](https://huggingface.co/unsloth/Qwen3-235B-A22B-Instruct-2507-GGUF) | 112.4 GB (3 parts) | **142.2 GB** (3 parts) | 249.9 GB (6 parts) | yes | Apache-2.0 ([card](https://huggingface.co/Qwen/Qwen3-235B-A22B-Instruct-2507)) | 262,144 native; non-thinking |
| Qwen3.5-397B-A17B | [unsloth/Qwen3.5-397B-A17B-GGUF](https://huggingface.co/unsloth/Qwen3.5-397B-A17B-GGUF) | 177.4 GB (Q3_K_M, 5 parts); UD-IQ3_XXS 140.4 GB (4 parts); UD-IQ2_M 123.0 GB | 244.1 GB (6 parts) | ~423 GB (10 parts) | yes | Apache-2.0 (per card metadata) | 262K (**unverified**) |

Other permissive options checked: no permissively licensed dense ~70B was found (Llama 3.x is restricted; Qwen2.5-72B uses the separate Qwen licence, not Apache; **unverified**, not opened). Qwen3.5-27B is the largest permissive dense model listed above. gpt-oss has no Q3 file; use MXFP4 as published.

Fit rule used: weights + roughly 5 to 15 GB for KV cache at 16 to 32K context + 8 GB for the OS and the app. The KV figure is an estimate (**unverified**); measure per model. For discrete GPUs, usable = VRAM + RAM minus the OS. On the Mac, macOS limits GPU-wired memory by default to roughly 75% of RAM (~192 GB on 256 GB; raising it needs `sysctl iogpu.wired_limit_mb`; **unverified**, not tested), so keep weights under ~180 GB unless the limit is raised.

### Target setup to recommended model

| Setup | Total memory | Recommended default | Stretch option | Notes |
|---|---|---|---|---|
| (a) RTX 5090 32 GB + 64 GB RAM | 96 GB | Qwen3.6-35B-A3B Q4_K_M (22.1 GB, fully in VRAM) or Qwen3-30B-A3B-2507 (18.6 GB) | gpt-oss-120b MXFP4 (63.4 GB) with `--n-cpu-moe` | 120b leaves ~25 GB for OS/KV: tight, workable at short context. Qwen3.5-122B Q3_K_M (56.4 GB) also fits. |
| (b) RX 6600 8 GB + 128 GB RAM | 136 GB | Qwen3-Next-80B-A3B Q4_K_M (48.4 GB, 3B active so RAM-speed friendly) | Qwen3.5-122B-A10B Q4_K_M (76.5 GB) | Qwen3-235B Q3_K_M (112 GB) is too tight and slow here. Needs a recent llama.cpp for the Qwen3-Next architecture (**unverified**). |
| (c) RTX 5090 32 GB + 128 GB RAM | 160 GB | Qwen3.5-122B-A10B Q4_K_M (76.5 GB) | Qwen3-235B-A22B-2507 Q3_K_M (112.4 GB) | 235B Q4_K_M (142 GB) leaves only ~18 GB: not recommended. |
| (d) Mac Studio 256 GB | 256 GB (~192 GB default GPU) | Qwen3-235B-A22B-2507 Q4_K_M (142.2 GB) | Qwen3.5-397B UD-IQ3_XXS (140.4 GB) or Q3_K_M (177.4 GB, needs a raised wired limit) | Qwen3.5-122B Q8_0 (129.9 GB) is the high-fidelity smaller option. 397B Q4_K_M (244 GB) does not fit. |

Notes:
- The unsloth "UD-" quants are mixed-precision dynamic quants; quality at low bit depth is better than plain Q3, but they are a third-party format (licence inherits Apache-2.0).
- The catalogue should hide any entry that does not fit memory with headroom (the hardware check in spec section 4). Show multi-part downloads as one item with a total size and a single hash list.
- All of these are download sizes in the 50 to 250 GB range, so the free-disk check and resumable download matter. Allow downloads of individual parts to be retried independently.
- Speeds are unmeasured. Add these models to the `llama-bench` task in section 5.

## 7. Non-Qwen models (vendor diversity)

HF API and model cards fetched 2026-11-20. "Licence" is the HF `license` field; none of these showed custom or modified terms in the metadata, but the LICENSE files were **not individually read**, so confirm each before shipping. "llama.cpp" = architecture string present in `src/llama-arch.cpp` on master ([source](https://github.com/ggml-org/llama.cpp/blob/master/src/llama-arch.cpp)); a runtime test is still needed. Fits: **PC** = current 64 GB + RX 6600; **Mac** = MacBook 24 GB; a to d = section 6 setups.

| Model (maker) | Licence | GGUF repo | Q4_K_M | Split | Context | llama.cpp arch | Fits |
|---|---|---|---|---|---|---|---|
| gpt-oss-20b / 120b (OpenAI) | Apache-2.0; harmony format required | see section 6 | 11.6 GB / 63.4 GB MXFP4 | no | 128K (unverified) | `gpt-oss` yes | 20b: PC, Mac. 120b: a (tight), b, c, d |
| Mistral Small 3.2 24B (Mistral AI; vision-capable) | Apache-2.0 ([card](https://huggingface.co/mistralai/Mistral-Small-3.2-24B-Instruct-2506)) | [unsloth/Mistral-Small-3.2-24B-Instruct-2506-GGUF](https://huggingface.co/unsloth/Mistral-Small-3.2-24B-Instruct-2506-GGUF) | 14.3 GB | no | not stated on card (128K believed; unverified) | `mistral3` yes | PC (dense: slow with partial offload), Mac (tight), a, b, c, d |
| Magistral Small 2509 (Mistral AI; reasoning) | Apache-2.0 | [unsloth/Magistral-Small-2509-GGUF](https://huggingface.co/unsloth/Magistral-Small-2509-GGUF) | 14.3 GB | no | unverified | `mistral3` yes | same as above |
| Devstral Small 2507 (Mistral AI; coding agent) | Apache-2.0 | [unsloth/Devstral-Small-2507-GGUF](https://huggingface.co/unsloth/Devstral-Small-2507-GGUF) | 14.3 GB | no | unverified | `mistral3` yes | coding only: not a general default |
| Phi-4 14B (Microsoft) | MIT | [microsoft/phi-4-gguf](https://huggingface.co/microsoft/phi-4-gguf) | 9.05 GB (Q4_K) | no | 16K (unverified) | `phi3` yes | PC, Mac, b, c, d |
| Phi-4-mini 3.8B (Microsoft) | MIT | [unsloth/Phi-4-mini-instruct-GGUF](https://huggingface.co/unsloth/Phi-4-mini-instruct-GGUF) | 2.49 GB | no | 128K (unverified) | `phi3` yes | all, incl. 8 GB machines |
| Granite 4.0-h-small, 32B-A9B hybrid MoE (IBM) | Apache-2.0 | [ibm-granite/granite-4.0-h-small-GGUF](https://huggingface.co/ibm-granite/granite-4.0-h-small-GGUF) | 19.5 GB | no | 128K ([card](https://huggingface.co/ibm-granite/granite-4.0-h-small)); 12 languages | `granitehybrid` yes | PC, a, b, c, d; Mac tight |
| Granite 4.0-h-tiny, 7B-A1B | Apache-2.0 | [ibm-granite/granite-4.0-h-tiny-GGUF](https://huggingface.co/ibm-granite/granite-4.0-h-tiny-GGUF) | 4.23 GB | no | 128K (unverified) | `granitehybrid` yes | all |
| Granite 4.0-h-micro 3B / h-1b / h-350m | Apache-2.0 | [h-micro](https://huggingface.co/ibm-granite/granite-4.0-h-micro-GGUF), [h-1b](https://huggingface.co/ibm-granite/granite-4.0-h-1b-GGUF), h-350m | 1.94 GB / 0.90 GB / n/a | no | unverified | `granitehybrid` yes | tiny devices |
| GLM-4.7-Flash, ~31B-A3B MoE (Zhipu AI) | MIT | [unsloth/GLM-4.7-Flash-GGUF](https://huggingface.co/unsloth/GLM-4.7-Flash-GGUF) | 18.3 GB | no | not stated on card | arch not confirmed in the part of the file read (unverified) | PC, a, b, c, d; Mac tight |
| GLM-4.5-Air, 106B-A12B (Zhipu AI) | MIT | [unsloth/GLM-4.5-Air-GGUF](https://huggingface.co/unsloth/GLM-4.5-Air-GGUF) | 73.0 GB (2 parts); Q3_K_M 57.2 GB | yes | unverified | `glm4moe` yes | a (Q3 only, very tight), b, c, d |
| GLM-4.6 | MIT | no smaller sibling found ("Air" exists only for 4.5); the smaller 4.6 line is the vision model GLM-4.6V-Flash; newer GLM-5.x-Flash exists but was not checked | n/a | n/a | n/a | n/a | not recommended |
| MiniMax-M2, ~230B-A10B (MiniMax) | MIT | [unsloth/MiniMax-M2-GGUF](https://huggingface.co/unsloth/MiniMax-M2-GGUF) | 138.3 GB (3 parts); Q3_K_M 109.3 GB; UD-IQ3_XXS 93.6 GB | yes | unverified | `minimax-m2` yes | c (Q3, tight), d (Q4) |
| OLMo 2 7B / 13B / 32B Instruct (Ai2) | 32B GGUF repo has **no licence field**; Apache-2.0 on the base model believed (unverified) | [allenai/OLMo-2-0325-32B-Instruct-GGUF](https://huggingface.co/allenai/OLMo-2-0325-32B-Instruct-GGUF) | none published (Q4_0 18.3 GB, Q3_K_M 15.6 GB) | no | 4K (unverified) | `olmo2` yes | superseded by Olmo 3 |
| Olmo 3 7B Instruct (Ai2) | Apache-2.0 | [unsloth/Olmo-3-7B-Instruct-GGUF](https://huggingface.co/unsloth/Olmo-3-7B-Instruct-GGUF) | 4.47 GB | no | unverified | `olmo3` not seen in the first 100K characters of the file (unverified; may reuse `olmo2`) | all 8+ GB machines |
| SmolLM3-3B (Hugging Face) | Apache-2.0 | [unsloth/SmolLM3-3B-GGUF](https://huggingface.co/unsloth/SmolLM3-3B-GGUF) (official repo returned 401) | 1.92 GB | no | 64K to 128K (unverified) | `smollm3` yes | all |
| DeepSeek R1-0528 / V3-0324 (DeepSeek), 671B | MIT | [unsloth/DeepSeek-R1-0528-GGUF](https://huggingface.co/unsloth/DeepSeek-R1-0528-GGUF), [V3-0324](https://huggingface.co/unsloth/DeepSeek-V3-0324-GGUF) | 404.9 GB (9 parts); smallest UD-IQ1_S 185.5 GB (4 parts) | yes | unverified | `deepseek2` yes | none. Even the 1-bit quant exceeds the Mac's default GPU limit. Exclude. |

Notes:
- **Not checked:** Granite's separate "Base" repos, vision variants and the 1b speech model are out of scope. Mistral's own LICENSE files and the Devstral/Magistral cards were not opened (licence taken from HF metadata).
- **Size/quality points:** Granite 4.0-h-small (9B active) will be slower than the A3B Qwen models when experts sit in RAM. GLM-4.7-Flash and Qwen3-30B-A3B are the same class (3B active).
- **Restricted lines still avoided:** Llama, Gemma, Mistral Research Licence models (for example Codestral). Mistral Small 3.x and Devstral 2507 are Apache-2.0, so they are usable.

### Proposed mixed-vendor default catalogue (Apache-2.0 or MIT only)

| Tier | Default (maker) | Alternate from a different maker |
|---|---|---|
| Large MoE (PC 64 GB, a) | Qwen3-30B-A3B-Instruct-2507, 18.6 GB (Alibaba) | GLM-4.7-Flash, 18.3 GB (Zhipu, MIT). Second alternate: Granite 4.0-h-small, 19.5 GB (IBM) |
| Medium ~14B (Mac 24 GB) | Qwen3-14B, 9.0 GB (Alibaba) | Phi-4, 9.05 GB (Microsoft, MIT). Premium step-up: Mistral Small 3.2 24B, 14.3 GB (Mistral AI) |
| Small 7-8B | Qwen3-8B, 5.0 GB (Alibaba) | Olmo-3-7B-Instruct, 4.5 GB (Ai2); or Granite 4.0-h-tiny, 4.2 GB (IBM) |
| Tiny 3-4B | Qwen3-4B, 2.5 GB (Alibaba) | Phi-4-mini, 2.5 GB (Microsoft, MIT); or SmolLM3-3B, 1.9 GB (Hugging Face) |
| Embeddings | nomic-embed-text-v1.5 Q8_0, 146 MB (Nomic) | Qwen3-Embedding-0.6B Q8_0, 640 MB (Alibaba) |
| Big 100B+ (b, c, d) | Qwen3.5-122B-A10B Q4_K_M, 76.5 GB (Alibaba) | gpt-oss-120b MXFP4, 63.4 GB (OpenAI). For 200B+: MiniMax-M2 Q3/Q4 (MiniMax, MIT) next to Qwen3-235B |

Rules for the catalogue: never auto-pick an alternate; it is shown under "Other choices". Run a smoke test (load, chat, embed) per entry on the llama.cpp build we bundle before enabling it.
