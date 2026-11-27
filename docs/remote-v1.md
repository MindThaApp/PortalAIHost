# Portal AI Host: LAN access protocol, version 1

Status: draft 2, 2026-11-24 (adds section 10, the relay). Audience: teams building a client that uses a Portal AI Host on another PC in the same
home network (the Portal AI chat app on a laptop or phone). Read [api-v1.md](api-v1.md) first: after pairing, a device
uses **the same endpoints, tokens, errors and streaming** as an app on the host PC. This document only adds what is
new: finding the host, pairing, TLS pinning and request signing.

## 1. Overview

- **Off by default.** The owner turns on "Let other devices use this PC's AI" in Portal AI Host (Settings, Other
  devices). Until then nothing listens on the network.
- **Pair once, then use.** The owner presses "Pair a device"; the host shows a 12-character code (`XXXX-XXXX-XXXX`) and a QR code
  for 5 minutes. The device pairs with it. Pairing gives the device an identity the host recognises; it does **not** give
  any app on the device access to the AI.
- **Each app still asks.** After pairing, an app on the device requests access exactly as in api-v1.md section 3
  (`POST /host/auth/requests`). The owner sees "**AI Portal on Kitchen tablet**" and allows or denies. The token is
  tied to the device.
- **Transport:** HTTPS (TLS 1.2 or 1.3) over TCP, default port **48123**, using a **self-signed certificate** that
  the device pins when pairing. There is no certificate authority and no host name to check.
- **Who may connect:** only source addresses in private or link-local ranges (10/8, 172.16/12, 192.168/16,
  169.254/16, fe80::/10, fc00::/7). Other sources are dropped before the TLS handshake.
- **Removing a device** in the host revokes it at once: its next request gets `401 device_unauthorized` and every
  token issued to it is revoked.

## 2. Finding the host and the QR code

There is no automatic discovery in v1. The device gets the host's address in one of two ways.

**Scan the QR code (preferred).** The text of the QR code is:

```
portalai://pair?h=<ipv4>&p=<port>&f=<fingerprint>&c=<code>&n=<host name>[&r=<relay url>[&a=<admission token>]]
```

| Field | Meaning |
|---|---|
| `h` | The PC's private IPv4 address. |
| `p` | TCP port (48123 to 48133). |
| `f` | SHA-256 of the host certificate (DER), base64url without padding (43 characters). |
| `c` | The pairing code, 12 characters without hyphens (section 3.2). |
| `n` | The PC's name, URL-escaped (at most 24 characters). |
| `r` | Optional. The relay base address (section 10.1), URL-escaped (the host leaves `:` and `/` as they are to keep the code small). Present only while the relay is on in the host; absent means no relay, LAN only. |
| `a` | Optional, and only together with `r`. The relay admission token (section 10.5): 32 random bytes, base64url without padding (43 characters). Present only while the owner has turned on "Relay access key" in the host. A device that finds it keeps it with the pairing and sends it to the relay; a malformed value counts as absent. |

Parse it as a URI. Unknown fields must be ignored. A device accepts `r` only if it is an absolute `https` URL (plain `http` only for a loopback address, for tests); anything else counts as no `r`. With a QR code the device knows the certificate fingerprint before
it connects, so it can refuse any other certificate (section 3).

**Type the code.** The device asks for the PC's address (or lets the user pick it) and the 12-character code. The
code itself commits to the host certificate (section 3.2), so a typed code is checked against the certificate the
device sees **during the TLS handshake, before any proof is sent**. If it does not match, the device aborts the
connection and says the PC could not be verified.

## 3. Pairing

All binary values are **base64url without padding** (RFC 4648 section 5). JSON is UTF-8, field names are snake_case.
Both pairing requests are `POST` with `Content-Type: application/json`.

### 3.1 Device key

The device makes a **P-256 (secp256r1) key pair** once, and keeps the private key in its secure storage. The public
key is sent as **SubjectPublicKeyInfo DER**. The device name is 1 to 64 characters, no control characters; it is shown
to the owner.

### 3.2 The code key

The code is 12 characters of **Crockford base32** (`0123456789ABCDEFGHJKMNPQRSTVWXYZ`; no I, L, O or U), shown as
`XXXX-XXXX-XXXX`. It is 60 bits:

- the first 8 characters (40 bits) are the **first 40 bits of the SHA-256 of the host certificate (DER)**, the same
  hash as `fingerprint`, read as eight 5-bit groups from the first byte;
- the last 4 characters (20 bits) are random.

