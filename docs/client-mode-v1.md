# Portal AI Host: client mode ("Use another PC's AI"), design v1

Status: draft 2, 2026-11-24 (owner decisions in section 7 applied). No code yet. Builds on [api-v1.md](api-v1.md) (what apps see) and
[remote-v1.md](remote-v1.md) (the host-to-host transport; section numbers below refer to it).

## Summary in plain words

- There is still one app. On the home PC it is the **server**, as today. On a laptop the same app can be switched to
  **"Use another PC's AI"**: it pairs with the home PC once, then every Portal app on the laptop keeps talking to the
  laptop's own host exactly as before, and the laptop host passes the work to the home PC.
- Apps change nothing. They find the laptop host through the same discovery file, ask for a token the same way, and
  call the same local address. They cannot tell whether the answer came from a local model or the home PC.
- Pairing uses the link or code the home PC already shows. The laptop gets "paste the link" and "type address + code"
  boxes (no camera needed). The home PC gets a "Copy link" button so the link can be sent to the laptop.
- At home the laptop talks to the home PC over the local network. Away from home it uses the relay, if the home PC
  allows it. Either way the connection is checked against the home PC's certificate, and every request is signed with a
  key that never leaves the laptop.
- Switching mode never deletes anything. Models on the laptop stay on disk. Leaving client mode unpairs nothing unless
  the user chooses "Forget this PC".
- If the home PC cannot be reached, the laptop offers a choice in Settings (and asks once, calmly, the first time):
  "Use this laptop's model" or "None". Until the user picks, it is "None": apps get `503 remote_unreachable` (a new
  code that says why) and use their own fallback (AI Portal's built-in AI). With the laptop model on, it loads only
  when needed and requests go back to the home PC as soon as it is reachable again.
- By default the home PC decides which model runs. The home PC owner can give a paired laptop **"Model control"**
  (off by default, revocable): then the laptop can see the PC's models, load and unload them, pick a model per laptop
  app, and run two at once (for example one on the graphics card, one in system memory). The PC owner's own actions
  always win, and the PC shows which device loaded what.
- Relay use is automatic by default (home network first, relay when away); a setting makes it manual.

## 1. Roles and mode switch

### 1.1 Modes

| Mode | Meaning |
|---|---|
| `server` (default) | Today's behaviour: local engine, models, LAN/relay listener for devices if the owner enabled it. |
| `client` | No local generation. `/v1` and the model-related `/host/*` endpoints are forwarded to the paired PC. |

A host is in exactly one mode. A host in `client` mode never listens on the LAN and never connects its relay bridge
(`RelayBridge`), so a laptop cannot be used as a hop to the home PC (no chains). It keeps the loopback listener, the
discovery file, the app token store and the Apps page.

### 1.2 Entering and leaving

- Settings gets a card **"How this PC uses AI"** with two choices: "This PC runs the AI" and "Use another PC's AI".
  Choosing the second starts the pairing flow (section 2). The mode only flips to `client` when pairing succeeded
  (or when an earlier pairing is still stored: switching back and forth needs no new code).
- Leaving client mode is one choice in the same card. The stored pairing is **kept** (so switching back is instant);
  a separate **"Forget this PC"** button deletes the device key and the pinned certificate after a confirmation.
- Mode change is applied without an app restart: the host swaps the handler set behind the loopback listener
  (section 4), the discovery file and `instance_id` stay, and `X-Portal-Capabilities-Revision` changes so apps
  re-read capabilities.

### 1.3 What happens to local models and the engine

| Item | On entering client mode | On leaving |
|---|---|---|
| Loaded model / engine process | **Unloaded** (frees memory). The engine path stays available for the laptop fallback below. Requests in flight finish first; requests queued behind them get a retryable `503 engine_restarting`. | Loaded again on first request, as at start. |
| Model files, downloads, models folder | **Untouched.** Not deleted, moved or rewritten, by this or any mode switch. Downloads in progress are paused, not cancelled. | Resumable. |
| Local model as fallback | Setting **"If the home PC is unreachable"** (Settings card, section 1.4): `None` (default) or `Use this laptop's model`. See below. | n/a |
| App tokens, priorities, pauses | Kept; they belong to the local apps, not to the mode. | Kept. |

**Unreachable-PC choice (owner decision 1).**

- Stored as `client_fallback: "none" | "local"` in the settings store; null means never chosen and behaves as `none`, so nothing is loaded on a
  laptop without the user's say-so.
- The first time the remote becomes unreachable while the value is still unset, the host shows **one** calm prompt
  (a tray notification of kind `problems` that opens the Settings card). It is never repeated; the choice stays one
  click away in Settings.
- `none`: apps get `503 remote_unreachable` (4.5) and use their own built-in AI.
- `local`: client mode does **not** permanently unload or remove the local engine path. When a request arrives and the
  remote is unreachable, the laptop's active model loads on demand (normal load path, memory checks and
  `insufficient_memory` answers) and the request runs locally. The model unloads by the host's normal idle rule. A
  request that starts on the PC finishes on the PC, and one that starts locally finishes locally (in-flight requests
  never move). As soon as the link reports reachable again, **new** requests go back to the PC. The Home chip reads
  "Using this laptop's model" while that is the case.

### 1.4 What the UI shows

UI rule: buttons and menu items are icon + 1-3 word label; explanations are tooltips. Settings cards, first-run and
empty states may carry text.

