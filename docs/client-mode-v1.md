# Portal AI Host: client mode ("Use another PC's AI"), design v1

Status: draft 1, 2026-11-24. No code yet. Builds on [api-v1.md](api-v1.md) (what apps see) and
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
- Switching mode never deletes anything. Models on the laptop stay on disk; they are just not used while client mode is
  on. Leaving client mode unpairs nothing unless the user chooses "Forget this PC".
- If the home PC cannot be reached, apps get a `503 unavailable` answer and use their own fallback (AI Portal's
  built-in AI). A new code, `remote_unreachable`, says why.

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
| Loaded model / engine process | **Unloaded** (frees memory). Requests in flight finish first; requests queued behind them get a retryable `503 engine_restarting`. | Loaded again on first request, as at start. |
| Model files, downloads, models folder | **Untouched.** Not deleted, moved or rewritten. Downloads in progress are paused, not cancelled. | Resumable. |
| Local models as fallback | **No** in v1: if the home PC is unreachable the host answers 503 and the app uses its own fallback. A local fallback would load a large model on a laptop without the owner asking (open question 1). | n/a |
| App tokens, priorities, pauses | Kept; they belong to the local apps, not to the mode. | Kept. |

### 1.4 What the UI shows

UI rule: buttons and menu items are icon + 1-3 word label; explanations are tooltips. Settings cards, first-run and
empty states may carry text.

