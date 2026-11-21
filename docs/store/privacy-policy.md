# Portal AI Host privacy policy

Last updated: 22 November 2026. Applies to Portal AI Host 1.0 for Windows.

Portal AI Host runs an AI model on your own PC and lets the apps you approve use it. It is built so that your
text stays on your PC.

## In short

- Your prompts and the model's answers are processed on your PC only. They are never sent to us or to anyone else.
- Prompts and answers are never saved or logged.
- No account, no sign-in, no advertising, no telemetry, no analytics and no crash reporting.
- The app connects to the internet only to download the AI model files you choose, from Hugging Face.

## What the app does with your text

When you or an approved app sends a request, the text goes to the AI engine that runs inside the app on your PC.
The answer comes back the same way. Requests are held in memory only while they are being answered. They are not
written to disk, not kept in a history and not logged. The "Try it" box works the same way: nothing typed there is
saved.

The app keeps only counts and timings in memory (for example how many requests are waiting), never their content.

## Which apps can use the model

The host listens on your PC only (the loopback address 127.0.0.1). Other computers, including those on your home
or office network, cannot reach it.

An app on your PC must ask first. Portal AI Host shows the app's name and whether its publisher is verified, and
you decide whether to allow it. You can remove an app's access at any time on the "Devices and apps" page. An app
running under another Windows user account is refused.

## What is stored on your PC

The app stores, in its own data folder on your PC:
- your settings (for example the models folder, start with Windows, and when to free memory);
- the list of apps you allowed, with their name, publisher or program path, and the date they were added and last
  used;
- the access keys for those apps, protected with Windows data protection (DPAPI) for your user account;
- a list of the model files it downloaded, with their sizes and checksums;
- a small file that tells approved apps on this PC how to reach the host.

Model files are saved in the app's own data folder, or in another models folder you choose. None of this leaves
your PC. Uninstalling the app removes its data folder, including the models in it; models in a folder you chose
yourself stay until you delete them.

## Internet connections

The app connects to the internet only to download AI model files. The list of models is built into the app and
signed, and every download address in it points to huggingface.co (Hugging Face, which hosts the models). A
download request contains the file address and a user agent of the form "PortalAIHost/1.0"; like any web request,
Hugging Face also sees your IP address. Hugging Face's own privacy policy applies to those requests:
https://huggingface.co/privacy

The app does not send prompts, answers, hardware details, usage statistics or any identifier to us or to any other
service. The hardware check on first run (memory, graphics card, free disk space) happens on your PC and is used
only to recommend a model.

## Children

The app does not collect personal information from anyone, including children.

## Changes

If a future version adds a feature that changes any of the above (for example sharing the model with your other
devices), this policy will be updated before that version is released, and the feature will be off until you turn
it on.

## Contact

Questions or concerns: open an issue at https://github.com/MindThaApp/portal-ai-host-privacy/issues
