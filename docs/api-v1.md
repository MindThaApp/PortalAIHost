# Portal AI Host API, version 1

Status: draft 1, 2026-11-20. Audience: teams building apps that use Portal AI Host (Voice Portal, PDF Portal, Files
Portal, a future chat app, third-party tools). This document is self-contained. .NET apps should use the
`Portal.AiHost.Client` NuGet package, which implements everything here, but any HTTP client works.

Phase 1 is **Windows, same PC only** (loopback). **LAN access** (a paired device on the same home network) uses the
same API and tokens over TLS; see [remote-v1.md](remote-v1.md). Internet relay access comes later.

## 1. Basics

- **Protocol:** HTTP/1.1 over `127.0.0.1` only. No TLS on loopback. All bodies are UTF-8 JSON.
- **OpenAI-compatible:** `/v1/chat/completions`, `/v1/embeddings` and `/v1/models` follow the OpenAI shapes, so
  existing OpenAI client libraries work if you set their base URL to `http://127.0.0.1:<port>/v1` and their API key
  to your Portal AI Host token.
- **Host extras:** live under `/host/...`.
- **Versioning:** this is API version `1`. Within v1, changes are additive only: new endpoints, new optional request
  fields and new response fields. **Clients must ignore unknown fields and unknown enum values.** A breaking change
  would ship as `/host/v2/...` and `api_version: "2"`, with v1 kept alongside.
- **Privacy guarantee for clients:** the host never stores or logs prompt or response content. It logs only counts
  and timings.

## 2. Finding the host (discovery)

Try these in order and use the first that answers correctly:

1. **Override:** the environment variable `PORTAL_AI_HOST_URL` (for example `http://127.0.0.1:41870`). For
   development only.
2. **Discovery file:** `%LOCALAPPDATA%\Packages\<HostPFN>\LocalState\discovery\host.json`.
   - `<HostPFN>` is the host's package family name. It is fixed once the Store identity is reserved, and the client
     library carries the list (Store build first, then the developer build).
   - Example file:
     ```json
     {
       "schema": 1,
       "service": "portal-ai-host",
       "api_version": "1",
       "base_url": "http://127.0.0.1:41870",
       "port": 41870,
       "pid": 18244,
       "instance_id": "6f1c2a9e4b7d4c0f9a51d3e8b2c47a10",
       "host_version": "1.0.0",
       "started_at": "2026-11-20T21:04:11Z"
     }
     ```
3. **Default port:** `http://127.0.0.1:41870`. If that port is taken, the host uses 41871-41879 or an OS-assigned
   port, and then only the discovery file knows the port.

**Validate** with `GET /host/ping`. You must get `service == "portal-ai-host"`, and if you read the discovery file,
the same `instance_id`. A mismatch or a refused connection means the file is stale and the host is not running.

**Host not running:** the app may start it by opening the URI `portal-ai-host:start`, then wait up to 10 s and
re-discover. If the host is not installed, that URI fails. Fall back to your built-in model, or show "Get Portal AI
Host".

## 3. Access tokens

Every endpoint except `/host/ping` and `/host/auth/requests*` needs `Authorization: Bearer <token>`.

**Getting a token (once per app, after a user action):**
1. Call `POST /host/auth/requests`. Do this only when the user has chosen to use the host (for example by switching
   on "Use Portal AI Host"), never silently at start-up.
   ```json
   { "app_id": "com.mindthaapp.voiceportal", "app_name": "Voice Portal", "app_version": "1.3.0",
     "reason": "Meeting minutes and summaries" }
   ```
   - `app_id` is stable reverse-DNS.
   - `app_name` and `reason` are shown to the user.

   The response is `202 Accepted`:
   ```json
   { "request_id": "ar_9d2f0c7b61e44a3f8b0e5c1a7d93e2f4", "status": "pending",
     "expires_at": "2026-11-20T21:14:11Z", "poll_wait_ms": 25000 }
   ```
2. The host shows: "Voice Portal wants to use Portal AI Host. Allow?" It also shows the verified package name or the
   exe path of the calling process.
3. Long-poll with `GET /host/auth/requests/{request_id}?wait_ms=25000`. The call returns when the status changes or
   when the wait ends. Repeat while `pending`. The request expires after 10 minutes.
   ```json
   { "status": "approved", "token": "pah_3hY...43chars", "token_id": "tk_51c0e2",
     "app_id": "com.mindthaapp.voiceportal" }
   ```

**Statuses:**
- `pending`, `approved` (the token is included **once**), `denied`, `expired`, and `claimed` (already approved and
  the token was already collected).
- After `denied`, new requests from the same `app_id` are refused with `403 access_denied` for 24 h, unless the user
  re-allows the app in the host.

**Storing the token:**
- Store it encrypted for the current user (Windows: DPAPI `CurrentUser`). Never log it.
- Tokens do not expire on a timer.

**Revocation:**
- The user can revoke an app at any time in the host. Your next call gets `401 token_revoked`.
- When that happens, delete the token, show "Access to Portal AI Host was removed", and do not request again until
  the user asks.
- `401 token_invalid` (for example after the host was reinstalled) is handled the same way.

**Signing out:** `DELETE /host/auth/token` revokes your own token. It returns `204`.

**Limits:**
- One pending request per `app_id`, and 10 requests per hour per app (`429 rate_limited`).
- Requests from a process running as a different Windows user are refused.

## 4. Request rules and headers

**Request headers (all optional except auth):**

| Header | Values | Default | Meaning |
|---|---|---|---|
| `Authorization` | `Bearer pah_...` | — | Required. |
| `X-Portal-Priority` | `interactive`, `batch` | `interactive` | Use `batch` for background work (minutes, bulk summaries). |
| `X-Portal-Deadline-Ms` | 1000–3600000 | 120000 interactive, 1800000 batch | Total time budget: queue + model load + generation. |
| `X-Portal-Load` | `wait`, `fail` | `wait` | If the model is not loaded: wait for it (within the deadline), or fail fast with `503 model_loading` while loading starts. |
| `X-Portal-Client-Request-Id` | up to 64 chars `[A-Za-z0-9._-]` | — | Echoed back, for your logs. |
| `X-Portal-Context-Tokens` | 512 up to `context_tokens` | — | Ask for a smaller working context for this request. Clamped, never an error (see below). |

**`X-Portal-Context-Tokens` (chat requests, host 1.0.2):**
- **What it does:** the engine's context size is fixed when the model loads, so the host cannot shrink it for one
  request. Instead the host clamps the asked value to 512 up to `context_tokens` (the context each request gets), then
  caps the reply length so that the prompt plus the reply stays inside that context. The cap goes on `max_tokens`
  (or `max_completion_tokens` when you sent that). It never raises a value you sent and never leaves less than 16
  tokens for the reply.
- **Prompt size:** the host estimates it as characters divided by three, plus four tokens per message, which errs on
  the high side. It does not count tokens exactly and does not reject a long prompt; a prompt longer than the asked
  context just gets the 16 token minimum reply.
