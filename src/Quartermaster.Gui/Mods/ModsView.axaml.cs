using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Quartermaster.Gui.Mods;

public partial class ModsView : UserControl
{
    private ModDetailsDialog? detailsDialog;
    public ModsView()
    {
        InitializeComponent();
        ModsList.ContextMenu!.Opening += (_, _) =>
        {
            AddToProfileMenuItem.Items.Clear();
            if (DataContext is not ModsViewModel { SelectedMod: { } selected } model) return;
            foreach (var profile in model.Profiles)
            {
                var modId = selected.Mod.Id; var profileId = profile.Id;
                AddToProfileMenuItem.Items.Add(new MenuItem
                {
                    Header = profile.Name,
                    Command = new Shared.AsyncCommand(() => model.AddToProfileAsync(modId, profileId),
                        () => model.Operations.CanInteract && model.Profiles.Any(p => p.Id == profileId &&
                            p.Entries.All(entry => entry.ModId != modId)), model.Operations.ReportError)
                });
            }
        };
        ModsList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(ModsList).Properties.IsRightButtonPressed || e.Source is not Avalonia.Visual source ||
                DataContext is not ModsViewModel model) return;
            var row = source.GetVisualAncestors().Prepend(source).OfType<ListBoxItem>().FirstOrDefault();
            if (row?.DataContext is ModListItem mod) model.SelectedMod = mod;
        }, RoutingStrategies.Tunnel);
    }
    private async void OpenModDetails(object? sender, RoutedEventArgs e)
    {
        if (detailsDialog is not null || DataContext is not ModsViewModel { Operations.CanInteract: true, Details: { } details } ||
            TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        ModsList.ContextMenu?.Close();
        detailsDialog = new() { DataContext = details };
        try { await owner.ShowDialogAsync<object?>(detailsDialog); }
        finally { detailsDialog = null; }
    }
}