**Before sending anything**, a device that has a code (typed or from the QR code) must check the first 8
characters against the certificate it is talking to. If they differ, it aborts and sends no proof. This is what stops
an impersonator: it would need a certificate whose hash starts with the same 40 bits, which it cannot make cheaply, and
without a matching certificate it never receives a proof to guess the last 20 bits from. (A device that also has `f`
from the QR code checks the whole fingerprint.)

When reading a typed code, ignore hyphens and spaces, ignore case, and read `O` as `0` and `I` or `L` as `1`. The
canonical form is the 12 upper-case characters without hyphens.

```
K = HKDF-SHA256(ikm = ASCII(canonical code), salt = ASCII("portalai-pair-v2"), info = ASCII("pair-mac-key"), length = 32)
```

### 3.3 Proofs

`MAC(label, fields...)` is HMAC-SHA256 with key `K` over `frame(label) || frame(field1) || frame(field2) ...`, where
`frame(x)` is the length of `x` as a 4-byte big-endian number followed by the bytes of `x`. The label is ASCII; the
fields are raw bytes (decoded nonces and keys, the UTF-8 device name, the raw 32-byte fingerprint).

| Proof | Label | Fields |
|---|---|---|
| start (device) | `start` | `client_nonce`, `device_name`, `device_public_key` |
| host | `host` | `client_nonce`, `device_name`, `device_public_key`, `host_nonce`, `fingerprint` |
| finish (device) | `finish` | same as host |
| done (host) | `done` | same as host, then `device_id` (ASCII) |

A worked example is in section 8.

### 3.4 Step 1: `POST /pair/start`

```json
{ "client_nonce": "<16 to 48 random bytes>", "device_name": "Kitchen tablet",
  "device_public_key": "<SPKI DER>", "mac": "<start proof>" }
```

The host checks the proof **before** it reveals anything derived from the code. A wrong proof counts as a wrong
attempt. Answer `200`:

```json
{ "host_nonce": "<32 random bytes>", "fingerprint": "<SHA-256 of the certificate DER>",
  "host_name": "DESKTOP-1", "mac": "<host proof>" }
```

The device must now check that the TLS certificate it saw has the SHA-256 `fingerprint` (and, if it had one from
the QR code, that it equals `f`), and that the `host` proof is correct. If either fails it stops and tells the
user, and it never sends step 2.

### 3.5 Step 2: `POST /pair/finish`

```json
{ "host_nonce": "<from step 1>", "mac": "<finish proof>" }
```

Answer `200`: `{ "device_id": "dev_...", "mac": "<done proof>" }`. The device checks the `done` proof (it covers the
`device_id`), stores `device_id`, and **pins the certificate fingerprint** for this host from now on.

### 3.6 Rules and errors

- Check the certificate against the code (section 3.2) before step 1; a mismatch is never sent to the host.
- A code is valid for **5 minutes** and serves **one** device. A new code replaces the old one.
- **5 wrong proofs void the code** (start or finish). After that every attempt answers `code_voided` until the owner
  opens a new code. A malformed message (`invalid`) does not count as a wrong attempt.
- At most 16 devices can be paired. The owner can remove them in the host.
- Errors are `403 pairing_failed` with `error.details.reason`:

| `reason` | Meaning |
|---|---|
| `no_offer` | No code is open (or it was used or cancelled). |
| `expired` | The code is older than 5 minutes. |
| `wrong_code` | The proof does not match. |
| `code_voided` | 5 wrong proofs; ask the owner for a new code. |
| `invalid` | Malformed field, key not P-256, bad name. |
| `no_session` | `/pair/finish` without a matching `/pair/start`. |
| `too_many_devices` | 16 devices are paired. |

- Anyone on the home network can burn a code by sending 5 wrong proofs; the owner then opens a new one. Codes are
  never logged.

## 4. Authenticated requests

Every request other than the two pairing calls must carry four headers:

| Header | Value |
|---|---|
| `X-Portal-Device` | The `device_id` from pairing. |
| `X-Portal-Timestamp` | Unix time in seconds (digits only). |
| `X-Portal-Nonce` | 16 to 48 random bytes, base64url, **new for every request**. |
| `X-Portal-Signature` | ECDSA P-256 / SHA-256 signature, **IEEE P1363 format** (r and s as 32 bytes each, 64 bytes), base64url. |

The signed text is six lines joined by `\n` (no trailing newline):

