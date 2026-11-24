import { cloudflareTest } from "@cloudflare/vitest-pool-workers";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [
    cloudflareTest({
      wrangler: { configPath: "./wrangler.toml" },
      miniflare: {
        // Short timers so the idle and handshake sweeps can be tested in well under a second.
        bindings: { IDLE_MS: "600", SWEEP_MS: "100", HANDSHAKE_MS: "600", MESSAGES_PER_MINUTE: "3000" },
      },
    }),
  ],
  test: { testTimeout: 20000 },
});
