// Shared constants and small helpers. The wire format is described in docs/remote-v1.md ("Relay").

export const PROTOCOL_VERSION = 1;
export const ROOM_PREFIX = "portalai-relay-v1";
export const HOST_PROOF_PREFIX = "portalai-relay-v1-host";

/** Abuse limits. Remote use is text chat only, so these are strict on purpose. */
export const LIMITS = {
  /** Open device streams per room. */
  MAX_STREAMS_PER_ROOM: 8,
  /** Bytes forwarded per room per minute, both directions together. */
  BYTES_PER_MINUTE: 20 * 1024 * 1024,
  /** Largest payload of one binary frame (the 4-byte stream tag on host frames is extra). */
  MAX_FRAME_BYTES: 64 * 1024,
  /** A device stream that carries no frame for this long is closed. */
  IDLE_MS: 5 * 60_000,
  /** A host socket must prove itself within this time of connecting. */
  HANDSHAKE_MS: 10_000,
  /** How often the idle/handshake sweep runs while something can time out. */
  SWEEP_MS: 30_000,
  /** Host sockets that have connected but not yet proven themselves, per room. */
  MAX_PENDING_HOSTS: 4,
  /** WebSocket connections (host or device) one IP may open per minute. */
  CONNECTS_PER_IP_PER_MINUTE: 30,
  /** Largest certificate the host may send. */
  MAX_CERT_BYTES: 2048,
} as const;

export interface Env {
  ROOM: DurableObjectNamespace;
  LIMITER: DurableObjectNamespace;
  // Optional overrides, used only by the tests.
  IDLE_MS?: string;
  SWEEP_MS?: string;
  HANDSHAKE_MS?: string;
}

export type EffectiveLimits = { -readonly [K in keyof typeof LIMITS]: number };

export function limitsFor(env: Env): EffectiveLimits {
  const l: EffectiveLimits = { ...LIMITS };
  const num = (v: string | undefined) => (v !== undefined && /^\d+$/.test(v) ? Number(v) : undefined);
  l.IDLE_MS = num(env.IDLE_MS) ?? l.IDLE_MS;
  l.SWEEP_MS = num(env.SWEEP_MS) ?? l.SWEEP_MS;
  l.HANDSHAKE_MS = num(env.HANDSHAKE_MS) ?? l.HANDSHAKE_MS;
  return l;
}

export const ROOM_ID_PATTERN = /^[A-Za-z0-9_-]{43}$/;

const enc = new TextEncoder();

export function toBase64Url(bytes: Uint8Array): string {
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export function fromBase64Url(text: string, maxBytes: number): Uint8Array | null {
  if (!/^[A-Za-z0-9_-]*$/.test(text) || text.length > Math.ceil((maxBytes * 4) / 3) + 2) return null;
  try {
    const b64 = text.replace(/-/g, "+").replace(/_/g, "/");
    const bin = atob(b64 + "=".repeat((4 - (b64.length % 4)) % 4));
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out.length <= maxBytes ? out : null;
  } catch {
    return null;
  }
}

export function concat(...parts: Uint8Array[]): Uint8Array {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let o = 0;
  for (const p of parts) {
    out.set(p, o);
    o += p.length;
  }
  return out;
}

/** Room id = base64url(SHA-256("portalai-relay-v1" || SHA-256(certificate DER))). */
export async function roomIdForFingerprint(fingerprint: Uint8Array): Promise<string> {
  return toBase64Url(new Uint8Array(await crypto.subtle.digest("SHA-256", concat(enc.encode(ROOM_PREFIX), fingerprint))));
}

export async function roomIdForCertificate(der: Uint8Array): Promise<string> {
  return roomIdForFingerprint(new Uint8Array(await crypto.subtle.digest("SHA-256", der)));
}

/** The bytes the host signs: "portalai-relay-v1-host\n" + roomId + "\n" followed by the 32 nonce bytes. */
export function hostProofMessage(roomId: string, nonce: Uint8Array): Uint8Array {
  return concat(enc.encode(`${HOST_PROOF_PREFIX}\n${roomId}\n`), nonce);
}
