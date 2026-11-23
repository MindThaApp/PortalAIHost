# Store listing (English, en-us)

Paste these into Partner Center > the submission > Store listings > English. Keep the texts in step with the app:
everything below is checked against the 1.0 code. Do not add LAN sharing, pairing, remote access, speech or
other languages until they ship.

## App name

Portal AI Host

(Reserve the name first. Fallbacks if it is taken: "Portal AI Host for Windows", "Portal Local AI Host".)

## Short description (Store "short description", up to 1,000 characters; shown on Xbox and in some lists)

Run a local AI model on your own PC and share it with your Portal apps. One click picks the best model for your
hardware, downloads it and keeps it ready. Your text stays on this PC: no account, no cloud, no subscription.

## Description

Portal AI Host gives your PC its own AI model, and lets your other apps use it.

Instead of every app downloading its own model, or sending your text to a cloud service, Portal AI Host keeps one
model on your PC and shares it with the apps you allow. Voice Portal and the other Portal apps can then write
summaries, minutes and answers with a bigger, better model than they could carry on their own.

Simple to set up
- The first run checks your PC (memory, graphics card, free disk space) and recommends the best model that runs well
  on it, with a plain explanation.
- One click downloads it. Downloads can be paused and resumed, and every file is checked before it is used.
- Nothing else to install: the engine is built in. No Python, no command line, no extra tools.

Private by design
- The model runs on this PC. Prompts and answers never leave it.
- No account and no sign-in.
- No analytics. Optional crash reports never include your files or what you type, and you can turn them off. Your prompts are never saved or logged.
- The host only listens on this PC (loopback). Other computers on your network cannot reach it.

You stay in control
- An app has to ask before it can use the host, and you decide: allow, deny or decide later. You can remove an app's
  access at any time.
- The host shows whether an app is verified (its publisher) or not, so you know who is asking.
- Choose where models are stored, and remove models you no longer need. The host only ever deletes files it
  downloaded itself.
- Start with Windows is optional and off until you turn it on.

Works with your hardware
- Uses your graphics card through Vulkan (AMD, NVIDIA and Intel), and falls back to the processor when there is no
  suitable GPU.
- Frees memory when the model is idle, or keeps it loaded if you prefer.

For developers
- The host offers a standard OpenAI-compatible API on 127.0.0.1 (chat completions with streaming, embeddings and a
  models list), so existing client libraries work. Every app needs the user's approval first.

Models are downloaded from their publishers' pages on Hugging Face. Each model has its own licence; the app only
offers models whose licences allow commercial use (Apache 2.0 or MIT).

## Features (Partner Center "Product features", max 20, 200 characters each)

1. Runs an AI model locally on your PC, no cloud needed
2. One model shared by all your Portal apps
3. Recommends the best model for your hardware
4. One-click download with pause, resume and file checks
5. Uses your graphics card (Vulkan) with automatic CPU fallback
6. No account, no sign-in, no subscription
7. No analytics; crash reports are optional and scrubbed; prompts are never saved or logged
8. Listens on this PC only (loopback)
9. Apps must ask first; you allow, deny or remove access
10. Shows whether an asking app is verified
11. Frees memory when idle, or keeps the model loaded
12. Choose the folder for your models
13. Optional start with Windows, with a tray icon
14. "Try it" box to check the model works
15. OpenAI-compatible local API for developers
16. Fair sharing when several apps ask at once
17. Recovers by itself if the engine stops

## Search terms (keywords, 7)

1. local AI
2. offline AI
3. private AI
4. LLM
5. AI model
6. llama
7. OpenAI compatible

## What's new in this version (1.0)

First release. Download a local AI model with one click and share it with your Portal apps, privately on your
own PC.

## System requirements (Partner Center "System requirements" + listing text)

Minimum (set in Partner Center > Properties > System requirements):
- Windows 11 (version 21H2, build 22000) or later, x64 or Arm64.
- Memory: 8 GB.
- Free disk space: 5 GB (the app itself is small; models are 2 to 20 GB, larger optional ones up to about 140 GB).

Recommended:
- Memory: 16 GB or more (32 GB or more for the large models).
- Graphics: a Vulkan-capable GPU with 8 GB of video memory or more (AMD, NVIDIA or Intel).
- Free disk space: 25 GB.

