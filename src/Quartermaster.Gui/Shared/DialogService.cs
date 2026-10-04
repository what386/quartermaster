using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Quartermaster.Gui.Shared;

public interface IDialogService
{
    Task<string?> PickModZipAsync();
    Task<string?> SaveModZipAsync(string suggestedName);
    Task<string?> PickProfileZipAsync();
    Task<string?> SaveProfileZipAsync(string suggestedName);
    Task<string?> PickFolderAsync(string title);
    Task<bool> ConfirmAsync(string title, string message, string acceptLabel);
    Task<string?> RequestTextAsync(string title, string prompt, string acceptLabel, string? initialValue = null);
}

public sealed class DialogService(Func<MainWindow> owner) : IDialogService
{
    public Task<string?> RequestTextAsync(string title, string prompt, string acceptLabel, string? initialValue = null) =>
        owner().ShowDialogAsync<string?>(new TextInputDialog(title, prompt, acceptLabel, initialValue));

    public Task<string?> SaveModZipAsync(string suggestedName) => SaveZipAsync("Export repatched mod", suggestedName);
    public Task<string?> SaveProfileZipAsync(string suggestedName) => SaveZipAsync("Export profile", suggestedName);
    public Task<string?> PickModZipAsync() => PickZipAsync("Import mod ZIP");
    public Task<string?> PickProfileZipAsync() => PickZipAsync("Import profile ZIP");
    private async Task<string?> SaveZipAsync(string title, string suggestedName)
    {
        var file = await owner().StorageProvider.SaveFilePickerAsync(new()
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("ZIP archive") { Patterns = ["*.zip"] }]
        });
        return file?.TryGetLocalPath();
    }
    private async Task<string?> PickZipAsync(string title)
    {
        var files = await owner().StorageProvider.OpenFilePickerAsync(new()
        {
            Title = title,
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
