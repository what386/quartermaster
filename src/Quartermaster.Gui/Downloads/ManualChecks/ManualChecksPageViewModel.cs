using Quartermaster.Gui.Services;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Downloads;

public sealed class ManualChecksPageViewModel(ModDownloads downloads) : ViewModelBase
{
    public ModDownloads Downloads { get; } = downloads;
}
