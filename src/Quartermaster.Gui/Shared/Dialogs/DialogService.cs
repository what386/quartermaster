using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Quartermaster.Gui.Mods;
using Quartermaster.Providers.Clients;

namespace Quartermaster.Gui.Shared;

public sealed record ProfileCreationRequest(string? Name = null, bool FromFile = false);
public enum AppUpdateChoice { Cancel, Update, Skip }

public interface IDialogService
{
    Task<Onboarding.SetupOutcome> ShowOnboardingAsync(Onboarding.SetupViewModel model, CancellationToken ct = default);
    Task<AppUpdateChoice> PromptAppUpdateAsync(string version, string notes, CancellationToken ct = default);
    Task<ModImportRequest?> RequestModImportAsync();
    Task<LocalModImportOptions?> ConfirmModImportAsync(string source);
    Task<ProviderFile?> ChooseModFileAsync(ProviderMod mod);
    Task<bool> ReviewDependenciesAsync(DependencyReviewViewModel model, CancellationToken ct = default);
    Task<string?> PickModZipAsync();
    Task<string?> SaveModZipAsync(string suggestedName);
    Task<string?> PickProfileZipAsync();
    Task<string?> SaveProfileZipAsync(string suggestedName);
    Task<ProfileCreationRequest?> RequestProfileCreationAsync();
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickProfileImageAsync();
    Task<GroupColors?> RequestGroupColorsAsync(string? background, string? text);
    Task<bool> ConfirmAsync(string title, string message, string acceptLabel, string cancelLabel = "Cancel");
    Task<string?> RequestTextAsync(string title, string prompt, string acceptLabel, string? initialValue = null);
}

public sealed class DialogService(Func<MainWindow> owner) : IDialogService
{
    public Task<GroupColors?> RequestGroupColorsAsync(string? background, string? text) =>
        owner().ShowDialogAsync<GroupColors?>(new GroupColorsDialog(background, text));
    public async Task<string?> PickProfileImageAsync()
    {
        var files = await owner().StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Localizer.Text("Choose profile thumbnail"), AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Localizer.Text("Images")) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] }]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    public async Task<Onboarding.SetupOutcome> ShowOnboardingAsync(Onboarding.SetupViewModel model, CancellationToken ct = default)
    {
        await owner().WaitForDialogAsync(ct);
        ct.ThrowIfCancellationRequested();
        return await owner().ShowDialogAsync<Onboarding.SetupOutcome>(new Onboarding.SetupDialog(model));
    }
    public async Task<AppUpdateChoice> PromptAppUpdateAsync(string version, string notes, CancellationToken ct = default)
    {
        await owner().WaitForDialogAsync(ct);
        ct.ThrowIfCancellationRequested();
        return await owner().ShowDialogAsync<AppUpdateChoice>(new AppUpdateDialog(version, notes));
    }
    public Task<ModImportRequest?> RequestModImportAsync() => owner().ShowDialogAsync<ModImportRequest?>(new AddModDialog());
    public Task<LocalModImportOptions?> ConfirmModImportAsync(string source) => owner().ShowDialogAsync<LocalModImportOptions?>(new ModImportDialog(source));
    public Task<ProviderFile?> ChooseModFileAsync(ProviderMod mod) => owner().ShowDialogAsync<ProviderFile?>(new ModFilesDialog(mod));
    public async Task<bool> ReviewDependenciesAsync(DependencyReviewViewModel model, CancellationToken ct = default)
    {
        var dialog = new DependencyReviewDialog(model);
        using var registration = ct.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(dialog.Cancel));
        ct.ThrowIfCancellationRequested();
        var accepted = await owner().ShowDialogAsync<bool>(dialog);
        await model.WhenUpdated;
        return accepted;
    }
    public async Task<ProfileCreationRequest?> RequestProfileCreationAsync()
    {
        var result = await owner().ShowDialogAsync<object?>(new TextInputDialog(Localizer.Text("Create profile"), Localizer.Text("Name your profile or import a profile ZIP"), Localizer.Text("Create"), allowFileChoice: true));
        return result switch
        {
            string name => new(Name: name),
            TextInputAction.ChooseFile => new(FromFile: true),
            _ => null
        };
    }
    public Task<string?> RequestTextAsync(string title, string prompt, string acceptLabel, string? initialValue = null) =>
        owner().ShowDialogAsync<string?>(new TextInputDialog(title, prompt, acceptLabel, initialValue));

    public Task<string?> SaveModZipAsync(string suggestedName) => SaveZipAsync(Localizer.Text("Export repatched mod"), suggestedName);
    public Task<string?> SaveProfileZipAsync(string suggestedName) => SaveZipAsync(Localizer.Text("Export profile"), suggestedName);
    public Task<string?> PickModZipAsync() => PickZipAsync(Localizer.Text("Import mod ZIP"));
    public Task<string?> PickProfileZipAsync() => PickZipAsync(Localizer.Text("Import profile ZIP"));
    private async Task<string?> SaveZipAsync(string title, string suggestedName)
    {
        var file = await owner().StorageProvider.SaveFilePickerAsync(new()
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(Localizer.Text("ZIP archive")) { Patterns = ["*.zip"] }]
        });
        return file?.TryGetLocalPath();
    }
    private async Task<string?> PickZipAsync(string title)
    {
        var files = await owner().StorageProvider.OpenFilePickerAsync(new()
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Localizer.Text("ZIP archives")) { Patterns = ["*.zip"] }]
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
