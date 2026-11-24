# Portal relay

A Cloudflare Worker with one Durable Object per host ("room"). It lets a paired device reach its Portal AI Host when
the device is away from home. The relay is a blind byte pipe: the device runs a normal TLS handshake to the host's
pinned certificate through it, so the relay only ever sees encrypted bytes. Pairing and request signing work
unchanged inside that tunnel. The wire format and what a device must implement are in `docs/remote-v1.md`, section
"Relay".

## Limits

All limits are constants in `src/protocol.ts` (`LIMITS`). Remote use is text chat only, so they are strict.

| Limit | Value |
|---|---|
| Device streams per room | 8 |
| Bytes per room per minute (both directions) | 20 MB (each frame counts at least 64 bytes) |
| Binary frames per room per minute | 12000 |
| Largest frame payload | 64 KiB |
| Idle device stream | 5 minutes |
| Host handshake | 10 seconds |
| Unproven host sockets per room | 4 |
| Connections per IP per minute | 30 |

Nothing that passes through is logged or stored. The only state is the rate counters (saved in the relay's own storage, so hibernation does not reset them) and the sockets' attachments.

## Develop and test

```
cd relay
npm install
npm test            # vitest in the Workers runtime
npm run typecheck
```

`wrangler.toml` holds no account id and no secrets.

## Deploy (done once by the owner of the Cloudflare account)

1. `cd relay && npm install`
2. `npx wrangler login` (opens the browser; use the Cloudflare account that should host the relay)
3. `npx wrangler deploy`
   The first deploy creates the Worker `portal-relay` and applies the Durable Object migration (SQLite-backed
   classes `Room` and `Limiter`, which the free plan allows).
4. Note the URL that wrangler prints (`https://portal-relay.<subdomain>.workers.dev`). The host's default relay
   address is `https://portal-relay.portalai.workers.dev`, so the account's workers.dev subdomain must be
   `portalai`, or the constant `RemoteDtos.RelayDefaultUrl` in the host must be changed to match.
5. Check it: `curl https://<relay>/v1/health` answers `ok`.

Free plan note: a hibernating Durable Object costs nothing while idle; each WebSocket message counts as 1/20 of a
request. Host keep-alive pings (`ping` / `pong`) are answered by the runtime without waking the room.
