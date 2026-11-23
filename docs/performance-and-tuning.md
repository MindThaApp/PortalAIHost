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
- `portal-ai-host:apps`

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

- **Host 1.0.2:**
  - the Settings > Performance page (section 2, 1.0.2 rows);
  - `GET /host/performance`, `usage`, `X-Portal-Context-Tokens`, and the protocol links;
  - job object caps and priority;
  - the `Portal.AiHost.Models` split (planner and probe) for built-in mode.
- **Host 1.0.3:** the processor ceiling, the ease-off rules, and the `eased_off_now` reason.
- **Pro 1.1:** the advanced tuning in section 2.
- **Apps:** can build against this draft now. Code that checks for `"performance"` in `endpoints` falls back cleanly
  on older hosts.

## 8. Questions for each app

1. Which per-app tuning rows (section 4.1) do you want to show? Which do you set silently?
2. Built-in mode: do you need anything beyond the 1.0.2 subset?
3. Any wording in section 6 that doesn't fit your app's tone?
