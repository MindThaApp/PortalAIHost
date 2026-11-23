# Privacy page text: crash reports (draft for the live page)

Draft for the Portal AI Host privacy page. The live page is kept in a separate repository; copy this section there.
The in-app "What's sent" link opens that page.

## Crash reports

Portal AI Host sends a short crash report when the app window or the background host stops unexpectedly. It helps us
find and fix problems quickly.

**It is on by default.** You can turn it off at any time, and we will not ask again.

### What is sent

- The error type and where in the app it happened (function and module names). Never the error text.
- The app version, the Windows version, and whether it is a store or development build.
- Whether the report came from the window or the host.

### What is never sent

- Your prompts or the model's answers, or anything you type.
- File names or folder paths. Error text is dropped entirely, so no
  path, Windows user name or models folder can leak.
- Your computer name, your user name or account name.
- Request contents, or the names of the apps that use the host.
- Your IP address. We do not store it.

### How to turn it off

- Settings > Privacy > "Send crash reports": switch it off. It stops at once.
- On first run, the welcome screen has one ticked option. Untick it to opt out before anything is sent.

### How long we keep it

Crash reports are deleted after 90 days.

### Contact

Questions, or a request to delete a report: mindthaapp@gmail.com.