- **Answer:** the effective value is sent back in a response header of the same name.
- **Ignored values:** a value that is not a whole number is ignored, as if the header were missing. A request without
  the header is not changed at all.

**Response headers:** `X-Portal-Request-Id`, `X-Portal-Queue-Ms` (time spent waiting), `X-Portal-Model` (the model
id that served the request) and `X-Portal-Capabilities-Revision`. When the revision changes, re-fetch
`/host/capabilities`.

**Other rules:**
- **Content type:** POST bodies must be sent with `Content-Type: application/json`.
- **No browser calls:** any request with an `Origin` header is refused (`403 forbidden_origin`). The `Host` header
  must be `127.0.0.1:<port>`, `localhost:<port>` or `[::1]:<port>`.
- **Body size:** at most `limits.max_request_bytes` from `/host/capabilities` (default 4 MiB).

**Queue and fairness:**
- The host runs a small number of parallel slots.
- Interactive requests go before batch ones, and apps take turns, so one app's backlog cannot starve another.
- One slot is kept free for interactive work whenever there is more than one slot.
- Running requests are never interrupted. **Batch clients should send pieces that finish in 2 minutes or less**
  (for example, chunked minutes followed by a merge step).
- Each app may have up to 8 queued requests. Beyond that, the host answers `429 queue_full`.
- **Per-app controls (set by the user on the host's Apps page).**
  - *Priority* is `high`, `normal` (default) or `low`. Within the interactive or batch class, a higher-priority app
    goes first, then apps take turns, then arrival order. A request that has waited 60 seconds moves up one level
    (and another level every further 60 seconds, up to `high`), so a `low` app is never starved.
  - *Pause*: while the user has paused your app, a new chat, embeddings or warm-up request is refused with
    `403 app_paused`. Requests already queued are not started until the user resumes; running requests finish.
    Their deadlines still apply. Pausing is not revoking: your token stays valid, so keep it.
  - Read your own state from `client.paused` and `client.priority` in `GET /host/status`.

## 5. Errors

All errors use the OpenAI shape, with two optional extras:

```json
{ "error": { "message": "The model is loading. Try again shortly.", "type": "unavailable",
             "code": "model_loading", "param": null,
             "retry_after_ms": 3000, "details": { "progress": 0.42 } } }
```

`Retry-After` (in seconds) is also set when a retry makes sense. Switch on `code`; the `message` text is not fixed
and may be localised later. Each code always comes with the same `type` (OpenAI-style), listed below.

| HTTP | `code` | `type` | Retry? | Meaning / what to do |
|---|---|---|---|---|
| 400 | `invalid_request` | `invalid_request_error` | no | Malformed JSON or field. `param` names the field. |
| 400 | `unsupported_parameter` | `invalid_request_error` | no | For example `n > 1`, image content, or `tools` on a model without tool support. |
| 400 | `context_length_exceeded` | `invalid_request_error` | no | `details`: `prompt_tokens`, `context_tokens`. Trim the history or split the input. |
| 401 | `auth_required` | `authentication_error` | no | The bearer token is missing. |
| 401 | `token_invalid` | `authentication_error` | no | Unknown token. Delete it and ask the user before requesting again. |
| 401 | `token_revoked` | `authentication_error` | no | The user removed access. Delete the token. |
| 401 | `device_unauthorized` | `authentication_error` | no | LAN only: the device is not paired (or was removed), or its request signature is not valid. `details.reason` says which ([remote-v1.md](remote-v1.md) section 4). |
| 403 | `pairing_failed` | `permission_error` | no | LAN only: a pairing step was refused. `details.reason` is `no_offer`, `expired`, `wrong_code`, `code_voided`, `invalid`, `no_session` or `too_many_devices` ([remote-v1.md](remote-v1.md) section 3). |
| 403 | `access_denied` | `permission_error` | no | The user denied the access request (24 h cool-down). |
| 403 | `model_control_required` | `permission_error` | no | LAN and relay only: this paired device does not have Model control, so it may not list, load or unload the PC's models. The PC owner turns it on per device. |
| 409 | `model_conflict` | `conflict_error` | no | A device model call conflicts with what is loaded. `details.reason` is `owner_changed`, `in_use_by_host`, `in_use_by_device` or `not_loaded_by_device` ([client-mode-v1.md](client-mode-v1.md) section 4.7). |
| 404 | `model_not_found` | `not_found_error` | no | LAN and relay only: the paired device may not use that model (its allowed-models list on the PC). |
| 503 | `device_paused` | `unavailable` | yes | LAN and relay only: the PC owner paused this device or the calling app. `retry_after_ms` is set. |
| 503 | `device_quiet_hours` | `unavailable` | yes | LAN and relay only: inside the device's quiet hours. `retry_after_ms` is the time until they end. |
| 503 | `host_busy_gaming` | `unavailable` | yes | LAN and relay only: gaming mode is on and a full-screen game or heavy GPU use is detected. |
| 429 | `device_limit_reached` | `rate_limit_error` | yes | LAN and relay only: today's request or token limit of the device is used up. `retry_after_ms` is the time until midnight on the PC. |
| 403 | `app_paused` | `permission_error` | no | The user paused your app in the host. Approval is kept. Requests that were already queued wait; running ones finish. Tell the user generation is paused, and retry only after `GET /host/status` shows `client.paused: false`. |
| 403 | `forbidden_origin` | `permission_error` | no | Browser origin or bad `Host` header. |
| 404 | `not_found` | `not_found_error` | no | Unknown route or request id. |
| 404 | `feature_disabled` | `not_found_error` | no | The endpoint exists in the API but is switched off on this host (for example a future feature). |
| 413 | `request_too_large` | `invalid_request_error` | no | The body is over `max_request_bytes`. |
| 429 | `queue_full` | `rate_limit_error` | yes | Back off for `retry_after_ms`. |
| 429 | `rate_limited` | `rate_limit_error` | yes | Too many access requests. |
| 503 | `no_model` | `unavailable` | no | No model of the needed kind is installed. `details.kind` is `chat` or `embedding`. Fall back and tell the user. |
| 503 | `model_loading` | `unavailable` | yes | Only with `X-Portal-Load: fail`. `details.progress` runs from 0 to 1. |
| 503 | `engine_restarting` | `unavailable` | yes | The engine stopped and is restarting. Retry shortly. |
| 503 | `insufficient_memory` | `unavailable` | no | The model cannot load in the memory available now. Fall back. |
| 503 | `host_stopping` | `unavailable` | no | The host is quitting. Fall back. |
| 503 | `auth_unavailable` | `unavailable` | yes | The host cannot read its token store right now (for example a locked file). Your token is not lost: keep it and retry shortly. |
| 503 | `remote_unreachable` | `unavailable` | yes | Client mode only: the PC that runs the AI cannot be reached now. `details.reason` is `offline`, `relay_host_offline`, `relay_busy`, `relay_admission`, `clock`, `refused`, `cert_changed`, `paused`, `quiet_hours`, `gaming_busy` or `limit_reached`; `retry_after_ms` follows the reconnect backoff. Use your own built-in AI meanwhile. |
| 504 | `deadline_exceeded` | `timeout` | no | `details.phase` is `queued`, `loading` or `generating`. |
| 400 | `consent_required` | `invalid_request_error` | no | Creating a voice needs `consent`: `own_voice` or `permission`. |
| 400 | `unsupported_audio` | `invalid_request_error` | no | The voice sample is not a readable MP3 or WAV file (or this host cannot read MP3 yet). |
| 404 | `voice_not_found` | `not_found_error` | no | No voice with that id. |
| 409 | `voice_in_use` | `conflict_error` | no | A speech job that is queued or running uses the voice. Cancel it or wait, then remove the voice. |
| 500 | `engine_error` | `server_error` | once | The engine failed unexpectedly. Retry at most once, then fall back. |
| 500 | `internal_error` | `server_error` | once | Unexpected host error. Retry at most once, then fall back. |

**Streaming errors:** if an error happens after a stream has started (HTTP 200 already sent), the host sends one
final event `data: {"error":{...}}` and closes the stream **without** `data: [DONE]`.

## 6. Endpoints

### `GET /host/ping` (no auth)
```json
{ "service": "portal-ai-host", "api_version": "1", "host_version": "1.0.0",
  "instance_id": "6f1c2a9e4b7d4c0f9a51d3e8b2c47a10" }
```

### `GET /host/status`
```json
{
  "api_version": "1",
  "host_version": "1.0.0",
  "instance_id": "6f1c2a9e4b7d4c0f9a51d3e8b2c47a10",
  "state": "ready",
  "client": { "app_id": "com.mindthaapp.voiceportal", "token_id": "tk_51c0e2", "approved_at": "2026-11-20T21:05:02Z",
             "paused": false, "priority": "normal" },
  "models": {
    "chat": {
      "id": "qwen3-30b-a3b-q4km", "display_name": "Qwen3 30B A3B",
      "state": "loaded", "loaded_since": "2026-11-20T21:06:40Z", "load_progress": 1.0,
      "keep_loaded": false, "idle_unload_seconds": 600
    },
    "embedding": { "id": "embed-small-q8", "display_name": "Small embeddings", "state": "unloaded",
                   "load_progress": 0.0, "keep_loaded": false, "idle_unload_seconds": 600 }
  },
  "queue": { "running": 1, "waiting": 3, "waiting_interactive": 1, "waiting_batch": 2, "yours_waiting": 0 },
  "hardware": {
    "os": "windows", "arch": "x64", "cpu_cores": 12, "ram_bytes": 68719476736,
    "gpus": [ { "name": "AMD Radeon RX 6600", "vendor": "amd", "vram_bytes": 8589934592, "backend": "vulkan" } ]
  },
  "upgrade_available": false,
  "sharing": { "lan": false, "relay": false },
  "usage": { "ram_used_bytes": 19327352832, "vram_used_bytes": 7516192768, "cpu_percent": 34,
             "generation_tokens_per_second": 21.5, "updated_at": "2026-11-23T21:40:02Z" }
}
```
- `state` is one of `ready` (loaded and idle), `busy` (generating), `loading`, `idle` (installed but not loaded; the
  next request loads it), `no_model`, `downloading` (no usable model yet) or `error`.
- `client.paused` is `true` while the user has paused your app (new generation requests get `403 app_paused`).
  `client.priority` is `high`, `normal` or `low`.
- `models.<kind>.state` is one of `unloaded`, `loading`, `loaded`, `failed` or `not_installed`. The keys under
  `models` are model kinds. Expect more kinds later (for example `transcription`) and ignore unknown ones. While a
  paired device holds a model in the second chat slot (Model control, [client-mode-v1.md](client-mode-v1.md) section
  4.7) an extra key `chat.2` appears with the same fields; it is absent otherwise. Treat `chat` as the PC's own model.
- `usage` is the live use of the engine process (read-only): `ram_used_bytes` (working set) and `vram_used_bytes` 
  (dedicated graphics memory), `cpu_percent` (share of the whole PC, 0 to 100), `generation_tokens_per_second` 
  (speed of the last finished request; missing until one has finished) and `updated_at`. It is sampled at most every 
  1.5 seconds and only while someone asks, so reading it often costs nothing extra. It is missing when the host cannot 
  read the engine. Ignore unknown fields.
- `memory` is the planned memory of every running engine against the user's RAM limit (read-only; missing until the
  host knows the PC's hardware): `ram_limit_bytes`, `ram_planned_bytes`, `ram_free_bytes`, `vram_total_bytes`,
  `vram_planned_bytes`, `vram_free_bytes` and `engines`, a list of `{ slot, model_id, ram_bytes, vram_bytes, loading,
  cpu_only }`. `slot` is `chat`, `chat.2`, `embedding` or `apps` (memory apps reserved with `reserved_bytes` on
  `POST /host/ease-off`). A model that is still loading counts from the moment its placement is decided
  (`loading: true`), so two loads started together cannot both claim the same free memory. A load that would not fit
  under `ram_limit_bytes` is refused with `503 insufficient_memory`; the message is a plain sentence and `details`
  carries `ram_limit_bytes` and `ram_planned_bytes`. These are the host's plan, not what the operating system
  reports. Ignore unknown fields.

### `GET /host/capabilities`
What the current model on this machine can do. It changes when the user switches models, when hardware changes or
the host re-plans memory; the revision number tells you when.
```json
{
  "api_version": "1",
  "capabilities_revision": 7,
  "chat": {
    "available": true,
    "model": {
      "id": "qwen3-30b-a3b-q4km", "display_name": "Qwen3 30B A3B", "family": "qwen3",
      "params_total_b": 30.5, "params_active_b": 3.3, "quant": "Q4_K_M", "size_bytes": 18600000000
    },
    "context_tokens": 32768,
    "context_tokens_max": 131072,
    "max_output_tokens": 8192,
    "parallel_slots": 2,
    "features": { "streaming": true, "system_prompt": true, "json_object": true, "json_schema": true,
                  "tools": true, "reasoning": true },
    "placement": { "backend": "vulkan", "gpu_offload": "partial", "gpu_layers": 48, "total_layers": 48,
                   "moe_expert_layers_on_cpu": 30 },
    "speed_estimate": { "prompt_tokens_per_second": 350, "generation_tokens_per_second": 22 }
  },
  "embeddings": {
    "available": true,
    "model": { "id": "embed-small-q8", "display_name": "Small embeddings" },
    "dimensions": 768,
    "max_input_tokens": 512,
    "max_batch_inputs": 64,
    "normalized": true
  },
  "limits": {
    "max_request_bytes": 4194304,
    "max_queued_per_app": 8,
    "default_deadline_ms": { "interactive": 120000, "batch": 1800000 },
    "max_deadline_ms": 3600000
  },
  "endpoints": ["chat.completions", "embeddings", "models", "tokenize", "warmup", "performance", "ease-off", "health",
                "voices", "speech"],
  "voice": { "cloning": true, "speech": true, "formats": ["wav"], "sample_formats": ["wav", "mp3"],
             "max_sample_seconds": 60, "max_text_chars": 20000 }
}
```
`endpoints` lists `"performance"` on hosts that have `GET /host/performance` (1.0.2 and later) and `"ease-off"` on
hosts that have `POST /host/ease-off` (1.0.3 and later), and `"health"` on hosts that have `GET /host/health`
(1.0.3 and later). Check for them before calling; older hosts do not list them.

`voice` appears on hosts that have the voice feature (section 6, Voice) and is absent on older hosts. `cloning` is
true when a voice can be made from a sample, `speech` when text can be read aloud with any voice. Both are `false` (and
`formats` is empty, with `reason: "no_model"`) while the voice engine or its models are not installed, so check them
before showing voice controls. `formats` lists the audio formats a speech job can produce: **WAV only for now. MP3
output is not offered**; it may be added later, and will then appear in this list, so never assume it.
`sample_formats` lists what a voice sample may be (`wav`, plus `mp3` when the host can decode it).

**How to use the fields:**
- `context_tokens` is the usable context **per request** right now. It grows on machines with more memory (up to
  `context_tokens_max`, the model's limit), so read it rather than assuming a size. Plan chunk sizes from it and
  from `speed_estimate`.
- **Model unavailable:** when there is no model of a kind, its section is `{ "available": false, "reason":
  "no_model" }`. Other reasons are `insufficient_memory` and `disabled`.
- **Unloaded models:** the numbers describe the model *as it will load*. Querying capabilities never triggers a load.
- **Feature flags:**
  - `features.tools`: the model and host support OpenAI tool calling (section 7).
  - `features.reasoning`: the model can produce separate reasoning text.
- **`speed_estimate`** is a planning hint, not a promise.

### `GET /host/performance` (host 1.0.2)
The limits the host runs under. Read-only: apps show these and link to the host's Settings, they never change them.
```json
{
  "preset": "balanced",
  "memory_limit_bytes": 54975581388,
  "keep_free_bytes": 8589934592,
  "compute": "auto",
  "compute_effective": "gpu",
  "gpu_memory_limit_bytes": 8053063680,
  "cpu_threads": 10,
  "priority": "low",
  "cpu_ceiling_percent": 100,
  "ease_off": ["fullscreen", "battery", "app_busy"],
  "eased_off_now": null,
  "fit": "fits_partial",
  "ranges": {
    "memory_limit_bytes": { "min": 2147483648, "max": 60129542144, "step": 1073741824 },
    "keep_free_bytes": { "min": 2147483648, "max": 34359738368, "step": 1073741824 },
    "gpu_memory_limit_bytes": { "min": 1073741824, "max": 8053063680, "step": 536870912 },
    "cpu_threads": { "min": 1, "max": 11 },
    "gpu_available": true
  },
  "settings_link": "portal-ai-host:settings/performance"
}
```
- `preset`: `quiet`, `balanced`, `fast` or `custom`. `compute`: `auto`, `gpu` or `cpu`; `compute_effective` is
  where the model really runs (`gpu` or `cpu`). `priority`: `low` (the engine runs below normal priority) or `normal`.
- `memory_limit_bytes` is also a hard cap on the engine process (a Windows job object), as well as the figure the
  host plans against. `keep_free_bytes` is memory the host leaves free for other programs.
- `gpu_memory_limit_bytes` and `ranges.gpu_memory_limit_bytes` are left out when the PC has no supported graphics
  card (4 GB of graphics memory or more); `ranges.gpu_available` says whether `gpu` can be chosen.
- `fit` is for the active chat model under these limits: `fits_fully`, `fits_partial`, `cpu_only`,
  `needs_smaller_model` or `too_big`. It is left out when no chat model is active.
- `memory_cap_active` is `false`, with a `memory_cap_reason`, when an engine could not start under the memory cap and
  was started once without it (the cap is `memory_limit_bytes` plus 1 GB or 10 %, whichever is larger).
- `cpu_ceiling_percent` (20 to 100, host 1.0.3; always `100` before) is the most of the PC's processor the engine may
  use, enforced as a hard cap on the engine process. `100` means no cap.
- `ease_off` lists the rules that are on (`fullscreen`, `gpu_busy`, `battery`, `app_busy`; empty before 1.0.3).
  `eased_off_now` is `null`, or the rule that is easing the host off right now (`app_busy`, `fullscreen`, `gpu_busy`
  or `battery`, in that order when several apply), so an app can say "Slower while a game is running". While the host
  eases off, the engine runs at the lowest priority and at most half of the processor (never above the user's own
  ceiling), then returns to its saved priority and ceiling when the reason ends. `gpu_busy` is accepted as a setting but
  is not detected yet in 1.0.3, so it never fires by itself.
- Changes apply live: the host re-plans, and if the loaded model no longer fits it reloads with the new placement or
  unloads. Watch `X-Portal-Capabilities-Revision`. Nothing is deleted; model files are never touched.

### `GET /host/health` (host 1.0.3)
Whether the host is working, in plain words, with a fix name where the host's own window has one. Needs a token.
Check for `"health"` in `endpoints` first. Read-only: it changes nothing and starts no engine.
```json
{
  "status": "warn",
  "needs_attention": 1,
  "checks": [
    { "id": "service", "status": "ok", "title": "Host service", "detail": "Portal AI Host is running and apps can connect." },
    { "id": "graphics", "status": "warn", "title": "Graphics card",
      "detail": "No supported graphics card was found, so models run on the processor. That works, but replies are slower." },
    { "id": "model", "status": "fail", "title": "Chat model", "detail": "...", "fix_action": "check_files", "fix_target": "chat-14b" }
  ]
}
```
- `status` is the worst check: `ok`, `warn` or `fail`. `needs_attention` counts the checks that are not `ok`.
- Each check has `id`, `status`, `title` and `detail` (one or two sentences meant for the user), and sometimes
  `fix_action` and `fix_target`. Clients ignore ids and fix names they do not know.
- Checks, in order: `service` (the API is listening), `engine_files` (the engine program files are there; fails
  without the processor build, warns without the graphics build), `graphics` (warns, never fails, when the model runs on
  the processor only), `model` (the active chat model: fails when a file check marked it damaged, when its files are
  gone, or when its engine failed to start), `disk` (the models drive: warns under 2 GB free, fails under 256 MB),
  `memory` (fails when the chat model does not fit the memory limit; the same fit words as `/host/performance`),
  `ease_off` (warns while the host is easing off, with the reason) and `memory_cap` (warns when an engine had to
  start without the memory cap).
- `fix_action` is one of `load_model`, `restart_engine`, `check_files` (`fix_target` is the model id),
  `open_performance` (`fix_target` is the `portal-ai-host:settings/performance` link) or `open_models`. Each names an
  operation the host's window already has; none deletes or overwrites a file. Apps do not run fixes over HTTP: they
  can show the `detail` and open the host's window with the link.

### `POST /host/ease-off` and `DELETE /host/ease-off` (host 1.0.3)
An app doing live, time-critical work (for example live dictation) asks the host to ease off batch work meanwhile.
Needs a token. Check for `"ease-off"` in `endpoints` first.
```json
{ "reason": "live_audio", "ttl_ms": 30000, "reserved_bytes": 1073741824 }
```
- `ttl_ms` is required (1000 to 120000, clamped). The request ends on its own when the time passes, so **renew it**
  (send it again) while the work goes on, for example every `ttl_ms / 2`. `DELETE /host/ease-off` ends it at once.
- `reason` is a short label of letters, digits, `_` and `-` (longer or other characters are dropped). It is never
  shown with any content.
- `reserved_bytes` (optional) is memory the app's own models hold; the host plans around it while the request lasts.
- Each app has one request. A repeat from the same app replaces its earlier one, and `DELETE` ends only the
  caller's own request (and is not an error when there is none). Both answer `204`. At most 64 apps may hold a
  request; a 65th gets `400 invalid_request`.
- The request only eases off when the `app_busy` rule is on in the host's settings; it is still stored when it is off.
- While it is active `GET /host/performance` reports `eased_off_now: "app_busy"`.

### `GET /v1/models`
OpenAI shape, with extra fields.
```json
{ "object": "list", "data": [
  { "id": "qwen3-30b-a3b-q4km", "object": "model", "owned_by": "portal-ai-host", "kind": "chat",
    "active": true, "loaded": true, "context_tokens": 32768 },
  { "id": "embed-small-q8", "object": "model", "owned_by": "portal-ai-host", "kind": "embedding",
    "active": true, "loaded": false }
] }
```
**`model` field in requests:**
- Send `"default"` (chat) or `"default-embedding"`.
- An active model's id is also accepted.
- In v1, any other name is served by the active model of the needed kind, and `X-Portal-Model` tells you which.
- Only active models serve requests. The user chooses the active model in the host.
- **Second chat model.** When a paired device with Model control holds a second chat model, `GET /v1/models` lists it
  too (same `kind: "chat"`, `active: true`), and a request that names it by id is served by it. Every other name still
  goes to the PC's own chat model. A device that names an installed model that is not loaded gets it loaded first when
  its grant and the conflict rules allow; otherwise it is served as above.

### `POST /host/warmup`
Body `{ "kind": "chat" }` (or `"embedding"`). It starts loading in the background and returns `202` with
`{ "state": "loading", "load_progress": 0.0 }`. If the model is already loaded, the response is `200` with
`{"state":"loaded"}`. Call it when the user opens a feature that will need the model soon.

### `POST /host/tokenize`
Counts tokens exactly as the active chat model will. Use it to trim chat history to `context_tokens`.

Request (either form):
```json
{ "model": "default", "messages": [ { "role": "system", "content": "..." }, { "role": "user", "content": "..." } ] }
{ "model": "default", "text": "plain text to count" }
```
Response:
```json
{ "tokens": 1534, "context_tokens": 32768 }
```
With `messages`, the count includes the chat template. Like any request, this loads the model if it is not loaded.

### `POST /v1/chat/completions`
OpenAI request shape. **Supported:**
- `messages`: roles `system`, `user`, `assistant` and `tool`. `content` is a string or an array of `{"type":"text"}`
  parts.
- `stream` and `stream_options.include_usage`.
- `max_tokens` / `max_completion_tokens`, clamped to `max_output_tokens`.
- `temperature`, `top_p`, `stop`, `seed`, `presence_penalty`, `frequency_penalty`.
- `response_format`: `{"type":"json_object"}`, or `{"type":"json_schema","json_schema":{...}}`, which constrains the
  output to the schema.
- `reasoning_effort`: `none` (the default: no reasoning text, which is fastest), or `low`, `medium` or `high`, which
  switch reasoning on where the model supports it. Reasoning text arrives in `reasoning_content`, separate from
  `content`.
- `tools`, `tool_choice` and `parallel_tool_calls`: see section 7.

**Not supported in v1:** `n > 1`, image or audio content parts, and `logprobs`. These return `400
unsupported_parameter`. Unknown fields are ignored.

**Non-streaming response:** OpenAI shape (`id`, `object: "chat.completion"`, `created`, `model`, `choices[0]
.message`, `finish_reason`, `usage`). `finish_reason` is one of `stop`, `length` or `tool_calls`.

**Streaming (`"stream": true`):**
- Server-Sent Events, `Content-Type: text/event-stream`. Each event is `data: <chat.completion.chunk JSON>`, and the
  stream ends with `data: [DONE]`.
- **Progress comments:** while a request waits in the queue or for a model load, the host sends SSE comment lines at
  least every 5 s. Standard SSE parsers ignore them, but apps may parse them to show "Waiting (2 ahead)..." or
  "Loading model 42 %...":
  ```
  : portal {"event":"queued","position":2}
  : portal {"event":"loading","progress":0.42}
  : portal {"event":"started"}
  ```
- Headers (HTTP 200) are sent as soon as the request is accepted into the queue, so a later failure arrives as an
  in-stream error event (section 5).
- With `stream_options.include_usage`, the last chunk before `[DONE]` carries `usage`.

**Cancellation:**
- Close the connection (for example, cancel the `HttpClient` request). The host removes a queued request, or stops
  generation within about 1 s and frees the slot.
- There is no separate cancel call, and no charge or side effect remains.
- Use this for "Stop" buttons and when the user leaves a screen.

**Timeouts:** set `X-Portal-Deadline-Ms`. When the deadline passes the host ends the request (`504
deadline_exceeded`, or an in-stream error). Set your HTTP client's own timeout slightly longer than the deadline, or
infinite for streams.

### `POST /v1/embeddings`
- **Request:** `{ "model": "default-embedding", "input": "text" | ["text", ...], "encoding_format": "float" |
  "base64" }`. At most `max_batch_inputs` items, each at most `max_input_tokens` tokens. Longer items get `400
  context_length_exceeded`, with the index in `param`.
- **Response:** OpenAI shape. Vectors are L2-normalised.
- **Store `model` with your vectors.** Vectors from different models must never be compared. Re-embed when
  `X-Portal-Model` differs from the stored id.

### Voice: cloning and text to speech (host 1.1)

Make a voice once from a short recording, then read any text with it. The library of voices lives in the host and is
shared by every Portal app on this PC and by paired devices, so a voice made in Voice Portal can be used by AI
Portal or Studio Portal. All routes need a token; paired devices sign requests as everywhere else. If the
`voice` capability says `speech: false` the jobs are refused with `503 no_model`.

**Consent.** Cloning a voice needs a statement of whose voice it is. `consent` is required and must be `own_voice`
(the person using this PC) or `permission` (the speaker agreed). The host stores it with the voice. Apps must ask the
user, never fill it in silently, and must not clone a voice without the speaker's permission.

**`POST /host/voices`** creates a voice. Body (JSON, so at most `max_request_bytes`, about 3 MB of audio):
`{ "name": "Anna", "consent": "own_voice", "sample_base64": "...", "sample_format": "wav" }`. The sample is an MP3 or WAV
file of 3 to 60 seconds of speech (10 to 30 seconds is best); `sample_format` is optional. For long WAV files send
mono 24 kHz, or MP3. The host stores its own converted copy (mono, 24 kHz, 16 bit) under a new unique id and never
keeps or changes the original. Answer `201`: `{ "id": "vc_...", "name", "consent", "created_at", "created_by",
"duration_ms", "sample_rate" }`.

**`GET /host/voices`** answers `{ "voices": [ ... ], "builtin": [ { "id": "af_heart", "name": "Heart (US, female)",
"language": "en-US" } ] }`. The built-in voices are fixed voices that need no sample (empty when that model is not
installed).

**`PATCH /host/voices/{id}`** with `{ "name": "New name" }` renames a voice. **`DELETE /host/voices/{id}`** removes it
(`204`). Both are allowed for the app that made the voice and for apps on the host PC; a paired device can change only its
own voices (`403 access_denied` otherwise).

**`POST /host/speech/jobs`** starts reading. Body: `{ "voice_id": "vc_..." | "builtin_voice": "af_heart", "text": "...",
"speed": 1.0, "format": "wav" }`. Send exactly one of `voice_id` and `builtin_voice`. `speed` is 0.5 to 2.0 (default 1);
`text` is at most `max_text_chars`. Answer `202` with the job: `{ "id": "sp_...", "status": "queued", "progress": 0,
"created_at" }`. Jobs run one at a time; each app may have 8 waiting (`429 queue_full`).

**`GET /host/speech/jobs/{id}`** returns `status` (`queued`, `running`, `completed`, `failed`, `cancelled`), `progress`
(0 to 1), `queue_position` while queued, and when completed `result`: `{ "format": "wav", "file_name", "bytes",
"duration_ms", "sample_rate", "path" }`. `path` is the file on the host PC and is set only for apps on that PC; every app
can fetch the audio itself with **`GET /host/speech/jobs/{id}/audio`** (`audio/wav`). A failed job has
`error: { "code", "message" }`. Jobs are visible only to the app that made them.

**`DELETE /host/speech/jobs/{id}`** cancels a queued or running job and returns it with status `cancelled`; a finished
job is returned unchanged. The result file is never deleted by the host.

Result files are written to the host's own output folder under a unique name (`speech-<time>-<job id>.wav`) and never
overwrite another file. Every file the host makes carries a WAV `LIST/INFO` comment `Made with Portal` (and software
`Portal AI Host`); keep it when you pass the audio on, and tell listeners that the voice is generated.

**Output formats.** WAV (16-bit mono, 24 kHz) only. MP3 output is not offered yet; the capability's `formats` list will say
when it is.

Client library: `GetVoiceCapabilitiesAsync`, `ListVoicesAsync`, `CreateVoiceAsync`, `RenameVoiceAsync`, `DeleteVoiceAsync`,
`StartSpeechAsync`, `GetSpeechJobAsync`, `CancelSpeechAsync`, `DownloadSpeechAsync` and `SpeakAsync` (start, poll, download).

## 7. Building a chat UI on the API

Everything a chat screen needs is in v1:
- **System prompts and history:** send the full `messages` list each turn. The API is stateless, and the host keeps
  no conversation. It reuses its prompt cache automatically when a new turn extends an earlier one, so long chats stay
  fast.
- **Long context:** budget with `/host/tokenize` against `capabilities.chat.context_tokens`, and drop or summarise the
  oldest turns.
- **Streaming and Stop:** use `stream: true` and close the connection on Stop.
- **Tool / function calling** (when `features.tools` is true):
  - Standard OpenAI `tools` (type `function`, with JSON-schema `parameters`) and `tool_choice` (`auto`, `none`,
    `required` or a named function) are passed to the engine.
  - Responses carry `message.tool_calls`, and streams carry `delta.tool_calls` fragments, as OpenAI does.
  - Your app runs the tool and sends the result back as a `{"role":"tool","tool_call_id":...}` message.
  - Quality depends on the model, and the host does not run tools itself.
- **Structured output:** use `response_format` with `json_schema`.

## 8. Client checklist

- Discover as in section 2. Cache the base URL, and re-discover once when the connection is refused.
- Request access only after a user action. Store the token encrypted, and never log it.
- Send `X-Portal-Priority: batch` for background jobs, and keep each request to 2 minutes or less.
- Set a deadline on every request.
- **Retry only these:** `queue_full`, `model_loading`, `engine_restarting` and a refused connection. Use jittered
  backoff from 250 ms to 4 s, honour `retry_after_ms`, stay within the deadline, and make at most 3 tries.
  - Never retry 400, 401, 403 or 413.
  - Never retry or switch to a fallback after the first streamed token. Show the error instead.
- **Fall back** to your built-in model on: host not installed or not running, `no_model`, `insufficient_memory`,
  `host_stopping`, a revoked or denied token, or a deadline before the first token. Tell the user, for example
  "Portal AI Host is closed — using this PC's model". Do not fall back for embeddings unless you re-embed everything
  with the fallback model.
- Ignore unknown JSON fields, unknown `: portal` comment events and unknown `models`/capabilities sections.

## 9. Reserved for later (do not use yet)

- **Speech to text:** `POST /v1/audio/transcriptions` (OpenAI-compatible, optional and off by default). Until it is
  enabled it returns `404 feature_disabled`. When enabled, a `transcription` section appears in
  `/host/capabilities` and `/host/status`.
- **Remote access over the internet:** the same endpoints and tokens, end-to-end encrypted through a relay, after
  the same one-time pairing. Not available yet.

**LAN access** is available (off by default, host 1.0.3): the same endpoints and tokens over TLS on the home network,
after one-time pairing. A token is tied to the device it was issued to: a loopback token does not work from a
device, and a device token does not work on loopback. The full protocol (pairing, request signing, certificate
pinning) is in [remote-v1.md](remote-v1.md).

## 10. Admin control pipe (the host's own window only)

Not for client apps. The window talks to the host over `\.\pipe\PortalAIHost.Control.<UserSID>` (current user
only). Messages are JSON envelopes `{ "id", "type", "payload" }`; answers are `ok` or `error`
(`{ "code", "message" }`, codes from section 5). Pushes have no `id`. Field names are snake_case.

**Client mode (steps 3 and 5)**

The current-user control pipe (api-v1 section 10) provides `client.status` and
`client.setmode` `{ mode: "server" | "client" }` from step 3. Step 5 adds:

- `client.setfallback` `{ fallback: "none" | "local" }`: saves the explicit fallback choice.
- `client.setrelaymode` `{ relay_mode: "auto" | "manual" }`: saves the relay policy (default `auto`).
- `client.userelay` `{ enabled: true | false }`: changes session-only relay permission; resets when the host quits.
- `client.promptshown` (no payload): persists `fallback_prompt_shown` and clears the one-time pending prompt.

All answer `ClientModeStatus`. Its step 5 properties are `ClientFallback`, `FallbackUnset`,
`FallbackPromptPending`, `UsingLaptopModel`, `RelayMode`, and `UseRelay`; on the wire these are
`client_fallback`, `fallback_unset`, `fallback_prompt_pending`, `using_laptop_model`, `relay_mode`, and `use_relay`.
`ClientFallback` is `none` until explicitly chosen; `FallbackUnset` distinguishes that from an explicit None.
The status also includes `Mode`, `Paired`, `PcName`, `State`, `Route`, `Reason`, `RttMs`, and `Refused`
(snake_case on the wire). `Refused` is `not_paired` or `failed` when a mode switch is refused.
Settings persist `client_fallback` (null until chosen), `relay_mode`, and `fallback_prompt_shown` (default false).
Use these messages to change them; generic `settings.set` preserves the host-owned values.

**Models**

- `models.list` answers `{ models: [...], models_folder, free_bytes, upgrade_model_id, catalogue_available }`.
  `models_bytes` is the disk space the models in the current folder use. Each model has `id, display_name, kind,
  engine, size_bytes, quality, recommended, installed, active, fits_this_pc, reason`, plus:
  - `last_used_at`: when an engine last loaded the model (absent when never);
  - `damaged`, `damaged_reason`: a file check found a file that does not match the model list (nothing was deleted);
  - `advice_summary`, `good_for`, `caveats`: the model list's advice, each only when present;
  - `memory_needed_bytes`: memory the planner says the model needs on this PC (0 when unknown);
  - `licence_name`, `licence_url`: the model's licence (SPDX name and an https link);
  - `existing_copy`: the model is a file the user added in place (used where it is, never deleted);
  - `download_state`: `downloading`, `paused`, `failed`, or absent when there is no unfinished download;
  - `bytes_done`: bytes already on disk for an unfinished download;
  - `store_folder`: absent for the current models folder; set for a model left in an earlier models folder
    (see "Changing the models folder"). Such a model can only be removed.
  - `catalogue_available` is `false` when no signed model list could be loaded; only installed models are listed
    then. A listing without the field means `true`.
- `downloads.start` / `downloads.resume` `{ catalogue_id }`: start, or continue a paused or failed download from
  its partial files. Refused up front with an error answer: `feature_disabled` (no model list, or no models
  folder), `not_found` (not in the list), `invalid_request` (not downloadable, a file the user added, or not
  enough free space; the message says how much is needed). Later failures arrive as `event.download_progress`
  with `state` `failed` (or `paused` when the drive is full) and a `message`; no failure is silent.
- `downloads.pause` keeps the partial files; `downloads.cancel` removes only the unfinished files the host made.
- `models.remove` `{ catalogue_id, store_folder?, switch_to_id? }`: removes a model through the store rules: only
  files the host downloaded and recorded (checked by marker, path, size and file ID) are deleted; a file the user
  added is only forgotten. The model in use can be removed too: the host first switches to `switch_to_id` (another
  installed model of the same kind) or, when none is given, leaves that kind with no model; it stops the engine and
  waits (up to 30 seconds) until the engine let go of the files, then removes. `invalid_request` when the switch
  target is not an installed model of that kind, when the model is downloading, when an engine does not let go in
  time, or when the store refuses (nothing that failed a check is deleted). A model that is not in use but is still
  loaded (an old engine still stopping) is refused with "try again in a few seconds". With `store_folder`, it
  removes from that earlier models folder with the same rules; the folder itself is never deleted.
- `models.verify` `{ catalogue_id }`: re-hashes an installed model against the model list's SHA-256 values.
  Read-only. The answer comes when the check has started; progress and the result arrive as `event.store_progress`
  with `operation` `verify`. A mismatch (or a missing file) ends with `state` `damaged` and a `message`; the model is
  marked damaged (`damaged` in `models.list`) and nothing is deleted or changed. The window offers "Download again",
  which the user confirms; it is `models.remove` followed by `downloads.start`. `invalid_request` for a file the user
  added (no checksum), a model that is not fully downloaded, a model not in the model list, or while another move or
  check runs.
- `models.import` `{ path }`: "Add your own model". `path` is the full path of a `.gguf` file on this PC. The host
  checks the header up front (`GGUF` magic, version 2 or 3, readable metadata) and refuses anything else with
  `invalid_request` and a plain message (not a GGUF file, cut short, unknown format version; `not_found` when the file
  is missing). Then it copies the file into its own models folder: written as a `.part` inside the store, size and
  SHA-256 checked, renamed into place, and recorded in `store.json` with its file ID and a `user_added` flag (older
  versions ignore the flag). The original file is only read: never moved, changed or deleted. The answer comes when
  the copy has started; progress and the result arrive as `event.store_progress` with `operation` `import`: `running`
  with `bytes_done`/`bytes_total`, then `done` (`catalogue_id` is the new model, `message` says what was added; the
  same file added twice ends `done` with the existing model), `failed` (not enough free space, read error, the file
  changed while copying) or `cancelled`. A cancelled or failed import removes only its own `.part`. The new model
  appears in `models.list` with `user_added` true, kind `chat` (or `embedding` for BERT-style architectures), name
  from the file's `general.name`, no memory-fit estimate (`fits_this_pc` is not meaningful), and activates and
  removes like any other model; removing it deletes only the host's copy. It has no checksum in the model list, so
  `models.verify` refuses it. Refused while a move, check, clean or another import runs.
- `store.open_folder` answers `{ path }`: the models folder. The host changes nothing; the window opens the path in
  Explorer itself. `not_found` when the folder does not exist, `feature_disabled` without a models folder.
- `store.move` `{ parent_folder }`: moves the models to `parent_folder\Portal AI Host Models`. Copy, verify (re-hash
  the copy), switch, and only then delete the old copies, one file at a time and only files the old `store.json`
  lists whose new copy verified. The host stops its engines first (they load again on the next request). Refused up
  front (`invalid_request`): not enough free space (the message says how much is needed, the models' size plus 2 %
  plus 2 GB), a folder that is missing, FAT32, inside the current models folder or already holding a store, a
  download running, or another move or check running. The answer comes when the move has started; progress arrives
  as `event.store_progress` with `operation` `move`. On success the models folder setting is the new folder.
- `store.cancel`: cancels a running move, check or import. A cancelled move undoes the copy and leaves the old folder as
  it was (`state` `cancelled`). Once the host has switched, the move can no longer be cancelled.
- `event.store_progress` `{ operation, state, catalogue_id?, fraction, bytes_done, bytes_total, message?,
  left_behind? }`: `operation` is `move`, `verify` or `import`; `state` is `running`, `removing_old` (move: switched, removing the
  old copies), `done`, `failed`, `cancelled` or `damaged` (verify). `left_behind` lists old files a finished move
  did not delete because they did not pass the checks.
- `event.download_progress` `{ catalogue_id, state, bytes_done, bytes_total, eta_seconds, message }`, `state` one
  of `queued, downloading, paused, verifying, done, failed, cancelled, removing, removed, remove_failed`
  (`removing` and `remove_failed` let the window show "Removing" at once and roll back with the reason in `message`).

**Status**

- `status.get` answers `{ status, safe_mode, unclean_exits, base_url, upgrade_model_id, on_battery,
  store_problem }`. `store_problem` is a plain-language reason the models folder could not be opened (a folder
  without the host's marker, a damaged `store.json`, a missing parent); the folder is left untouched and
  downloads are off until the setting is changed.

**Live activity and the tray pop-up**

- `clients.list` answers `{ clients: [...] }`; each client has `token_id, app_id, app_name, device_id, created_at,
  last_used_at?, revoked, paused, priority` (`priority` is `high`, `normal` or `low`).
- `apps.set_control` `{ app_id, paused?, priority? }`: pauses or resumes an approved app and/or sets its queue
  priority. A missing field keeps its value. Pausing keeps the approval and the token. Errors: `not_found` (no
  approved app with that id), `invalid_request` (priority is not `high`, `normal` or `low`). The setting is saved
  in `clients.json` (`app_controls`) with the approved apps and survives restarts. Removing access does not clear it.
- `activity.get` answers `{ apps: [...] }`: today's per-app counts, newest first. Each app has `app_id, app_name,
  requests, last_seen, tokens, running` (`running` = requests generating right now). Counts and times only, never
  content; kept in memory and reset at local midnight.
- `engine.load` / `engine.unload` `{ kind? }` (chat when missing): load the active model now, or stop its engine
  process now. Unload never touches model files; the next request loads the model again. The answer comes when the
  work is done; failures are error answers (for example `insufficient_memory`, or `not_found` when no model is active).
- `event.tray_popup` `{ x, y }` (push): the tray icon was clicked at that screen position. A connected window shows
  its small status pop-up. With no window connected the host starts the window with `--tray-popup=x,y` instead.
- `status.get` carries the same `usage` as `GET /host/status`.

**Try it**

- `tryit.stream` `{ run_id, request }` (`request` is a chat request; `run_id` is chosen by the window): runs the chat
  through the same queue and engine as app requests, with the host itself as the caller (`app_id` `host.tryit`,
  shown as "Try it" in `activity.get`). The reply is capped at 768 tokens and never beyond the room left in one
  context slot. Pieces arrive as `event.tryit_chunk` pushes `{ run_id, seq, content?, reasoning? }`, batched about
  every 40 ms and numbered from 1. The answer comes when generation has ended: `{ run_id, cancelled, finish_reason?,
  first_token_ms?, total_ms, wait_ms, tokens, chunks, tokens_per_second? }`. `chunks` is the number of pushes sent;
  the answer can arrive before the last pushes, so the window waits until it has `chunks` pieces. `first_token_ms`
  counts from the start of generation (after queueing and loading, `wait_ms`). Nothing is stored; no text is kept.
- `tryit.cancel` stops the running Try it request at once (the stream answer then has `cancelled: true`) and frees its
  queue slot. A new `tryit.stream` also replaces a running one. `tryit.chat` is the older one-piece version.
- The same path exists only on the pipe; there is no HTTP route for it.

**Settings**

- `settings.get` / `settings.set` carry `HostSettings` (`start_with_windows, backend, models_folder,
  max_memory_percent, prevent_sleep_while_downloading, bandwidth_cap_bytes_per_second, kinds`, the performance keys
  `preset, memory_limit_bytes, keep_free_bytes, compute, gpu_memory_limit_bytes, cpu_threads, priority,
  cpu_ceiling_percent, ease_off`, and the
  host-owned `previous_store_folders`, which `settings.set` ignores).
- **Notifications:** `muted_notifications` (list of `downloads`, `approvals`, `problems`; omitted or empty means all on)
  names the notification kinds the user turned off. Unknown names are ignored, so a kind added later starts on.
  No pipe message was added: the host raises notifications itself from the tray icon (see architecture.md,
  "Notifications"), and a click opens the window page through the existing show path.
- **Performance keys:** a key left out keeps its default (`null` bytes and threads mean "the default for this PC").
  An older `max_memory_percent` becomes `memory_limit_bytes` the first time the new host reads the file; until then it
  still applies. `backend` is the older name of `compute`: when `compute` is `auto`, `backend` still counts, and the
  window writes both.
- `settings.set` answers `SettingsSaved` `{ models_folder_changed, applies_at_restart, note, folder_refused,
  performance_reloaded }`. The performance limits apply at once; `performance_reloaded` (`{ reloaded: [kind],
  unloaded: [kind] }`) is set when a model was reloaded with a new placement or stopped because it no longer fits.
- **Other devices (LAN access, [remote-v1.md](remote-v1.md)):** `remote` in `HostSettings` is `{ enabled, port }` (off,
  48123 by default; the port is 48123 to 48133, the range the package firewall rule opens). Saving settings starts or
  stops the LAN listener at once. Messages: `remote.get` answers `{ enabled, listening, port, problem?, host_name,
  addresses, fingerprint?, devices: [{ device_id, name, created_at, last_seen_at? }], pairing? }`;
  `remote.pair_start` opens a code and answers `{ code, expires_at, qr_text, host_name, address, port, fingerprint }`
  (`feature_disabled` while the setting is off); `remote.pair_cancel`; `remote.device_remove` `{ device_id }` removes
  the device and revokes its tokens (`not_found` for an unknown id). Model control ([client-mode-v1.md](client-mode-v1.md)
  section 4.7): `remote.model_control` `{ device_id, enabled }` turns the per-device grant on or off at once (answers
  `remote.get`'s shape; `not_found` for an unknown id), `remote.take_back` `{ kind }` unloads the model a device loaded
  (memory only) and gives this PC's own choice back; `remote.get` also carries `devices[].model_control`,
  `allow_replace_models` and `loaded_models: [{ kind, model_id, display_name, device_id?, device_name?, placement }]`. The
  setting `remote.allow_replace_models` is "Devices may replace models". `event.remote_changed` (push, no payload) means
  ask `remote.get` again. `approvals.list` and `event.approval_pending` carry `device_name` for a request from a
  device, and `clients.list` carries `device_name`.
- `health.get` answers the `GET /host/health` body (`HealthReport`). Read-only.
- `performance.get` (optional `HostSettings` payload) answers `{ info, ram_bytes, logical_cores, gpu_name,
  gpu_vram_bytes, gpu_unavailable_reason, fit_model, smaller_model, memory_stops, presets }`: `info` is the
  `GET /host/performance` body (for the payload's settings when one is sent, which are previewed and not saved),
  `memory_stops` has one entry per 1 GB step of the memory slider with the best model that fits there, and `presets`
  has the values of Quiet, Balanced and Fast on this PC.
  `folder_refused` is set when the new models folder was refused (missing, FAT32, or inside the current models
  folder); the other settings were still saved and the models folder stayed as it was.

**Changing the models folder**

- The host keeps models in its own marked subfolder `Portal AI Host Models` inside the chosen folder. The chosen
  folder must already exist; the subfolder is created at the next host start (never missing parents). An
  existing `Portal AI Host Models` folder is only used when it carries the host's marker.
- Nothing is moved or deleted when the folder changes. The previous store folder is kept in
  `previous_store_folders`; its models stay listed (with `store_folder`) so their space can be freed with
  `models.remove`.
