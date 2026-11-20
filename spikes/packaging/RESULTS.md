# Packaging spike results (2026-11-20)

Environment: Windows 11 26300, Developer Mode on, loose-layout `Add-AppxPackage -Register`, unsigned, x64.
Test apps are plain full-trust WinExe (.NET 10, framework-dependent) rather than WinUI: the questions are about
manifest and process behaviour, not UI. Rebuild with `spikes/packaging/build.ps1`. Both spike packages were removed
afterwards (`Get-AppxPackage *PortalAIHost.Spike*` returns nothing).

| # | Assumption | Verdict |
|---|---|---|
| 1 | StartupTask on the `AppListEntry="none"` headless app, next to a visible app in the same package | PASS |
| 2 | Headless exe launches a packaged child exe + DLL, child binds 127.0.0.1:0, Job Object kills it with the parent | PASS |
| 3 | A second packaged full-trust app reads the first package's `LocalState\discovery\host.json` | PASS (pipe fallback also works, not needed) |

## 1. StartupTask

Registered fine. From the hidden app itself (launched via `explorer.exe shell:AppsFolder\<PFN>!Service`):
`StartupTask.GetAsync("SpikeHostTask")` returned `Disabled`; `RequestEnableAsync()` returned `Enabled`. A second
launch saw `Enabled` (state persists). Not verified: an actual logon launch (cannot log off).

```xml
<Application Id="Service" Executable="Service\SpikeHost.exe" EntryPoint="Windows.FullTrustApplication">
  <uap:VisualElements DisplayName="..." Description="..." BackgroundColor="transparent"
      Square150x150Logo="Assets\logo.png" Square44x44Logo="Assets\logo.png" AppListEntry="none" />
  <Extensions>
    <desktop:Extension Category="windows.startupTask" Executable="Service\SpikeHost.exe"
        EntryPoint="Windows.FullTrustApplication">
      <desktop:StartupTask TaskId="SpikeHostTask" Enabled="false" DisplayName="Spike Host" />
    </desktop:Extension>
  </Extensions>
</Application>
```
Namespaces: `desktop` = `.../manifest/desktop/windows10`, `uap` = `.../manifest/uap/windows10` (both in
`IgnorableNamespaces`; `desktop` is not strictly needed to be ignorable but harmless). Capability
`rescap:runFullTrust`. The extension sits inside the `Application` element, and its `Executable` must equal the
application's executable.

## 2. Child exe and Job Object

- `Process.Start` of `Service\child\Child.exe` (inside the package folder) works, with stdout redirected and
  `CreateNoWindow`. The dummy DLL (a managed assembly referenced by the child) resolved beside it.
- Child bound `TcpListener(127.0.0.1, 0)` (port 64412 in the run); the host connected to it. No capability needed
  for loopback between full-trust processes.
- Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` (0x2000, class 9 = ExtendedLimitInformation): assignment
  succeeded; after hard-killing the parent the child was gone within 2 s.
- The child's `Environment.ProcessPath` is the real layout path (loose) or the WindowsApps path (installed); either
  way it is readable and executable by the package's own processes.
- Not tested: a native llama-server.exe with real CUDA/Vulkan DLLs, and a signed/installed (WindowsApps) copy, where
  the package folder is read-only (fine for executing; anything the engine writes must go to LocalState).

## 3. Cross-package discovery

Client package (separate identity `PortalAIHost.Spike.Client`) read
`C:\Users\<u>\AppData\Local\Packages\PortalAIHost.Spike.Host_dv5s6scmqnwz2\LocalState\discovery\host.json`
with plain `File.ReadAllText`; contents were exactly what the host wrote. No capability or manifest entry was needed
on either side. The host wrote via the path from `ApplicationData.Current.LocalFolder.Path` (the real location).

Fallback also checked: host serves a `NamedPipeServerStream` (`PipeOptions.CurrentUserOnly`, name suffixed with the
user SID); the packaged client connected and read the same JSON. Keep it as the documented fallback only if a
future client runtime (sandboxed/AppContainer) is denied the file.

Caveat: the client was full trust. An AppContainer / MSIX-sandboxed client would NOT be able to read another
package's LocalState; the pipe would also need an ACL for it. Voice Portal and the other Portal apps are full trust,
so this holds for the plan.

## Gotchas

- Loose registration needs the logo files referenced in the manifest to exist (any PNG).
- Activation of a hidden (`AppListEntry="none"`) app works by AUMID: `explorer.exe shell:AppsFolder\<PFN>!<AppId>`.
  Launching through explorer keeps it non-elevated even from an elevated dev shell.
- WinExe test apps show no window; a console-subsystem exe activated by AUMID would open a console host.
- The host wrote to the real LocalState via `ApplicationData.Current.LocalFolder.Path`; the client found it at that path.
- The packaged process's `ExecutablePath` and command line point into the layout folder, so kill-by-path works.
- `Remove-AppxPackage` removed both spike packages cleanly (startup task residue not inspected).
