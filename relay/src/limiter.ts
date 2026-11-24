import { DurableObject } from "cloudflare:workers";
import { LIMITS } from "./protocol";

/** Counts connections per IP in a one-minute window (one instance per IP). Holds no personal data beyond the count. */
export class Limiter extends DurableObject {
  private windowStart = 0;
  private count = 0;

  async fetch(): Promise<Response> {
    const now = Date.now();
    if (now - this.windowStart >= 60_000) {
      this.windowStart = now;
      this.count = 0;
    }
    this.count++;
    return this.count > LIMITS.CONNECTS_PER_IP_PER_MINUTE
      ? new Response("slow down", { status: 429, headers: { "retry-after": "60" } })
      : new Response("ok");
  }
}
