# Portal AI Host: performance limits and tuning (draft 0.1, 2026-11-23)

How Portal AI Host limits memory and processing power, and how Portal apps (AI Portal, Voice Portal and others) show
the same options, limited by the PC's hardware. It covers:
- the host's settings;
- one shared settings model, so every app uses the same names, ranges, defaults and wording;
- additions to API v1 (`docs/api-v1.md`) so apps can read the host's limits and tune their own requests.

Status: a proposal for host 1.0.2. Every API addition is additive, so `api_version` stays `"1"`.

## 1. Principles

- **One owner per PC.** When the host is in use, the host alone owns the machine-wide limits (memory, graphics card,
  cores, priority). Apps show those limits and link to the host's Settings, but never change them, so two apps
  can't fight over the PC.
- **Apps tune their own work.** Each app may make its own requests faster or better (context size, reply length,
  reasoning, priority), always within the host's limits. The host clamps the values and never fails a request
  because of them.
- **Same words everywhere.** A setting has the same name, range, default and help text in every app.
- **The hardware decides what's offered.** Options that can't work on this PC are hidden, or disabled with a reason
  ("No supported graphics card found").
- **Show the outcome before applying.** Every limit change previews its effect, for example "Fits fully on the
  graphics card" or "Needs a smaller model", using the shared planner.

## 2. Settings model

These are the shared names, used as JSON keys in the API and in settings files. App UI uses the labels.

| Key | Label | Values | Default | Ships |
|---|---|---|---|---|
| `preset` | Performance | `quiet`, `balanced`, `fast`, `custom` | `balanced` | 1.0.2 |
| `memory_limit_bytes` | Maximum memory for AI | 2 GB up to RAM minus `keep_free_bytes` | 80 % of RAM | 1.0.2 |
| `keep_free_bytes` | Always leave free | 2 GB to half of RAM | 4 GB, or 8 GB on 32 GB+ PCs | 1.0.2 |
| `compute` | Run on | `auto`, `gpu`, `cpu` | `auto` | 1.0.2 |
| `gpu_memory_limit_bytes` | Graphics memory for AI | 1 GB to the card's VRAM minus 0.5 GB | all of the card, minus 0.5 GB | 1.0.2 |
| `cpu_threads` | Processor cores | 1 to logical cores minus 1 | logical cores minus 2 (minimum 1) | 1.0.2 |
| `priority` | Priority | `low`, `normal` | `low` | 1.0.2 |
| `cpu_ceiling_percent` | Maximum processor use | 20–100 | 100 | 1.0.3 |
| `ease_off` | Ease off when… | flags: `fullscreen`, `gpu_busy`, `battery`, `app_busy` | `fullscreen`, `battery`, `app_busy` | 1.0.3 |

**`app_busy` (Voice Portal, 2026-11-23):** an app doing live, time-critical work (live transcription) asks the host
to ease off batch work while it runs:
- `POST /host/ease-off` with `{ "reason": "live_audio", "ttl_ms": 30000 }`, renewed while busy, ended with
  `DELETE /host/ease-off`.
- While it's active, the host:
  - runs `batch` requests at lowest priority with fewer threads, and drops back between tokens;
  - leaves `interactive` requests as they are;
  - reports `eased_off_now: "app_busy"`.
- In built-in mode, the same effect comes from `EngineSupervisor` priority and thread controls on the app's own
  side process.

**Memory an app already holds:** the planner's `CommittedMemory` input (in `RecommendContext`) takes the bytes an
app's other models already use, for example Voice Portal's speech model. The fit preview and memory cap then leave
room for it. In host mode the same is reported with `POST /host/ease-off`'s optional `reserved_bytes`, or is simply
seen as used RAM.

**Presets** set the values below. Moving any slider switches the preset to `custom`.

| Preset | Memory | Run on | Cores | Priority | Trade-off text |
|---|---|---|---|---|---|
| `quiet` | 50 % of RAM | `auto` | half the cores | `low` | "About half the speed. Your PC stays responsive." |
| `balanced` | 80 % of RAM | `auto` | cores minus 2 | `low` | "Good speed, and your other programs come first." |
| `fast` | RAM minus `keep_free_bytes` | `auto` | cores minus 1 | `normal` | "Fastest replies. Other programs may slow down." |

