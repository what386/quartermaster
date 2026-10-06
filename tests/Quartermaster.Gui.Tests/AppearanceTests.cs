using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Settings;
using Xunit;

namespace Quartermaster.Gui.Tests;

public class AppearanceTests
{
    private static Color ResourceColor(string key) => Assert.IsType<SolidColorBrush>(Application.Current!.Resources[key]).Color;

    [AvaloniaFact]
    public async Task AppearancePreviewsOnExistingControlsAndPersistsOnlyWithSave()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        f.Shell.Navigate(PageKind.Settings);
        var settings = Assert.IsType<SettingsViewModel>(f.Shell.CurrentPage);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var appearance = Assert.Single(window.GetVisualDescendants().OfType<AppearanceSettingsView>());
            var picker = appearance.FindControl<ColorPicker>("AccentPicker")!;
            var selector = appearance.FindControl<ComboBox>("ThemeSelector")!;
            var save = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Save"));
            Assert.False(save.IsEffectivelyEnabled);
            Assert.Equal(Color.Parse(ThemeManager.DefaultAccent), picker.Color);

            selector.SelectedIndex = (int)ThemePreset.Light;
            picker.Color = Color.Parse("#397ADA");
            Dispatcher.UIThread.RunJobs(); window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
            Assert.Equal(Color.Parse("#F3F4F6"), Assert.IsType<SolidColorBrush>(window.Background).Color);
            Assert.Equal(Color.Parse("#397ADA"), Assert.IsType<SolidColorBrush>(save.Background).Color);
            Assert.Equal(Color.Parse("#16805C"), ResourceColor("SuccessBrush"));
            Assert.NotEqual(ResourceColor("SuccessBrush"), ResourceColor("AccentBrush"));
            var fluent = Assert.Single(Application.Current!.Styles.OfType<FluentTheme>());
            Assert.Equal(Color.Parse("#397ADA"), fluent.Palettes[ThemeVariant.Light].Accent);
            var stored = await new SettingsStore(f.Data).LoadAsync(CancellationToken.None);
            Assert.Equal(ThemePreset.Dark, stored.Theme);
            Assert.Equal(ThemeManager.DefaultAccent, stored.AccentColor);

            settings.Search = "appearance"; window.CaptureRenderedFrame()?.Dispose();
            Assert.True(appearance.IsEffectivelyVisible);
            await f.Services.Session.ReloadAsync(CancellationToken.None);
            Assert.Equal((int)ThemePreset.Light, settings.ThemeChoice);
            Assert.Equal(Color.Parse("#397ADA"), ResourceColor("AccentBrush"));
            await settings.SaveCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
            Assert.False(settings.SaveCommand.CanExecute(null));
            stored = await new SettingsStore(f.Data).LoadAsync(CancellationToken.None);
            Assert.Equal(ThemePreset.Light, stored.Theme); Assert.Equal("#397ADA", stored.AccentColor);

            // Startup must restore saved appearance even after another palette was applied.
            ThemeManager.Apply(ThemePreset.Dark, Color.Parse(ThemeManager.DefaultAccent));
            await using var reopened = f.ReopenServices();
            await reopened.Session.InitializeAsync(CancellationToken.None);
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
            Assert.Equal(Color.Parse("#397ADA"), ResourceColor("AccentBrush"));

            settings.ResetCommand.Execute(null); Dispatcher.UIThread.RunJobs();
            Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
            Assert.Equal(Color.Parse(ThemeManager.DefaultAccent), ResourceColor("AccentBrush"));
            stored = await new SettingsStore(f.Data).LoadAsync(CancellationToken.None);
            Assert.Equal(ThemePreset.Light, stored.Theme);
            await settings.SaveCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
            stored = await new SettingsStore(f.Data).LoadAsync(CancellationToken.None);
            Assert.Equal(ThemePreset.Dark, stored.Theme); Assert.Equal(ThemeManager.DefaultAccent, stored.AccentColor);
        }
        finally { window.Close(); ThemeManager.Apply(ThemePreset.Dark, Color.Parse(ThemeManager.DefaultAccent)); }
    }

    [AvaloniaFact]
    public async Task SlatePreviewSurvivesFailedSaveAndAccentTextRemainsReadable()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        f.Shell.Navigate(PageKind.Settings);
        var settings = Assert.IsType<SettingsViewModel>(f.Shell.CurrentPage);
        try
        {
            settings.ThemeChoice = (int)ThemePreset.Slate;
            settings.AccentColor = Colors.White;
            Assert.Equal(Color.Parse("#1A2230"), ResourceColor("AppBackground"));
            Assert.Equal(Color.Parse("#101214"), ResourceColor("AccentForegroundBrush"));
            settings.AccentColor = Colors.Black;
            Assert.Equal(Colors.White, ResourceColor("AccentForegroundBrush"));
            settings.GamePath = Path.Combine(f.Root, "missing");
            await settings.SaveCommand.ExecuteAsync(); Assert.True(f.Services.Operations.IsError);
            Assert.Equal(ThemePreset.Dark, (await new SettingsStore(f.Data).LoadAsync(CancellationToken.None)).Theme);
            Assert.Equal((int)ThemePreset.Slate, settings.ThemeChoice);
            Assert.Equal(Colors.Black, ResourceColor("AccentBrush"));
            settings.GamePath = f.Game;
            await settings.SaveCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
            Assert.Equal(ThemePreset.Slate, f.Services.Session.Settings.Theme);
            Assert.Equal("#000000", f.Services.Session.Settings.AccentColor);
        }
        finally { ThemeManager.Apply(ThemePreset.Dark, Color.Parse(ThemeManager.DefaultAccent)); }
    }

    [AvaloniaFact]
    public async Task OlderSettingsUseOriginalPaletteAndInvalidAppearanceDoesNotOverwriteSettings()
    {
        using var f = new Fixture(); Directory.CreateDirectory(f.Data);
        File.WriteAllText(Path.Combine(f.Data, "settings.json"), """{"Repatch":1}""");
        var store = new SettingsStore(f.Data);
        var legacy = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(ThemePreset.Dark, legacy.Theme); Assert.Equal(ThemeManager.DefaultAccent, legacy.AccentColor);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(legacy with { Theme = (ThemePreset)99 }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(legacy with { AccentColor = "#00000000" }, CancellationToken.None));
        Assert.Equal(legacy, await store.LoadAsync(CancellationToken.None));
    }
}
