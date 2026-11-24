import { DurableObject } from "cloudflare:workers";
import { LIMITS, type Env } from "./protocol";
import { PersistedWindow } from "./window";

/** Counts connections per IP in a one-minute window (one instance per IP). Holds no personal data beyond the count. */
export class Limiter extends DurableObject<Env> {
  // The window survives hibernation and eviction (saved every 4 connections and on each window roll).
  private readonly window: PersistedWindow;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.window = new PersistedWindow(ctx.storage.kv, "w", 1, [4]);
  }

  async fetch(): Promise<Response> {
    const count = this.window.add(0, 1);
    return count > LIMITS.CONNECTS_PER_IP_PER_MINUTE
      ? new Response("slow down", { status: 429, headers: { "retry-after": "60" } })
      : new Response("ok");
  }
}