```
PORTALAI1
<METHOD>
<request target>
<timestamp>
<nonce>
<hex sha-256 of the request body, lower case; the hash of the empty string when there is no body>
```

`<request target>` is the path and query **exactly as on the request line**, not decoded or normalised
(`/host/auth/requests/ar_1?wait_ms=1000`). `<METHOD>` is upper case. The `Authorization: Bearer ...` header is the
app's token and is not part of the signed text (it is sent over TLS).

The host refuses a request with `401 device_unauthorized` and `error.details.reason`:

| `reason` | Meaning |
|---|---|
| `malformed` | A header is missing or has the wrong shape. |
| `unknown_device` | The device was never paired, or the owner removed it. Forget the pairing and tell the user. |
| `stale_timestamp` | The timestamp is more than **2 minutes** from the host clock, in either direction. Fix the clock. |
| `bad_signature` | The signature does not match method, target, time, nonce and body. |
| `replayed` | The same nonce was already used by this device in the last few minutes. |

A device that sends more than **600 signed requests a minute**, or has 4000 request ids still remembered, gets
`429 rate_limited` (`details.reason` `rate_limited`, with `Retry-After`). Back off and retry; a request is only counted
when its signature is valid, so other people on the network cannot use up a device's allowance, and one device cannot
affect another.

A request the host cannot verify never reaches the API, and a nonce is only remembered for a valid signature. Streaming
responses (`text/event-stream`) are protected by TLS, so a client must pin the certificate (section 5).

**Streaming and long polls:** sign once when you open the request. A long poll or stream may last longer than 2
minutes; the signature is only checked when the request arrives.

## 5. TLS and pinning

- Connect with HTTPS to `https://<ip>:<port>`. Do **not** validate against the system trust store or the host name.
- Accept exactly one certificate: the one whose SHA-256 (over the DER bytes) equals the pinned fingerprint. If it
  changes (the host data was reset), the pairing is void: stop and ask the user to pair again.
- The certificate is ECDSA P-256, valid for 20 years, `CN=Portal AI Host`. The host's private key never leaves the PC.
- Use HTTP/1.1.

## 6. Using the API from a device

1. `POST /host/auth/requests` (signed, no bearer token yet) with `app_id`, `app_name` and `reason` as in api-v1.md
   section 3. The owner sees "`<app_name>` on `<device name>`" and an "unverified" mark: the host cannot check which
   program on the device is calling, so the owner decides by name.
2. Poll `GET /host/auth/requests/{id}?wait_ms=...` (signed) until `approved`; the token arrives once.
3. Call `/v1/...` and `/host/...` with `Authorization: Bearer <token>` **and** the four signature headers.

Differences from loopback:

- A device token only works from the device it was issued to, and a loopback token does not work from a device
  (`401 token_invalid`).
- Removing the device in the host revokes its tokens and ends its requests in flight (queued and running generations are cancelled, open streams are closed); the owner can also remove one app (Apps page) as usual.
- Pausing, priorities and the fair queue apply by app id, the same as for local apps.
- `GET /host/ping` also needs the signature headers over the LAN.
- Browser requests (`Origin`, `Sec-Fetch-*` headers) are refused, as on loopback.

## 7. Limits and security notes

- 64 simultaneous connections; request bodies up to 4 MiB (8 KiB for the pairing calls).
- **Typed and scanned codes are equally protected, if the device checks the certificate first.** The last 20 bits of
  the code are the only secret; they stop guessing on the network (5 attempts per code). The first 40 bits bind the
  code to the host certificate. An attacker who terminates TLS with its own certificate cannot present one that matches
  those 40 bits without about 2^40 key generations during the 5-minute life of the code, and a device that checks
  the certificate before its proof never gives the attacker a proof to test guesses against. A device that skips
  that check (or accepts a typed code without it) loses this protection and must not be shipped.
- The device's private key must stay on the device. The host stores only the public key.
- The time check needs reasonably correct clocks (within 2 minutes).
- Not in v1: automatic discovery (mDNS), certificate rotation. Reaching the host away from home is the optional relay of section 10.

## 8. Worked example

Code `M2GT-58X4-7K3Q` (canonical `M2GT58X47K3Q`; `M2GT58X4` is the start of the fingerprint below, `7K3Q` is the
random part), device name `Laptop`, device key (SPKI)

```
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEB2Vj2tcXSTCV2wJ4C9XMN1QgGOXlr3m1X-JXp1YURY7eDf-WRBEzemJZumgn1_ssKU4F5bozdw09LrpkCbFALg
```

