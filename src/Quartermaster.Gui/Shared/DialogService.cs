using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace Quartermaster.Gui.Shared;

public interface IDialogService
{
    Task<string?> PickModZipAsync();
    Task<string?> PickFolderAsync(string title);
    Task<bool> ConfirmAsync(string title, string message, string acceptLabel);
}

public sealed class DialogService(Func<Window> owner) : IDialogService
{
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
    public async Task<bool> ConfirmAsync(string title, string message, string acceptLabel)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 480,
            CanResize = false,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var accept = new Button { Content = acceptLabel, Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel" };
        accept.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new(24),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = title, FontSize = 22, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10, Children = { cancel, accept } }
            }
        };
        return await dialog.ShowDialog<bool>(owner());
    }
}
