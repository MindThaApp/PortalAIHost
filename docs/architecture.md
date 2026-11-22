# Portal AI Host: phase-1 architecture

Status: draft 1, 2026-11-20. Scope: Windows only, loopback only. Read with `product-spec.md` (the what) and
`api-v1.md` (the client contract). This file holds the decisions, each with a short reason.

## 0. Shape in one picture

```
 Client apps (Voice Portal, ...)        Portal.AiHost.Client (NuGet)
        |  HTTP 127.0.0.1:41870, Bearer token
        v
 PortalAIHost.Service.exe  (headless, NativeAOT, tray icon)       PortalAIHost.App.exe (WinUI 3)
   Portal.AiHost.Server   Kestrel loopback listener  <--named pipe (admin)-->  window, approval prompt
   Portal.AiHost.Core     auth, fair queue, catalogue, store, download, planner
   Portal.AiHost.Engine   supervisor --> llama-server.exe (chat)       127.0.0.1:<random>, --api-key <random>
                                     --> llama-server.exe (embeddings)  127.0.0.1:<random>, CPU
```

Every library above the two executables is UI-free (section 9).

## 1. Engine integration

**Decision: Option A, the bundled `llama-server.exe` as a supervised child process.** Reasons:
- **Crash isolation.** A Vulkan driver fault or native assert kills the child, not the host; the tray, queue and
  client connections survive and the host restarts the engine.
- **Clean unload.** Ending the process returns all RAM and VRAM. In-process unloads (LLamaSharp/llama.dll) leave
  driver allocations behind, which the 60-min "no growth" soak would catch.
- **Upstream features for free:** continuous batching, parallel slots, prompt cache, SSE, stop-on-disconnect,
  JSON-schema grammars, tools via `--jinja`, embeddings, `/tokenize`, split GGUF loading and MoE expert offload
  (`--n-cpu-moe`, `--override-tensor`). LLamaSharp trails upstream by months; 60-250 GB MoE models need the newest.
- **Backend swap = folder swap**, and Voice Portal already runs packaged engine child processes the same way.

Option B is rejected. A chat app that embeds the engine reuses Option A through the libraries (section 9).

**Public API: a .NET Kestrel front-end proxies to an internal llama-server; the engine is never exposed.**
- **Engine binding:** `127.0.0.1`, random free port, random per-launch `--api-key` (no local bypass of host auth),
  `--no-webui`.
- **The front-end owns** tokens, the fair queue, load-on-demand, stable error codes across engine upgrades, `/host/*`,
  size limits and Origin/Host checks. LAN TLS and the relay (phases 2-3) attach here only.
- **Hand-written forwarding** for four routes: the body is inspected and rewritten (model alias, `max_tokens` clamp,
  reasoning switch), so YARP adds little. `ResponseHeadersRead`, SSE streamed through unbuffered.
- **Cancellation:** the client's `RequestAborted` cancels the upstream call and llama-server frees the slot. WP2
  proves it for stream and non-stream on the pinned build (slot free within 1 s); if non-stream fails, the proxy
  always streams upstream and aggregates.

