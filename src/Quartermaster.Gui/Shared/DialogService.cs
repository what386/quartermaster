using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Quartermaster.Gui.Mods;
using Quartermaster.Providers.Clients;

namespace Quartermaster.Gui.Shared;

public sealed record ProfileCreationRequest(string? Name = null, bool FromFile = false);

public interface IDialogService
{
    Task<ModImportRequest?> RequestModImportAsync();
    Task<LocalModImportOptions?> ConfirmModImportAsync(string source);
    Task<ProviderFile?> ChooseModFileAsync(ProviderMod mod);
    Task<string?> PickModZipAsync();
    Task<string?> SaveModZipAsync(string suggestedName);
    Task<string?> PickProfileZipAsync();
    Task<string?> SaveProfileZipAsync(string suggestedName);
    Task<ProfileCreationRequest?> RequestProfileCreationAsync();
    Task<string?> PickFolderAsync(string title);
    Task<bool> ConfirmAsync(string title, string message, string acceptLabel, string cancelLabel = "Cancel");
    Task<string?> RequestTextAsync(string title, string prompt, string acceptLabel, string? initialValue = null);
}

public sealed class DialogService(Func<MainWindow> owner) : IDialogService
{
    public Task<ModImportRequest?> RequestModImportAsync() => owner().ShowDialogAsync<ModImportRequest?>(new AddModDialog());
    public Task<LocalModImportOptions?> ConfirmModImportAsync(string source) => owner().ShowDialogAsync<LocalModImportOptions?>(new ModImportDialog(source));
    public Task<ProviderFile?> ChooseModFileAsync(ProviderMod mod) => owner().ShowDialogAsync<ProviderFile?>(new ModFilesDialog(mod));
    public async Task<ProfileCreationRequest?> RequestProfileCreationAsync()
    {
        var result = await owner().ShowDialogAsync<object?>(new TextInputDialog("Create profile", "Name your profile or import a profile ZIP", "Create", allowFileChoice: true));
        return result switch
        {
            string name => new(Name: name),
            TextInputAction.ChooseFile => new(FromFile: true),
            _ => null
        };
    }
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
    public Task<bool> ConfirmAsync(string title, string message, string acceptLabel, string cancelLabel = "Cancel") =>
        owner().ShowDialogAsync<bool>(new ConfirmationDialog(title, message, acceptLabel, cancelLabel));
}
