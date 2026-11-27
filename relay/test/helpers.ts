import { SELF } from "cloudflare:test";
import { admissionHash, concat, fromBase64Url, hostProofMessage, roomIdForCertificate, toBase64Url } from "../src/protocol";

const te = new TextEncoder();

function len(n: number): number[] {
  if (n < 128) return [n];
  if (n < 256) return [0x81, n];
  return [0x82, n >> 8, n & 0xff];
}

function der(tag: number, ...parts: Uint8Array[]): Uint8Array {
  const body = concat(...parts);
  return concat(new Uint8Array([tag, ...len(body.length)]), body);
}

/** A structurally valid X.509 certificate around a real P-256 key (the relay never checks the certificate's own signature). */
function buildCertificate(spki: Uint8Array): Uint8Array {
  const sigAlg = der(0x30, new Uint8Array([0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x04, 0x03, 0x02]));
  const name = der(0x30, der(0x31, der(0x30, new Uint8Array([0x06, 0x03, 0x55, 0x04, 0x03]), der(0x0c, te.encode("test")))));
  const time = der(0x17, te.encode("260101000000Z"));
  const tbs = der(
    0x30,
    der(0xa0, der(0x02, new Uint8Array([2]))),
    der(0x02, new Uint8Array([1])),
    sigAlg,
    name,
    der(0x30, time, time),
    name,
    spki,
  );
  return der(0x30, tbs, sigAlg, der(0x03, new Uint8Array([0, 1, 2])));
}

export interface Identity {
  cert: Uint8Array;
  room: string;
  key: CryptoKey;
}

export async function newIdentity(): Promise<Identity> {
  const pair = (await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"])) as CryptoKeyPair;
  const spki = new Uint8Array((await crypto.subtle.exportKey("spki", pair.publicKey)) as ArrayBuffer);
  const cert = buildCertificate(spki);
  return { cert, room: await roomIdForCertificate(cert), key: pair.privateKey };
}

export async function sign(id: Identity, room: string, nonce: Uint8Array, adm?: Uint8Array): Promise<Uint8Array> {
  return new Uint8Array(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, id.key, hostProofMessage(room, nonce, adm) as BufferSource));
}

let ipCounter = 0;
export function freshIp(): string {
  ipCounter++;
  return `10.${(ipCounter >> 8) & 255}.${ipCounter & 255}.7`;
}

export class Sock {
  private queue: (string | ArrayBuffer)[] = [];
  private waiters: ((m: string | ArrayBuffer) => void)[] = [];
  closed: { code: number; reason: string } | null = null;
  private closeWaiters: (() => void)[] = [];

  constructor(readonly ws: WebSocket) {
    ws.accept();
    ws.binaryType = "arraybuffer";
    ws.addEventListener("message", (e) => {
      const w = this.waiters.shift();
      if (w) w(e.data as string | ArrayBuffer);
      else this.queue.push(e.data as string | ArrayBuffer);
    });
    ws.addEventListener("close", (e) => {
      this.closed = { code: e.code, reason: e.reason };
      for (const w of this.closeWaiters.splice(0)) w();
      try {
        ws.close(e.code === 1005 ? 1000 : e.code);
      } catch {
        // already closed
      }
    });
  }

  next(ms = 3000): Promise<string | ArrayBuffer> {
    const q = this.queue.shift();
    if (q !== undefined) return Promise.resolve(q);
    return new Promise((resolve, reject) => {
      const t = setTimeout(() => reject(new Error("no message")), ms);
      this.waiters.push((m) => {
        clearTimeout(t);
        resolve(m);
      });
    });
  }

  async nextJson(ms = 3000): Promise<Record<string, unknown>> {
    const m = await this.next(ms);
    if (typeof m !== "string") throw new Error("expected text");
    return JSON.parse(m);
  }

  async nextBinary(ms = 3000): Promise<Uint8Array> {
    const m = await this.next(ms);
    if (typeof m === "string") throw new Error("expected binary, got " + m);
    return new Uint8Array(m);
  }

  waitClosed(ms = 3000): Promise<{ code: number; reason: string }> {
    if (this.closed) return Promise.resolve(this.closed);
    return new Promise((resolve, reject) => {
      const t = setTimeout(() => reject(new Error("not closed")), ms);
      this.closeWaiters.push(() => {
        clearTimeout(t);
        resolve(this.closed!);
      });
    });
  }

  /** True when nothing arrives for a short while. */
  async quiet(ms = 150): Promise<boolean> {
    try {
      await this.next(ms);
      return false;
    } catch {
      return true;
    }
  }

  sendJson(v: object) {
    this.ws.send(JSON.stringify(v));
  }
}

export type Opened = { sock: Sock } | { status: number; body: string };

export async function open(
  role: "host" | "device",
  room: string,
  ip: string = freshIp(),
  extra: Record<string, string> = {},
): Promise<Opened> {
  const res = await SELF.fetch(`https://relay.test/v1/${role}/${room}`, {
    headers: { Upgrade: "websocket", "CF-Connecting-IP": ip, ...extra },
  });
  if (res.status === 101 && res.webSocket) return { sock: new Sock(res.webSocket) };
  return { status: res.status, body: await res.text() };
}

export async function mustOpen(role: "host" | "device", room: string, ip?: string): Promise<Sock> {
  const o = await open(role, room, ip);
  if (!("sock" in o)) throw new Error(`refused ${o.status} ${o.body}`);
  return o.sock;
}

/** Connects as the host and completes the handshake. */
export async function connectHost(id: Identity, admissionToken?: Uint8Array): Promise<Sock> {
  const sock = await mustOpen("host", id.room);
  const challenge = await sock.nextJson();
  const nonce = fromBase64Url(String(challenge.nonce), 32)!;
  const adm = admissionToken ? await admissionHash(admissionToken) : undefined;
  sock.sendJson({
    t: "hello",
    cert: toBase64Url(id.cert),
    sig: toBase64Url(await sign(id, id.room, nonce, adm)),
    ...(adm ? { adm: toBase64Url(adm) } : {}),
  });
  const ready = await sock.nextJson();
  if (ready.t !== "ready") throw new Error("not ready");
  return sock;
}

/** Connects a device and returns it with the stream id the host was told. */
export async function connectDevice(id: Identity, host: Sock): Promise<{ sock: Sock; stream: number }> {
  const sock = await mustOpen("device", id.room);
  const opened = await host.nextJson();
  if (opened.t !== "open") throw new Error("host not told");
  await sock.nextJson(); // {"t":"open"}
  return { sock, stream: opened.s as number };
}

export function tagged(stream: number, payload: Uint8Array): Uint8Array {
  const out = new Uint8Array(4 + payload.length);
  new DataView(out.buffer).setUint32(0, stream);
  out.set(payload, 4);
  return out;
}