**Hard caps:**
- The host enforces `memory_limit_bytes` and (from 1.0.3) `cpu_ceiling_percent` on the engine process through a
  Windows job object, as well as planning against them.
- `priority: low` runs the engine at BELOW_NORMAL priority.
- **Cap margin (1.0.2):** the job object cap is `memory_limit_bytes` plus a margin of 1 GB or 10 %, whichever is
  larger, because the engine needs some memory the planner does not count. The planner still plans against
  `memory_limit_bytes` itself.
- **Cap fallback (1.0.2):** the cap has not been tried against every engine build. If an engine fails to start while a
  cap is set, the host starts it once more without the cap. It writes a line to the host's trace output (no prompt or
  reply text) and `GET /host/performance` reports `"memory_cap_active": false` with a `memory_cap_reason`, until the
  limits are applied again. Only start failures are retried; a crash after a successful start is not.

**Tuning for Pro 1.1 (host only, not offered in apps):** context length, the number of graphics-card layers, batch
size, memory locking, and several models loaded at once.

## 3. Hardware limits on what's offered

Apps and the host build each control from the hardware probe:

- **Memory slider:** from 2 GB up to total RAM minus `keep_free_bytes`, in 1 GB steps.
  - Each stop shows what fits, e.g. "16 GB · fits Qwen3 14B fully".
  - Labels come from the planner and the catalogue's advice wording.
- **Run on:**
  - `gpu` appears only if a supported graphics card has 4 GB of VRAM or more (Vulkan on Windows, Metal on Mac).
  - Otherwise the row reads "Processor only: no supported graphics card found".
- **Graphics memory:** shown only when `compute` is not `cpu`.
- **Cores:** from 1 to logical cores minus 1. The label gives physical and logical counts: "8 of 12 cores".
- **Battery and full-screen rules:** the battery option appears only on PCs with a battery.
- **Fit preview** before applying, using the shared planner, which returns one of:
  - `fits_fully`: "Runs fully on the graphics card";
  - `fits_partial`: "Part on the graphics card, part on the processor: slower";
  - `cpu_only`: "Runs on the processor: slowest";
  - `needs_smaller_model`: "Too little memory for <model>. Switch to <smaller model>?";
  - `too_big`: "Too big for this PC" plus the "Run it on a more powerful PC with Portal AI Host" link.

## 4. How apps use this

### 4.1 Host mode (the host is installed or paired)

The app uses the host. It has no model of its own (AI Portal rule (a), 2026-11-23).

**Show** a read-only summary card from `GET /host/performance` (section 5.1):
- "AI uses up to 24 GB · graphics card · Balanced", with live use from `/host/status` `usage` (section 5.2);
- the "Change in Portal AI Host" button, which opens `portal-ai-host:settings/performance`.

**Per-app tuning**, all optional and clamped by the host:

| App option (label) | Values | Sent as |
|---|---|---|
| Reply speed vs quality | Faster / Balanced / Better | Faster sends `reasoning_effort: none` and a smaller `X-Portal-Context-Tokens`; Better sends `reasoning_effort: medium` and full context |
| Longest reply | Short / Medium / Long | `max_tokens` 512 / 2048 / host `max_output_tokens` |
| Creativity | Precise / Balanced / Creative | `temperature` 0.2 / 0.7 / 1.0 |
| Background jobs | Run in the background (default on for long jobs) | `X-Portal-Priority: batch` |
| Text per chunk (long documents) | automatic | planned from `capabilities.chat.context_tokens` and `speed_estimate` |

Apps that do only one job, like Voice Portal minutes, may skip the visible controls and just pick sensible values.
The recommended minimum is "Reply speed vs quality".

### 4.2 Built-in mode (no host: AI Portal's own engine, or Voice Portal's built-in text model)

- **Code:** use the shared code (`Portal.AiHost.Models`: contracts, signed catalogue, planner, Windows hardware
  probe; project or local reference, not on nuget.org). Run the same engine build pinned in
  `engine/engine.lock.json`, as a side process.
- **Settings:** offer the 1.0.2 subset: Performance preset, Maximum memory, Run on, Processor cores and Priority.
  Use the same keys, ranges, defaults and texts as section 2.
