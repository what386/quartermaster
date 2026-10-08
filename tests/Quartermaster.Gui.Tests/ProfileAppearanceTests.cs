using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Profiles;
using Quartermaster.Gui.Shared;
using Xunit;

namespace Quartermaster.Gui.Tests;

public sealed class ProfileAppearanceTests
{
    [AvaloniaFact]
    public async Task GroupColorsUpdateExistingRowsAndResetToThemeDefaults()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var model = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        f.Dialogs.InputText="Weapons"; await model.AddGroupCommand.ExecuteAsync();
        var row=Assert.Single(model.VisibleItems.OfType<ProfileGroupItem>());
        var view = new ProfileGroupView { DataContext=row };
        var window = new Window { Content=view }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            f.Dialogs.Colors=new("#fee800","#000000"); await row.ColorsCommand.ExecuteAsync();
            Assert.Same(row,Assert.Single(model.VisibleItems.OfType<ProfileGroupItem>()));
            Assert.Equal("#FEE800",row.BackgroundColor);
            var button=view.FindControl<Button>("GroupButton")!;
            Assert.Equal(Color.Parse("#fee800"),Assert.IsType<SolidColorBrush>(button.Background).Color);
            Assert.Equal(Colors.Black,Assert.IsType<SolidColorBrush>(button.Foreground).Color);
            var center=button.TranslatePoint(new Point(button.Bounds.Width/2,button.Bounds.Height/2),window)!.Value;
            window.MouseMove(center); window.CaptureRenderedFrame()?.Dispose();
            var presenter=Assert.Single(button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>(),p => p.Name=="PART_ContentPresenter");
            Assert.Equal(Color.Parse("#fee800"),Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color);
            f.Dialogs.Colors=null; await row.ColorsCommand.ExecuteAsync(); Assert.Equal("#FEE800",row.BackgroundColor);
            f.Dialogs.Colors=new(null,null); await row.ColorsCommand.ExecuteAsync();
            Assert.Null(row.BackgroundColor); Assert.Null(row.TextColor);
            Assert.NotEqual(Color.Parse("#fee800"),Assert.IsType<SolidColorBrush>(button.Background).Color);
        }
        finally { window.Close(); }
        var dialog=new GroupColorsDialog("#ffee00","#000000");
        dialog.FindControl<TextBox>("BackgroundInput")!.Text="invalid";
        Assert.False(dialog.TryAccept());
        dialog.FindControl<TextBox>("BackgroundInput")!.Text="";
        Assert.True(dialog.TryAccept());
    }

    [AvaloniaFact]
    public async Task ProfileThumbnailsRenderDuplicateAndCanBeRemoved()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var image=Path.Combine(f.Root,"profile.png");
        using(var pixels=new WriteableBitmap(new PixelSize(24,24),new Vector(96,96))) pixels.Save(image,PngBitmapEncoderOptions.Default);
        f.Dialogs.ImagePath=image;
        var profile=Assert.Single(f.Shell.SidebarProfiles);
        var window=new MainWindow { DataContext=f.Shell }; window.Show();
        try
        {
            await profile.ChangeThumbnailCommand.ExecuteAsync();
            Assert.False(f.Services.Operations.IsError); Assert.True(profile.HasThumbnail);
            window.CaptureRenderedFrame()?.Dispose();
            var preview=Assert.Single(window.GetVisualDescendants().OfType<ProfileThumbnailImage>());
            Assert.NotNull(preview.Source);
            var monogram=Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),text => text.Name=="ProfileMonogram");
            Assert.False(monogram.IsVisible);
            var thumbnail=profile.Thumbnail;
            await profile.DuplicateCommand.ExecuteAsync();
            Assert.Equal(thumbnail,f.Shell.SidebarProfiles.Last().Thumbnail);
            f.Dialogs.ImagePath=null; await profile.ChangeThumbnailCommand.ExecuteAsync();
            Assert.Equal(thumbnail,profile.Thumbnail);
            await profile.RemoveThumbnailCommand.ExecuteAsync();
            Assert.False(profile.HasThumbnail); Assert.Null(preview.Source);
            Assert.True(monogram.IsVisible);
            Assert.Equal(thumbnail,f.Shell.SidebarProfiles.Last().Thumbnail);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TagsSaveOnCloseAndFindModsInLibraryAndProfiles()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.Source("Reticle"),CancellationToken.None);
        var mod=Assert.Single(f.Services.Session.State.Mods);
        var profiles=Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        await f.Services.Library.AddToProfileAsync(mod.Id,f.Services.Session.ActiveProfile!.Id);
        await f.Services.Session.ReloadAsync(CancellationToken.None);
        var details=new ModDetailsViewModel(mod,f.Services);
        var view=new ModDetailsDialog { DataContext=details };
        var window=new Window { Content=view }; window.Show();
        try
        {
            view.FindControl<TextBox>("TagsInput")!.Text=" UI, hud, UI "; Dispatcher.UIThread.RunJobs();
            Assert.True(await details.SaveOnCloseAsync());
            Assert.Equal(new[]{"UI","hud"},f.Services.Session.State.Mods.Single().Tags);
            profiles.Search="HUD";
            Assert.Equal(mod.Id,Assert.Single(profiles.VisibleItems.OfType<ProfileModItem>()).Mod.Id);
            Assert.Equal(mod.Id,Assert.Single(profiles.VisibleEntries).Mod.Id);
            f.Shell.Navigate(PageKind.Mods);
            var library=Assert.IsType<ModsViewModel>(f.Shell.CurrentPage); library.Search="hud";
            Assert.Equal(mod.Id,Assert.Single(library.Mods).Mod.Id);
            Assert.Equal("UI · hud",library.Mods[0].TagSummary);
            details.TagsInput=""; Assert.True(await details.SaveOnCloseAsync());
            Assert.Empty(f.Services.Session.State.Mods.Single().Tags); Assert.Empty(library.Mods);
        }
        finally { window.Close(); }
    }
}