| Value | Content |
|---|---|
| `client_nonce` | bytes 0x00 to 0x0F: `AAECAwQFBgcICQoLDA0ODw` |
| `host_nonce` | bytes 0x20 to 0x3F: `ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8` |
| `fingerprint` | bytes 0xA0 to 0xBF: `oKGio6SlpqeoqaqrrK2ur7CxsrO0tba3uLm6u7y9vr8` |
| `K` (hex) | `0181418c89c8b739fdf913fad5453c77b7a79765fe3ad773bd97dc29d1d4a674` |
| start proof | `NVg6_dPR4WnirC8PK0LPSxFFQR4gsJGNCvwgMOVGcCk` |
| host proof | `991fMO6W38DQnzcUWWOFFbUG8w2v-QLrc57y6aV4ju8` |
| finish proof | `TCO2jZIvRw75PKbJWmCQfcb46W7AjtTDRRdz2OO7WNc` |
| done proof (`device_id` = `dev_AAAAAAAAAAAAAAAAAAAAAA`) | `pN69WGM6OotASkGecpTZ61KG80O80xYHQmrlC4OD3-I` |

Signed text for `POST /v1/chat/completions` at time `1790000000`, nonce `AAECAwQFBgcICQoLDA0ODw`, body
`{"model":"x"}`:

```
PORTALAI1
POST
/v1/chat/completions
1790000000
AAECAwQFBgcICQoLDA0ODw
78b0e11c6754fc9671c2034f4a068d3a0e53c1c22a5a33900dd46bc8cd9136f3
```

The signature itself is random for ECDSA, so check yours by having a second implementation verify it, or by sending
a request to a host.

## 9. Client checklist

- Pin the fingerprint; never fall back to normal certificate validation.
- Send the four headers on every request, with a new nonce each time; keep the clock right.
- On `device_unauthorized` with `unknown_device`: the owner removed this device. Delete the pairing and the tokens.
- On `device_unauthorized` with `stale_timestamp`: tell the user to check the device clock.
- Treat `token_invalid` after a host reset like loopback: ask again, do not loop.
- Show the host name from pairing, and the owner's approval status, in your own words.
- Keep the private key in the platform key store; never log codes, tokens, keys or signatures.

## 10. Relay: reaching the host away from home

When the owner leaves "Also reach this PC away from home" on (the default, setting `remote.relay`), the host keeps one
outbound connection to a small relay on the internet. A device that is not on the home network opens a second connection
to the same relay and the relay copies bytes between the two. **The relay is a blind pipe.** The device runs its normal
TLS handshake to the host's pinned certificate **through** the pipe, so the relay only ever sees encrypted bytes.
Pairing (section 3), request signing (section 4) and everything else in this document work unchanged inside that tunnel.
Remote use is meant for text chat; the limits below are strict on purpose.

### 10.1 What a device needs

- The pinned certificate fingerprint `F` (32 bytes, SHA-256 of the certificate DER; the `f` field of the QR code).
  Nothing else: **the room id is derived from `F`**.
- `room = base64url_nopad( SHA-256( "portalai-relay-v1" || F ) )`, where `"portalai-relay-v1"` is the 17 ASCII bytes
  and `F` the 32 raw bytes. The result is 43 characters.
- The relay address, from the `r` field of the QR code (section 2). The host puts its own address there whenever the
  relay is on: by default `https://portal-relay.portalai.workers.dev`, or the address the owner set in the host's
  settings for testing. A device never hard-codes it, and **without `r` it must not use a relay**.

A device tries the home network first (section 2) and falls back to the relay when the host's address cannot be reached.

### 10.2 Device connection

1. Open a WebSocket to `wss://<relay>/v1/device/<room>`.
   - HTTP `503 {"error":"host_offline"}` before the upgrade: the host is not connected to the relay.
   - HTTP `429 {"error":"too_many_streams"}`: 8 streams are already open for this host.
   - HTTP `429 {"error":"too_many_connections"}`: this address opened too many connections in the last minute.
   - A host that asks for an admission token (section 10.5) closes a device without the right token with code 4403
     right after the upgrade, before any `open`.
2. The relay's first message is the text `{"t":"open"}`: the stream is bound to the host.
3. From then on **binary messages are the raw bytes of one TCP stream** to the host's HTTPS port, with no extra framing:
   run TLS (client side) over them, pin `F`, then use HTTP/1.1 exactly as in sections 3 to 6. Each binary message carries
   at most 65536 bytes (split larger writes). A device must not send text messages.