- **Limits:** enforce them the same way: a job object memory cap and BELOW_NORMAL priority for `low`.
- **Link** at the bottom: "Want more control, or a bigger model on a more powerful PC? Get Portal AI Host."
- **Same per-app tuning** as 4.1, mapped onto the local engine (`reasoning_effort`, `max_tokens`, `temperature`,
  context).

### 4.3 Switching modes

- **When a host appears** (installed or paired), the app offers "Use Portal AI Host". Once switched, the app
  unloads its own engine and offers to remove its model ("frees 9.1 GB"), so the PC never keeps two copies.
- **Settings don't carry over:** the host's settings win, and the app's own copy is kept for when the host goes
  away.

## 5. API additions (host 1.0.2)

`/host/capabilities` adds `"performance"` to `endpoints` when these exist. Check that before calling.

### 5.1 `GET /host/performance` (auth)

```json
{
  "preset": "balanced",
  "memory_limit_bytes": 54975581388,
  "keep_free_bytes": 8589934592,
  "compute": "auto",
  "compute_effective": "gpu",
  "gpu_memory_limit_bytes": 8053063680,
  "cpu_threads": 10,
  "priority": "low",
  "cpu_ceiling_percent": 100,
  "ease_off": ["fullscreen", "battery"],
  "eased_off_now": null,
  "fit": "fits_partial",
  "ranges": {
    "memory_limit_bytes": { "min": 2147483648, "max": 60129542144, "step": 1073741824 },
    "cpu_threads": { "min": 1, "max": 11 },
    "gpu_available": true
  },
  "settings_link": "portal-ai-host:settings/performance"
}
```

- **Read-only:** apps can't change these values.
- `eased_off_now` is `null`, or the reason the host is easing off right now (`"fullscreen"`, `"battery"` or
  `"gpu_busy"`), so the app can say "Slower while a game is running".

### 5.2 `GET /host/status` gains `usage`

```json
"usage": { "ram_used_bytes": 19327352832, "vram_used_bytes": 7516192768, "cpu_percent": 34,
           "generation_tokens_per_second": 21.5, "updated_at": "2026-11-23T21:40:02Z" }
```
Read-only, sampled at most every 1.5 s and only while someone asks. `ram_used_bytes` and `vram_used_bytes` are the engine process's own use; 
`cpu_percent` is its share of the whole PC; `generation_tokens_per_second` is the last finished request. Field names are as in api-v1 section 6.

### 5.3 New request header

| Header | Values | Meaning |
|---|---|---|
| `X-Portal-Context-Tokens` | 512 up to `context_tokens` | Ask for a smaller working context, which is faster and leaves more room for other apps. It is clamped, never an error. |

`reasoning_effort`, `max_tokens`, `temperature` and `X-Portal-Priority` already exist (api-v1 sections 4 and 6).

### 5.4 Protocol links

- `portal-ai-host:settings/performance`
- `portal-ai-host:models`
- `portal-ai-host:models?category=<id>` (the Models page filtered to one category: `chat`, `coding`, `reasoning`, `vision`, `search`, `image`, `video`, `speech` or `other`; an unknown id opens the generic Models page)
- `portal-ai-host:apps`
- `portal-ai-host:try` (the Try it page)

Each opens the host window on that page. If the host isn't installed, open the Store page
`ms-windows-store://pdp/?ProductId=9MW5MTZ8LWP8`.

## 6. Shared wording (English source; translations follow the 21 languages)

- **Performance:**
  - Quiet: "About half the speed. Your PC stays responsive."
  - Balanced: "Good speed, and your other programs come first."
  - Fast: "Fastest replies. Other programs may slow down."
- Maximum memory for AI: "The AI never uses more than this. Lower means a smaller model or slower replies."
- Always leave free: "Memory kept free for your other programs."
- **Run on:**
  - Automatic: "Uses the graphics card when it helps."
  - Graphics card: "Fastest, if the model fits."
  - Processor: "Slower, but leaves the graphics card free for games."
- Processor cores: "Fewer cores keep your PC responsive while the AI works."
- **Priority:**
  - Low: "Your own work always comes first."
  - Normal: "Faster, but can slow other programs."
