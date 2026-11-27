import { SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { spkiFromCertificate } from "../src/der";
import { fromBase64Url, hostProofMessage, LIMITS, roomIdForCertificate, toBase64Url } from "../src/protocol";
import fixture from "./fixtures/dotnet-host.json";
import { connectDevice, connectHost, freshIp, mustOpen, newIdentity, open, Sock, sign, tagged } from "./helpers";

const bytes = (...n: number[]) => new Uint8Array(n);
const status = (o: Awaited<ReturnType<typeof open>>) => ("status" in o ? o.status : 101);

describe("persisted window", () => {
  it("restores counters after the owner is recreated (hibernation) and rolls when expired", async () => {
    const { PersistedWindow } = await import("../src/window");
    const store = new Map<string, unknown>();
    const kv = { get: (k: string) => store.get(k), put: (k: string, v: unknown) => void store.set(k, structuredClone(v)) } as unknown as SyncKvStorage;
    const a = new PersistedWindow(kv, "w", 1, [4]);
    for (let i = 0; i < 8; i++) a.add(0, 1);
    const b = new PersistedWindow(kv, "w", 1, [4]); // a fresh instance after eviction
    expect(b.add(0, 1)).toBeGreaterThanOrEqual(9 - 3); // at most 3 unsaved hits forgotten
    const expired = new PersistedWindow(kv, "w", 1, [4], -1); // window length -1: always expired
    expect(expired.add(0, 1)).toBe(1);
  });
});

describe("room id and host proof", () => {
  it("matches a certificate and signature made by the host's own code", async () => {
    const der = fromBase64Url(fixture.cert, 2048)!;
    expect(await roomIdForCertificate(der)).toBe(fixture.room);
    const spki = spkiFromCertificate(der)!;
    expect(spki).not.toBeNull();
    const key = await crypto.subtle.importKey("spki", spki, { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
    const nonce = fromBase64Url(fixture.nonce, 32)!;
    const sig = fromBase64Url(fixture.sig, 64)!;
    expect(await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key, sig, hostProofMessage(fixture.room, nonce))).toBe(true);
    nonce[0] ^= 1;
    expect(await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key, sig, hostProofMessage(fixture.room, nonce))).toBe(false);
  });

  it("answers health and refuses bad paths", async () => {
    expect((await SELF.fetch("https://relay.test/v1/health")).status).toBe(200);
    expect((await SELF.fetch("https://relay.test/nope")).status).toBe(404);
    expect((await SELF.fetch("https://relay.test/v1/host/short", { headers: { Upgrade: "websocket" } })).status).toBe(400);
    const id = await newIdentity();
    expect((await SELF.fetch(`https://relay.test/v1/host/${id.room}`)).status).toBe(426);
  });
});

describe("host handshake", () => {
  it("accepts the real owner of the room", async () => {
    const id = await newIdentity();
    const host = await connectHost(id);
    expect(host.closed).toBeNull();
  });

  it("refuses a bad signature", async () => {
    const id = await newIdentity();
    const sock = await mustOpen("host", id.room);
    const ch = await sock.nextJson();
    const nonce = fromBase64Url(String(ch.nonce), 32)!;
    const sig = await sign(id, id.room, nonce);
    sig[10] ^= 0xff;
    sock.sendJson({ t: "hello", cert: toBase64Url(id.cert), sig: toBase64Url(sig) });
    expect((await sock.waitClosed()).code).toBe(4401);
  });

  it("refuses a signature over a different nonce", async () => {
    const id = await newIdentity();
    const sock = await mustOpen("host", id.room);
    await sock.nextJson();
    const sig = await sign(id, id.room, new Uint8Array(32));
    sock.sendJson({ t: "hello", cert: toBase64Url(id.cert), sig: toBase64Url(sig) });
    expect((await sock.waitClosed()).code).toBe(4401);
  });

  it("refuses a certificate that does not belong to the room", async () => {
    const owner = await newIdentity();
    const squatter = await newIdentity();
    const sock = await mustOpen("host", owner.room);
    const ch = await sock.nextJson();
    const nonce = fromBase64Url(String(ch.nonce), 32)!;
    // The squatter signs correctly with its own key, but the room is not the hash of its certificate.
    sock.sendJson({ t: "hello", cert: toBase64Url(squatter.cert), sig: toBase64Url(await sign(squatter, owner.room, nonce)) });
    expect((await sock.waitClosed()).code).toBe(4401);
  });

  it("refuses garbage and closes a silent socket", async () => {
    const id = await newIdentity();
    const a = await mustOpen("host", id.room);
    await a.nextJson();
    a.ws.send("not json");
    expect((await a.waitClosed()).code).toBe(4401);

    const b = await mustOpen("host", id.room);
    await b.nextJson();
    expect((await b.waitClosed(4000)).code).toBe(4408); // HANDSHAKE_MS is 600 in the tests
  });

  it("drops the oldest unproven socket instead of refusing a newcomer", async () => {
    const id = await newIdentity();
    const socks = [];
    for (let i = 0; i < LIMITS.MAX_PENDING_HOSTS; i++) socks.push(await mustOpen("host", id.room));
    const extra = await mustOpen("host", id.room);
    expect((await socks[0]!.waitClosed()).code).toBe(4408);
    expect(extra.closed).toBeNull();
  });

  it("keeps a real host out of reach of a flood of unproven sockets from one address", async () => {
    const id = await newIdentity();
    const attacker = freshIp();
    const flood = [];
    for (let i = 0; i < LIMITS.MAX_PENDING_HOSTS + 2; i++) flood.push(await mustOpen("host", id.room, attacker));
    await flood[0]!.waitClosed();
    const open2 = flood.filter((f) => f.closed === null);
    expect(open2.length).toBeLessThanOrEqual(LIMITS.MAX_PENDING_PER_IP);
    await connectHost(id); // the real host still gets in
  });
});

describe("pipe", () => {
  it("refuses a device while the host is away", async () => {
    const id = await newIdentity();
    expect(status(await open("device", id.room))).toBe(503);
  });

  it("forwards frames both ways, tagged with the stream id", async () => {
    const id = await newIdentity();
    const host = await connectHost(id);
    const a = await connectDevice(id, host);
    const b = await connectDevice(id, host);
    expect(a.stream).not.toBe(b.stream);

    a.sock.ws.send(bytes(1, 2, 3));
    b.sock.ws.send(bytes(9));
    expect(await host.nextBinary()).toEqual(tagged(a.stream, bytes(1, 2, 3)));
    expect(await host.nextBinary()).toEqual(tagged(b.stream, bytes(9)));

    host.ws.send(tagged(b.stream, bytes(7, 7)));
    expect(await b.sock.nextBinary()).toEqual(bytes(7, 7));
    expect(await a.sock.quiet()).toBe(true);
  });

  it("tells the host when a device leaves, and the device when the host closes the stream", async () => {
    const id = await newIdentity();
    const host = await connectHost(id);
    const a = await connectDevice(id, host);
    a.sock.ws.close(1000, "bye");
    expect(await host.nextJson()).toEqual({ t: "close", s: a.stream });

    const b = await connectDevice(id, host);
    host.sendJson({ t: "close", s: b.stream });
    expect((await b.sock.waitClosed()).code).toBe(1000);
  });

  it("limits the streams per room", async () => {
    const id = await newIdentity();
    const host = await connectHost(id);
    const devices = [];
    for (let i = 0; i < LIMITS.MAX_STREAMS_PER_ROOM; i++) devices.push(await connectDevice(id, host));
    expect(status(await open("device", id.room))).toBe(429);

    // A free slot can be used again.
    devices[0]!.sock.ws.close(1000);
    expect((await host.nextJson()).t).toBe("close");
    await connectDevice(id, host);
  });

  it("closes a stream that sends an oversized frame", async () => {
    const id = await newIdentity();
    const host = await connectHost(id);
    const d = await connectDevice(id, host);
    d.sock.ws.send(new Uint8Array(LIMITS.MAX_FRAME_BYTES + 1));
    expect((await d.sock.waitClosed()).code).toBe(1009);
    expect(await host.nextJson()).toEqual({ t: "close", s: d.stream });
  });

  it("closes a stream that sends text", async () => {
    const id = await newIdentity();
    const host = await connectHost(id);
    const d = await connectDevice(id, host);
    d.sock.ws.send("hello");
    expect((await d.sock.waitClosed()).code).toBe(1003);
  });

  it("stops a stream that goes over the byte budget", async () => {
    const id = await newIdentity();
    const host = await connectHost(id);
    const d = await connectDevice(id, host);
    const frame = new Uint8Array(LIMITS.MAX_FRAME_BYTES);
    const frames = LIMITS.BYTES_PER_MINUTE / LIMITS.MAX_FRAME_BYTES; // the budget (each frame also pays the minimum overhead)
    for (let i = 0; i < frames; i++) d.sock.ws.send(frame);
    d.sock.ws.send(bytes(1)); // one byte too many
    expect((await d.sock.waitClosed(15000)).code).toBe(4429);
  });

  it("does not forward empty frames and still charges them toward the frame budget", async () => {
    const id = await newIdentity();
    const host = await connectHost(id);
    const d = await connectDevice(id, host);
    d.sock.ws.send(new Uint8Array(0));
    d.sock.ws.send(bytes(1));
    const got = await host.nextBinary();
    expect(got.length).toBe(5); // the first frame the host sees is the 1-byte one
    for (let i = 0; i < 3000; i++) d.sock.ws.send(new Uint8Array(0)); // MESSAGES_PER_MINUTE is 3000 in the tests
    expect((await d.sock.waitClosed(20000)).code).toBe(4429);
  });

  it("limits the streams one address may hold in a room", async () => {
    const id = await newIdentity();
    const host = await connectHost(id);
    const ip = freshIp();
    for (let i = 0; i < LIMITS.MAX_STREAMS_PER_IP; i++) await mustOpen("device", id.room, ip);
    expect(status(await open("device", id.room, ip))).toBe(429);
    await connectDevice(id, host).catch(() => undefined); // other addresses are not affected
  });

  it("closes a device stream that has gone idle", async () => {
    const id = await newIdentity();
    const host = await connectHost(id);
    const d = await connectDevice(id, host);
    expect((await d.sock.waitClosed(5000)).code).toBe(4408); // IDLE_MS is 600 in the tests
    expect(await host.nextJson()).toEqual({ t: "close", s: d.stream });
  });

  it("ends every stream when the host goes away", async () => {
    const id = await newIdentity();
    const host = await connectHost(id);
    const d = await connectDevice(id, host);
    host.ws.close(1000, "bye");
    expect((await d.sock.waitClosed()).code).toBe(4410);
    expect(status(await open("device", id.room))).toBe(503);
  });
});

describe("one host per room", () => {
  it("lets a newer verified host replace the older one", async () => {
    const id = await newIdentity();
    const first = await connectHost(id);
    const d = await connectDevice(id, first);
    const second = await connectHost(id);
    expect((await first.waitClosed()).code).toBe(4409);
    expect((await d.sock.waitClosed()).code).toBe(4410);

    // New streams go to the new host.
    const again = await connectDevice(id, second);
    again.sock.ws.send(bytes(5));
    expect(await second.nextBinary()).toEqual(tagged(again.stream, bytes(5)));
  });

  it("does not kill streams of the new host when the replaced host's socket finishes closing", async () => {
    const id = await newIdentity();
    const first = await connectHost(id);
    const second = await connectHost(id);
    expect((await first.waitClosed()).code).toBe(4409);
    const d = await connectDevice(id, second);
    await new Promise((r) => setTimeout(r, 300)); // let the old socket's close handler run
    expect(d.sock.closed).toBeNull();
    d.sock.ws.send(bytes(4));
    expect(await second.nextBinary()).toEqual(tagged(d.stream, bytes(4)));
  });

  it("does not let an unproven socket replace the host", async () => {
    const id = await newIdentity();
    const real = await connectHost(id);
    const fake = await mustOpen("host", id.room);
    await fake.nextJson();
    fake.ws.send("{}");
    expect((await fake.waitClosed()).code).toBe(4401);
    expect(real.closed).toBeNull();
    await connectDevice(id, real);
  });
});

describe("rate limit per address", () => {
  it("caps the connections one IP may open per minute", async () => {
    const id = await newIdentity();
    const ip = freshIp();
    for (let i = 0; i < LIMITS.CONNECTS_PER_IP_PER_MINUTE; i++) {
      expect(status(await open("device", id.room, ip))).toBe(503);
    }

    expect(status(await open("device", id.room, ip))).toBe(429);
    expect(status(await open("device", id.room, freshIp()))).toBe(503);
  });
});

describe("device admission", () => {
  const token = crypto.getRandomValues(new Uint8Array(32));
  const header = (t: Uint8Array) => ({ "x-portal-admission": toBase64Url(t) });

  it("lets a device with the right token in, and tells the host", async () => {
    const id = await newIdentity();
    const host = await connectHost(id, token);
    const o = await open("device", id.room, freshIp(), header(token));
    expect("sock" in o).toBe(true);
    if (!("sock" in o)) return;
    expect((await host.nextJson()).t).toBe("open");
    expect((await o.sock.nextJson()).t).toBe("open");
  });

  it("closes a device without a token with 4403 and never tells the host", async () => {
    const id = await newIdentity();
    const host = await connectHost(id, token);
    const o = await open("device", id.room);
    expect("sock" in o).toBe(true);
    if (!("sock" in o)) return;
    expect((await o.sock.waitClosed()).code).toBe(4403);
    expect(await host.quiet()).toBe(true);
  });

  it("closes a device with a wrong, truncated or malformed token", async () => {
    const id = await newIdentity();
    const host = await connectHost(id, token);
    const wrong = crypto.getRandomValues(new Uint8Array(32));
    for (const h of [header(wrong), header(token.slice(0, 31)), { "x-portal-admission": "not base64!" }, { "x-portal-admission": "" }]) {
      const o = await open("device", id.room, freshIp(), h);
      expect("sock" in o).toBe(true);
      if (!("sock" in o)) return;
      expect((await o.sock.waitClosed()).code).toBe(4403);
    }

    expect(await host.quiet()).toBe(true);
  });

  it("does not read the token from the URL", async () => {
    const id = await newIdentity();
    await connectHost(id, token);
    const res = await SELF.fetch(`https://relay.test/v1/device/${id.room}?a=${toBase64Url(token)}`, {
      headers: { Upgrade: "websocket", "CF-Connecting-IP": freshIp() },
    });
    const sock = new Sock(res.webSocket!);
    expect((await sock.waitClosed()).code).toBe(4403);
  });

  it("keeps today's behaviour for a host that publishes nothing", async () => {
    const id = await newIdentity();
    const host = await connectHost(id);
    const d = await connectDevice(id, host);
    expect(d.stream).toBeTypeOf("number");
    const extra = await open("device", id.room, freshIp(), header(token)); // a token nobody asked for is ignored
    expect("sock" in extra).toBe(true);
  });

  it("refuses a hello whose admission hash is malformed or not covered by the signature", async () => {
    const id = await newIdentity();
    for (const adm of ["short", toBase64Url(new Uint8Array(33)), 42]) {
      const sock = await mustOpen("host", id.room);
      const nonce = fromBase64Url(String((await sock.nextJson()).nonce), 32)!;
      sock.sendJson({ t: "hello", cert: toBase64Url(id.cert), sig: toBase64Url(await sign(id, id.room, nonce)), adm });
      expect((await sock.waitClosed()).code).toBe(4401);
    }

    // A valid-looking hash that the signature does not cover (someone edited the hello in transit).
    const sock = await mustOpen("host", id.room);
    const nonce = fromBase64Url(String((await sock.nextJson()).nonce), 32)!;
    sock.sendJson({ t: "hello", cert: toBase64Url(id.cert), sig: toBase64Url(await sign(id, id.room, nonce)), adm: toBase64Url(new Uint8Array(32)) });
    expect((await sock.waitClosed()).code).toBe(4401);
  });

  it("forgets the requirement when the host reconnects without one", async () => {
    const id = await newIdentity();
    await connectHost(id, token);
    await connectHost(id); // replaces the first host, publishing nothing
    const o = await open("device", id.room);
    expect("sock" in o).toBe(true);
    if ("sock" in o) expect(await o.sock.quiet()).toBe(false); // gets {"t":"open"}, not a close
  });
});
