# Microsoft Store submission checklist (Portal AI Host 1.0)

Owner steps, in order. Items marked **blocker** stop certification or make the app useless to testers.

## A. Before building

1. **Blocker: catalogue key.** Release builds trust only the compiled-in catalogue keys, and the list is empty
   today (`CatalogueTrust.PublicKeysSpkiBase64` in `src/Portal.AiHost.Core/Catalogue/CatalogueVerifier.cs`). Without
   a key the packaged app shows "Model list unavailable" and cannot download anything.
   - Create the key pair offline: `./scripts/sign-catalogue.ps1 -NewKeyPair -OutDir <a folder outside the repo>`.
     Keep the private key off the PC's synced folders and back it up.
   - Put the public key (SPKI, base64; current and next) into `CatalogueTrust.PublicKeysSpkiBase64`.
2. **Blocker: signed catalogue.** After the catalogue work is final:
   `./scripts/sign-catalogue.ps1 -PrivateKeyPath <path to the private key>` writes `catalogue/catalogue.json.sig`.
   `build-msix.ps1 -Store` refuses to build without it. Commit the `.sig` with the catalogue.
3. Confirm the app-code items in the compliance findings are done (dev switches gated in Release).
4. **Privacy page:** done. Published at https://mindthaapp.github.io/portal-ai-host/privacy.html (repo
   `MindThaApp/MindThaApp.github.io`, page `portal-ai-host/privacy.md`). Keep it in step with
   `docs/store/privacy-policy.md`.
5. Decide the price (free, or free with a later paid add-on). The listing texts assume free with no purchases.

## B. Partner Center: reserve and read the identity

1. Partner Center > Apps and games > New product > MSIX or PWA app > reserve the name "Portal AI Host"
   (fallbacks in `listing-en.md`).
2. Product > Product management > Product identity. Copy:
   - `Package/Identity/Name` (for example `MindThaApp.PortalAIHost`) -> `-IdentityName`
   - `Package/Identity/Publisher` (`CN=...` GUID string) -> `-Publisher`
   - `Package/Properties/PublisherDisplayName` -> `-PublisherDisplayName`
   These replace the `PLACEHOLDER` values in `src/PortalAIHost.App/Package.appxmanifest`. Either pass them on the
   command line every time (below) or commit them into the manifest; they are not secrets.

## C. Build the package

Versions: `Major.Minor.Build.0`. The revision (4th field) must be 0 for the Store; the script refuses anything else.
Every new upload needs a higher version: bump the 3rd field (1.0.0.0, then 1.0.1.0, 1.0.2.0 ...). Keep the
manifest's `Version` in step.

```powershell
./scripts/fetch-engine.ps1          # pinned llama.cpp builds, SHA-256 checked
./scripts/build-msix.ps1 -Store -Version 1.0.0.0 `
    -IdentityName '<Identity Name>' -Publisher 'CN=<...>' -PublisherDisplayName '<Publisher display name>' |
    Select-Object -Last 15
```

Output: `builds\msix\1.0.0.0\PortalAIHost_1.0.0.0.msixupload` (unsigned; the Store signs it), with x64 and arm64
in one bundle. `-PlaceholderIdentity` makes a check-only `*.PLACEHOLDER.msixupload`: never upload that file.

Check before upload:
- the summary shows both architectures and the Service exe as NativeAOT;
- no `.pdb` files and no local build paths in the package (PathMap; spot-check with a text search for `\Dev\`);
- the Store build ran on a clean checkout of the commit you mean to ship.

## D. Test the package (owner, on a test PC or VM, not the dev PC)

1. Build the dev-signed version the same way without `-Store` (it writes `PortalAIHost-dev-test.cer`); trust the
   certificate on the test machine and install the `.msixbundle` there.
2. Launch from Start: first run, recommended model, download, Try it answers. Close the window: the tray icon stays
   and the API keeps serving. Tray > Quit stops everything.
3. Check the Start menu and taskbar icons (light and dark taskbar), and that the hidden host has no Start entry.
4. Turn on Start with Windows, sign out and in: the host starts (tray icon). Turn it off in Task Manager > Startup
   apps: the app shows the Task Manager hint.
5. No crash dialog on the first launch after install (release blocker).
6. Run the Windows App Certification Kit on the test machine against the installed package (Windows SDK > App
   Certification Kit > Validate Store App). Fix every failure; warnings about the resource map name: see note E.

## E. Notes for known checks

- **Resource map name:** the PRI's main resource map is named `PortalAIHost.App` (the WinUI build default). Windows
  10.0.19041 and later do not require it to match the package name, and the package needs 10.0.22000. If WACK
  still flags it, rebuild the App with `-p:ProjectPriIndexName=<Identity Name>` in `Invoke-Publish` and re-test.
- **runFullTrust:** a restricted capability; the justification text is in `listing-en.md`.
- **Startup task:** declared with `Enabled="false"`; the app only asks Windows to enable it after the user ticks the
  box (`RequestEnableAsync`). Mention it in the certification notes (already in `listing-en.md`).
- **Languages:** the package declares `en-us` only, so Partner Center asks for one listing (English (United
  States)). Adding `en-gb` to the manifest would add a second required listing; leave it for the translation work.

## F. Partner Center submission

1. Pricing and availability: markets (all), price (free), discoverability (public). Free trial: none.
2. Properties: category Productivity; tick "This product accesses, collects or transmits personal information"
   = **No** (nothing leaves the PC; model downloads carry no personal data). Privacy policy URL: the page from A.4.
   Support contact: mindthaapp@gmail.com (the address on the privacy page). System requirements from
   `listing-en.md`.
3. Age ratings: answer the IARC questionnaire as in `listing-en.md`.
4. Packages: upload `PortalAIHost_<version>.msixupload`.
   - The package is about 220 MB (both architectures), well over 64 MB. Browser automation uploads are capped
     (10 MB), and drag and drop does not work: use the **Browse** button by hand in a normal browser window.
   - If "Analyzing 99%" hangs, open the Packages page in a new tab and save the submission from there.
   - Device families: Windows 10/11 Desktop only.
5. Store listings > English (United States): paste the texts from `listing-en.md`; upload the 300x300 logo
   `docs/store/logo/store-logo-300.png` and the screenshots (`screenshots.md`).
6. Submission options: paste the runFullTrust justification and the certification notes from `listing-en.md`.
   Publishing: "Publish this submission as soon as it passes certification" or "Manually"; your choice.
7. Check the mandatory-update setting (it carries over between submissions).
8. Review, then press Submit yourself. Certification usually takes up to 3 business days; with no news after that,
   open a support ticket.

## G. After release

- Keep the privacy page in step with every release that changes data handling.
- For the next upload, bump the 3rd version field and rebuild with the same identity values.
