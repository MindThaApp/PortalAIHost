# Portal AI Host API, version 1

Status: draft 1, 2026-11-20. Audience: teams building apps that use Portal AI Host (Voice Portal, PDF Portal, Files
Portal, a future chat app, third-party tools). This document is self-contained. .NET apps should use the
`Portal.AiHost.Client` NuGet package, which implements everything here, but any HTTP client works.

Phase 1 is **Windows, same PC only** (loopback). LAN and remote access come later, under the same API and tokens.

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

## 5. Errors

All errors use the OpenAI shape, with two optional extras:

```json
{ "error": { "message": "The model is loading. Try again shortly.", "type": "unavailable",
             "code": "model_loading", "param": null,
             "retry_after_ms": 3000, "details": { "progress": 0.42 } } }
```

`Retry-After` (in seconds) is also set when a retry makes sense. Switch on `code`; the `message` text is not fixed
and may be localised later.

| HTTP | `code` | Retry? | Meaning / what to do |
|---|---|---|---|
| 400 | `invalid_request` | no | Malformed JSON or field. `param` names the field. |
| 400 | `unsupported_parameter` | no | For example `n > 1`, image content, or `tools` on a model without tool support. |
| 400 | `context_length_exceeded` | no | `details`: `prompt_tokens`, `context_tokens`. Trim the history or split the input. |
| 401 | `auth_required` | no | The bearer token is missing. |
| 401 | `token_invalid` | no | Unknown token. Delete it and ask the user before requesting again. |
| 401 | `token_revoked` | no | The user removed access. Delete the token. |
| 403 | `access_denied` | no | The user denied the access request (24 h cool-down). |
| 403 | `forbidden_origin` | no | Browser origin or bad `Host` header. |
| 404 | `not_found` | no | Unknown route or request id. |
| 404 | `feature_disabled` | no | The endpoint exists in the API but is switched off on this host (for example a future feature). |
| 413 | `request_too_large` | no | The body is over `max_request_bytes`. |
| 429 | `queue_full` | yes | Back off for `retry_after_ms`. |
| 429 | `rate_limited` | yes | Too many access requests. |
| 503 | `no_model` | no | No model of the needed kind is installed. `details.kind` is `chat` or `embedding`. Fall back and tell the user. |
| 503 | `model_loading` | yes | Only with `X-Portal-Load: fail`. `details.progress` runs from 0 to 1. |
| 503 | `engine_restarting` | yes | The engine stopped and is restarting. Retry shortly. |
| 503 | `insufficient_memory` | no | The model cannot load in the memory available now. Fall back. |
| 503 | `host_stopping` | no | The host is quitting. Fall back. |
| 504 | `deadline_exceeded` | no | `details.phase` is `queued`, `loading` or `generating`. |
| 500 | `engine_error` / `internal_error` | once | Unexpected. Retry at most once, then fall back. |

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
  "client": { "app_id": "com.mindthaapp.voiceportal", "token_id": "tk_51c0e2", "approved_at": "2026-11-20T21:05:02Z" },
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
  "sharing": { "lan": false, "relay": false }
}
```
- `state` is one of `ready` (loaded and idle), `busy` (generating), `loading`, `idle` (installed but not loaded; the
  next request loads it), `no_model`, `downloading` (no usable model yet) or `error`.
- `models.<kind>.state` is one of `unloaded`, `loading`, `loaded`, `failed` or `not_installed`. The keys under
  `models` are model kinds. Expect more kinds later (for example `transcription`) and ignore unknown ones.

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
  "endpoints": ["chat.completions", "embeddings", "models", "tokenize", "warmup"]
}
```

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
- **LAN and remote access:** the same endpoints and tokens, over TLS or end-to-end encryption, after one-time
  pairing. Tokens will be tied to a device; existing loopback tokens stay valid for local use.