4. Either side closing ends the stream. Treat a close as the TCP connection closing. Close codes used by the relay:

| Code | Meaning |
|---|---|
| 1000 | The host closed the stream (the PC hung up). |
| 1003 | A text message was sent. |
| 1009 | A frame was larger than 65536 bytes. |
| 4404 | The host left the relay. |
| 4408 | The stream was idle for 2 minutes (or the unproven host socket timed out or was evicted). |
| 4403 | The host asks for an admission token (section 10.5) and this device sent none or a wrong one. Pair with the PC again. |
| 4410 | The host disconnected or was replaced; reconnect. |
| 4429 | The room went over its byte or frame budget; try again in a minute. |

The host answers a TLS handshake only for its own certificate. If the pinned fingerprint does not match what arrives,
abort as in section 5. The relay cannot tell a good device from a bad one; the host decides, as always, by the device
signature on every request, so an unpaired device gets `401 device_unauthorized` and nothing else.

Keep-alive: sending the text message `ping` is allowed (the one exception to "no text"); the relay answers `pong`
without waking the room. A ping does **not** count as activity for the 2-minute idle limit; only data frames do.

### 10.3 Host connection (for implementers of the host side)

1. Open a WebSocket to `wss://<relay>/v1/host/<room>` for the host's own room.
2. The relay sends `{"t":"challenge","v":1,"room":"<room>","nonce":"<base64url, 32 bytes>"}`.
3. The host answers within 10 seconds with `{"t":"hello","cert":"<base64url DER>","sig":"<base64url, 64 bytes>"[,"adm":"<base64url, 32 bytes>"]}`:
   - `cert` is the host certificate (at most 2048 bytes);
   - `sig` is ECDSA P-256 / SHA-256, in the fixed 64-byte `r || s` form (IEEE P1363, not DER), over the bytes
     `"portalai-relay-v1-host\n" + room + "\n"` (UTF-8) followed by the 32 nonce bytes, and, when `adm` is sent,
     followed by the 32 bytes of `adm` as well;
   - `adm` is optional (section 10.5). A malformed `adm` (not exactly 32 bytes) closes the socket with 4401.
4. The relay accepts only if `room` equals the room id derived from the certificate **and** the signature verifies
   against the certificate's public key. So only the holder of the certificate's private key can open the room;
   nobody can squat another host's room. Failure closes the socket with code 4401.
5. `{"t":"ready","v":1,"limits":{...}}` follows. Only one host is connected per room: a newer verified host replaces the
   older one (older socket closed with 4409, its device streams closed with 4410). A host that is closed with 4409 or
   4401 waits the longest backoff before it tries again, so two copies do not fight over a room.
6. Streams. When a device connects the relay sends the host the text `{"t":"open","s":<n>}` (`n` is an unsigned 32-bit
   id). The host opens one local TCP connection for it. After that:
   - **binary frames in both directions are tagged:** the first 4 bytes are `n` (big-endian), the rest is payload (at most
     65536 bytes). The relay strips the tag when it forwards to the device and adds it in the other direction;
   - `{"t":"close","s":<n>}` in either direction ends stream `n`. The relay sends it when the device leaves or times out;
     the host sends it when its local connection ends. A `close` is not echoed back to its sender.
7. Keep-alive: send the text `ping` about every 45 seconds; the relay answers `pong`. If nothing at all arrives for
   about 2 minutes, drop the connection and reconnect.
8. Reconnect with exponential backoff (2 s doubling to 5 minutes, with jitter); start over after a connection that
   reached `ready`.

In the Windows host the local connections go to an internal listener on `127.0.0.1` and a random port. It uses the same
certificate and the same request handler as the LAN listener, but accepts loopback sources because every connection on
it comes from the bridge. The LAN listener itself still refuses loopback. Nothing is exposed beyond this PC: the
internal listener is not reachable from the network.

A program on this PC that finds the internal port gets nothing either. The bridge writes a one-time token, 32 random
bytes, as the first bytes of every local connection, before the TLS handshake. The listener keeps the tokens it
issued, compares what it reads against all of them in constant time, and removes a token the moment it is used. A
token is also dropped 5 seconds after it was issued, and at most 64 are outstanding at once. A connection that sends no
token within 3 seconds, a wrong, reused or expired one, or anything else that is not a token is closed at once
without a reply. The tokens live only in this process's memory.

### 10.4 Limits (constants of the relay, `relay/src/protocol.ts`)

