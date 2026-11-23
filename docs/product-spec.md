# Portal AI Host — product spec (draft 0.1, 2026-11-20)

## 1. Summary
Portal AI Host is a small app for Windows (first) and macOS (second) that downloads and runs a local language model with one click and shares it with the owner's other apps: Voice Portal, PDF Portal Editor/Plans/Takeoff, Files Portal and future apps. Those apps can run on the same PC or on another device, for example a work laptop paired with a home PC.

Goals:
- No extra software for the user: no Ollama, Python or command line.
- One model on disk and in memory, shared by every app.
- Works from anywhere after a one-time pairing, end-to-end encrypted.
- Private by default: text stays on the user's own machines.

Non-goals (v1):
- It is not a chat app. A minimal "Try it" box is fine.
- No model training or fine-tuning.
- No audio processing in v1. Apps send text only. Speech-to-text comes later as a user option (section 13).
- No vision models in v1 (see section 12).

## 2. Users and scenarios
1. **Same PC:** Voice Portal on a desktop uses the host's bigger model for meeting minutes instead of its small built-in one.
2. **Home and office:** a work laptop running Voice Portal pairs with the home PC. Meetings at the office get minutes made by the home PC's model.
3. **Many apps:** PDF Portal asks questions about a PDF, and Files Portal summarises documents. Both use the same host.
4. **Developer:** the owner builds new apps against one standard API.

Owner hardware for testing and default tuning:
- Home PC: 64 GB DDR5, AMD RX 6600 8 GB (Vulkan), no NPU.
- MacBook: M4 Pro, 24 GB unified memory.

## 3. Platforms
- **Windows 10/11, x64 and arm64.** WinUI 3 / .NET 10, MSIX, Microsoft Store, the same stack as the other Portal apps. The app sits in the tray when running.
- **macOS 14+, Apple silicon.** A menu-bar app (Swift/SwiftUI), Mac App Store or notarised direct download. Second phase.
- Both bundle the llama.cpp engine:
  - Windows: Vulkan for AMD, NVIDIA and Intel GPUs, with CPU fallback. Partial GPU offload, with the rest in RAM.
  - Mac: Metal.

## 4. One-click model setup
- **Hardware check on first run:** RAM, VRAM, GPU vendor and backend, free disk space, CPU cores.
- **Recommended model:** the biggest model that runs well, with a plain-language reason ("Best for your PC: quality ★★★★, minutes in about 3 min"). Other options are listed with size, speed and quality.
- **Default catalogue** (licences must be confirmed before shipping; only models allowed for commercial use without extra restrictions):

| Tier | Example | Size (Q4) | Default for |
|---|---|---|---|
| Large MoE | Qwen3-30B-A3B (Apache 2.0) | ~18 GB | PCs with 32 GB+ RAM |
| Medium | 14B-class instruct | ~9 GB | Macs with 16–24 GB, PCs with 16 GB RAM + 8 GB GPU |
| Small | 7–8B-class instruct | ~5 GB | 16 GB machines |
| Tiny | 3–4B-class instruct | ~2.5 GB | 8 GB machines |
| Embeddings | small embedding model | ~100 MB | always (search in long documents) |

- **Download:** resumable, with a hash check, a progress bar and a disk-space check first. It never deletes anything it did not create.
- **Updates:** the app offers "New recommended model available". The old model is kept until the new one is verified, and the user chooses when to remove it.
- **Memory:**
  - The model loads on first request and unloads after an idle timeout (setting).
  - A "Keep loaded" option is available.
  - On the Mac, the app keeps the machine awake only while it is shared and plugged in.

## 5. API for client apps
- **Standard:** OpenAI-compatible HTTP API, so any existing client library works.
  - `POST /v1/chat/completions` (streaming via SSE)
  - `POST /v1/embeddings`
  - `GET /v1/models`
- **Extra endpoints:**
  - `GET /host/status`: model loaded or not, queue length, hardware, version.
  - `GET /host/capabilities`: context length, embeddings available, maximum request size.
- **Local binding:** 127.0.0.1 only by default. Same-LAN and relay access are off until the user turns sharing on.
- **Authentication:**
  - Each client app gets a token, approved by the user on the host the first time ("Voice Portal on WORK-LAPTOP wants to use this host. Allow?").
  - Tokens are stored with DPAPI on Windows and in the Keychain on Mac, and can be revoked per device or app.
- **Requests:**
  - A fair queue across apps, with priority for interactive requests over batch work such as minutes.
  - Per-request timeout and cancellation.
- **Client helper:** a small .NET library (NuGet or shared source) for the Portal apps. It handles discovery, pairing, the relay, retries and falling back to the app's built-in model.

## 6. Pairing and remote access
- **Pairing:**
  - The host shows a pairing code and QR code, valid for 10 minutes. The client enters or scans it once.
  - Pairing exchanges long-term public keys (X25519) and a device ID. The code is used with a PAKE or similar so the relay never learns the keys.