- Host summary card: "Managed by Portal AI Host" with the "Change in Portal AI Host" button.

## 7. Timeline

**Built for 1.0.2 (branch `performance`; UI checks pending):**
- Settings model (section 2, 1.0.2 rows): the seven keys in `HostSettings`, with the defaults, ranges and presets;
  an older `max_memory_percent` maps to `memory_limit_bytes` on first read.
- Enforcement: the planner plans against the memory limit, "always leave free" and the graphics memory limit;
  `compute` `cpu` forces zero graphics layers and `gpu` falls back to the processor without a card of 4 GB or more;
  `cpu_threads` goes to llama-server `--threads`; `low` priority is BELOW_NORMAL; the memory limit is also a job object
  cap on the engine process. Changes apply live (reload or unload, never a delete).
- `GET /host/performance` and `"performance"` in `/host/capabilities` `endpoints`. `cpu_ceiling_percent` is fixed at
  100, `ease_off` is empty and `eased_off_now` is `null` until 1.0.3 (see below).
- `X-Portal-Context-Tokens`: clamps the reply length against an estimate of the prompt (the engine cannot change its
  context per request); see `docs/api-v1.md` section 4.
- Protocol links `portal-ai-host:settings/performance`, `:models`, `:apps` and `:try`.
- Settings > Performance page: preset, maximum memory (with what fits at each size), always leave free, run on,
  graphics memory, processor cores, priority, the fit line and reset to default.
- Already in place before this branch: `usage` in `GET /host/status` (section 5.2) and the `Portal.AiHost.Models` split
  (planner and probe).

**Built for 1.0.3 (branch `ease-off`; UI checks pending):**
- Settings model: `cpu_ceiling_percent` (20 to 100, default 100) and `ease_off` (flags, default `fullscreen`,
  `battery`, `app_busy`) in `HostSettings`; unknown flags are dropped, the ceiling is clamped.
- Processor ceiling: a hard-cap CPU rate on the engine's job object (hundredths of a percent of the whole PC; none at
  100). It is remembered by the supervisor and re-applied at every engine start, so reloads keep it.
- Ease-off: `POST` and `DELETE /host/ease-off` (one lease per app, `ttl_ms` 1 to 120 s, `reserved_bytes` counted as
  memory held by other apps). The host looks at the leases and at the screen and power state every 3 s (and at each
  request), with one cheap call per rule that is on: `SHQueryUserNotificationState` for `fullscreen` (full-screen
  program, Direct3D full screen or presentation mode), `GetSystemPowerStatus` for `battery`. While easing off, the
  engine runs at IDLE priority under at most 50 % of the processor (never above the user's own ceiling); it goes back
  when the reason ends. `eased_off_now` reports the reason (order: `app_busy`, `fullscreen`, `gpu_busy`, `battery`).
- Decisions where this draft was open: easing off is for the whole engine process rather than per request class
  (the engine cannot change threads or priority per request, and `interactive` requests are slowed too while eased
  off); the thread count is not lowered, the processor cap does that job without a restart. `gpu_busy` has no
  reliable cheap reading, so it is a stored flag that never fires yet, and the page does not offer it.
- Settings > Performance page: a "Maximum processor use" slider (20 to 100 in steps of 5) and "Ease off when" check
  boxes for the three working rules. They are not part of any preset, so changing them keeps the preset.

**Planned timeline:**
- **Host 1.0.2:**
  - the Settings > Performance page (section 2, 1.0.2 rows);
  - `GET /host/performance`, `usage`, `X-Portal-Context-Tokens`, and the protocol links;
  - job object caps and priority;
  - the `Portal.AiHost.Models` split (planner and probe) for built-in mode.
- **Host 1.0.3:** the processor ceiling, the ease-off rules, and the `eased_off_now` reason (built, see above;
  `gpu_busy` detection still to do).
- **Pro 1.1:** the advanced tuning in section 2.
- **Apps:** can build against this draft now. Code that checks for `"performance"` in `endpoints` falls back cleanly
  on older hosts.

## 8. Questions for each app

1. Which per-app tuning rows (section 4.1) do you want to show? Which do you set silently?
2. Built-in mode: do you need anything beyond the 1.0.2 subset?
3. Any wording in section 6 that doesn't fit your app's tone?
