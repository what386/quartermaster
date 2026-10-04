using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Quartermaster.Gui.Shared;

public interface IDialogService
{
    Task<string?> PickModZipAsync();
    Task<string?> SaveModZipAsync(string suggestedName);
    Task<string?> PickFolderAsync(string title);
    Task<bool> ConfirmAsync(string title, string message, string acceptLabel);
    Task<string?> RequestTextAsync(string title, string prompt, string acceptLabel, string? initialValue = null);
}

public sealed class DialogService(Func<MainWindow> owner) : IDialogService
{
    public Task<string?> RequestTextAsync(string title, string prompt, string acceptLabel, string? initialValue = null) =>
        owner().ShowDialogAsync<string?>(new TextInputDialog(title, prompt, acceptLabel, initialValue));

    public async Task<string?> SaveModZipAsync(string suggestedName)
    {
        var file = await owner().StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "Export repatched mod",
            SuggestedFileName = suggestedName,
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("ZIP archive") { Patterns = ["*.zip"] }]
        });
        return file?.TryGetLocalPath();
    }
    public async Task<string?> PickModZipAsync()
    {
        var files = await owner().StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Import mod ZIP",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("ZIP archives") { Patterns = ["*.zip"] }]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await owner().StorageProvider.OpenFolderPickerAsync(new() { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
    public Task<bool> ConfirmAsync(string title, string message, string acceptLabel) =>
        owner().ShowDialogAsync<bool>(new ConfirmationDialog(title, message, acceptLabel));
}
