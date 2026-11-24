import { DurableObject } from "cloudflare:workers";
import { spkiFromCertificate } from "./der";
import {
  fromBase64Url,
  hostProofMessage,
  limitsFor,
  roomIdForCertificate,
  toBase64Url,
  type EffectiveLimits,
  type Env,
} from "./protocol";

/** Close codes (4000-4999 are free for applications). */
const Close = {
  Normal: 1000,
  Unsupported: 1003,
  TooBig: 1009,
  Auth: 4401,
  HostOffline: 4404,
  Timeout: 4408,
  Replaced: 4409,
  HostGone: 4410,
  Budget: 4429,
} as const;

interface Attachment {
  role: "host" | "device";
  authed: boolean;
  room: string;
  nonce?: string; // host, until proven (base64url)
  since: number;
  last: number;
  stream?: number; // device
  closing?: boolean; // the relay (or the host) ended this stream, so the other side already knows
}

/**
 * One room per host. A blind byte pipe: it checks who the host is, then copies TLS bytes between the host and up to
 * eight device streams. It never reads, logs or stores what passes through. The only state is in the sockets'
 * attachments (so it survives hibernation) and a few in-memory counters.
 */
export class Room extends DurableObject<Env> {
  private readonly limits: EffectiveLimits;
  private nextStream: number;
  private windowStart = 0;
  private windowBytes = 0;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.limits = limitsFor(env);
    this.nextStream = crypto.getRandomValues(new Uint32Array(1))[0]!;
    // Keep-alive pings are answered without waking the room.
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  async fetch(request: Request): Promise<Response> {
    const [, , role, roomId] = new URL(request.url).pathname.split("/");
    if (role === "host") return this.acceptHost(roomId!);
    if (role === "device") return this.acceptDevice(roomId!);
    return new Response(null, { status: 404 });
  }

  // ---- accepting sockets -------------------------------------------------------------------------------------------

  private acceptHost(room: string): Response {
    const pending = this.ctx.getWebSockets("host").filter((w) => !this.att(w).authed).length;
    if (pending >= this.limits.MAX_PENDING_HOSTS) return this.refuse(429, "too_many_pending");
    const pair = new WebSocketPair();
    const server = pair[1];
    this.ctx.acceptWebSocket(server, ["host"]);
    const nonce = toBase64Url(crypto.getRandomValues(new Uint8Array(32)));
    const now = Date.now();
    server.serializeAttachment({ role: "host", authed: false, room, nonce, since: now, last: now } satisfies Attachment);
    server.send(JSON.stringify({ t: "challenge", v: 1, room, nonce }));
    return this.accepted(pair[0]);
  }

  private acceptDevice(room: string): Response {
    if (!this.host()) return this.refuse(503, "host_offline");
    const devices = this.ctx.getWebSockets("device");
    if (devices.length >= this.limits.MAX_STREAMS_PER_ROOM) return this.refuse(429, "too_many_streams");

    const pair = new WebSocketPair();
    const server = pair[1];
    const used = new Set(devices.map((w) => this.att(w).stream));
    let stream = this.nextStream;
    while (used.has(stream)) stream = (stream + 1) >>> 0;
    this.nextStream = (stream + 1) >>> 0;
    const now = Date.now();
    this.ctx.acceptWebSocket(server, ["device"]);
    server.serializeAttachment({ role: "device", authed: true, room, since: now, last: now, stream } satisfies Attachment);
    this.sendHost({ t: "open", s: stream });
    server.send(JSON.stringify({ t: "open" }));
    return this.accepted(pair[0]);
  }

  private accepted(client: WebSocket): Response {
    void this.schedule();
    return new Response(null, { status: 101, webSocket: client });
  }

  private refuse(status: number, error: string): Response {
    return new Response(JSON.stringify({ error }), {
      status,
      headers: { "content-type": "application/json", "cache-control": "no-store" },
    });
  }

  // ---- hibernation handlers ----------------------------------------------------------------------------------------

  async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    const a = this.att(ws);
    a.last = Date.now();
    ws.serializeAttachment(a);
    if (a.role === "host") {
      if (!a.authed) return this.handleHello(ws, a, message);
      return this.fromHost(ws, message);
    }

