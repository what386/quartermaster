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
            if (DataContext is not ModsViewModel { HasSelection: true } model) return;
            var ids = model.SelectedMods.Select(item => item.Mod.Id).ToArray();
            foreach (var profile in model.Profiles)
            {
                var profileId = profile.Id;
                AddToProfileMenuItem.Items.Add(new MenuItem
                {
                    Header = profile.Name,
                    Command = new Shared.AsyncCommand(() => model.AddSelectedToProfileAsync(profileId),
                        () => model.Operations.CanInteract && model.Profiles.Any(p => p.Id == profileId &&
                            ids.Any(id => p.Entries.All(entry => entry.ModId != id))), model.Operations.ReportError)
                });
            }
        };
        ModsList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(ModsList).Properties.IsRightButtonPressed || e.Source is not Avalonia.Visual source ||
                DataContext is not ModsViewModel model) return;
            var row = source.GetVisualAncestors().Prepend(source).OfType<ListBoxItem>().FirstOrDefault();
            if (row?.DataContext is ModListItem mod)
            {
                if (!model.SelectedMods.Contains(mod)) model.SelectedMod = mod;
                // Keep the entire selection when opening its context menu.
                e.Handled = true;
            }
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
