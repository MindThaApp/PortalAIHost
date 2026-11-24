using Windows.Storage.Pickers;

namespace PortalAIHost.App.Services;

/// <summary>The Windows file picker for "Add your own model", opened on the main window. Picking reads nothing.</summary>
public sealed class WinUiModelFilePicker : IModelFilePicker
{
    /// <summary>Set by the main window once it exists; a picker needs the window handle in a desktop app.</summary>
    public IntPtr WindowHandle { get; set; }

    public async Task<string?> PickModelFileAsync()
    {
        if (WindowHandle == IntPtr.Zero) return null;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads, ViewMode = PickerViewMode.List };
        picker.FileTypeFilter.Add(".gguf");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
