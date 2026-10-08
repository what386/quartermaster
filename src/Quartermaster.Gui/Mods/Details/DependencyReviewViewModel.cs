using Quartermaster.Gui.Shared;
using Quartermaster.Providers.Clients;

namespace Quartermaster.Gui.Mods;

public sealed record DependencyReviewPlan(IReadOnlyList<DependencyReviewItem> Items, IReadOnlyList<ModRequirement> Manual);

public sealed class DependencyReviewItem : ViewModelBase
{
    private ProviderFile? selectedFile;
    private readonly Func<ProviderFile, Task> changed;
    public string Name { get; }
    public string Action { get; }
    public IReadOnlyList<ProviderFile> Files { get; }
    public bool HasFileChoice => Files.Count > 1;
    public string? FileName => selectedFile?.FileName;
    public bool HasFile => selectedFile is not null;
    public ProviderFile? SelectedFile
    {
        get => selectedFile;
        set
        {
            if (value is null || value.FileId == selectedFile?.FileId) return;
            Set(ref selectedFile, value);
            _ = changed(value);
        }
    }
    public DependencyReviewItem(string name, string action, IReadOnlyList<ProviderFile> files, ProviderFile? file, Func<ProviderFile, Task> changed)
    { Name = name; Action = action; Files = files; selectedFile = file; this.changed = changed; }
}

public sealed class DependencyReviewViewModel : ViewModelBase
{
    private bool updating;
    private string error = "";
    private readonly Func<ProviderFile, Task<DependencyReviewPlan>> rebuild;
    public string Name { get; }
    public string AcceptLabel { get; }
    public string CancelLabel { get; }
    public IReadOnlyList<DependencyReviewItem> Items { get; private set; }
    public IReadOnlyList<ModRequirement> Manual { get; private set; }
    public bool HasManual => Manual.Count > 0;
    public bool IsUpdating { get => updating; private set { Set(ref updating, value); Notify(nameof(CanAccept)); } }
    public bool CanAccept => !IsUpdating && Error.Length == 0;
    public string Error { get => error; private set { Set(ref error, value); Notify(nameof(CanAccept)); } }
    public bool HasError => Error.Length > 0;
    public Task WhenUpdated { get; private set; } = Task.CompletedTask;
    public DependencyReviewViewModel(string name, string acceptLabel, string cancelLabel, DependencyReviewPlan plan, Func<ProviderFile, Task<DependencyReviewPlan>> rebuild)
    { Name = name; AcceptLabel = acceptLabel; CancelLabel = cancelLabel; Items = plan.Items; Manual = plan.Manual; this.rebuild = rebuild; }
    public Task ChangeFileAsync(ProviderFile file)
    {
        if (IsUpdating) return WhenUpdated;
        return WhenUpdated = ChangeAsync(file);
    }
    private async Task ChangeAsync(ProviderFile file)
    {
        IsUpdating = true; Error = ""; Notify(nameof(HasError));
        try
        {
            var plan = await rebuild(file);
            Items = plan.Items; Manual = plan.Manual;
            Notify(nameof(Items)); Notify(nameof(Manual)); Notify(nameof(HasManual));
        }
        catch (Exception ex) { Error = ex.Message; Notify(nameof(HasError)); }
        finally { IsUpdating = false; }
    }
}
