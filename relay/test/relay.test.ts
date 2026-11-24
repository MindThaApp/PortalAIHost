import { SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { spkiFromCertificate } from "../src/der";
import { fromBase64Url, hostProofMessage, LIMITS, roomIdForCertificate, toBase64Url } from "../src/protocol";
import fixture from "./fixtures/dotnet-host.json";
import { connectDevice, connectHost, freshIp, mustOpen, newIdentity, open, sign, tagged } from "./helpers";

const bytes = (...n: number[]) => new Uint8Array(n);
const status = (o: Awaited<ReturnType<typeof open>>) => ("status" in o ? o.status : 101);

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

  it("limits sockets that have not proven themselves", async () => {
    const id = await newIdentity();
    for (let i = 0; i < LIMITS.MAX_PENDING_HOSTS; i++) await mustOpen("host", id.room);
    expect(status(await open("host", id.room))).toBe(429);
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
    const frames = LIMITS.BYTES_PER_MINUTE / LIMITS.MAX_FRAME_BYTES; // exactly the budget
    for (let i = 0; i < frames; i++) d.sock.ws.send(frame);
    d.sock.ws.send(bytes(1)); // one byte too many
    expect((await d.sock.waitClosed(15000)).code).toBe(4429);
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
