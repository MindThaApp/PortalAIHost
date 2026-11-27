# Portal AI Host: user guide

Portal AI Host runs an AI model on your PC and shares it with your Portal apps. Your questions and files stay on your
own machines. This guide explains every part of the app, what its settings do, and what to do when something goes wrong.

Places where a picture belongs are marked `[screenshot: ...]`.

## What the Store version includes

- Download and run chat and text models on your PC, with a memory budget and placement (graphics card, memory or processor).
- Share them with Portal apps on this PC, with a permission prompt for each app.
- Pair other devices on your home network and use this PC's models from a laptop.
- Devices page: pause a device, daily limits, quiet hours, which models each may use, gaming mode.
- Two models at once for a trusted device.
- Notifications, crash reports (on by default, can be turned off) and your data kept on your own machines.

**Not in this version:** image and video generation, a voice helper, and MP4 video or clip joining. Those need extra
components that are not part of the Store package. The relay access key (section 13) stays off until the relay service is
updated; the home network works without it.

## Contents

1. [Quick start](#1-quick-start)
2. [System requirements, install, update, uninstall](#2-system-requirements-install-update-uninstall)
3. [Finding your way around](#3-finding-your-way-around)
4. [Models](#4-models)
5. [Apps and permissions](#5-apps-and-permissions)
6. [Try it](#6-try-it)
7. [Performance settings](#7-performance-settings)
8. [Notifications](#8-notifications)
9. [Letting other devices use this PC](#9-letting-other-devices-use-this-pc)
10. [Devices on this PC](#10-devices-on-this-pc)
11. [Model control and two models at once](#11-model-control-and-two-models-at-once)
12. [Using your home PC from a laptop (client mode)](#12-using-your-home-pc-from-a-laptop-client-mode)
13. [Away from home: the relay and the relay access key](#13-away-from-home-the-relay-and-the-relay-access-key)
14. [Privacy and crash reports](#14-privacy-and-crash-reports)
15. [Where your data is stored, backup and removal](#15-where-your-data-is-stored-backup-and-removal)
16. [Keyboard shortcuts](#16-keyboard-shortcuts)
17. [Troubleshooting and FAQ](#17-troubleshooting-and-faq)
18. [Coming later](#18-coming-later)
19. [Support](#19-support)

---

## 1. Quick start

1. Install Portal AI Host from the Microsoft Store and open it.
2. The welcome window checks your PC (memory, graphics card, free disk space) and recommends one model. Choose
   **Start with recommended settings**, or **Download** to pick the model yourself. **Choose later** skips it.
3. Look for the tray icon near the clock. Portal AI Host waits there quietly. Click it for a quick status.
4. Open **Try it** and ask a short question to check that the model works.
5. The first time a Portal app wants to use AI, a prompt asks you to allow or deny it.

[screenshot: the welcome window with the recommended model]

On a laptop that should use your home PC's AI instead, choose **Use my home PC** in the welcome window. See
[section 12](#12-using-your-home-pc-from-a-laptop-client-mode).

**Free or paid:** Portal AI Host is free. No account, sign-in or purchase is needed for any feature in this guide.

---

## 2. System requirements, install, update, uninstall

### Requirements

| | Minimum | Recommended |
|---|---|---|
| Windows | Windows 11 (21H2, build 22000) or later, x64 or Arm64 | The same |
| Memory | 8 GB | 16 GB or more (32 GB or more for the large models) |
| Graphics | None (the model then runs on the processor, which is slower) | A Vulkan graphics card with 8 GB of video memory or more (AMD, NVIDIA or Intel) |
| Free disk space | 5 GB | 25 GB |

Models are 2 to 20 GB each. Arm64 PCs run the model on the processor. An internet connection is needed only to download
models and for the relay; once a model is on your PC, it works offline.

### Install

Install from the Microsoft Store. Nothing else is needed: no Python, no command line, no other software. The AI engine
is part of the app.

### Start with Windows

**Settings > General > Start with Windows** (default: off, apart from the welcome window's "Start with recommended
settings", which turns it on). When on, Portal AI Host waits in the tray after you sign in, so apps can use it straight
away.
- If the switch says it is only available in the installed app, you are running a development copy.
- If you turned it off in Task Manager (Startup apps), turn it back on there.
- If your organisation sets it, the switch is locked.

### Update

The Store updates the app. After an update, **What's new** opens once. You can open it again from the app menu.
Models stay where they are across updates.

### Uninstall

Remove the app in **Windows Settings > Apps**. Models in the default folder are stored inside the app's own data and are
removed with it. A custom models folder that you chose is not touched. See
[section 15](#15-where-your-data-is-stored-backup-and-removal) before uninstalling if you want to keep anything.

---

## 3. Finding your way around

The left side has five pages.

| Page | What it is for | Shortcut |
|---|---|---|
| Home | Status, the active model, memory use, the app using AI right now | Ctrl+1 |
| Models | Download, use and remove models | Ctrl+2 |
| Devices and apps | Allow or remove Portal apps, pause them, set priority | Ctrl+3 |
| Try it | Ask the model one short question | Ctrl+4 |
| Settings | Everything configurable | Ctrl+, |

A sixth page, **This PC's devices**, lists the laptops and phones paired with this PC (see
[section 10](#10-devices-on-this-pc)). The app menu holds Settings, Keyboard shortcuts, What's new and About.

**Home** shows whether the AI is Ready, Busy answering, Getting the model ready, Resting (the model wakes up when an app
asks), or has no model yet. **RIGHT NOW** shows which app is getting an answer and about how many words a second. It also
shows how much memory and graphics memory the model uses, and a **Load now** / **Unload now** button. **TODAY** counts
each app's requests. A health card says "All good" or lists what needs attention, each with a one-click fix such as
Load the model, Restart the engine, Check files or Open performance settings.

[screenshot: Home page while an app is using the model]

The tray icon opens a small pop-up: model, memory, last request and queue, with **Open app** and **Unload model**.

[screenshot: the tray pop-up]

If the host program is not running, Home offers **Start Portal AI Host**. If it closed unexpectedly more than once, a
**Safe mode** bar says it started with careful settings. Your apps still work; restart the PC if it keeps happening.

---

## 4. Models

A model is the AI that does the work. **Models** (Ctrl+2) lists what you have and what you can add.

### Recommended model

The first run and the Home page recommend "the best model for this PC", with a plain reason, based on your memory,
graphics memory and free disk space. When a better one becomes available, a bar says **A better model is available**.
Your current model stays until the new one is ready, and you choose when to remove the old one.

### Download a model

1. Open **Models**. Pick a model under **Models you can add**.
2. Press **Download**. A bar shows progress and time left.
3. **Pause** and **Resume** at any time. **Stop** discards the part already downloaded (after a confirmation).
4. The host checks the downloaded files before using them.
5. When it is done, press **Use this model**.

Downloads can be resumed after a restart and are checked file by file. A download needs enough free disk space; if not,
the page says how much is missing and offers **Change folder in Settings**. The PC is kept awake while a download runs
(Settings > Performance, default on).

[screenshot: Models page with a download in progress]

A model marked "This one may be too big for this PC" can still be downloaded, but may run slowly or not fit.

### Details, check files, download again, remove

- **Details** shows what the model is good for, what to keep in mind, the memory it needs, its licence, when it was last
  used and how much space it uses.
- **Check files** re-checks a model on disk. It never deletes anything. A damaged model is marked **Damaged**.
- **Download again** (for a damaged model) removes the damaged copy and downloads it again. Nothing else is deleted.
- **Remove** takes a model off this PC after a confirmation that says how much space it frees. If the model is in use, the
  host unloads it first and asks what to use next. Only files the host downloaded are deleted.

### Add your own model

**Add your own model...** lets you pick a `.gguf` file. The host makes its own copy in the models folder. Your original is
never changed or moved. The row is marked **Added by you**, and the host cannot estimate how much memory it needs, so try
it and see. **Remove** on such a model deletes only the host's copy.

### Free up space

**Free up space** lists leftovers the app itself created: unfinished downloads nothing is resuming, old copies left after
moving your models, and models a check found damaged. You confirm before anything is removed. Models you use and files you
added yourself are never listed. Unfinished downloads that you can still resume are kept for a week.

### Models folder and moving your models

**Settings > Models folder** sets where new downloads go. Leave it empty for the default folder; type a full path such as
`D:\Models` and press Enter. Models already downloaded are not moved or deleted. They stay where they are and stay listed.
To move them, accept **Move my models there**. They are copied, checked, and only then are the old copies removed. The
model in use is unloaded while this runs, and **Cancel move** leaves everything where it was.

### Memory budget and bars

The **Memory budget** bars on the Models page show RAM and graphics memory: how much the loaded models use, how much is
reserved while a model loads, and how much is free against your limits (see
[section 7](#7-performance-settings)). A load that would go over your memory limit is refused with a plain reason before
anything is stopped.

[screenshot: the Memory budget bars with two models loaded]

### Placement: graphics card, memory or processor only

Where a model runs is decided by **Settings > Performance > Run on** (Automatic, Graphics card, Processor). A model that
does not fit the graphics card runs partly on the processor (slower). See also the per-model placement in
[section 11](#11-model-control-and-two-models-at-once), which includes **Processor only**.

### Limits and free versus paid

- All models in the list are free for commercial use (Apache 2.0 or MIT licences); each model shows its own licence.
- Models are downloaded from their publishers' pages on Hugging Face. The host only accepts a trusted, signed model list.
  If it cannot load that list, a bar says **Model list unavailable**; models already on this PC still work.

### Troubleshooting

- *No models listed to download:* check your internet connection and press **Try again** on Home.
- *Download stops:* press **Resume**; if free space is short, free some space or change the folder.
- *Model "may be too big":* choose a smaller one, or lower the load in Settings > Performance.

---

## 5. Apps and permissions

Open **Devices and apps** (Ctrl+3).

- **Waiting for your answer:** an app that asked to use the AI shows here and as a prompt ("Let Voice Portal use AI on this
  PC?"). Choose **Allow**, **Don't allow** or **Decide later**. The prompt shows whether the app is a Verified Portal app,
  an installed app that is not a Portal app, or not verified (then it names where it runs from; allow it only if you
  started it yourself).
- **Apps that can use your AI host:** each allowed app shows requests today and when it was last used.
  - **Pause** refuses new requests until you resume; the app keeps its access.
  - **Priority** (High, Normal, Low) decides who goes first when several apps ask at once. Interactive requests go
    before batch work, and a request that waits a long time moves up so a low-priority app is never starved.
  - **Remove access** stops the app until you allow it again. **Undo** appears for a moment.

Apps must ask before they can use the host. The host listens only on this PC unless you turn on sharing with other devices
(section 9).

[screenshot: Devices and apps page with one waiting app and one allowed app]

Developers: the host offers an OpenAI-compatible local API at `http://127.0.0.1:41870` (chat completions with streaming,
embeddings, model list). See `api-v1.md`.

**Where it is stored:** allowed apps and their tokens are kept in the host's data folder, protected for your Windows user.
Removing an app's access removes its token.

---

## 6. Try it

**Try it** (Ctrl+4) checks that the model works. Type a short question and press **Ask** (or Ctrl+Enter). **Stop** ends the
answer, **Clear** empties the page. Suggested questions are offered. It shows how long the first words took and the speed in
tokens per second. Nothing you type here is saved.

[screenshot: Try it with an answer and the timing line]

---

## 7. Performance settings

**Settings > Performance** keeps the AI from slowing your PC down. Changes apply at once; a model that no longer fits is
reloaded with the new limits or unloaded (it stays on disk). **Reset to default** restores the section.

| Setting | What it does | Default |
|---|---|---|
| Performance | A preset: **Quiet** (about half the speed, PC stays responsive), **Balanced** (good speed, your programs come first), **Fast** (fastest replies, other programs may slow down), **Custom** | Balanced |
| Maximum memory for AI | The AI never uses more memory than this | 80% of the PC's memory |
| Always leave free | Memory kept free for your other programs | Set by the preset |
| Run on | **Automatic** (graphics card when it helps), **Graphics card** (fastest, if the model fits), **Processor** (slower, leaves the graphics card free for games) | Automatic |
| Graphics memory for AI | A cap on graphics memory the AI may use | Set by the preset |
| Processor cores | Fewer cores keep your PC responsive | Set by the preset |
| Priority | **Low** (your own work first) or **Normal** | Low |
| Maximum processor use | A ceiling on processor use | No limit (100%) |
| Ease off when | The AI runs at the lowest priority and uses at most half of your processor, and goes back to normal when it ends. Choose any of: a game or full-screen program is running, the PC is on battery, a Portal app is busy with live work such as dictation | Full-screen program, battery and busy app are on |

Under **Free up memory when idle** in **Settings > General** you choose **Keep the model loaded**, **After 5 minutes** or
**After 10 minutes** (default 10 minutes, 5 on battery). The model loads again the next time an app asks. Keeping it loaded
answers faster but uses memory all the time.

The slider hints say whether the chosen memory fits a model fully on the graphics card, partly on the processor, or only on
the processor, and offer a smaller model when the current one no longer fits.

[screenshot: Settings > Performance]

**Troubleshooting:** slow answers on a graphics-card PC usually mean the model only partly fits the graphics card. Pick a
smaller model, or raise Graphics memory for AI. If your PC stutters during use, pick Quiet or lower Maximum processor use.

---

## 8. Notifications

**Settings > Notifications** controls short Windows messages shown when something needs you and the window is closed. They
stay quiet while a full-screen program is in front, and never repeat more than once every few minutes.

| Switch | Meaning |
|---|---|
| A model download finishes or fails | On by default |
| An app asks for approval | On by default |
| The AI engine stops or a check fails | On by default |
| A device pairs or connects from away | On by default (the alerts of [section 10](#10-devices-on-this-pc)) |

Each can be turned off on its own. The same page's **Reset to default** restores them.

---

## 9. Letting other devices use this PC

**Settings > Other devices.** Off by default. When on, your own laptop or another PC on your home network can ask to use
this PC's AI. A device must be paired with a code first, and each app on it still needs your approval.

### Pair a device

1. Turn on **Let other devices use this PC's AI**. Windows may ask to allow a network rule.
2. Press **Pair a device**. A code and QR code appear, and **Copy link** copies a pairing link.
3. On the other device, open its Portal app (or Portal AI Host in client mode, [section 12](#12-using-your-home-pc-from-a-laptop-client-mode)), choose to connect to a PC, then scan the code, type it, or paste the link.
4. It checks that it is talking to this PC before sending anything, then shows the PC's name and a fingerprint to confirm.

The code works once, for 5 minutes, for one device. Wrong tries use it up; make a new one with **Pair a device**. **Cancel
pairing** ends it.

[screenshot: the pairing code and QR code]

### Paired devices

Each paired device is listed with its last-used time and a **Remove** button. Removing a device stops its access at once,
and its requests in flight end. It must pair again to return. At most **16** devices can be paired.

### Other settings in this card

- **Also reach this PC away from home** (the relay), see [section 13](#13-away-from-home-the-relay-and-the-relay-access-key). Default: on once other devices are enabled.
- **Relay access key**, see section 13. Default: off.
- **Devices may replace models**, see [section 11](#11-model-control-and-two-models-at-once). Default: off.

**What crosses the network:** requests and answers travel encrypted between your own devices. The home network connection
uses a certificate that the device pinned when it paired. Nothing goes to a server of ours except, optionally, the blind
relay (section 13).

**Troubleshooting:** *A device cannot find this PC:* check both are on the same network and that this PC is awake and that
the host is running. *The code is "not right" or "ran out":* make a new one. *"Not on your home network":* pair once at
home first.

---

## 10. Devices on this PC

The **This PC's devices** page shows one card per paired laptop. It is the place to see what each laptop is doing and to
limit it.

[screenshot: Devices on this PC with two device cards]

### What a card shows

- Status (Online, Idle, Offline), route (Home network or Relay (away)) and last seen.
- What it is running now, and what is waiting.
- Today's requests and tokens, with a 7-day bar, per app.
- The model in use, who loaded it, and speed in tokens per second and time to the first byte.

### Controls

| Control | What it does |
|---|---|
| Pause / Resume | Pauses the whole device, or one of its apps. The laptop gets a clear "paused" answer and uses its own fallback, if you set one. |
| Rename | Changes the name shown for the device |
| Limits | Priority, daily limits, quiet hours and allowed models (below) |
| Model control (switch) | Lets this device load and unload models. See [section 11](#11-model-control-and-two-models-at-once) |
| Take back | Unloads the model this device loaded. This PC's own choice comes back. No model file is touched. |
| Forget | Removes the device. Its access stops at once and its usage history is deleted. Models are not touched. |

**Limits** (under the card's **Limits** button, then **Save**):

- **Same priority as this PC's apps.** Off (default): this PC's own apps go first. On: the device waits in the same line.
- **Requests per day** and **Tokens per day.** Default: no limit. When used up, the device is told to come back after
  midnight on this PC.
- **Quiet hours**, with **From** and **Until** (this PC's time). The device gets no answers between those times. Default: off.
- **Models this device may use.** Default: all models.

### Gaming mode

At the top of the page. **Default: off.** When on, calls from other devices are told this PC is busy while you are
playing, so the games keep the graphics card. This PC counts as "gaming" when Windows reports a full-screen game, or when
another program keeps the graphics card's 3D engine busy at 40% or more for about 10 seconds, so games in a window or a
borderless window count too. It stops counting about 30 seconds after the load ends. If the graphics counters are not
available on your PC, only the full-screen check is used. The laptop then falls back as set in its own settings.

### Alerts

A Windows notification appears when a new device pairs, and when a device connects through the relay from away. Turn them off
with **Settings > Notifications > A device pairs or connects from away**.

### Data

Usage numbers are counts only (requests and tokens per app per day, 7 days kept). No prompt or answer text is stored.
**Forget** deletes that device's usage file and nothing else.

---

## 11. Model control and two models at once

By default a paired laptop uses whatever model this PC has loaded. You can give a device more.

### Model control

**Settings > Other devices**, on the device's row: **Model control** (default: off). With it on, the laptop can:
- load and unload installed models on this PC,
- choose where a model runs (Automatic, Graphics card, System memory or Processor only),
- choose which of this PC's models each of its own apps uses.

It can never download, remove, check or import models, change settings or touch your files, with or without the grant.
Turn it off and the next call from the device is refused. Removing the device removes the grant, and pairing again does not
restore it.

The PC shows who loaded what: "Loaded by <device>: <model> (graphics card)". **Take back** (on the card and in Settings)
unloads the model a device loaded. This PC's own choice comes back and loads on the next request.

### Two models at once

With Model control, a laptop may keep a second model loaded on this PC, for example one on the graphics card and one in
system memory.
- Your own model stays in the first slot. A device's model goes to the second slot, so the device never pushes out your
  model just to get its own.
- Each slot has its own queue, so two models can answer at the same time. A request that names the second model goes to it;
  anything else goes to your own.
- Before loading, the host counts what the other slot already uses against your limits. A second model that does not fit is
  refused ("not enough memory"). Nothing is silently stopped.
- Your own choices always win. If you pick another model, unload one, or your own model needs the memory, the device's
  model is stopped and the device is told.
- A device's second model is never kept loaded forever; it unloads like any other when idle.
- **Processor only** runs a model with no graphics layers, using about half of the processor, for example a helper model on
  the processor while another uses the graphics card.

[screenshot: Models page with two models loaded by different owners]

### Devices may replace models

**Settings > Other devices > Devices may replace models** (default: off). Off: a device cannot unload a model your own apps
are using. On: it can, and your apps load their model again on their next request. With two devices, the first one to load
keeps its model.

---

## 12. Using your home PC from a laptop (client mode)

Client mode makes Portal AI Host on a laptop use the AI of another PC of yours instead of running a model itself. Apps on the
laptop work as before.

### Set up

1. On the home PC, turn on **Let other devices use this PC's AI** and press **Pair a device** ([section 9](#9-letting-other-devices-use-this-pc)). Press **Copy link** and send the link to the laptop.
2. On the laptop, open **Settings > Home PC** (or choose **Use my home PC** in the welcome window).
3. In **Where the AI runs**, choose **Use my home PC**. A **Use my home PC** window opens. Paste the link with **Paste link**, press **Next**, check the PC name and fingerprint, then **Pair**. Name the laptop.
4. Home now shows the PC's name and a status: **Connected - home network**, **Connected - via relay**, **Connecting...** or **Not reachable**.

[screenshot: the pairing window on the laptop]

### Settings (Settings > Home PC)

| Setting | Choices | Default |
|---|---|---|
| Where the AI runs | This PC / Use my home PC | This PC |
| If the home PC is unreachable | **None** (apps use their own built-in AI) / **Use this laptop's model** (loads on demand) | None |
| Connection away from home | **Automatic** / **Manual** (then a **Use relay** switch applies to this session only and resets when the app closes) | Automatic |

- The first time the home PC cannot be reached, you are asked once what to do; you can change it here at any time.
- **Pair PC** pairs again; **Unpair** forgets the PC (models and downloads stay as they are); **Change PC** (on Home) opens these settings; **Run AI here** (on Home) leaves client mode and keeps the pairing saved.
- With Model control granted by the home PC, **Models on <PC name>** appears with Load, Unload and placement buttons, plus **PC model for each app**. Without it, the page says "Ask the owner of <PC name> to turn on Model control for this laptop".
- A laptop in client mode does not accept connections from other devices and cannot be a hop to your home PC.

### What happens to this laptop's own models

On entering client mode the laptop's loaded model is unloaded to free memory. Model files, downloads and the models folder are
untouched. On leaving, the model loads again when asked.

### Troubleshooting

- **Not reachable:** check the home PC is on and on the same network, or turn on the relay.
- **"This laptop was removed from the PC":** the home PC removed it. Choose **Pair again**.
- **"The identity of <PC> changed":** the PC's certificate no longer matches what the laptop saved. If you did not expect
  this, do not pair again. Otherwise unpair, then pair again.
- **Paired from away fails:** pair once at home first; the pairing link must point at an address on your home network.
- **Paused, quiet hours, a game running or a daily limit used up:** the PC tells the laptop why; the laptop uses its fallback.

**Where it is stored:** the laptop keeps its pairing in the host's data folder under `client\`. The device key and tokens are
protected for your Windows user. **Unpair** removes them. Backups of the folder cannot be restored on another Windows user.

---

## 13. Away from home: the relay and the relay access key

The relay lets a paired device reach your PC from anywhere. **Settings > Other devices > Also reach this PC away from
home.**

- Your PC connects out to a small relay service. The relay only passes along scrambled bytes it cannot read, and it keeps no
  content. Each device must still be paired and signs every request.
- The status line says **Reachable away from home**, **Home network only**, **Connecting to the relay...** or **Can't reach
  the relay**.
- It works over the usual web port, so most office networks allow it.
- **Company devices:** work content will go to your personal PC. Check your employer's rules first.

### Relay access key

**Settings > Other devices > Relay access key** (default: off). When on, only devices paired with the key in the pairing
code can reach this PC through the relay, which stops strangers from using up the relay slots for your PC.

- Turn it **on before pairing**. Devices paired earlier must pair again to use the relay (the home network keeps working).
- **New key** makes a fresh key after a confirmation. Every paired device must pair again to use the relay; at home nothing
  changes. Removing a device does not change the key, so use **New key** after removing a device you do not trust.
- If the key file cannot be used, the relay stays off rather than open, and the home network keeps working. A damaged key file
  is kept aside under a `.bad` name and never overwritten.
- If the switch will not stay on, the relay service may need updating on its side. Turn the switch off again.

**Limits:** the relay limits how fast and how much a device can send, so that one device cannot flood the service. Normal use
is far below these limits.

---

## 14. Privacy and crash reports

- **No analytics or usage tracking.** Only a crash report can leave your PC, and only if you leave it on.
- **Your text stays on your machines.** Prompts and answers are never saved or logged by the host. Logs hold only counts,
  timings and states.
- **Crash reports are on by default and you can turn them off.** If the window or the background host crashes, a short report
  helps us fix it. Turn it off at any time in **Settings > Privacy > Send crash reports**; it stops straight away. The
  welcome window has the same choice, ticked by default, before anything is sent.
  - **Sent:** the error type and where it happened (function and module names), the app and Windows versions, whether it is
    a Store build, and whether it came from the window or the host.
  - **Never sent:** prompts or answers, anything you type, file names or folder paths, your computer or user name, which apps
    use the host, or your IP address. Error text is dropped.
  - **Kept for 90 days**, then deleted.
  - **What's sent** (Settings > Privacy) opens the privacy page.
- **Everything is encrypted** when it leaves this PC for another of your devices.
- **Privacy policy:** About > **Privacy policy**.
- Questions, or a request to delete a report: mindthaapp@gmail.com.

[screenshot: Settings > Privacy with the crash report switch]

---

## 15. Where your data is stored, backup and removal

Everything lives under the app's own data folder. **About > Open data folder** opens it.

| What | Where (inside the data folder) | Back up? |
|---|---|---|
| Downloaded models | `models\`, or `Portal AI Host Models\` inside the folder you chose in Settings | Optional. They can be downloaded again. They are large. |
| Settings | `settings.json` | Yes |
| Allowed apps and their tokens | The host's app store | Not portable (protected for your Windows user) |
| Paired devices, limits, usage stats | The host's device store and one small stats file per device | Not portable |
| Laptop pairing (client mode) | `client\` | Not portable. Pair again on a new PC. |
| Logs | `logs\` (rolling, capped at 10 MB) | No |
| Discovery file for apps | `discovery\host.json` | No |

**Back up:** close Portal AI Host, then copy the data folder. Settings and models can be restored by copying them back. App
permissions, device pairings and tokens are protected for your Windows user and your PC, so on a new PC you allow apps and
pair devices again.

**Remove:**
- *One model:* **Models > Remove**. Only files the host downloaded are deleted.
- *Leftovers:* **Models > Free up space**.
- *A device:* **Forget** (Devices on this PC) or **Remove** (Settings).
- *An app's access:* **Remove access** (Devices and apps).
- *Everything:* uninstall the app. A custom models folder you chose is left in place; delete its `Portal AI Host Models`
  folder yourself if you want it gone.

The app never deletes anything it did not create, never overwrites your files, and a model you added yourself is only removed as
the host's own copy (your original file is untouched).

---

## 16. Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+1 | Home |
| Ctrl+2 | Models |
| Ctrl+3 | Devices and apps |
| Ctrl+4 | Try it |
| Ctrl+, | Settings |
| F1 | About |
| Ctrl+Enter | Ask, on Try it |
| Esc | Close What's new or a dialog |

The list is also under the app menu > **Keyboard shortcuts**.

Appearance: **Settings > Appearance** offers Theme (default: your Windows setting), Window material (Mica), Corners
(Rounded), Density (Standard), Text size (Normal) and Motion (Follow Windows). Windows high contrast always uses solid
system colours.

---

## 17. Troubleshooting and FAQ

**The host says it is not running.** Press **Start Portal AI Host** on Home. If it cannot start, restart the PC; if that
fails, reinstall from the Store.

**"Another Windows user on this PC is already running Portal AI Host."** Ask that user to quit it, or sign in as them.

**Answers are slow.** See section 7. A model that is too big for your graphics memory runs partly on the processor.

**An app says no AI is available.** Check that the app is allowed (Devices and apps), not paused, and that a model is
installed and not too big for the PC.

**The first answer is slow, later ones are fast.** The model was resting. It loads on the first request. Choose **Keep the
model loaded** in Settings > General to avoid this.

**Is my data sent anywhere?** No. Prompts and answers stay on your devices. Only crash reports can leave (section 14), and
you can turn them off.

**Can I use it without internet?** Yes, once a model is downloaded.

**Does it cost anything?** No.

**Which model should I choose?** The recommended one. Pick a smaller one if your PC feels slow, a larger one if you want
better answers and have the memory.

**Did the download get stuck at "Checking the download"?** Wait. Large files take a minute to check.

**Pairing says "too many devices".** The limit is 16 devices. Remove one.

**Where is the tray icon?** Click the arrow near the clock to show hidden icons.

If you are still stuck, use **About > Copy version**, then write to mindthaapp@gmail.com and paste the version into your
message.

---

## 18. Coming later

These are planned and not in the app today.

- **Model categories on the Models page:** grouped by task (chat and writing, coding, reasoning, vision, embeddings, speech)
  with filters and search.
- **Image and video generation** on this PC, including a "director" that chains shots into a video. Video models will appear
  as "Coming soon" until the engine exists.
- **Choosing which model answers**, and models that ask each other for help, in AI Portal.
- **macOS version.**

---

## 19. Support

- **Feedback and ideas:** About > **Send feedback or suggest an idea**.
- **Support and privacy questions:** mindthaapp@gmail.com.
- **Version:** About shows it, and **Copy version** puts it on the clipboard for your message.
- **Licences:** Portal AI Host includes llama.cpp (MIT licence), which runs the models on your PC. Models are downloaded
  under their own licences (Apache 2.0 or MIT). See About.
