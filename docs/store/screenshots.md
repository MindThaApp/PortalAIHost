# Store screenshots plan

Capture after the new look is final. Rules (Store lessons):
- 1920x1080 (preferred) or 1366x768 PNG, app window only (the fixed-size Welcome window is centred on a plain
  neutral 1366x768 canvas with a thin outline, so the window itself is untouched): no desktop icons, taskbar, other apps or personal folder
  names (the models folder must show a neutral path, for example `D:\Models`).
- Light theme first, then a dark copy of the first two if there is time.
- Real captures of the real app with a real model loaded (no `--fake` scenario, no mock-ups), in a clean test
  profile on a test PC or VM.
- Caption each one in Partner Center (up to 200 characters).

## 1.0.1 set

Real captures of this build, real hardware detection and a real model answering (no `--fake`), fresh test data
roots, no personal paths. Files are in `docs/store/screenshots/1.0.1/`; each has a `-dark` copy. Captions are up
to 200 characters. Images 1 to 3 are the Welcome window centred on a neutral 1366x768 canvas; images 4 to 7 are the
main window at exactly 1366x768 (host version 1.0.1, a small test model loaded).

| # | File | What it shows | Caption |
|---|---|---|---|
| 1 | `01-welcome.png` | Welcome step 1: "Runs on this PC", "Stays private", "Made for your Portal apps" | Welcome to Portal AI Host: AI that runs on your own PC. It picks a model to suit it and you're ready in a few clicks. |
| 2 | `02-pick-a-model.png` | Welcome step 2, "Pick a model": the recommended model for this PC with its reason, size and your PC's memory, graphics and free disk space | It checks your PC and recommends the model that suits it best, with the size and how fast it should answer. Download it now or choose later. |
| 3 | `03-where-it-lives.png` | Welcome step 3: where it lives in the tray, the "you will be asked" note and the Start with Windows switch | It waits quietly in the tray and always asks before a Portal app can use it. Starting with Windows is your choice. |
| 4 | `04-home.png` | Home: status "Ready", host version 1.0.1, the active model loaded, no apps connected yet, no requests waiting | Home shows at a glance that your AI model is loaded and ready, which model is in use, and whether any apps are waiting for your say-so. |
| 5 | `05-try-it-answer.png` | Try it after an answer: the question, and the reply with its key word shown in bold; "Nothing you type here is saved" | Check that it works: ask something short and read the reply here. Nothing you type in Try it is saved. |
| 6 | `06-models.png` | Models: the list for this PC, the best choice marked, size, speed and quality for each, a Download button, and the "A better model is available" note | Models lists what suits your PC, with size, speed and quality. The best choice is marked, and you are told when a better model is available. |
| 7 | `07-approval-prompt.png` | The access prompt over Home: who is asking, the reason given, the verified publisher line, and Allow / Don't allow / Decide later | Apps have to ask first. You see who is asking and why, and you choose Allow, Don't allow or Decide later. |

Notes for Partner Center:
- The prompt in image 7 comes from a real request made by PowerShell, so it shows a real verified publisher.
- Settings is not recaptured: its page, text and layout are unchanged since 1.0, so the 1.0 Settings image is
  still accurate for 1.0.1. A new capture from an unpackaged test build would show a test folder path and the
  "only available in the installed app" note, which the Store build does not have.
- The 1.0 images of Home, Try it and the prompt show host version 1.0.0 and "Deny"; use the 1.0.1 images above
  instead.

## Plan for a full set

The 1.0.1 set above covers the plan except the items below. Welcome is 3 steps and the prompt button is
"Don't allow".

| Page / state | What it must show | Status |
|---|---|---|
| Models, a download in progress | Recommended model, list with sizes and quality, progress with time left, pause button | Not captured: needs a real download of a large model |
| Devices and apps with one or two allowed apps | App names, last used, Revoke | Not captured |
| Settings | Start with Windows, free memory when idle, models folder | Reuse the 1.0 image (see notes above) |

Optional extra: the tray icon menu (Open / Quit) cropped with the window behind it, only if it can be captured without
other tray icons.