**MSIX constraints:**
- `Engine\vulkan\` and `Engine\cpu\` inside the package, each with `llama-server.exe`, `llama.dll`, `ggml*.dll` and
  the `ggml-cpu-*` variants. Children of a `runFullTrust` packaged process start from `WindowsApps` and inherit
  identity. No restricted capability beyond `runFullTrust` (no `unvirtualizedResources`, `packagedServices`,
  `allowElevation`).
- `vulkan-1.dll` comes from the GPU driver and is never bundled; if absent, the probe fails and CPU is used.
- Official llama.cpp release zips pinned by tag + SHA-256 in `engine/engine.lock.json`, fetched and verified by
  `scripts/fetch-engine.ps1` (gitignored output). MIT notice in THIRD-PARTY-NOTICES.
- x64: Vulkan + CPU. arm64: CPU only (no official Vulkan arm64 build; same call as Voice Portal).
- Store installs carry no mark-of-the-web, so SmartScreen does not apply; the package signature covers the exes.
  Loopback-only binding means no Firewall prompt: nothing binds `0.0.0.0` in phase 1 (a unit test asserts the args).
  PathMap for managed PDBs.

**Supervision (`EngineSupervisor`, one per role):**
- States `Stopped -> Starting -> Loading -> Ready -> Stopping`, plus `Failed`, serialised on one async lock; each
  change raises an event for the admin pipe and `/host/status`.
- Each child sits in a Job Object with `KILL_ON_JOB_CLOSE`, so a host crash never orphans a 20 GB engine.
- `GET /health` every 500 ms while loading (timeout 60 s + 10 s per GB); heartbeat every 10 s when ready; three
  misses mean kill and restart.
- Crash: in-flight requests get `engine_restarting`; restarts back off 1 s, 5 s, 30 s; three crashes in 10 min mean
  `Failed`. Crashes on Vulkan mark Vulkan bad for (adapter, driver, engine build) and restart on CPU with a notice.
- stdout/stderr go through a line filter into a capped ring buffer; raw engine output never reaches disk (it can
  hold prompt text).
- A `.running` marker per run; two unclean exits in a row start in safe mode (no auto-load, banner). After resume
  from sleep, a failed health check (Vulkan device lost) restarts the engine.

**Vulkan or CPU at runtime:** the user setting (Auto/GPU/CPU) first. Auto probes
`Engine\vulkan\llama-server.exe --list-devices` (10 s timeout, own process, so a bad driver only kills the probe),
caches the result in `tuning.json` keyed by adapter LUID + driver + engine build, and picks CPU if the probe fails,
reports 0 MB, or Vulkan was marked bad.

**Engine-agnostic (so whisper.cpp, spec section 13, is an addition, not a rewrite):**
- `engine` (`llama-server`, later `whisper-server`) and `model_kind` (`chat`, `embedding`, later `transcription`) are
  open strings in Contracts and the catalogue; older hosts ignore unknown kinds.
- `IEngineAdapter` per engine kind: argument list from a `Placement`, readiness and backend probe, upstream routes.
  `EngineSupervisor` is generic over it; one supervisor per active role.
- A route table maps endpoint -> model kind -> supervisor; a disabled or absent kind answers `feature_disabled` or
  `no_model`. The scheduler queues per role with shared fairness rules and a per-job cost estimate. The planner
  budgets RAM/VRAM through one ledger across all loaded roles.

## 2. Host process model

**Decision: two executables in one MSIX package.**
- **`PortalAIHost.Service.exe` (the host):** headless, NativeAOT (Kestrel `CreateSlimBuilder`, source-generated JSON
  only). Owns the API, engines, downloads, tokens, the tray icon (plain `Shell_NotifyIcon` on its own message-loop
  thread) and the admin pipe. Second `<Application>` in the manifest with `AppListEntry="none"`. Not a Windows
  service (`packagedServices` is restricted and needs admin).
- **`PortalAIHost.App.exe` (the window):** WinUI 3, started from Start, the tray or an approval request. Closing the
  window ends this process; the host keeps serving.

Why: WinUI fail-fasts (0x800710DD class, lessons file) cannot take down the API clients are using, idle memory
stays small, and the phase-4 macOS host mirrors the split.

- **Startup task:** `desktop:StartupTask` on the Service application, offered as "Start with Windows" and enabled at
  the end of first-run setup (`RequestEnableAsync`). `DisabledByUser` shows the Task Manager hint; never fought.
- **Single instance (host):** the admin pipe `\\.\pipe\PortalAIHost.Control.<UserSID>` with
  `FirstPipeInstance | CurrentUserOnly`. A second launch sends `show` and exits. The SID separates users (pipe names
  are machine-wide; fast user switching, RDP).
- **Single instance (UI):** `AppInstance.FindOrRegisterForKey("main")` with redirect; release the key on shutdown.
- **Starting each other:** the UI starts the host if the pipe is absent; the host opens the UI by AUMID activation
  for approval prompts. Protocol `portal-ai-host:` lets clients start the host; it accepts only `start` and `open`.
- **Window closed:** everything keeps serving. Only tray "Quit" stops the host: 5 s stream grace, engines stopped,
  own discovery file deleted.
- **Admin only over the pipe** (approve, revoke, download, settings, Try it), using `Portal.AiHost.Contracts.Control`
  messages. No admin route exists on HTTP, so phase 2-3 network exposure cannot reach it.

## 3. API contract v1 (decisions; the full contract is `api-v1.md`)

- **Port:** default `127.0.0.1:41870` (also `[::1]` if free), clear of the excluded 50000s and the dynamic range
  (49152+). Busy: 41871-41879, then OS-assigned. The real port is always in the discovery file.
- **Discovery file:** `%LOCALAPPDATA%\Packages\<HostPFN>\LocalState\discovery\host.json`, written atomically. It is
  the host's real LocalState, so AppData virtualisation does not hide it from other same-user apps. Client order:
  env override, file, default-port `GET /host/ping`; `instance_id` must match, which catches stale files. *WP8 spike:
  confirm a packaged client can read it; fallback is a per-user read-only pipe returning the same JSON.*
- **Auth:** per-app bearer tokens `pah_<43 base64url chars>`.
  - `POST /host/auth/requests` creates a pending request; the host shows "Voice Portal wants to use Portal AI Host.
    Allow?"; the client long-polls `GET /host/auth/requests/{id}`; the token is returned once.
  - The host maps the caller's TCP connection to a PID (`GetExtendedTcpTable`), then to its package family name or
    exe path + signer, and shows "verified: MindThaApp.VoicePortal" or "unverified: C:\...\x.exe". A caller running
    as another Windows user is refused.
  - The host stores only HMAC-SHA256 of tokens (DPAPI-protected host key) plus metadata (app id, name, PFN,
    `device_id` = `local`, created, last used). Clients store tokens with DPAPI (CurrentUser, entropy = app id).
  - Revoke per app in "Devices and apps" (`token_revoked`) or by the client (`DELETE /host/auth/token`). A denied app
    is auto-denied for 24 h unless re-allowed.
  - Threat model: loopback trusts the same Windows user. Tokens give consent, attribution and revocation, not a
    defence against malware already running as that user.
- **Browser defence:** requests with `Origin` are refused, `Host` must be `127.0.0.1|localhost|[::1]:<port>`, and
  POSTs need `Content-Type: application/json` (forces a CORS preflight that never succeeds). Blocks web pages and
  DNS rebinding.
- **Fair queue (`FairScheduler`):** N engine slots (`--parallel`, 1-4 by memory). Interactive before batch;
  round-robin across app ids within a class; with N >= 2 batch never takes the last free slot; batch waiting 5 min
  or more is promoted; 8 queued per app, 64 per host, then `429 queue_full`. Clients set `X-Portal-Priority:
  interactive|batch` (default interactive) and `X-Portal-Deadline-Ms`. No pre-emption, so batch clients send chunks
  of 2 min or less.
- **Transport seam (phases 2-3):** `ApiPipeline` takes a parsed request + `ClientPrincipal` (app id, device id,
  transport) and writes to an `IResponseSink`. Kestrel endpoints are thin adapters; LAN TLS adds a listener; the
  relay feeds decrypted frames into the same pipeline.

## 4. Model storage and download

- **Default location:** `ApplicationData.Current.LocalFolder\models`, which is the real
  `%LOCALAPPDATA%\Packages\<PFN>\LocalState\models`: not virtualised, removed on uninstall.
- **User-chosen location** (with a hint such as "this model needs 140 GB; C: has 60 GB free"): the host creates its
  own subfolder `Portal AI Host Models\` with a marker `.portal-ai-host-store` holding a store GUID. It never adopts
  an existing folder. FAT32 is refused (4 GB file limit); NTFS and exFAT are fine.
- **Moving the store:** copy each file, verify SHA-256, switch, then offer to remove the old copies file by file.
- **Inventory:** `store.json` lists every file the host created (path, size, sha256, catalogue id,
  `partial|verified|in_use`).

**Destructive-code rules (acceptance gate, spec section 11; checked in review):**
- The host never overwrites or deletes a file it did not create. "Created by the host" means listed in `store.json`
  *and* inside a store folder whose marker GUID matches.
- No recursive deletes, except on a folder the host created and that holds its marker; even then listed files go
  first and the folder is removed only if empty.
- Moves are copy, then verify, then delete, never delete first. New files are written as `*.part` and renamed with
  `overwrite: false`; a name clash gets `name (2).ext`, never a replace.
- User-added GGUFs (later "Add a model file") are `origin: external`, used in place; "Remove" only forgets them.
- Upgrades keep the old model until the new one is hashed, test-loaded and has answered a probe prompt; then the user
  chooses whether to remove the old one.
- Discovery file, settings, logs and `.part` files are host-created files in LocalState under the same rules.

**Download (`ModelDownloader`, built for 60-250 GB):**
- **Split GGUFs:** an entry lists one or more files (`-00001-of-0000N.gguf`; llama-server loads from part 1). Each part
  is its own resumable unit with size + SHA-256; two parts download in parallel.
- **Resume:** HTTP Range + `If-Range` (ETag) with a `.part.json` sidecar (URL, size, hash, ETag, bytes done).
  Hashing is incremental; on resume the existing prefix is re-hashed first. Survives restarts, reboots, sleep and
  network loss; retries at 5 s, 30 s, 2 min, then every 10 min.
- **URLs:** Hugging Face `resolve/<commit-sha>/...` (immutable) plus optional mirrors. Hash mismatch: delete own
  `.part`, retry once from zero, then stop with "file damaged at source".
- **Disk:** free space >= remaining + 2 % + 2 GB before start, re-checked every 1 GB; low space pauses.
- **Long runs:** sleep blocked (`ES_SYSTEM_REQUIRED`) only while downloading and the setting is on; metered warning,
  bandwidth cap, total ETA across parts.

**Catalogue (data-driven, no hard-coded tiers):**
- `catalogue/catalogue.json` here is the source; published to the public repo `MindThaApp/portal-ai-host-catalogue`
  (GitHub Pages) with a detached `catalogue.json.sig`. A copy ships in the package as the offline baseline.
- ECDSA P-256/SHA-256 (built into .NET). Two public keys (current, next) compiled in; private key offline with the
  owner (`scripts/sign-catalogue.ps1`). `catalogue_version` never goes backwards; refreshed at start and every 24 h;
  bad or unsigned copies are ignored.
- **Entry fields:** `id`, `display_name`, `family`, `engine`, `kind` (open: `chat|embedding`, later
  `transcription`), `licence` {spdx, url, commercial_ok}, `quality` (numeric, comparable), `params_total_b`,
  `params_active_b`, `quant`, `files[]` {name, size, sha256, urls[]}, `memory` {`weights_bytes`, `non_expert_bytes`,
  `expert_bytes`, `n_layers`, `kv_bytes_per_1k_ctx` (f16), `compute_overhead_bytes`}, `context_max`,
  `context_default`, `features` (`tools`, `json_schema`, `reasoning`), `min_host_version`, `recommended_sampling`.
- No raw engine arguments from the network: typed fields only, mapped by host code to known flags.
- `GgufReader` reads headers itself (layers, per-block tensor sizes, expert tensors) to cross-check the catalogue and
  to plan external models.

## 5. Memory policy and scaling

**Hardware check (`HardwareProbe`):** RAM total/available; per GPU name, vendor, LUID, driver, VRAM (DXGI
`DedicatedVideoMemory` + `QueryVideoMemoryInfo` budget, cross-checked with the Vulkan probe); CPU cores (P/E) and
arch; free space and file system per drive. Output: `HardwareProfile` + fingerprint.

**Recommender** (pure function, table-tested from 8 GB laptops to 32 GB GPU/128 GB RAM PCs and 256 GB Macs):
1. For each entry that fits on disk, ask the planner for the best placement.
2. Keep entries that fit with headroom: max(4 GB, 15 %) of RAM free, 10 % of VRAM free, within the user's
   "maximum memory use".
3. Estimate generation speed from active parameters and where they sit (GPU vs RAM bandwidth); drop below 8 tok/s.
4. Pick the highest `quality`, ties by speed. The plain-language reason comes from the same numbers.

Bigger hardware needs no code change: a 250 GB MoE simply starts to fit.

**Planner (`PlacementPlanner` -> `Placement` {backend, devices[] with layers, gpu_layers, n_cpu_moe/override rules,
context, parallel, kv type}):**
- VRAM budget = live free budget at load time minus reserve, within the cap.
- Dense: `gpu_layers = floor((budget - output/embedding tensors - compute_overhead) / (layer_bytes +
  kv_per_layer(ctx)))`, clamped to `0..n_layers`.
- MoE: non-expert tensors + KV on the GPU (`-ngl 999`), experts of the first K layers on the CPU (`--n-cpu-moe K`),
  smallest K that fits. Fits Qwen3-30B-A3B on the 8 GB RX 6600 + RAM, and a 235B-class MoE on 32 GB GPU + 128 GB RAM.
- RAM check: CPU-side weights (mmapped), CPU KV and buffers must fit available RAM minus headroom, else
  `insufficient_memory` (no mmap thrash).
- Context grows with memory: from `context_default`, doubling up to `context_max` while it fits (GPU layers win over
  context on small GPUs); q8_0 KV above 32K. `--parallel` grows with spare memory; per-slot context is advertised.
- Multi-GPU later: `devices[]` is already a list (phase 1 fills the largest GPU); later mapped to `--tensor-split`
  /`--device` with no API change.
- Out-of-memory ladder on load failure: GPU layers -25 %, more MoE layers to CPU, half context, CPU only. The first
  working config is stored in `tuning.json` per (model, hardware fingerprint, engine build).

**Lifecycle:** load on first request or `/host/warmup` (requests wait in the queue within their deadline). Idle
unload stops the engine process after the timeout (default 10 min, 5 min on battery), counted from the end of the
last request or stream. "Keep loaded" disables idle unload; a Windows low-memory notification
(`CreateMemoryResourceNotification`) still unloads an *idle* engine. Embeddings run in their own supervisor on CPU
(about 100 MB, no VRAM contention) with their own idle timer.

**Hardware re-check:** at each start the fingerprint (RAM, GPU set, VRAM, driver, engine build) is compared; on a
change, or "Re-check hardware" in Settings, probe and recommender re-run. The active model is always re-planned (no
download). If a better model now fits, Home shows "Upgrade available: <model>, quality ★★★★★, 48 GB download". The
old model stays until the new one is verified and the user agrees.

## 6. Client library `Portal.AiHost.Client`

- **Target.** `net10.0`, trimming/AOT-safe (source-generated JSON), no WinAppSDK dependency. DPAPI is behind
  `ITokenStore`, so a Keychain store can be added for macOS clients later.
- **Public surface:**
  - `AiHostClient.Create(AiHostClientOptions)`: `AppId` (reverse-DNS, stable), `AppDisplayName`, `TokenStore`,
    `Fallback` (`IAiFallback?`), `Retry`, `DefaultPriority`, `DiscoveryOverride`, `Logger`.
  - `State` + `StateChanged`: `NotInstalled | NotRunning | NeedsAccess | Denied | Revoked | NoModel | Ready`.
  - Host calls: `CheckAsync()` (never throws), `RequestAccessAsync(wait, ct)`, `ForgetAccessAsync()`,
    `TryStartHostAsync()` (protocol launch), `GetStatusAsync`, `GetCapabilitiesAsync`, `WarmUpAsync`,
    `CountTokensAsync`.
  - Model calls: `ChatAsync`, `StreamChatAsync` (`IAsyncEnumerable<ChatDelta>`), `EmbedAsync`, each with optional
    `RequestOptions` {Priority, Deadline, LoadBehavior, AllowFallback}. Results carry `Source` (`Host|Fallback`) and
    `FallbackReason` for messages like "Portal AI Host is closed — using this PC's model".
  - `BaseAddress` and `GetBearerToken()` let an app use any OpenAI SDK instead.
- **Discovery.** Follows section 3. The result is cached and re-discovered once on connection failure.
- **Tokens.** Never requested implicitly. `RequestAccessAsync` is called from a user action ("Use Portal AI Host" on),
  so the host never pops prompts by surprise. On `token_revoked` or `token_invalid`, the token is deleted, the state
  goes to `Revoked`, and the client falls back.
- **Retry policy.** Retried: connection refused (re-discover once), `engine_restarting`, `model_loading`,
  `queue_full`, with jittered backoff 250 ms to 4 s, honouring `Retry-After`, inside the deadline, at most 3 tries.
  Never retried: 400/401/403/413. Nothing is retried or sent to fallback after the first streamed token.
- **Fallback hook.** `IAiFallback` (chat, stream, optional embed) is the app's built-in model. Embedding fallback is
  off by default, because vectors from different models do not mix. `EmbedResult.ModelId` must be stored with the
  vectors.
- **Sharing: NuGet**, not source copies. There are two packages, `Portal.AiHost.Contracts` and
  `Portal.AiHost.Client`, on a private GitHub Packages feed (works locally and in cloud CI). Packages are built into
  `builds\nuget\` here. SemVer, with the major version tied to `api_version`.

## 7. Solution layout and tests

```
PortalAIHost.slnx, Directory.Build.props, Directory.Packages.props, global.json
src/
  Portal.AiHost.Contracts/          API + control-pipe DTOs, error codes, JSON contexts (net10.0)
  Portal.AiHost.Core/               Abstractions/, Api/ (ApiPipeline), Auth/, Scheduling/, Catalogue/,
                                    Store/, Download/, Planning/, Settings/  (UI-free, OS calls via interfaces)
  Portal.AiHost.Engine/             Supervisor/, Launch/ (args, Job Object), Probe/, Proxy/, Gguf/
  Portal.AiHost.Platform.Windows/   Hardware/ (DXGI, memory), Identity/ (TCP owner, package id, DPAPI),
                                    Power/, Tray/, Pipes/
  Portal.AiHost.Server/             Kestrel loopback listener, endpoint adapters, discovery file, Origin/Host guard
  PortalAIHost.Service/             host exe: composition root, single instance, admin pipe server (thin)
  PortalAIHost.App/                 WinUI 3 exe + Package.appxmanifest (two Applications, StartupTask, protocol)
  Portal.AiHost.Client/             client library (NuGet)