- **Connection order:**
  1. Same LAN: direct (mDNS discovery plus TLS pinned to the paired key).
  2. Otherwise: through the Portal relay.
- **Relay:**
  - A small service we run. It is reached over HTTPS/WebSocket on port 443, so it works on most office networks.
  - It forwards end-to-end encrypted frames only (e.g. Noise protocol or libsodium boxes keyed from pairing). It cannot read prompts or answers.
  - It stores no content and no logs of content. It keeps only device IDs, a connection count and bytes, for abuse limits.
  - Rate limits per device; reconnects automatically.
- **Unreachable host:** the client shows "Home host offline — using this PC's model" and falls back.
- **Company devices:** before the first remote use from a device, show a short warning that work content will go to the user's personal host, so they should check their employer's rules.

## 7. Host app UI
- **Home:** status (Ready, Busy or Off), current model, loaded or idle, connected apps and devices, a "Share" switch.
- **Models:** recommended model, catalogue, download, remove, storage location.
- **Devices and apps:** paired devices, per-app access, revoke, "Pair a new device".
- **Settings:** start with Windows or at login, idle unload, keep awake while shared (Mac), GPU or CPU choice, maximum memory use, LAN sharing, relay sharing.
- **Try it:** one prompt box to check the model works.
- Follow the Portal apps' design language, with accessibility names on all controls. Translated into the same 21 languages.

## 8. Privacy and security
- **No analytics or usage telemetry.** The only thing that leaves the PC is a crash report.
- **Crash reports are on by default (opt-out).** A scrubbed report (error type and where it happened; never error text) is sent when the window or the host crashes. It never holds prompt or response text, file names or paths, machine or user names, IP addresses, request bodies or model folder paths. Settings > Privacy has a "Send crash reports" switch; turning it off stops reports at once and is never asked again. The welcome offers one pre-ticked option with a "What's sent" link to the privacy page. Reports are kept for 90 days. Contact: mindthaapp@gmail.com. The address of the crash-report service is added at build time and is not in the source; a build without it sends nothing.
- **No logging of request content.** Only counts and timing are logged.
- **Encryption:** TLS and E2E for all non-loopback traffic. Unpaired clients cannot reach the API.
- **Security review** before the first remote-access release, covering pairing, the relay protocol, token storage and the API surface.
- **Privacy policy page** in a public repo (pattern: `voice-portal-privacy`), kept in step with the app.

## 9. Business model (open)
Options:
- Host free, with AI features as part of each app's Pro.
- Host free locally, and "Host Pro" for remote pairing and the relay (pays for the relay).
Decide before the Store listing.

## 10. Phases
1. **Windows host, local only:** hardware check, model catalogue and download, API on loopback, client helper library, Voice Portal integration ("use Portal AI Host").
2. **LAN sharing and pairing.**
3. **Relay plus remote pairing,** security review.
4. **macOS menu-bar host.**
5. **Other apps join:** PDF Portal (ask questions about a PDF with page citations, summaries, data extraction), Files Portal.
6. **Speech-to-text (optional):** Whisper models on the host for the same PC and LAN (section 13).
7. **Later:** vision model for drawings and scans on big-RAM machines; optional cloud bring-your-own-key backend behind the same API.

## 11. Acceptance for phase 1
- Fresh Windows PC: install, recommended model chosen, download, "Try it" answers, all without other software.
- Voice Portal makes meeting minutes through the host, and falls back cleanly when the host is closed.
- Two apps using the host at once are queued fairly, with no crash and memory within the chosen limit.
- 60-minute soak test with repeated requests and load/unload cycles: no memory or handle growth.
- Destructive-code gate passes: the app deletes only files it created, inside its own model folder.

## 12. Open questions
- Final model choices and licence check.
- Relay hosting: provider, region, cost cap.
- Business model (section 9).
- Name check for the Store.
- Whether Voice Portal's built-in text model should migrate to the host when both are installed, to avoid two copies.

## 13. Speech-to-text (later, optional)
Every part of this is a user option and off by default.
- **Engine:** whisper.cpp (MIT), bundled beside llama.cpp. Whisper models (MIT) such as large-v3 and large-v3-turbo come from the same data-driven catalogue, with the same hardware-based recommendation.
- **API:** the OpenAI-compatible `POST /v1/audio/transcriptions`, with the same tokens, queue and priorities as text.
- **Reach:**
  - Same PC and LAN first.
  - Audio over the relay is a separate opt-in switch, off by default, because of size (~30–60 MB per hour) and privacy. If enabled, it carries the same E2E encryption and per-device limits.
- **Clients:** Voice Portal keeps its built-in Whisper. Users can choose "Use Portal AI Host for transcription" when the host is reachable. Live dictation stays on-device by default.
- **Privacy:** no audio or transcript is stored or logged on the host; audio is deleted from memory once transcribed.
