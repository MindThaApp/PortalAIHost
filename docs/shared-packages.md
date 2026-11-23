# Shared packages

Four NuGet packages let other Portal apps reuse the model store, planner, catalogue and engine supervisor without
the host. All are version `1.0.2-preview.2`, MIT licensed. `scripts\pack-shared.ps1` writes them to
`builds\packages`.

| Package | Target | Holds |
|---|---|---|
| `Portal.AiHost.Contracts` | net10.0 | Wire types and constants. A normal dependency of the others (not bundled), so an app using several packages gets one copy. |
| `Portal.AiHost.Models` | net10.0 | Abstractions, signed catalogue and verifier, placement planner, recommender, downloader, model store. Namespaces are still `Portal.AiHost.Core.*`. |
| `Portal.AiHost.Models.Windows` | net10.0-windows | Hardware detection and power state. Namespaces `Portal.AiHost.Platform.Windows.*`. |
| `Portal.AiHost.Engine` | net10.0 | `EngineSupervisor` for a local llama.cpp server, GGUF header reader, runtime probe. Depends on Models. |

## Using a local-folder feed

Add the folder as a source (for example in `nuget.config`):

```xml
<packageSources>
  <add key="portal-shared" value="C:\path\to\PortalAIHost\builds\packages" />
</packageSources>
```

Then `dotnet add package Portal.AiHost.Models --version 1.0.2-preview.2 --prerelease`.

## The store-marker rule

A model store only touches a folder it created and can identify. The folder holds a marker file whose name and
`kind` string must both match the store's `StoreOptions`, and whose GUID is checked before every delete, move or
overwrite. An unmarked folder is never adopted: opening one throws `StoreRefusedException`.

Use `StoreOptions.ForApp("your-app-key")` for your app's own store. It derives the folder name
(`your-app-key Models`), the marker file (`.your-app-key-store`) and the marker kind (`your-app-key-store`) together,
and passes them as the last argument to `OpenOrCreate`, `Open`, `CreateInParent`, `OpenOrCreateInParent` and
`StoreFolderIn`. The app key is lower-case letters, digits and single hyphens. Never build `StoreOptions` by hand:
options that differ from the host's defaults but reuse its marker file name or kind are refused, so another app's
store can never open the host's folder (or the other way round). Names are checked against a strict whitelist, and
`store.json`, `*.part`, `*.json` and `*.tmp` names are refused.

A store is also locked while it is open: a second `Open` of the same folder, from another process or another store
object, throws `StoreRefusedException`. Keep one `ModelStore` per process and call `Dispose()` on it when you are
done (for example before reopening it).

## Adopting a file you already have

`ModelStore.AdoptFromPathAsync(modelId, fileName, fullSourcePath, sha256, size, ct)` copies the file into the store
as a `.part`, checks size and SHA-256, flushes, then commits it. The source must be a full path; it is opened
read-only and is never moved, locked for write or deleted. It returns an `AdoptResult` whose `Status` is one of
`Adopted`, `AlreadyStored`, `SourceMissing`, `SourceIsLink`, `SizeMismatch`, `HashMismatch`, `Busy` (the model is
being downloaded or adopted), `PartialDownloadExists` (resume that download instead), `DifferentFileStored` and
`NotEnoughSpace`. It only ever discards the `.part` it created itself, and never touches an unfinished download. A
path inside the store, including a hard link to a stored file, is refused with `StoreRefusedException`.

## Planning and recommending

- `Recommender.Recommend` returns candidates with a stable `ReasonCode` (`Recommendable`, `NotEnoughMemory`,
  `TooSlow`, `NotEnoughDisk`) plus `NeededBytes`, `AvailableBytes` and `DriveFreeBytes`. `Reason` is display text
  only; map the code to your own wording.
- Catalogue entries may carry an optional `advice` object (`summary`, `good_for`, `caveats`, `hardware`). It is part
  of the signed catalogue, so adding text means signing the catalogue again.
- **CommittedMemory.** If your app already holds memory the host cannot see (for example a loaded speech model),
  pass it in `RecommendContext.Committed` (and to `PlacementPlanner.Plan`). The planner subtracts it from the RAM
  and per-GPU budgets before fitting a model, so recommendations leave room for it.

## EngineSupervisor

- Talks to the engine on loopback only, with a random port and a random API key per run.
- The child process sits in a job object with kill-on-close, so it dies with your app.
- Falls back to CPU when the graphics backend fails; x64 and arm64 builds are supported.
- Health checks, restart with backoff, crash-window limits and idle unload (`IdleUnloadAfter`, `IdleUnloadPolicy`).
- `SetPriority(ProcessPriorityClass)` changes the engine's OS priority at runtime and re-applies it after restarts;
  `RequestedPriority` reports the last request.
- Engine download sizes (pinned in `engine\engine.lock.json`): Vulkan 38.9 MB, CPU 25.0 MB, CPU arm64 13.1 MB. The
  engine zips are not in this checkout, so installed sizes are not measured here.

The store lock is strong on Windows (an exclusive open of `.store-lock`); on other systems it is only advisory.