- **Home page, client mode:** a status card with the PC name, a state chip ("Connected - home network", "Connected -
  via relay", "Not reachable"), the round-trip time and the number of running requests. Buttons: `Reconnect`,
  `Change PC`, `Run AI here` (leave client mode). The local-model sections (loaded model, memory use, warm-up) are
  hidden unless the laptop model is serving as fallback (the chip then reads "Using this laptop's model"); an
  empty-state sentence explains that the AI runs on the other PC.
- **Settings, "How this PC uses AI" card (client mode rows):** `If the home PC is unreachable` (choice: "None" /
  "Use this laptop's model", default None); `Connection away from home` (choice: "Automatic" / "Manual"; with Manual,
  a `Use relay` toggle turns the relay on for the current session and resets when the app closes); and, when the home
  PC has granted Model control, a `Models on <PC name>` section (4.7).
- **Models page:** shows the remote PC's models ("on <PC name>"): read-only without Model control, with load, unload
  and placement buttons when granted (4.7). Local download controls stay available but are labelled "for when this PC
  runs the AI". Nothing ever offers to delete a remote model.
- **Apps page:** unchanged (local apps, local approvals).
- **Tray:** the icon gets a small link badge; the popup shows the state chip and the same three buttons. Unreachable
  gets a warning badge, not a toast on every retry.
- **First run:** the welcome window gets a second path, "Use another PC's AI", beside the existing one.

### 1.5 State and secrets

All under the host's existing state directory (the one that holds the settings and the device store), in a new folder
`client\`:

| File | Content | Protection |
|---|---|---|
| `client\remote.json` | mode hint, PC name, LAN address and port, relay URL, pinned fingerprint, `device_id`, paired-at | Plain JSON via `AtomicFile` (no secrets). |
| `client\device.key` | the device's P-256 private key (PKCS#8) | **DPAPI**, user scope, in the style of `DpapiHostKeyProtector` / `DpapiTokenStore`. Never logged, never exported. |
| `client\apps.json` | per-laptop-app model mapping (4.7): app id to model id | Plain JSON via `AtomicFile` (no secrets). |
| `client\token.dat` | the host's own access token for the remote PC (3.3) | DPAPI, same as above. |

The authoritative mode flag lives in the existing settings store (`mode: "server" | "client"`), together with `client_fallback` (1.3) and `relay_mode: "auto" | "manual"` (3.1). `Forget this PC` removes only the own `client\` files above and nothing else.

### 1.6 Implemented control messages (steps 3, 5 and 6)

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

Step 6 (PC side, Model control) adds `remote.model_control` `{ device_id, enabled }` and `remote.take_back`
`{ kind }` (4.7); both answer `RemoteStatus`.

## 2. Pairing from the laptop

### 2.1 Getting the link

The server's pairing offer already has a code and a QR text `portalai://pair?h&p&f&c&n[&r]` (section 2). Server-side
additions (small): the "Pair a device" dialog gets **Copy link** (copies the QR text to the clipboard; icon + "Copy
link", tooltip "Send this to the other PC. It works for 5 minutes and one device.") and shows the address and the
12-character code as text next to the QR code, so a laptop user can type them.

### 2.2 Laptop input options

| Option | Fields | Notes |
|---|---|---|
| Paste link (default) | one box | Parsed by `PairingLink.TryParse` (reused as is). Gives address, port, full fingerprint, code, name, relay. |
| Type address + code | PC address (IPv4, or a name the OS resolves), optional port (default 48123), 12-char code | Code normalised with `PairingCrypto.Normalize`. No fingerprint, so the TLS check below is mandatory. No relay known: the relay is only available when a link was used. |
| Find on network | not in v1 (no mDNS, remote-v1 section 7) | Left out. |

If the clipboard holds a `portalai://pair?` link when the dialog opens, a "Paste link" button is highlighted
(tooltip "Found a link on the clipboard"). It is a button press, never silent.

### 2.3 Steps (device side, exactly remote-v1 section 3)

1. Generate the P-256 device key (3.1) in memory. **Not stored yet.**
2. Open TLS to `<h>:<p>` with a validation callback that accepts any certificate and records its DER. Before sending
   anything, check the code's first 8 characters against that certificate (`PairingCrypto.CodeMatchesCertificate`,
   3.2); if the link had `f`, check the full SHA-256 too. On a mismatch abort, send nothing, show "This PC could not
   be verified". Refuse addresses that are not private or link-local (`RemoteNetwork.IsPrivateAddress`); remote use
   goes through the relay only.
3. `POST /pair/start` with the start proof, verify the `host` proof and that `fingerprint` equals the certificate
   seen, `POST /pair/finish`, verify the `done` proof (3.3 to 3.5).
4. On success only: protect and write `device.key`, write `remote.json` with the pinned fingerprint, `device_id`, LAN
   address and relay URL (if `r` was present), request the host token (3.3), then flip to client mode. A failure at
   any step leaves no files.
5. The device name sent is the laptop's machine name (editable in the dialog; 1 to 64 characters).

Pairing errors map to plain sentences keyed on `details.reason` (`expired` "The code ran out. Ask for a new one",
`code_voided`, `no_offer`, `too_many_devices`, `wrong_code` "The code is not right", and a transport error "Could not
reach that address").

### 2.4 What is pinned

The SHA-256 of the host certificate DER (43-character base64url), the only trust anchor. Never the system trust store
or the host name (section 5). The relay room id is derived from it (10.1), so nothing else needs to be stored for the
relay except its URL.

## 3. Transport on the device side

A new `RemoteHostLink` owns all connections to the home PC and exposes one method: *send this HTTP request, give me
the response stream*. It hides LAN vs relay.

### 3.1 Route choice

Relay use follows the setting `relay_mode` (owner decision 3). **Auto** (default): the routes below are used in
order, home network first and the relay when the LAN fails, whenever the link carried `r`. **Manual**: only the LAN
is used until the user turns `Use relay` on in Settings (or the status card) for the current session; it resets when
the app closes. A pairing made by typing address + code without a link has no relay in either setting until a link
with `r` has been used once (owner decision 5).

1. **LAN first:** HTTPS to `https://<h>:<p>` with a custom certificate callback that accepts exactly the pinned
   fingerprint (SHA-256 over the DER) and nothing else; HTTP/1.1 only; TLS 1.2 or 1.3 (section 5). Connect timeout
   1.5 s (a wrong network must not stall apps).
2. **Relay fallback** (only if the pairing link carried `r`; without it there is no relay, 10.1): WebSocket to
   `wss://<relay>/v1/device/<room>` with `room = base64url(SHA-256("portalai-relay-v1" || F))`. Wait for the text
   `{"t":"open"}` (10.2 step 2), then run the TLS client handshake **over the WebSocket's binary messages** (a
   `Stream` adapter that maps reads and writes to binary frames of at most 65536 bytes; no text frames except
   `ping`), pinned to `F` exactly as on the LAN, then HTTP/1.1 on top (10.2 step 3). Relay HTTP `503 host_offline`,
   `429 too_many_streams`, `429 too_many_connections`, and the close codes 1000, 4404, 4408, 4410, 4429 (10.2 table)
   map to "unreachable" with the right retry delay (4429: 60 s). The text `ping` is sent about every 45 s on idle
   open streams (answered `pong`; it does not count as activity for the 2-minute idle limit).
3. The last working route is remembered in memory (not on disk) and tried first for the next request; the other route
   is tried when it fails. A request that has started streaming is never moved between routes.

### 3.2 Signing

Every request over either route gets the four headers of section 4: `X-Portal-Device`, `X-Portal-Timestamp`, a fresh
`X-Portal-Nonce` (16 to 48 random bytes) and `X-Portal-Signature`, built with the existing `RemoteSigning.Sign` (Core,
reused). The request target is the path and query exactly as forwarded; the body hash is over the exact bytes sent.
It is signed once when the request is opened; long streams are not re-signed (section 4).

### 3.3 The host's own token

Remote-v1 section 6 requires an approved token per app on a device. In client mode the laptop **host** is the only
program talking to the home PC, so it acts as one app: `app_id = "com.mindthaapp.portalaihost.client"` (the host only accepts lowercase reverse-DNS ids), `app_name = "Portal AI Host
(client mode)"`, requested once after pairing through `POST /host/auth/requests` and polling (both signed). The home
PC owner sees "Portal AI Host (client mode) on <laptop>" and approves; the pairing dialog shows this as the last step
("Waiting for approval on <PC name>", tooltip "Approve it in Portal AI Host on that PC", 2-minute timeout, cancel
leaves no files). The token is stored DPAPI-protected (`client\token.dat`). On `token_invalid` or `token_revoked` the
host asks once more (new request), never in a loop.

The token is the device, so security stays per laptop. The home PC still sees the calling app's name as a label
(`X-Portal-Client-App`, 4.3 and 5.7), used for display and per-app pause. Fair queueing works per laptop. The laptop host applies its own per-app pause, priority and
queue limits locally before forwarding, so the laptop owner's controls still work (owner decision 2).

### 3.4 Connection reuse

One keep-alive HTTP/1.1 connection pool per route (`SocketsHttpHandler` with a `ConnectCallback` that returns the
pinned-TLS stream: a TCP stream for LAN, the WebSocket-backed stream for relay). Relay streams are capped at 8 per
host and 3 per source address (10.4), so the relay route uses at most 2 concurrent connections from a laptop; more
requests queue.

### 3.5 Health check, reconnect, backoff

- **Health:** a signed `GET /host/ping` (needs the headers over the LAN, section 6) every 20 s while a local app has a
  recent request, every 60 s when idle, and on demand when the user presses `Reconnect`. The result drives the UI
  chip. Round-trip time is recorded.
- **Backoff when unreachable:** retries at 2 s doubling to 60 s with jitter, reset on success. Requests do not wait for
  the backoff: while the link is down, a request makes one fresh attempt (LAN then relay); if that fails it gets the
  503 at once.
- **Network change** (`NetworkChange.NetworkAddressChanged`): drop pooled connections, probe immediately.
- **Clock:** a `stale_timestamp` answer shows "Check this PC's clock" in the status card.

## 4. Request forwarding

### 4.1 Architecture

The loopback `AiHostServer` + `ApiPipeline` stay in front. They keep doing everything about the **local** app:
loopback, origin and body checks, bearer-token check, per-app pause, priority and queue limits, client registry. Only
the handlers that would use the engine are replaced: in client mode `ApiPipeline.Map(...)` registers forwarding
handlers for those routes (an `IInferenceBackend` seam: `LocalBackend` today, `RemoteBackend` in client mode). The
response is written through the same `IResponseSink`, so SSE and error shapes are identical to a local answer.

### 4.2 Endpoint map

| Local endpoint | Client mode |
|---|---|
| `GET /host/ping` | **Local.** Answers for the laptop host; never proves the remote is up. |
| `POST /host/auth/requests`, `GET /host/auth/requests/{id}` | **Local.** Local apps ask the laptop owner. Remote credentials never appear here. |
| `GET /host/status` | **Local, enriched:** adds `mode: "client"` and `remote: { name, route: "lan" or "relay" or "down", rtt_ms }`. Model fields come from the cached remote capabilities. Client and pause info are local. |
| `GET /host/capabilities` | **Forwarded**, cached 5 s. Limits and models come from the remote, `host_version` stays local, `X-Portal-Capabilities-Revision` is a hash of (remote revision, mode, link state). When the remote cannot be reached: `503 remote_unreachable` (no invented answer). |
| `GET /host/performance`, `GET /host/health` | **Forwarded** (the remote machine's numbers, labelled so in the UI). |
| `POST /host/ease-off`, `DELETE /host/ease-off`, `POST /host/warmup` | **Forwarded** as the same calls; if the remote refuses a device, its answer passes through. They cannot unload or change the remote's models. |
| `POST /host/tokenize` | **Forwarded.** |
| `GET /v1/models` | **Forwarded**: the remote's models, ids as the remote reports them. Local models are not listed while the PC is the backend. |
| `POST /v1/chat/completions`, `POST /v1/embeddings` | **Forwarded**, see 4.3 and 4.4. With Model control the laptop sets `model` from the app's mapping (4.7). With `client_fallback: local` and the PC unreachable they run on the laptop engine (1.3). |
| Admin control pipe (`models.*`, `settings.*`, `downloads.*`) | **Local only**; no pipe message is ever forwarded. The Models page reads remote models through `client.models` (cached capabilities, or the signed `GET /host/models` when Model control is granted). Remote model actions are separate `client.remote_*` messages (4.7) that the laptop host turns into its own signed device calls. |
| Remote model management (`/host/models*`, `/host/placement`, 4.7) | **Only with the Model control grant**, and only from the laptop host's own UI. Never reachable by apps. |

Unknown `/host/*` and `/v1/*` routes: `404 not_found` locally, not forwarded (an allow-list, not a proxy).

### 4.3 Headers

| Header | Handling |
|---|---|
| `Authorization` (local app token) | Consumed locally. Replaced by the host's own token (3.3) on the outgoing request. |
| `X-Portal-Priority`, `X-Portal-Load`, `X-Portal-Context-Tokens` | Forwarded unchanged. Priority also orders the laptop's own queue. |
| `X-Portal-Deadline-Ms` | The local deadline is the budget. The remote receives the **remaining** budget minus 1 s (floor 1000), so the remote ends first and its `504` (with `details.phase`) wins; the laptop returns its own `504 deadline_exceeded` only if the remote did not answer in time. |
| `X-Portal-Client-Request-Id` | Forwarded and echoed. |
| `X-Portal-Client-App` | Added by the laptop host: the calling app's display name (max 64 characters, control characters stripped; empty becomes `Unknown app`). Label only (5.7). Never trusted for access. |
| Response `X-Portal-Request-Id`, `X-Portal-Queue-Ms`, `X-Portal-Model` | Passed through from the remote; the laptop's own wait is added to `X-Portal-Queue-Ms`. |
| Device signature headers | Added by the laptop host (3.2); never exposed to apps. |
| `Origin`, `Sec-Fetch-*` | Already refused on loopback; never forwarded. |

Request bodies are forwarded as the same bytes (the 4 MiB limit is the same on both sides). Neither host stores or
logs content (api-v1 section 1); the forwarder logs counts and timings only.

### 4.4 Streaming and cancellation

- SSE is copied chunk by chunk: the laptop reads the remote response stream and writes to the app's sink with a flush
  per read, without buffering whole events. Progress comments (`: portal {...}`) pass through, so apps still show
  "Waiting (2 ahead)". The remote's own 5 s comments also keep the relay's 2-minute stream idle limit away.
- Until the remote's response headers arrive, the laptop sends its own `: portal {"event":"queued"}` comment at least
  every 5 s once the app's headers are out, so app idle timers behave as with a local host.
- **Cancellation:** closing the app's loopback connection cancels the forwarding request, which closes the remote
  connection; the remote then stops generation (api-v1 section 6, cancellation). A laptop-side deadline does the same.
- A failure after the 200 was sent becomes an in-stream `data: {"error":{...}}` event without `[DONE]` (api-v1
  section 5).
- If the connection to the remote drops mid-stream: in-stream error `remote_unreachable` and **no automatic retry**
  (partial text must not be duplicated); the app decides.

### 4.5 Errors when the remote is unreachable

One new error code is proposed, because the existing ones would mislead the app (the model is not "missing" and the
laptop's own engine is not restarting):

| HTTP | `code` | `type` | Retry? | Meaning |
|---|---|---|---|---|
| 503 | `remote_unreachable` | `unavailable` | yes | The PC that runs the AI cannot be reached now. `details.reason`: `offline` (nothing answered on LAN or relay), `relay_host_offline`, `relay_busy` (429s, close 4429), `clock` (`stale_timestamp`), `refused` (the pairing is no longer valid, see 5.2), `cert_changed` (5.3). `retry_after_ms` follows the backoff. Sent to apps when `client_fallback` is `none` (default), or when the local fallback could not load (then the engine's own error, such as `insufficient_memory`, is passed on). |

This is additive under api-v1 rules (new code, same `type`; clients ignore unknown codes and fall back, which is the
right outcome for AI Portal). Apps that handle only the old codes treat any `503 unavailable` as "fall back".

Mapping of remote answers on the way back:

| Remote answer | Laptop answers the app |
|---|---|
| 200, or any OpenAI-shaped error from the home host (`no_model`, `model_loading`, `queue_full`, `deadline_exceeded`, `insufficient_memory`, `context_length_exceeded`, `engine_error`, ...) | **Passed through unchanged** (same code and details). |
| `401 device_unauthorized`, reason `unknown_device` | `503 remote_unreachable`, `reason: "refused"`; the host marks the pairing invalid (5.2). |
| `401 device_unauthorized`, reason `stale_timestamp` | `503 remote_unreachable`, `reason: "clock"`. |
| `401 token_invalid` / `token_revoked` (the host's token) | The host requests a new token once (3.3); meanwhile `503 remote_unreachable`, `reason: "refused"`. |
| `503 device_paused`, `503 device_quiet_hours`, `503 host_busy_gaming`, `429 device_limit_reached` (5.7) | `503 remote_unreachable` with `reason`: `paused`, `quiet_hours`, `gaming_busy`, `limit_reached`. `retry_after_ms` is passed on. With `client_fallback` set to the laptop model, the request falls back as for any `remote_unreachable` (decision 1); with `None` the app sees the error. |
| `429 rate_limited` (device limit 600 per minute) | Passed through as `429 rate_limited` with `Retry-After`. |
| `403 model_control_required` or `409 model_conflict` (4.7) | Not sent to apps; shown in the laptop's Models page as a plain message. If a mapped model is missing, apps get the normal `model_not_found` / `no_model`. |
| TLS fingerprint mismatch | `503 remote_unreachable`, `reason: "cert_changed"`; no request is sent. |
| Relay reports the home PC absent | `503 remote_unreachable`, `reason: "relay_host_offline"`. |
| Relay capacity or traffic budget exhausted | `503 remote_unreachable`, `reason: "relay_busy"`. |
| No connection on LAN or relay | `503 remote_unreachable`, `reason: "offline"` or the relay reason. |

### 4.6 Timeouts and relay budget

- Connect: 1.5 s LAN, 5 s relay (WebSocket open, `open` message and TLS included).
- Response headers: bounded by the request deadline (defaults 120 s interactive, 30 min batch). No separate read
  timeout once streaming has started; the deadline and the relay's 2-minute stream idle limit apply (the remote sends
  progress comments, and a stalled generation is still bounded by the deadline).
- The relay budget is 20 MiB per minute per host room (10.4). Interactive requests that hit `429 too_many_*` or close
  4429 fail fast with `relay_busy`; `batch` requests wait the 60 s and retry once. The forwarder does not split or
  reshape request bodies (large embedding batches over the relay are the app's problem to keep small; documented in
  api-v1 when this ships).

### 4.7 Models and Model control

**Implemented in step 6 (PC side).** `ModelControl` (`src/Portal.AiHost.Server/ModelControl.cs`) holds the routes, the
"loaded by" table and the conflict rules; the model store reaches it through `IModelControlHost` (read-only: list
installed models, resolve one). The four routes below are mapped on the host's own pipeline for a signed device only (a
request from this PC finds no route, `404 not_found`; routes outside the table, such as remove, download, verify or
import, also answer `404 not_found`). The grant is read from `devices.json` on every call. **Single slot:** one model
per kind is active at a time, so a second load of the same kind replaces the first under the conflict rules; the
**second concurrent slot is its own step** (engine and planner work), and the "Two models at once" paragraph below
describes that later step. Placement: `placement` is `auto`, `gpu` or `cpu`; the PC's own limits always win (a PC set to
`cpu` stays `cpu`; a device's memory and thread numbers are only ever cut to the PC's). A model that is not in the model
list has no memory estimate, so only its thread count is applied. A device's model is never saved as the PC's choice
(`active-models.json` keeps the PC's own model); when the device's engine stops (idle, crash, `Take back`) the PC's own
choice becomes active again and loads on the next request. Device calls answer `400 invalid_request`, `404 not_found`,
`403 model_control_required` and `409 model_conflict` with `details.reason` `owner_changed`, `in_use_by_host`,
`in_use_by_device` or `not_loaded_by_device` (a device unloads or re-places only a model it loaded itself).
`GET /host/capabilities` for a signed device carries `model_control: true | false`.

**Default (no grant).** Remote models are what apps see in `/v1/models` and in capabilities. Which model is active on
the home PC is the home owner's decision; the laptop uses whatever the PC has loaded. The laptop's own installed
models appear in the Models page under "On this PC" and are not offered to apps while the PC is the backend (they are
used only as the fallback of 1.3).

**Model control (owner decision 4).** The home PC owner can grant a paired device **Model control**.

- **Grant.** A per-device flag `model_control` in the host's device store, **off by default**, shown as a switch on
  the device's row in "Other devices" (label `Model control`; tooltip "Lets this device load and unload models and
  choose which model each of its apps uses"). Turning it off takes effect at once: the next call gets `403
  model_control_required`. Removing the device removes the grant, and re-pairing does not restore it. The laptop
  learns its grant from a `model_control` entry in the capabilities answer it gets as a device, and from the `403`.
- **What becomes reachable.** Only for a device with the grant, and signed like every other device request (3.2,
  remote-v1 section 6: same timestamp, nonce and body hash, same 600 per minute limit). The device-visible model API
  reuses the shapes of the existing admin pipe messages (api-v1 section 8, "Models" and `engine.load` /
  `engine.unload`; `ControlMessages.cs` `ListModels`, `ActivateModel` with `ActivateModelCommand.CatalogueId`) and the
  performance keys of `GET /host/performance` / `settings.set` (`compute`, `gpu_memory_limit_bytes`,
  `memory_limit_bytes`, `cpu_threads`; api-v1 section 7):

| Device call | Reuses | Behaviour |
|---|---|---|
| `GET /host/models` | `models.list` | Installed models only (no catalogue or download data): `id, display_name, kind, size_bytes, fits_this_pc, state`, `placement`, `loaded_by`. |
| `POST /host/models/load` `{ model_id, placement? }` | `models.activate` + `engine.load` | Loads an installed model, optionally with a placement (below) and in a second slot. Answers when the load is done, or `409 model_conflict`, `503 insufficient_memory`, `404 not_found`. |
| `POST /host/models/unload` `{ model_id }` | `engine.unload` | Unloads only a model **this device loaded**. Never touches files. |
| `GET /host/placement`, `POST /host/placement` `{ model_id, compute, gpu_memory_limit_bytes?, memory_limit_bytes?, cpu_threads? }` | the performance keys above | The same VRAM and RAM controls the PC has locally, scoped to models this device loaded. The PC's global preset and ease-off rules still apply on top. |

  No endpoint for download, remove, verify, import, settings, models folder, app tokens or the control pipe is ever
  reachable by a device, with or without the grant. `models.remove`, `downloads.*` and every file operation stay local
  to the PC.
- **Per-app model mapping.** Lives on the laptop (`client\apps.json`, 1.5), edited in the laptop's Apps page (a
  "Model" dropdown per app, default "PC's choice"). The laptop host writes the mapped model id into `model` on every
  forwarded `chat/completions` / `embeddings` request (the host's own change; the app is unaware). Without the grant
  the mapping is hidden and `model` is left as the app sent it. A mapped model that is not loaded on the PC is loaded
  **only** if the grant exists and the conflict rules below allow it; otherwise the PC's normal `model_not_found` /
  `no_model` answer passes through.
- **Two models at once.** With the grant the laptop may keep up to two models loaded on the PC, for example one on the
  graphics card (`compute: gpu`) and one in system memory (`compute: cpu`), within the PC's memory limit and `fit`
  checks. **This needs host work:** today the host has one active model per kind (api-v1 `models.<kind>`), so a second
  concurrent slot per kind is new engine and planner work (6.4 step 6). Until it ships, a second load replaces the
  first under the conflict rules.
- **Conflict rules.**
  1. The PC owner's local action always wins. An `engine.load`, `models.activate` or `settings.set` on the PC takes
     effect at once and may unload a device-loaded model; the device then sees `409 model_conflict` with
     `details.reason: "owner_changed"` and its list refreshes.
  2. A device load that would evict a model **in use by the PC's own apps** (loaded, and not loaded by a device) is
     refused with `409 model_conflict`, `details.reason: "in_use_by_host"`, unless the PC owner turned on **"Devices
     may replace models"** (Settings, Other devices; PC-wide, off by default). The PC's own apps then simply reload
     on their next request.
  3. With two granted devices, the first loader keeps its model; a second device may not evict it (`409
     model_conflict`, `reason: "in_use_by_device"`).
  4. A load that does not fit the PC's memory limit fails with `503 insufficient_memory`; nothing is evicted silently.
- **What the PC shows.** In the Models page and the Other devices row, each loaded model reads "Loaded by <device
  name>" (or "by this PC") with its placement (graphics card / system memory) and a `Take back` button that unloads a
  device's model. A tray notification of kind `approvals` ("<device> loaded <model>") appears once per load.
- **Laptop UI.** With the grant, the Models page shows a `Models on <PC name>` section: installed models with `Load` /
  `Unload` buttons and a placement control (Auto, Graphics card, System memory; same labels as on the PC), plus the
  per-app dropdown in the Apps page. Without the grant the section is an empty state ("Ask the owner of <PC name> to
  turn on Model control for this laptop").

## 5. Security

### 5.1 Credentials never reach local apps

- The device key, pinned fingerprint and host token stay inside the host process. Apps only hold their own loopback
  token, which is valid only on the laptop host (a loopback token does not work on the home PC, remote-v1 section 6).
- No endpoint returns remote credentials, the remote's LAN address or the pairing code. `/host/status` shows the PC
  name and route only. The admin pipe has no message that reads `device.key` or `token.dat`.
- A program on the laptop that can call the loopback API still needs an owner-approved token, exactly as before, so
  client mode adds no new local attack surface beyond "the owner chose to share the home PC's AI with approved local
  apps".

### 5.1a Model control and the grant

`model_control` is checked on the home PC for every call in 4.7, from the device store, never from anything the
device sends. A device without it gets `403 model_control_required` on every one of those routes. Device-loaded
models are tracked by device id, so a device can only unload its own. Nothing a device can call deletes, moves or
rewrites a model file.

### 5.2 Revocation (server side)

Removing the device in the home host revokes it at once (remote-v1 section 1; this also drops its Model control grant, and models it loaded stay loaded until idle or until the PC takes them back): the next request gets `401
device_unauthorized` / `unknown_device`, and the host token is revoked. The laptop shows "That PC removed this
laptop. Pair again to continue" in the status card, switches the chip to "Not paired", keeps the mode, and sends no
more requests (apps get `remote_unreachable` / `refused`). It deletes **nothing** by itself; the user presses `Change
PC` or `Forget this PC`.

### 5.3 Certificate change

If the certificate at the pinned address differs, the connection is closed before any request bytes are sent
(remote-v1 section 5: the pairing is void). The status card says "This PC's identity changed" with a `Pair again`
button; there is no automatic re-pin, ever. A changed certificate looks the same as an attack on the network, so the
only fix is a new pairing with a code from the real PC. The relay path performs the same check inside the tunnel.

### 5.4 Rate limits

The laptop host keeps under the home PC's 600 signed requests per minute per device with its own limiter (token
bucket, 10 per second, burst 50, shared by all local apps and fair per app), and passes any `429` through with
`Retry-After`. Relay limits (8 streams, 20 MiB per minute) are handled by queuing and fast failure (4.6).

### 5.5 Privacy: what crosses the network

- **Sent to the home PC:** the prompt content, chat history, generation parameters and the priority and deadline
  headers of forwarded requests; embedding input text; the laptop's device name; the host token's app name.
- **Sent to the relay (if used):** only TLS ciphertext, with its size and timing; the room id is a hash of the
  fingerprint. The relay logs no payloads and cannot read or change messages (10.4).
- **Never sent:** local files, local model names or paths, other apps' tokens, anything outside forwarded requests.
- Content is not stored or logged on either host (api-v1 section 1). The Settings card tells the owner once: "Your
  questions go to <PC name>." Relay use is shown separately ("via relay").

### 5.6 Data safety (destructive-code gate)

Client mode **never deletes, overwrites or moves** user files or model files. Its only writes are the host's own
`client\` state files, written with `AtomicFile` (temp file, then replace of the host's own file). It writes nothing
in the models folder or in user documents. "Forget this PC" deletes only `client\device.key`, `token.dat` and
`remote.json` (own, identifiable files), after a confirmation. Mode switching does not touch the model store or an
in-progress download. Review of the implementation must read every write and delete path in the new classes.

### 5.7 Devices on this PC

The home PC's Devices view lists every paired laptop. There is one card per laptop, titled with the laptop name.
The laptop is still one device with one token. The card also shows the laptop's apps, using the name each forwarded
request carries in `X-Portal-Client-App` (display name, max 64 characters, control characters stripped). The name is
a label for display and per-app pause only. Any app on the laptop can send any name, so it never grants or widens
access; the token is the device.

**Info on each card**

- Status (online, idle, offline), route (home network or relay) and last seen.
- Apps active right now, with queue position.
- Today's usage per app (requests, tokens) with a 7-day bar.
- Current model and "loaded by" (the PC or this device), plus speed (tokens per second) and latency.

**Controls on each card**

- **Pause:** the whole device or one app. Paused calls get a clear paused error and the laptop falls back as in
  decision 1.
- **Priority:** default is the PC's own apps first; option `Equal`.
- **Gaming mode:** refuse remote calls as busy while a full-screen game or heavy GPU use is detected. PC-wide switch,
  default off.
- **Limits and quiet hours:** requests and tokens per day, and a quiet-hours window, per device.
- **Allowed models:** which of the PC's models this device may use, per device.
- **Model control:** the grant and `Take back` are the ones in 4.7 and 5.1a. The card links to them and adds no
  second set of rules.
- **Rename** the device, and **Forget** it. Forget is the existing revoke (5.2).
- **Alerts:** a tray notification on a new pairing, and when a device connects through the relay from away.

**Error codes the laptop sees**

| HTTP | `code` | When | Laptop maps it to |
|---|---|---|---|
| 503 | `device_paused` | The device or the calling app is paused. | `remote_unreachable`, reason `paused` |
| 503 | `device_quiet_hours` | Inside the device's quiet hours. | `remote_unreachable`, reason `quiet_hours` |
| 503 | `host_busy_gaming` | Gaming mode is on and a game or heavy GPU use is detected. | `remote_unreachable`, reason `gaming_busy` |
| 429 | `device_limit_reached` | Today's request or token limit is used up. | `remote_unreachable`, reason `limit_reached` |

Each carries `retry_after_ms` (for quiet hours, the time until it ends; for limits, until midnight on the PC). The
laptop applies its `client_fallback` setting exactly as for `offline`. A model the device may not use gets the
normal `model_not_found`.

**Data**

Usage stats live in one small file per device under the host state folder, with 7 days kept (older days are dropped
on rollover). The file holds counts only: requests and tokens per app and per day. App names are labels, and no
prompt or answer text is stored (5.5). Writes are atomic (temp file, then replace). `Forget` deletes that device's
stats file only; it touches no other file, and never a model file.

**UI rule**

Buttons and menu items show an icon and a 1 to 3 word label. Explanations go in the tooltip and the automation
HelpText, never as text under a button. Settings cards and empty states may carry short text.

## 6. Code plan

### 6.1 New components

| Class | Location | Role |
|---|---|---|
| `ClientState` store + `ISecretProtector` | `src/Portal.AiHost.Core/Remote/Client/ClientState.cs`; DPAPI implementation in `src/Portal.AiHost.Platform.Windows/Identity/` beside `DpapiHostKeyProtector` | Reads and writes `client\remote.json`; protects key and token. |
| `DevicePairingClient` | `src/Portal.AiHost.Core/Remote/Client/DevicePairingClient.cs` | Device side of remote-v1 section 3, using `PairingCrypto`, `RemoteCodec`, `PairingLink`. Transport comes in through `IRemoteConnector`, so it is testable. |
| `IRemoteConnector`, `LanConnector`, `RelayConnector`, `WebSocketStream` | `src/Portal.AiHost.Core/Remote/Client/` | Pinned-TLS stream over TCP, or over relay WebSocket frames (3.1). |
| `RemoteHostLink` | `src/Portal.AiHost.Core/Remote/Client/RemoteHostLink.cs` | Route choice, signing (`RemoteSigning.Sign`), host token, health, backoff, state events. One method: `SendAsync(HttpRequestMessage, ct)`. |
| `IInferenceBackend` seam | `src/Portal.AiHost.Core/Api/` | Lets `ApiPipeline` pick local or remote handlers. |
| `RemoteBackend` | `src/Portal.AiHost.Server/RemoteBackend.cs` | Forwarding handlers, header rewriting, error mapping, SSE passthrough into `IResponseSink` (4.2 to 4.5). |
| `ClientModeController` | `src/Portal.AiHost.Server/ClientModeController.cs` | Switches the handler set, unloads the engine (path kept for the laptop fallback, 1.3), stops/starts `LanServer` and `RelayBridge`, bumps the capabilities revision. Never touches model files. |
| `FallbackRouter` | `src/Portal.AiHost.Server/` | Chooses PC or local engine per new request from link state and `client_fallback`; in-flight requests are never moved (1.3). |
| `ModelControlGrant`, `DeviceModelEndpoints` | device store field in `src/Portal.AiHost.Core/Remote/`; routes in `src/Portal.AiHost.Server/` | Server side of 4.7: per-device flag, allow-listed routes, conflict rules, "loaded by" tracking. |
| `RemoteModelClient`, per-app mapping store | `src/Portal.AiHost.Core/Remote/Client/` | Laptop side of 4.7: signed calls, `client\apps.json`, `model` rewrite in `RemoteBackend`. |
| Control pipe messages `client.pair`, `client.status`, `client.setmode`, `client.forget`, `client.models`, `client.remote_load`, `client.remote_unload`, `client.remote_placement`, `client.appmodel` | pipe contracts in Core, `PipeHostControl` in the app | UI to host. |
| UI | `src/PortalAIHost.App/Pages/SettingsPage.xaml` (mode card incl. fallback and relay mode), `ModelsPage.xaml`, `AppsPage.xaml` (model dropdown), `HomePage.xaml`, `TrayPopupWindow.xaml`, a new pairing dialog, strings in `Strings/` | Section 1.4. |
| Server-side additions | the "Pair a device" dialog and `RemoteAccess` | "Copy link" and visible address + code (2.1); the `Model control` switch, "Devices may replace models" and "Loaded by" display (4.7). |

### 6.2 Reused as is

`PairingLink.TryParse`, `PairingCrypto` (key derivation, `Normalize`, `CodeMatchesCertificate`, MACs),
`RemoteSigning`, `RemoteCodec`, `RemoteApi` constants, `RemoteNetwork.IsPrivateAddress`, `AtomicFile`, `ApiPipeline`
and `IResponseSink`, the `DpapiTokenStore` pattern. From `Portal.AiHost.Client` (the *local* app SDK) only helpers
are reusable (DPAPI storage style); its discovery, exception mapping and SSE reader are loopback-oriented and are not
needed, because the forwarder works at the HTTP level and does not parse chat JSON. The error code is added to
`docs/api-v1.md` section 5 and to `Portal.AiHost.Contracts` and the client SDK's code list, so SDK users see it as a
known `unavailable` code.

### 6.3 Test plan (in-process, loopback only)

- **Fake remote host:** an in-test Kestrel server on `127.0.0.1` with a generated self-signed certificate, running the
  real pairing service, `DeviceRequestVerifier` and API pipeline (with the fake engine already in `tests/`). A fake
  relay (WebSocket server speaking the 10.2/10.3 framing) tests `RelayConnector` and `WebSocketStream`, including
  64 KiB frame splitting and every close code.
- **Pairing:** correct code, wrong code, certificate mismatch before any proof is sent, expired, voided, link with and
  without `r`, typed code without a link, non-private address refused, failure leaves no files.
- **Forwarding:** non-streaming chat, SSE passthrough (progress comments, `[DONE]`, in-stream error), cancellation
  (closing the app connection is seen as an abort by the fake remote), deadline rewrite, the header table, the error
  mapping table, 429 passthrough.
- **Link:** LAN failure then relay success, fingerprint change (no bytes sent), revoked device, stale timestamp,
  backoff timing with an injected `TimeProvider`, health events.
- **Fallback:** default `none` loads nothing; `local` loads on demand only when the remote is unreachable, in-flight
  requests finish where they started, new requests return to the PC when it is reachable, idle unload, the prompt is
  shown once.
- **Relay mode:** Auto uses LAN then relay; Manual never touches the relay until the toggle; typed code without `r`
  never uses the relay.
- **Model control:** no grant means every 4.7 route is `403`; grant on and off; routes outside the allow-list stay
  refused even with the grant; signature required; owner wins; `in_use_by_host` refused unless "Devices may replace
  models"; `in_use_by_device`; `insufficient_memory`; a device unloads only its own models; per-app `model` rewrite;
  no model file is created, changed or deleted by any call (file tree compared before and after).
- **Safety:** a mode switch leaves the models folder and downloads unchanged; `Forget` removes only the own
  `client\` files; no token or key appears in captured logs.
- Real relay, real network and the real UI need a manual run with the owner's go-ahead for any input window.

### 6.4 Implementation steps (in order, one focused worker each)

1. **Device pairing and state.** `ClientState`, `ISecretProtector` + DPAPI implementation, `DevicePairingClient`, LAN
   pinned-TLS connector only; server "Copy link" and visible address/code. Tests: pairing against the fake host.
2. **`RemoteHostLink` on LAN.** Signing, host token flow (3.3), health, backoff, state events, the
   `remote_unreachable` code (docs, Contracts, SDK code list). Tests: link behaviours incl. fingerprint change and
   revocation.
3. **Forwarding backend and mode controller.** `IInferenceBackend` seam, `RemoteBackend`, the endpoint map, streaming
   and cancellation, `ClientModeController` (engine unload, listener and bridge off, revision bump), control-pipe
   messages. Tests: the forwarding suite against the fake remote.
4. **Relay connector.** `WebSocketStream`, `RelayConnector`, route fallback, relay error mapping and the budget rules
   (4.6). Tests: fake relay.
5. **Fallback and relay setting.** `client_fallback` (None default, Use this laptop's model) with `FallbackRouter`, the
   one-time prompt, load on demand and switch back; `relay_mode` Auto / Manual. Tests: the Fallback and Relay mode
   lists in 6.3. (Needs steps 3 and 4.)
6. **Model control, server side.** `model_control` flag in the device store, "Devices may replace models" setting, the
   4.7 routes (list, load, unload, placement), conflict rules, "loaded by" tracking, the PC UI (switch, "Loaded by",
   `Take back`). Includes the second concurrent model slot in the engine planner; if that proves large, ship
   single-slot first and make the second slot its own step. Tests: the Model control list in 6.3 against the fake
   remote. Needs an independent review (it can change what is loaded on the PC: read every load, unload and eviction
   path).
7. **Devices on this PC.** The Devices view of 5.7: `X-Portal-Client-App` on the laptop and its parsing on the PC,
   per-device and per-app stats store (7-day rolling file), pause, priority, gaming mode, limits, quiet hours,
   allowed models, rename, the four error codes and their laptop mapping, alerts, the Devices UI. Needs step 6 (it
   links to model control and `Take back`). Tests: app-name header parsing (length, control characters, empty);
   pause per device and per app; quiet hours (start, end, across midnight); limits per day; gaming-busy refusal;
   stats rollover at 7 days; `Forget` removes that device's stats file only; laptop mapping of the four codes to
   fallback. Needs an independent review for the parts that touch user data: the stats file (atomic write,
   rollover, delete on `Forget`) and anything that deletes or rewrites a file. The reviewer gets the code, not
   conclusions.
8. **Model control, laptop side.** `RemoteModelClient`, `client.remote_*` and `client.appmodel` messages, per-app
   mapping store, `model` rewrite in `RemoteBackend`, the `Models on <PC name>` UI, the Apps page dropdown, the empty
   state without the grant. Tests: mapping and rewrite, grant revoked mid-use.
9. **UI.** Settings card, pairing dialog, Home/Models/tray states, first-run path, strings, accessibility names, UI
   rule check; then the owner's live test (laptop + home PC, including the relay, fallback, Model control and the Devices view).

## 7. Owner decisions (2026-11-24)

| # | Decision | Where applied |
|---|---|---|
| 1 | **Home PC unreachable:** the laptop offers `None` or `Use this laptop's model` in Settings, plus one calm prompt the first time. Default `None` until the user picks. With the laptop model on, the local model loads on demand while the PC is unreachable, unloads by the normal idle rule, and new requests return to the PC when it is reachable again (in-flight requests finish where they started). Model files are never deleted by mode switches. | 1.3, 1.4, 4.5, 6.1, 6.4 step 5 |
| 2 | The home PC sees the laptop as **one paired device with one token** (unchanged), and also shows the laptop's apps. The laptop sends each calling app's name with every forwarded request (`X-Portal-Client-App`, 5.7). The name is a display label and a per-app pause switch only, never a security boundary: the token is the device. | 3.3, 4.3, 5.7 |
| 3 | **Relay:** `Auto` (default: home network first, relay when away, if the link carried `r`) or `Manual` (relay only when the user turns it on for the session). | 1.4, 3.1, 6.4 step 5 |
| 4 | **Model control:** the PC decides by default. The PC owner can grant a paired device `Model control` (per device, off by default, revocable): the laptop can list, load and unload the PC's models, map a model per laptop app, and use two at once with the PC's placement controls. The PC owner's local action wins; evicting a model in use by the PC's own apps needs "Devices may replace models" or is refused. The PC shows which device loaded what. | 1.4, 1.5, 4.2, 4.5, 4.7, 5.1a, 6.1, 6.3, 6.4 steps 6 and 8 |
| 5 | **Address + code without a link:** allowed, LAN only until a link with `r` has been used. | 2.2, 3.1 |

Points to confirm before step 6: (a) the second concurrent model slot is new engine work, not only remote plumbing
(4.7); (b) a device that loses its grant leaves its models loaded until idle or taken back; (c) "Devices may replace
models" is PC-wide, not per device.
