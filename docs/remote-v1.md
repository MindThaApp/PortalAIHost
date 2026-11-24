# Portal AI Host: LAN access protocol, version 1

Status: draft 1, 2026-11-24. Audience: teams building a client that uses a Portal AI Host on another PC in the same
home network (the Portal AI chat app on a laptop or phone). Read [api-v1.md](api-v1.md) first: after pairing, a device
uses **the same endpoints, tokens, errors and streaming** as an app on the host PC. This document only adds what is
new: finding the host, pairing, TLS pinning and request signing.

## 1. Overview

- **Off by default.** The owner turns on "Let other devices use this PC's AI" in Portal AI Host (Settings, Other
  devices). Until then nothing listens on the network.
- **Pair once, then use.** The owner presses "Pair a device"; the host shows a 6-digit code and a QR code for 5
  minutes. The device pairs with it. Pairing gives the device an identity the host recognises; it does **not** give
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
portalai://pair?h=<ipv4>&p=<port>&f=<fingerprint>&c=<code>&n=<host name>
```

| Field | Meaning |
|---|---|
| `h` | The PC's private IPv4 address. |
| `p` | TCP port (48123 to 48133). |
| `f` | SHA-256 of the host certificate (DER), base64url without padding (43 characters). |
| `c` | The 6-digit pairing code. |
| `n` | The PC's name, URL-escaped (at most 24 characters). |

Parse it as a URI. Unknown fields must be ignored. With a QR code the device knows the certificate fingerprint before
it connects, so it can refuse any other certificate (section 3).

**Type the code.** The device asks for the PC's address (or lets the user pick it) and the 6 digits. It has no
fingerprint then and must trust the certificate it sees on first use; the proofs in section 3 then show that the host
knows the code. The owner can compare the "security check" groups shown in Portal AI Host with the fingerprint the
device shows (the first 12 characters of the fingerprint in three groups of four). Prefer the QR code.

## 3. Pairing

All binary values are **base64url without padding** (RFC 4648 section 5). JSON is UTF-8, field names are snake_case.
Both pairing requests are `POST` with `Content-Type: application/json`.

### 3.1 Device key

The device makes a **P-256 (secp256r1) key pair** once, and keeps the private key in its secure storage. The public
key is sent as **SubjectPublicKeyInfo DER**. The device name is 1 to 64 characters, no control characters; it is shown
to the owner.

### 3.2 The code key

```
K = HKDF-SHA256(ikm = ASCII(code), salt = ASCII("portalai-pair-v1"), info = ASCII("pair-mac-key"), length = 32)
```

`code` is the six digits as text, with leading zeros ("042917").

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

The device must now check, **in this order**: (1) the TLS certificate it saw has the SHA-256 `fingerprint` (and, if it
had one from the QR code, that it equals `f`); (2) the `host` proof is correct. If either fails it stops and tells the
user, and it never sends step 2.

### 3.5 Step 2: `POST /pair/finish`

```json
{ "host_nonce": "<from step 1>", "mac": "<finish proof>" }
```

Answer `200`: `{ "device_id": "dev_...", "mac": "<done proof>" }`. The device checks the `done` proof (it covers the
`device_id`), stores `device_id`, and **pins the certificate fingerprint** for this host from now on.

### 3.6 Rules and errors

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
- Removing the device in the host revokes its tokens; the owner can also remove one app (Apps page) as usual.
- Pausing, priorities and the fair queue apply by app id, the same as for local apps.
- `GET /host/ping` also needs the signature headers over the LAN.
- Browser requests (`Origin`, `Sec-Fetch-*` headers) are refused, as on loopback.

## 7. Limits and security notes

- 64 simultaneous connections; request bodies up to 4 MiB (8 KiB for the pairing calls).
- **Code-only pairing is weaker than QR pairing.** The 6-digit code protects the pairing against guessing on the
  network (5 attempts), but a device that did not pin a fingerprint from a QR code could be talking to an
  impersonator that answers the first step and then tries all one million codes offline against the device's proof.
  With the QR code the device refuses a certificate that does not match, so this cannot happen. Offer the QR code
  first and tell the user to pair on their own home network.
- The device's private key must stay on the device. The host stores only the public key.
- The time check needs reasonably correct clocks (within 2 minutes).
- Not in v1: automatic discovery (mDNS), relay over the internet, certificate rotation, per-device limits.

## 8. Worked example

Code `042917`, device name `Laptop`, device key (SPKI)

```
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEB2Vj2tcXSTCV2wJ4C9XMN1QgGOXlr3m1X-JXp1YURY7eDf-WRBEzemJZumgn1_ssKU4F5bozdw09LrpkCbFALg
```

| Value | Content |
|---|---|
| `client_nonce` | bytes 0x00 to 0x0F: `AAECAwQFBgcICQoLDA0ODw` |
| `host_nonce` | bytes 0x20 to 0x3F: `ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8` |
| `fingerprint` | bytes 0xA0 to 0xBF: `oKGio6SlpqeoqaqrrK2ur7CxsrO0tba3uLm6u7y9vr8` |
| `K` (hex) | `5f3abb16fdf627e6606db39cb11c1770dae1e12118f241b72289f6095c40d017` |
| start proof | `_mVgkyCbQBBPwtuB1KEJHycZ_DBsr2nphSyweWk4GpM` |
| host proof | `9kf1LBbviBmOCTM1dfN_nICMUONW2izxZEI45QNgXIE` |
| finish proof | `V89eqyNSMfWmq7-5WEbN_c7isxKZLuGwU74AniA0xCY` |
| done proof (`device_id` = `dev_AAAAAAAAAAAAAAAAAAAAAA`) | `zf7Hj8380FourQgCI-PG8zz7oXEMl9m-aj8f-sgIcOE` |

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
