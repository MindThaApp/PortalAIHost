/**
 * A one-minute counting window whose state survives Durable Object hibernation and eviction (sync KV storage).
 * A write happens when the window rolls and whenever a slot has gained `flushAt[slot]` since the last write,
 * so after a restart at most that much spend per slot is forgotten (bounded slack), not the whole window.
 */
export class PersistedWindow {
  private start = 0;
  private counts: number[];
  private dirty = 0;

  constructor(
    private readonly kv: SyncKvStorage,
    private readonly key: string,
    private readonly slots: number,
    /** Per slot, how much spend may stay unsaved before a write. */
    private readonly flushAt: number[],
    private readonly windowMs = 60_000,
  ) {
    this.counts = new Array(slots).fill(0);
    const saved = kv.get<{ s: number; c: number[] }>(key);
    if (saved && Array.isArray(saved.c) && saved.c.length === slots && Number.isFinite(saved.s)) {
      this.start = saved.s;
      this.counts = saved.c.map((n) => (Number.isFinite(n) ? n : 0));
    }
  }

  /** Adds to a slot and returns the new total for that slot, rolling the window first when it has expired. */
  add(slot: number, amount: number): number {
    const now = Date.now();
    if (now - this.start >= this.windowMs || now < this.start) {
      this.start = now;
      this.counts.fill(0);
      this.counts[slot] = amount;
      this.dirty = 0;
      this.persist();
      return amount;
    }

    this.counts[slot]! += amount;
    this.dirty += amount / this.flushAt[slot]!;
    if (this.dirty >= 1) this.persist();
    return this.counts[slot]!;
  }

  private persist(): void {
    this.dirty = 0;
    this.kv.put(this.key, { s: this.start, c: this.counts });
  }
}
