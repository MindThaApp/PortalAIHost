# Store screenshots plan

Capture after the new look is final. Rules (Store lessons):
- 1920x1080 (preferred) or 1366x768 PNG, app window only (the fixed-size Welcome window is centred on a plain
  neutral 1366x768 canvas with a thin outline, so the window itself is untouched): no desktop icons, taskbar, other apps or personal folder
  names (the models folder must show a neutral path, for example `D:\Models`).
- Light theme first, then a dark copy of the first two if there is time.
- Real captures of the real app with a real model loaded (no `--fake` scenario, no mock-ups), in a clean test
  profile on a test PC or VM.
- Caption each one in Partner Center (up to 200 characters).

## 1.0.1 set (new Welcome window)

Real captures of this build, real hardware detection (no `--fake`), a fresh test data root, no personal paths.
Files are in `docs/store/screenshots/1.0.1/`; each has a `-dark` copy. Captions are up to 200 characters.

| # | File | What it shows | Caption |
|---|---|---|---|
| 1 | `01-welcome.png` | Welcome step 1: "Runs on this PC", "Stays private", "Made for your Portal apps" | Welcome to Portal AI Host: AI that runs on your own PC. It picks a model to suit it, and there is nothing to set up. |
| 2 | `02-pick-a-model.png` | Welcome step 2, "Pick a model": the recommended model for this PC with its reason, size and your PC's memory, graphics and free disk space | It checks your PC and recommends the model that suits it best, with the size and how fast it should answer. Download it now or choose later. |
| 3 | `03-where-it-lives.png` | Welcome step 3: where it lives in the tray, the "you will be asked" note and the Start with Windows switch | It waits quietly in the tray and always asks before a Portal app can use it. Starting with Windows is your choice. |

Still to capture for 1.0.1 (needs a loaded model or a paired app, so not possible without a model download):
Home with a model ready, Try it (the answer now shows bold text properly), the approval prompt ("Don't allow"
instead of "Deny") and Models with a download in progress. Until then the 1.0 images for those pages stay in
Partner Center; do not reuse them in new rows, as they show host version 1.0.0 and raw `**` markdown.

## Plan for a full set

Original plan, with the changes for 1.0.1 (welcome is 3 steps, not "2 of 3"/"2 of 4"; the prompt button is
"Don't allow"):

| # | Page / state | What it must show | Caption |
|---|---|---|---|
| 1 | Home, model loaded | Status "ready", the loaded model's name, one or two allowed apps, queue empty | Your own AI model, ready for your apps |
| 2 | Welcome window, step 2 (Pick a model) | Recommended model with its plain reason and size | Picks the best model for your PC |
| 3 | Models, a download in progress | Recommended model, catalogue list with sizes and quality, progress with time left, pause button | One-click download with pause and resume |
| 4 | Devices and apps, approval prompt open | "Let Voice Portal use your AI host?" with the verified publisher line, Allow / Don't allow / Decide later | Apps ask first; you decide |
| 5 | Try it, after an answer | A short question and the model's reply; "nothing you type here is saved" visible | Check it works; nothing is saved |
| 6 | Settings | Start with Windows, free memory when idle, models folder | Starts with Windows only if you want it |

Optional 7th: the tray icon menu (Open / Quit) cropped with the window behind it, only if it can be captured without
other tray icons.
