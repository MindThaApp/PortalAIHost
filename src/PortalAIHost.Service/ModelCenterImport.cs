using System.Diagnostics;
using Portal.AiHost.Contracts;
using Portal.AiHost.Contracts.Control;
using Portal.AiHost.Core.Store;
using Portal.AiHost.Platform.Windows.Pipes;

namespace PortalAIHost.Service;

/// <summary>
/// "Add your own model": the single entry point for bringing a GGUF file the user has into the host. Pro gating,
/// when it comes, belongs here (at the start of <see cref="StartImportAsync"/>). The original file is only read.
/// </summary>
internal sealed partial class ModelCenter
{
    /// <summary>
    /// Checks the file up front (a path, a readable GGUF header) and starts copying it into the models folder.
    /// Problems known now are pipe errors; progress, the result and failures arrive as event.store_progress.
    /// </summary>
    public async Task<Task> StartImportAsync(string path, CancellationToken ct)
    {
        if (_store is not IModelStoreImporter importer)
        {
            throw new ControlPipeException(ErrorCodes.FeatureDisabled, "The models folder is not available.");
        }

        if (_disposed)
        {
            throw new ControlPipeException(ErrorCodes.HostStopping, "The host is shutting down.");
        }

        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "Pick a model file from a drive on this PC.");
        }

        if (!string.Equals(Path.GetExtension(path), ".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "This is not a GGUF model file. Pick a file that ends in .gguf.");
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new ControlPipeException(ErrorCodes.NotFound, "That file was not found.");
        }

        try
        {
            GgufReader.Read(path);
        }
        catch (GgufFormatException ex)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ControlPipeException(ErrorCodes.InvalidRequest, "The file could not be read: " + ex.Message);
        }

        var total = info.Length;
        var cts = BeginOperation(StoreOperations.Import);
        try
        {
            await PushStoreAsync(new StoreProgressEvent { Operation = StoreOperations.Import, State = StoreStates.Running, BytesTotal = total }).ConfigureAwait(false);
        }
        catch
        {
            EndOperation(cts);
            throw;
        }

        return Task.Run(() => ImportCoreAsync(importer, path, total, cts), CancellationToken.None);
    }

    private async Task ImportCoreAsync(IModelStoreImporter importer, string path, long total, CancellationTokenSource cts)
    {
        try
        {
            var result = await importer.ImportGgufAsync(path, copied =>
            {
                _ = PushStoreAsync(new StoreProgressEvent
                {
                    Operation = StoreOperations.Import, State = StoreStates.Running,
                    Fraction = Math.Min(1.0, (double)copied / Math.Max(1, total)), BytesDone = copied, BytesTotal = total,
                });
            }, cts.Token).ConfigureAwait(false);

            switch (result.Status)
            {
                case ImportStatus.Imported:
                case ImportStatus.AlreadyStored:
                    _lastStored = null;
                    await PushStoreAsync(new StoreProgressEvent
                    {
                        Operation = StoreOperations.Import, State = StoreStates.Done, CatalogueId = result.Model?.Id, Fraction = 1,
                        BytesDone = total, BytesTotal = total,
                        Message = result.Status == ImportStatus.AlreadyStored
                            ? "That model was already added."
                            : "Added " + (result.Model?.DisplayName ?? "your model") + ".",
                    }).ConfigureAwait(false);
                    break;
                default:
                    await PushStoreAsync(new StoreProgressEvent
                    {
                        Operation = StoreOperations.Import, State = StoreStates.Failed,
                        Message = result.Status switch
                        {
                            ImportStatus.NotEnoughSpace => $"Not enough space in the models folder: this file needs about {result.NeededBytes / 1e9:0.0} GB free.",
                            ImportStatus.SourceMissing => "That file was not found.",
                            _ => "Links cannot be added. Pick the model file itself.",
                        },
                    }).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            await PushStoreAsync(new StoreProgressEvent
            {
                Operation = StoreOperations.Import, State = StoreStates.Cancelled, Message = "Adding the model was cancelled.",
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await PushStoreAsync(new StoreProgressEvent
            {
                Operation = StoreOperations.Import, State = StoreStates.Failed, Message = ex.Message,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Anything unexpected (a busy model, a missing record): still end the operation so the page is not left busy.
            Trace.WriteLine("Portal AI Host: import failed: " + ex.GetType().Name);
            await PushStoreAsync(new StoreProgressEvent
            {
                Operation = StoreOperations.Import, State = StoreStates.Failed,
                Message = "Something went wrong while adding the model. Nothing was kept, and your original file was not changed.",
            }).ConfigureAwait(false);
        }
        finally
        {
            EndOperation(cts);
        }
    }
}