- **Home page, client mode:** a status card with the PC name, a state chip ("Connected - home network", "Connected -
  via relay", "Not reachable"), the round-trip time and the number of running requests. Buttons: `Reconnect`,
  `Change PC`, `Run AI here` (leave client mode). The local-model sections (loaded model, memory use, warm-up) are
  hidden; an empty-state sentence explains that the AI runs on the other PC.
- **Models page:** shows the remote PC's models (read-only list, "on <PC name>"); local download controls stay
  available but are labelled "for when this PC runs the AI". Nothing offers to delete.
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
| `client\token.dat` | the host's own access token for the remote PC (3.3) | DPAPI, same as above. |

The authoritative mode flag lives in the existing settings store (`mode: "server" | "client"`).

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
program talking to the home PC, so it acts as one app: `app_id = "portal-ai-host-client"`, `app_name = "Portal AI Host
(client mode)"`, requested once after pairing through `POST /host/auth/requests` and polling (both signed). The home
PC owner sees "Portal AI Host (client mode) on <laptop>" and approves; the pairing dialog shows this as the last step
("Waiting for approval on <PC name>", tooltip "Approve it in Portal AI Host on that PC", 2-minute timeout, cancel
leaves no files). The token is stored DPAPI-protected (`client\token.dat`). On `token_invalid` or `token_revoked` the
host asks once more (new request), never in a loop.

Per-app identity is lost over this hop: the home PC sees one app, not "AI Portal". Fair queueing and the owner's
per-app pause on the home PC therefore work per laptop. The laptop host applies its own per-app pause, priority and
queue limits locally before forwarding, so the laptop owner's controls still work (open question 2).

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
| `GET /v1/models` | **Forwarded**: the remote's models, ids as the remote reports them. Local models are not listed in client mode. |
| `POST /v1/chat/completions`, `POST /v1/embeddings` | **Forwarded**, see 4.3 and 4.4. |
| Admin control pipe (`models.*`, `settings.*`, `downloads.*`) | **Local only**; no pipe message is ever forwarded. The Models page reads remote models through a read-only `client.models` message backed by the cached capabilities. |

Unknown `/host/*` and `/v1/*` routes: `404 not_found` locally, not forwarded (an allow-list, not a proxy).

### 4.3 Headers

| Header | Handling |
|---|---|
| `Authorization` (local app token) | Consumed locally. Replaced by the host's own token (3.3) on the outgoing request. |
| `X-Portal-Priority`, `X-Portal-Load`, `X-Portal-Context-Tokens` | Forwarded unchanged. Priority also orders the laptop's own queue. |
| `X-Portal-Deadline-Ms` | The local deadline is the budget. The remote receives the **remaining** budget minus 1 s (floor 1000), so the remote ends first and its `504` (with `details.phase`) wins; the laptop returns its own `504 deadline_exceeded` only if the remote did not answer in time. |
| `X-Portal-Client-Request-Id` | Forwarded and echoed. |
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
| 503 | `remote_unreachable` | `unavailable` | yes | The PC that runs the AI cannot be reached now. `details.reason`: `offline` (nothing answered on LAN or relay), `relay_host_offline`, `relay_busy` (429s, close 4429), `clock` (`stale_timestamp`), `refused` (the pairing is no longer valid, see 5.2), `cert_changed` (5.3). `retry_after_ms` follows the backoff. |

This is additive under api-v1 rules (new code, same `type`; clients ignore unknown codes and fall back, which is the
right outcome for AI Portal). Apps that handle only the old codes treat any `503 unavailable` as "fall back".

Mapping of remote answers on the way back:

| Remote answer | Laptop answers the app |
|---|---|
| 200, or any OpenAI-shaped error from the home host (`no_model`, `model_loading`, `queue_full`, `deadline_exceeded`, `insufficient_memory`, `context_length_exceeded`, `engine_error`, ...) | **Passed through unchanged** (same code and details). |
| `401 device_unauthorized`, reason `unknown_device` | `503 remote_unreachable`, `reason: "refused"`; the host marks the pairing invalid (5.2). |
| `401 device_unauthorized`, reason `stale_timestamp` | `503 remote_unreachable`, `reason: "clock"`. |
| `401 token_invalid` / `token_revoked` (the host's token) | The host requests a new token once (3.3); meanwhile `503 remote_unreachable`, `reason: "refused"`. |
| `429 rate_limited` (device limit 600 per minute) | Passed through as `429 rate_limited` with `Retry-After`. |
| TLS fingerprint mismatch | `503 remote_unreachable`, `reason: "cert_changed"`; no request is sent. |
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

### 4.7 Models

Remote models are what apps see in `/v1/models` and in capabilities. The laptop's own installed models appear in the
Models page under "On this PC" and are not offered to apps while client mode is on. Which model is active on the home
PC stays the home owner's decision; the laptop cannot change it (open question 5).

## 5. Security

### 5.1 Credentials never reach local apps

- The device key, pinned fingerprint and host token stay inside the host process. Apps only hold their own loopback
  token, which is valid only on the laptop host (a loopback token does not work on the home PC, remote-v1 section 6).
- No endpoint returns remote credentials, the remote's LAN address or the pairing code. `/host/status` shows the PC
  name and route only. The admin pipe has no message that reads `device.key` or `token.dat`.
- A program on the laptop that can call the loopback API still needs an owner-approved token, exactly as before, so
  client mode adds no new local attack surface beyond "the owner chose to share the home PC's AI with approved local
  apps".

### 5.2 Revocation (server side)

Removing the device in the home host revokes it at once (remote-v1 section 1): the next request gets `401
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
| `ClientModeController` | `src/Portal.AiHost.Server/ClientModeController.cs` | Switches the handler set, unloads/loads the engine, stops/starts `LanServer` and `RelayBridge`, bumps the capabilities revision. |
| Control pipe messages `client.pair`, `client.status`, `client.setmode`, `client.forget`, `client.models` | pipe contracts in Core, `PipeHostControl` in the app | UI to host. |
| UI | `src/PortalAIHost.App/Pages/SettingsPage.xaml` (mode card), `HomePage.xaml`, `TrayPopupWindow.xaml`, a new pairing dialog, strings in `Strings/` | Section 1.4. |
| Server-side additions | the "Pair a device" dialog and `RemoteAccess` | "Copy link" and visible address + code (2.1). |

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
- **Safety:** a mode switch leaves the models folder and downloads unchanged; `Forget` removes only the three own
  files; no token or key appears in captured logs.
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
5. **UI.** Settings card, pairing dialog, Home/Models/tray states, first-run path, strings, accessibility names, UI
   rule check; then the owner's live test (laptop + home PC, including the relay).

## 7. Open questions for the owner

| # | Question | Recommended default |
|---|---|---|
| 1 | Should the laptop use its **own local model** when the home PC is unreachable? | **No** in v1: apps get `503 remote_unreachable` and AI Portal uses its built-in AI. Later, an optional "Fall back to a model on this PC" switch, off by default. |
| 2 | Should the home PC owner see which **app** on the laptop made a request (so per-app pause works there)? | **No** in v1: the home PC sees one "Portal AI Host (client mode)" app; pause and priority are controlled on the laptop. Revisit with a signed app-label header if wanted. |
| 3 | May client mode use the **relay** when away from home? | **Yes**, whenever the pairing link carried `r` (the home owner left the relay on); the status card always shows "via relay". |
| 4 | Pair by **address + code** without a link? | **Yes**, LAN only (no relay) until a link has been used. |
| 5 | May the laptop **change which model is active** on the home PC? | **No**: that stays the home owner's decision. |