| Limit | Value |
|---|---|
| Open device streams per host | 8 |
| Bytes per host per minute (both directions together) | 20 MiB; every frame counts at least 64 bytes, and empty frames are counted but not forwarded |
| Binary frames per host per minute (both directions together) | 60000 |
| Largest frame payload | 64 KiB (host frames add the 4-byte tag) |
| Idle device stream | 2 minutes |
| Device streams per source address per room | 3 |
| Time to answer the host challenge | 10 seconds |
| Unproven host sockets per room | 4 (a newcomer evicts the oldest instead of being refused) |
| Unproven host sockets per source address per room | 2 (oldest of that address is evicted) |
| Connections (host or device) per source address per minute | 30 |

The relay logs no payloads and no headers, and stores nothing; it keeps only rate counters (including one per source
address for the connection limit). The counters are saved in the relay's own small storage so they survive a restart or hibernation; after one, up to 1 MiB or 2048 frames (rooms) or 4 connections (addresses) of recent spend can be forgotten. Because everything
inside the tunnel is TLS with the host's pinned certificate and every request is signed by the device, a compromised or
malicious relay can stop the service, delay it or read its size and timing, but it cannot read or change messages, and it
cannot act as a paired device.

### 10.5 Optional device admission (relay access key)

Without it, anyone who knows a room id can open streams to the host through the relay (rate-limited, and still unable to
do anything without a paired device's signature). A host may close that door with a per-PC admission token. It is off
by default (setting `remote.relay_admission`); a host that does not publish one behaves exactly as described above.

- **Token.** 32 random bytes, made once by the host and kept on the PC (protected, next to the certificate). It is
  unrelated to the device signing keys and to the certificate key.
- **Hash.** The host sends `adm = base64url( SHA-256( "portalai-relay-v1-admit\n" || token ) )` in its hello and signs it
  (section 10.3 step 3), so only the certificate's owner can set it. The relay keeps `adm` for as long as that host
  socket lives, and forgets it when the host leaves or reconnects without one.
- **Pairing.** While the setting is on, the pairing link carries the token as `a` (section 2). The device stores it with
  the pairing, protected like its other secrets, and forgets it with "Forget this PC". Devices paired before the setting
  was turned on do not have it and must pair again to use the relay (the home network keeps working).
- **Device connection.** The device sends the token, base64url, in the request header `X-Portal-Admission` when it
  opens the device WebSocket. It is never put in the URL, so it cannot end up in an access log. The relay hashes it,
  compares with `adm` in constant time and, on a mismatch or when the header is missing, accepts the upgrade only to
  close at once with code 4403. The host never hears of that connection. The relay never logs the header.
- **What it does and does not give.** The relay never sees the device's signing key, the certificate key or any traffic
  content; it sees the token on each device connection (it has to check it), like any shared secret presented over
  TLS. A relay that is compromised can therefore replay the token, but gains nothing it did not have: it could always
  open streams for the room id, and the host still demands a device signature on every request. The goal is only
  to stop strangers from using up a host's stream and budget slots.
- **Rotating.** Settings has a "Relay access key" switch and a "New key" button (control message
  `remote.relay_key_rotate`). Rotating makes a new token, keeps the old file next to it as `remote-admission.bin.bad`
  (`.bad2`, ...), republishes the hash and ends the relay access of every device paired with the old token; they must
  pair again. The home network is not affected. **Forgetting a device does not rotate the key**: a removed device
  still holds the token and could keep opening streams through the relay (it cannot do anything without a paired
  device's signature, but it can use up slots). Rotate after removing a device you do not trust.
- **A key that cannot be used.** The host never makes a new token silently and never overwrites the old file. If the file
  is definitely damaged (or the wrong size), it is copied to a unique `.bad` name, a new token is made and the host
  shows "devices must pair again to use the relay". If opening it fails in a way that may be temporary (the key
  protector cannot answer now), nothing changes: **the relay stays off** (never an open room), the pairing code offers
  no relay, the home network keeps working, and the host tries again at the next start or settings change.
- **Order of deployment.** The relay must be updated before any host turns the key on: an older relay cannot verify
  a hello that carries `adm` and closes with 4401. The host says so ("the relay may need updating") and the switch can
  be turned off again.
- **Pairing link size.** The QR code holds at most 213 bytes. With the default relay address and a key the host
  shortens, then drops, the `n` (name) field to fit; "Copy link" always carries every field.
- **Internal listener.** At most 16 connections may wait for their token on the internal listener at once; further ones
  are dropped at once.