tests/
  Portal.AiHost.FakeEngine/         exe that mimics llama-server: /health, chat (stream + non-stream),
                                    embeddings, /tokenize, --list-devices; modes: slow-load, crash, hang,
                                    oom-on-load, slow-tokens
  Portal.AiHost.Core.Tests/         scheduler fairness, auth, catalogue signature/rollback, planner/recommender
                                    tables, store delete rules, downloader against a local Range server
  Portal.AiHost.Engine.Tests/       supervisor state machine + restart/backoff/OOM ladder against FakeEngine
  Portal.AiHost.Server.Tests/       in-process host + FakeEngine; golden JSON for every api-v1 example
  Portal.AiHost.Client.Tests/       client against a stub host (no dependency on Server)
  PortalAIHost.Soak/                60-min soak driver
catalogue/  engine/engine.lock.json  scripts/ (fetch-engine, sign-catalogue, build-msix)  builds/ (gitignored)
```

**Testing:**
- **Default tests** use FakeEngine (no GPU, no model) and run with `dotnet test --solution PortalAIHost.slnx`. One
  `RealEngine`-tagged smoke test with a tiny GGUF from `%TEMP%` checks args, cancellation and embeddings on the
  pinned build.
- **Test isolation (fail closed).** `PORTALAIHOST_TEST_DATA_ROOT` must contain `.portal-ai-host-test-root`, or the
  host exits with code 3. Test mode uses ports 41890-41899, never writes the real discovery file or startup task, and
  runs from the tester's own `%TEMP%` copy.
- **Soak (spec section 11)**, started detached, results to a file. 60 min, real engine, small model:
  - **Load:** three simulated apps (interactive stream, batch, embeddings), 10 % random cancels, a 60 s idle timeout
    (about 15 unload/load cycles), two injected engine kills, one OOM-ladder trip.
  - **Sampled every 30 s:** host private bytes, handles, threads, and engine processes (matched by full command line).
  - **PASS when:** the private-bytes slope (min 20-60, after warm-up) is under 5 MB/h; handles grow by 50 or fewer; no engine is
    left after each unload or at the end; every request ends in an expected state; interactive p95 queue wait is 2 s
    or less under batch load. A FakeEngine variant runs in routine checks.
- **Acceptance (spec section 11):** a two-app fairness test (Server.Tests), a memory-cap test (Core.Tests) and the
  destructive-gate review of WP3.

## 8. Phase-1 work packages

WP1 lands first (about one day). It defines every interface the others code against, so WP2-WP7 can then run in
parallel. Each package owns only the listed paths, and changes to another package's files go through its owner.

| WP | Scope | Owns | Depends on |
|---|---|---|---|
| WP1 Foundation | slnx/props, `Contracts` (all api-v1 DTOs, error codes, control-pipe messages), `Core/Abstractions` (IEngineSupervisor, IModelStore, IHardwareProbe, IClientRegistry, IScheduler, IResponseSink), FakeEngine, empty test projects | root build files, `src/Portal.AiHost.Contracts/**`, `src/Portal.AiHost.Core/Abstractions/**`, `tests/Portal.AiHost.FakeEngine/**` | none |
| WP2 Engine | supervisor, args builder (loopback-only assert), Job Object, Vulkan/CPU probe, proxy, cancel proof, GGUF reader, fetch-engine script + lock file | `src/Portal.AiHost.Engine/**`, `tests/Portal.AiHost.Engine.Tests/**`, `engine/**`, `scripts/fetch-engine.ps1` | WP1 |
| WP3 Models | catalogue (schema, ECDSA verify, rollback), store + destructive rules, downloader (split, resume, hash, disk), hardware probe, recommender, planner, re-check | `Core/{Catalogue,Store,Download,Planning}/**`, `Platform.Windows/{Hardware,Power}/**`, `catalogue/**`, `scripts/sign-catalogue.ps1`, matching tests | WP1 |
| WP4 Auth and queue | `ApiPipeline`, request validation and limits, token issue/HMAC/revoke/deny-cooldown, caller identity (TCP owner, PFN), fair scheduler | `Core/{Api,Auth,Scheduling,Settings}/**`, `Platform.Windows/{Identity}/**`, matching tests | WP1 |
| WP5 Server and host exe | Kestrel listener + port fallback, endpoint adapters, Origin/Host guard, SSE + `: portal` comments, discovery file, admin pipe server, single instance, tray, safe mode, test-root guard | `src/Portal.AiHost.Server/**`, `src/PortalAIHost.Service/**`, `Platform.Windows/{Tray,Pipes}/**`, `tests/Portal.AiHost.Server.Tests/**` | WP1; integrates WP2-4 |
| WP6 WinUI app | Home, Models (recommendation, download progress, location, upgrade), Devices and apps, Settings, Try it, approval prompt, first-run flow, string keys + pseudo-loc, accessibility names | `src/PortalAIHost.App/**` except the manifest | WP1 (control messages) |
| WP7 Client library | everything in section 6, NuGet pack into `builds\nuget\`, stub host for tests | `src/Portal.AiHost.Client/**`, `tests/Portal.AiHost.Client.Tests/**` | WP1 |
| WP8 Package and soak | **day-1 spike:** StartupTask on the `AppListEntry="none"` app, child exe from the package, another package reading `LocalState\discovery`; then manifest, x64/arm64 bundles, engine folders in the package, PathMap, build script, soak driver | `src/PortalAIHost.App/Package.appxmanifest`, `scripts/build-msix.ps1`, `tests/PortalAIHost.Soak/**` | WP1; soak needs WP5 |

- **Reviews.** WP3 (deletes, moves, downloads) and WP4 (tokens, caller identity) each get an independent code-only
  review before the first build.
- **Voice Portal integration** ("use Portal AI Host" plus fallback to its built-in model) follows in the Voice Portal
  repo, once WP7 publishes `1.0.0-preview`.

## 9. Reuse by other products (chat app)

- **Two layers.** The UI-free libraries (`Contracts`, `Core`, `Engine`, `Platform.Windows`, `Server`) are the
  product. `PortalAIHost.Service` and `PortalAIHost.App` are thin shells.
- **One composition entry.** `Core` exposes `AiHostRuntime` (catalogue, store, downloader, planner, supervisors,
  scheduler), started with `AiHostRuntime.StartAsync(AiHostRuntimeOptions)`. Callers either attach `Server` to it or
  call `ApiPipeline` in process.
- **Chat app as a client (preferred).** It uses `Portal.AiHost.Client` with `StreamChatAsync`. The v1 API already
  covers this: system prompts, multi-turn messages, SSE streaming, cancellation by abort, long context
  (`/host/capabilities` reports per-slot context, and `/host/tokenize` helps trim history), JSON-schema output,
  `reasoning_effort`, and tools/function calling passed through to llama-server (`--jinja`) when the model's
  catalogue `features` include `tools`.
- **Prompt reuse.** Multi-turn chats benefit from llama-server's prompt cache. The host forwards `cache_prompt`, and
  slot reuse is by prompt similarity.
- **Chat app standalone.** It references the same libraries and runs `AiHostRuntime` in its own process, with or
  without `Server`. It gets the same catalogue, download, hardware check, planner and supervision, with no fork.
  If a Portal AI Host is already running, the app should act as a client instead of loading a second model copy
  (spec goal: one model in memory).

## 10. Cross-cutting

- **Privacy.** No telemetry. Request content is never logged; logs hold only counts, timings, token ids (not tokens)
  and states. They live in LocalState `logs\`, rolling and capped at 10 MB.
- **Settings.** `settings.json` with a schema version, and a backup before every migration (lessons file).
- **Localisation.** Every UI string is a key (21 languages later); protocol strings and error codes are never
  localised.
- **Releases.** Store builds use version revision field 0. Two unclean exits trigger safe mode. Test windows are
  handled as the lessons file says.

**Open security items** (known gaps, not yet fixed):
- **Caller verification (WP4).** "verified" means only that the calling process has a package family name; the
  package signer is not checked. The process-id fallback, used when neither a package name nor an exe path is
  known, can match a reused process id. The caller's integrity level is not checked.
- **HTTP adapter limits (WP5).** The `Host` header allowlist, and a body limit enforced while the body is read
  (chunked bodies included), belong in the Kestrel adapter. `ApiPipeline` only checks the size of a body it is
  handed.

## 11. Open questions for the owner

1. Store name and identity reservation. The package family name is baked into client discovery.
2. Where the client NuGet lives: a private GitHub Packages feed (proposed), or public nuget.org for outside developers.
3. Whether the host starts with Windows by default after setup (proposed: on, with a clear switch).
4. The default idle unload time (proposed: 10 min, 5 min on battery).
5. Catalogue hosting on GitHub Pages and the owner holding the offline signing key.
6. Whether tool calling is promised in v1 (proposed: passthrough, flagged per model, best effort).

## 12. Open items

1. `scripts/sign-catalogue.ps1` reads an unencrypted PEM private key, and it neither checks the signing key against
   `CatalogueTrust` nor checks that `catalogue_version` is higher than the published copy before signing. Decide on a
   password-protected key (or a hardware key) and add both checks before the first published catalogue.