Listing text ("Additional system requirements" / description footer):
> Works best with 16 GB of memory and a graphics card with 8 GB of video memory. Without a suitable graphics card the
> model runs on the processor, which is slower. Arm64 PCs run the model on the processor. Models need 2 to 20 GB of
> free disk space.

Partner Center hardware checkboxes: Memory 8 GB (minimum) / 16 GB (recommended); DirectX: not required; Video
memory: leave minimum empty, recommended 8 GB. Do not tick touch, pen, camera, microphone or Bluetooth.

## Category

- Category: Productivity
- Subcategory: none (alternative: Developer tools > if Microsoft ever lists a subcategory; Productivity fits the
  audience better).

## Age rating (IARC questionnaire guidance)

Category in the questionnaire: "Productivity / Utility" (not a game, not social, not a browser).
- Violence, fear, sexual content, gambling, crude humour, drugs, alcohol, tobacco: No.
- Does the app let users interact or exchange content with other users? No (no chat between people, no sharing).
- Does the app share the user's location with other users? No.
- Does the app allow purchases of digital goods? No (unless a paid add-on is added later; then update the rating).
- Unrestricted internet access (a browser or search engine)? No. It only downloads model files from fixed addresses.
- Does the app generate content with AI? Yes, if asked: a local AI model writes text on request. It has no image
  generation. Answer the generative AI question truthfully ("yes, text"); the expected rating is PEGI 3 / ESRB
  Everyone, possibly with "Users Interact: No" and an AI content note, depending on the questionnaire wording.

## Restricted capability justification (runFullTrust)

Paste into Submission options > "Why do you need the runFullTrust capability":

> Portal AI Host is a desktop (Win32 / WinUI 3) app. runFullTrust is needed because:
> 1. The package has two full-trust desktop executables: the window (PortalAIHost.App.exe) and a headless host
>    (PortalAIHost.Service.exe) that keeps serving local apps after the window is closed and shows a tray icon.
> 2. The host starts the bundled llama.cpp engine (llama-server.exe, inside the package) as a child process in a Job
>    Object, so the AI model runs out of process and a graphics-driver fault cannot take the host down.
> 3. The host serves a local HTTP API bound to 127.0.0.1 only, and a per-user named pipe for its own window. It uses
>    the GPU through Vulkan.
> 4. The optional "Start with Windows" uses the standard desktop StartupTask, off by default and turned on only when
>    the user ticks it.
> No restricted capabilities other than runFullTrust are requested. No drivers, services or elevation are
> installed. The app writes only to its own package data folder and to a models folder the user chooses.

## Notes for certification (Submission options > Notes for certification)

> How to test:
> 1. Start "Portal AI Host" from Start. The first run checks the PC and recommends a model.
> 2. A GPU is not required. On a PC or virtual machine without a suitable graphics card the app recommends a small
>    model and runs it on the processor (slower, but it works). For a quick test pick the smallest model in the list
>    (SmolLM3 3B, about 1.9 GB, or Qwen3 4B, about 2.5 GB).
> 3. The first model download is large (2 to 20 GB depending on the model) and takes a few minutes on a fast
>    connection. It comes from huggingface.co. The download can be paused and resumed.
> 4. After the download, open "Try it", type a short question and press the button: the local model answers. Nothing
>    typed there is saved.
> 5. The host runs in the background (tray icon) so other apps on this PC can use the model after the window is
>    closed. To stop it, use the tray icon > Quit.
> 6. "Start with Windows" is offered in step 3 of setup and in Settings. It is off until the user ticks it (Windows
>    StartupTask; it can also be turned off in Task Manager > Startup apps).
> 7. The "Devices and apps" page lists apps that asked to use the host. No other app is needed to test the main
>    flow. The local API listens on 127.0.0.1 only and needs the user's approval per app.
> No account, sign-in or test credentials are needed. The app has no in-app purchases.

## Copyright and trademark

- Copyright: (c) 2026 MindThaApp
- Third-party notice (Additional licence terms or the description footer, optional): "Includes llama.cpp (MIT
  licence). AI models are downloaded from Hugging Face under their own licences (Apache 2.0 or MIT)."