    this.fromDevice(ws, a, message);
  }

  async webSocketClose(ws: WebSocket, code: number): Promise<void> {
    this.gone(ws);
    this.closeSocket(ws, code >= 1000 && code < 5000 && code !== 1005 && code !== 1006 ? code : Close.Normal);
  }

  async webSocketError(ws: WebSocket): Promise<void> {
    this.gone(ws);
    this.closeSocket(ws, 1011);
  }

  async alarm(): Promise<void> {
    const now = Date.now();
    for (const ws of this.ctx.getWebSockets()) {
      const a = this.att(ws);
      if (a.role === "host" && !a.authed && now - a.since > this.limits.HANDSHAKE_MS) {
        this.closeSocket(ws, Close.Timeout, "handshake_timeout");
      } else if (a.role === "device" && now - a.last > this.limits.IDLE_MS) {
        this.closeSocket(ws, Close.Timeout, "idle");
        this.sendHost({ t: "close", s: a.stream });
      }
    }

    await this.schedule();
  }

  // ---- host side ---------------------------------------------------------------------------------------------------

  private async handleHello(ws: WebSocket, a: Attachment, message: string | ArrayBuffer): Promise<void> {
    const fail = () => this.closeSocket(ws, Close.Auth, "auth");
    if (typeof message !== "string" || message.length > 4096) return fail();
    let hello: { t?: unknown; cert?: unknown; sig?: unknown };
    try {
      hello = JSON.parse(message);
    } catch {
      return fail();
    }

    if (hello.t !== "hello" || typeof hello.cert !== "string" || typeof hello.sig !== "string") return fail();
    const der = fromBase64Url(hello.cert, this.limits.MAX_CERT_BYTES);
    const sig = fromBase64Url(hello.sig, 64);
    const nonce = a.nonce ? fromBase64Url(a.nonce, 32) : null;
    if (!der || !sig || sig.length !== 64 || !nonce || !(await this.proves(der, sig, nonce, a.room))) return fail();

    // Proven. A newer verified host replaces the older one, and the streams that belonged to the older one end.
    for (const other of this.ctx.getWebSockets("host")) {
      if (other !== ws && this.att(other).authed) this.closeSocket(other, Close.Replaced, "replaced");
    }

    for (const d of this.ctx.getWebSockets("device")) this.closeSocket(d, Close.HostGone, "host_changed");
    a.authed = true;
    a.nonce = undefined;
    ws.serializeAttachment(a);
    ws.send(JSON.stringify({ t: "ready", v: 1, limits: this.publicLimits() }));
  }

  /** The room id must be the hash of the certificate, and the host must hold that certificate's private key. */
  private async proves(der: Uint8Array, sig: Uint8Array, nonce: Uint8Array, roomId: string): Promise<boolean> {
    try {
      if ((await roomIdForCertificate(der)) !== roomId) return false;
      const spki = spkiFromCertificate(der);
      if (!spki) return false;
      const key = await crypto.subtle.importKey("spki", spki, { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
      return await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key, sig, hostProofMessage(roomId, nonce));
    } catch {
      return false;
    }
  }

  private fromHost(ws: WebSocket, message: string | ArrayBuffer): void {
    if (typeof message === "string") {
      let m: { t?: unknown; s?: unknown };
      try {
        m = JSON.parse(message);
      } catch {
        return;
      }

      if (m.t === "close" && typeof m.s === "number") {
        const d = this.device(m.s);
        if (d) this.closeSocket(d, Close.Normal, "host_closed");
      }

      return;
    }

    if (message.byteLength < 4) return;
    if (message.byteLength > this.limits.MAX_FRAME_BYTES + 4) {
      this.closeSocket(ws, Close.TooBig, "frame_too_big");
      return;
    }

    const stream = new DataView(message).getUint32(0);
    const d = this.device(stream);
    if (!d) return; // the device already left
    if (!this.spend(message.byteLength - 4)) {
      this.closeSocket(d, Close.Budget, "budget");
      this.sendHost({ t: "close", s: stream });
      return;
    }

    this.touch(d);
    try {
      d.send(message.slice(4));
    } catch {
      // closing
    }
  }

  // ---- device side -------------------------------------------------------------------------------------------------

  private fromDevice(ws: WebSocket, a: Attachment, message: string | ArrayBuffer): void {
    if (typeof message === "string") {
      this.closeSocket(ws, Close.Unsupported, "binary_only");
      this.sendHost({ t: "close", s: a.stream });
      return;
    }

    if (message.byteLength > this.limits.MAX_FRAME_BYTES) {
      this.closeSocket(ws, Close.TooBig, "frame_too_big");
      this.sendHost({ t: "close", s: a.stream });
      return;
    }

    const host = this.host();
    if (!host) {
      this.closeSocket(ws, Close.HostOffline, "host_offline");
      return;
    }

    if (!this.spend(message.byteLength)) {
      this.closeSocket(ws, Close.Budget, "budget");
      this.sendHost({ t: "close", s: a.stream });
      return;
    }

    const frame = new Uint8Array(4 + message.byteLength);
    new DataView(frame.buffer).setUint32(0, a.stream!);
    frame.set(new Uint8Array(message), 4);
    try {
      host.send(frame);
    } catch {
      // closing
    }
  }

  // ---- shared ------------------------------------------------------------------------------------------------------

  /** A socket ended (by itself or by us): tell the other side. */
  private gone(ws: WebSocket): void {
    const a = this.att(ws);
    if (a.role === "device") {
      if (!a.closing) this.sendHost({ t: "close", s: a.stream });
    } else if (a.authed) {
      for (const d of this.ctx.getWebSockets("device")) this.closeSocket(d, Close.HostGone, "host_gone");
    }
  }

  private att(ws: WebSocket): Attachment {
    return (ws.deserializeAttachment() ?? { role: "device", authed: false, room: "", since: 0, last: 0 }) as Attachment;
  }

  private touch(ws: WebSocket): void {
    const a = this.att(ws);
    a.last = Date.now();
    ws.serializeAttachment(a);
  }

  private host(): WebSocket | undefined {
    return this.ctx.getWebSockets("host").find((w) => this.att(w).authed && w.readyState === WebSocket.READY_STATE_OPEN);
  }

  private device(stream: number): WebSocket | undefined {
    return this.ctx
      .getWebSockets("device")
      .find((w) => this.att(w).stream === stream && w.readyState === WebSocket.READY_STATE_OPEN);
  }

  private sendHost(message: object): void {
    try {
      this.host()?.send(JSON.stringify(message));
    } catch {
      // closing
    }
  }

  private closeSocket(ws: WebSocket, code: number, reason?: string): void {
    try {
      const a = this.att(ws);
      if (a.role === "device" && !a.closing) {
        a.closing = true;
        ws.serializeAttachment(a);
      }

      ws.close(code, reason);
    } catch {
      // already closed
    }
  }

  /** Per-room byte budget for the current minute. */
  private spend(bytes: number): boolean {
    const now = Date.now();
    if (now - this.windowStart >= 60_000) {
      this.windowStart = now;
      this.windowBytes = 0;
    }

    this.windowBytes += bytes;
    return this.windowBytes <= this.limits.BYTES_PER_MINUTE;
  }

  private publicLimits(): object {
    return {
      maxStreams: this.limits.MAX_STREAMS_PER_ROOM,
      maxFrameBytes: this.limits.MAX_FRAME_BYTES,
      bytesPerMinute: this.limits.BYTES_PER_MINUTE,
      idleSeconds: Math.round(this.limits.IDLE_MS / 1000),
    };
  }

  /** Keeps the sweep running while anything can time out; no alarm when the room is quiet. */
  private async schedule(): Promise<void> {
    const watch = this.ctx.getWebSockets().some((w) => {
      const a = this.att(w);
      return a.role === "device" || !a.authed;
    });
    if (watch && (await this.ctx.storage.getAlarm()) === null) {
      await this.ctx.storage.setAlarm(Date.now() + this.limits.SWEEP_MS);
    }
  }
}
