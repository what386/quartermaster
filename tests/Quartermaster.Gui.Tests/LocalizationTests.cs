using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Gui.Localization;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Settings;
using Quartermaster.Gui.Shared;
using Xunit;

namespace Quartermaster.Gui.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void PartialCatalogUsesRegionalParentAndEnglishFallbackAndReordersArguments()
    {
        using var catalogs = new CatalogDirectory();
        catalogs.Write("de", new() { ["Settings"] = "Einstellungen", ["Disable {0}"] = "{0} deaktivieren" });
        var localizer = new Localizer();
        var culture = CultureInfo.CurrentUICulture;
        var defaultCulture = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            Assert.Empty(localizer.LoadDirectory(catalogs.Path));
            localizer.SetLanguage("de-DE");
            Assert.Equal("de", localizer.Language);
            Assert.Equal("Einstellungen", localizer.Get("Settings"));
            Assert.Equal("Refresh", localizer.Get("Refresh"));
            Assert.Equal("Settings deaktivieren", localizer.Format("Disable {0}", "Settings"));
            Assert.Equal("A user supplied name", localizer.Get("A user supplied name"));
            localizer.SetLanguage("zz-Unknown");
            Assert.Equal("en", localizer.Language);
        }
        finally { CultureInfo.CurrentUICulture = culture; CultureInfo.DefaultThreadCurrentUICulture = defaultCulture; }
    }

    [Theory]
    [InlineData("Disable {1}")]
    [InlineData("Disable")]
    [InlineData("Disable {0")]
    public void InvalidPlaceholdersRejectOnlyTheBadCatalog(string translation)
    {
        using var catalogs = new CatalogDirectory();
        catalogs.Write("de", new() { ["Disable {0}"] = translation });
        catalogs.Write("es", new() { ["Settings"] = "Ajustes" });
        var localizer = new Localizer();
        Assert.Single(localizer.LoadDirectory(catalogs.Path));
        Assert.DoesNotContain(localizer.Languages, language => language.Code == "de");
        Assert.Contains(localizer.Languages, language => language.Code == "es");
    }

    [Fact]
    public void RegionalCatalogFallsBackThroughItsParentAndInvalidNumericFormatsUseEnglish()
    {
        using var catalogs = new CatalogDirectory();
        catalogs.Write("de", new() { ["Settings"] = "Einstellungen" });
        catalogs.Write("de-DE", new() { ["Save"] = "Speichern", ["Downloading app update · {0} MB"] = "{0:Q}" });
        var localizer = new Localizer();
        Assert.Empty(localizer.LoadDirectory(catalogs.Path));
        localizer.SetLanguage("de-DE");
        Assert.Equal("Speichern", localizer.Get("Save"));
        Assert.Equal("Einstellungen", localizer.Get("Settings"));
        Assert.Equal("Refresh", localizer.Get("Refresh"));
        Assert.Equal("Downloading app update · 15 MB", localizer.Format("Downloading app update · {0} MB", 15));
    }

    [Fact]
    public void EnglishCatalogContainsOnlyIdentityTranslationsAndValidFormatStrings()
    {
        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream("Quartermaster.Gui.Localization.Catalogs.en.json")!;
        using var json = JsonDocument.Parse(stream);
        foreach (var entry in json.RootElement.GetProperty("strings").EnumerateObject())
        {
            Assert.Equal(entry.Name, entry.Value.GetString());
            _ = System.Text.CompositeFormat.Parse(entry.Name);
        }
    }

    [Fact]
    public async Task OldSettingsDefaultToSystemLanguageAndSaveTheLanguageAlongsideExistingPreferences()
    {
        using var catalogs = new CatalogDirectory();
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(System.IO.Path.Combine(catalogs.Path, "settings.json"), "{\"AllowAutomaticUpdate\":true}", ct);
        var store = new SettingsStore(catalogs.Path);
        var old = await store.LoadAsync(ct);
        Assert.Null(old.Language); Assert.True(old.AllowAutomaticUpdate);
        await store.SaveAsync(old with { Language = "en" }, ct);
        var saved = await store.LoadAsync(ct);
        Assert.Equal("en", saved.Language); Assert.True(saved.AllowAutomaticUpdate);
    }

    [AvaloniaFact]
    public async Task LanguageSettingSavesWithoutChangingTheRunningLanguage()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync(); f.Shell.Navigate(PageKind.Settings);
        var settings = Assert.IsType<SettingsViewModel>(f.Shell.CurrentPage);
        var before = Localizer.Current.Language;
        settings.SelectedLanguage = settings.Languages.Single(language => language.Code == "en");
        Assert.True(settings.SaveCommand.CanExecute(null));
        await settings.SaveCommand.ExecuteAsync();
        Assert.False(f.Services.Operations.IsError);
        Assert.Equal("en", f.Services.Session.Settings.Language);
        Assert.Equal(before, Localizer.Current.Language);
        settings.ResetCommand.Execute(null);
        Assert.Equal("", settings.SelectedLanguage.Code);
    }

    [AvaloniaFact]
    public async Task StartupLocalizesXamlNavigationAndSettingsSearchWithoutTranslatingProfileNames()
    {
        using var f = new Fixture();
        var store = new SettingsStore(f.Services.DataDirectory);
        Directory.CreateDirectory(store.LocalizationDirectory);
        await File.WriteAllTextAsync(System.IO.Path.Combine(store.LocalizationDirectory, "de.json"), JsonSerializer.Serialize(new
        {
            language = "de", name = "Deutsch", strings = new Dictionary<string,string>
            { ["Settings"] = "Einstellungen", ["Save"] = "Speichern", ["Language"] = "Sprache", ["Library"] = "Bibliothek" }
        }));
        await store.SaveAsync(new(Language: "de", OnboardingCompleted: true), default);
        MainWindow? window = null;
        try
        {
            await f.Shell.InitializeAsync(); f.Shell.Navigate(PageKind.Settings);
            window = new MainWindow { DataContext = f.Shell }; window.Show();
            window.CaptureRenderedFrame()?.Dispose(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("Bibliothek", f.Shell.LibraryNavigation.Label);
            Assert.Equal("Default", Assert.Single(f.Shell.SidebarProfiles).Name);
            var view = Assert.Single(window.GetVisualDescendants().OfType<SettingsView>());
            Assert.Contains(view.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Speichern"));
            var model = Assert.IsType<SettingsViewModel>(view.DataContext);
            Assert.Equal("de", model.SelectedLanguage.Code);
            Assert.Contains(model.Languages, language => language.Code == "de");
            model.Search = "Sprache";
            Assert.True(model.ShowLanguage); Assert.True(model.ShowAppSettings); Assert.False(model.ShowGameSettings);
            Assert.Equal("Cancel", new ConfirmationDialog("Title", "Message", "Save").FindControl<Button>("CancelButton")!.Content);
        }
        finally { window?.Close(); Localizer.Current.SetLanguage("en"); }
    }

    [AvaloniaFact]
    public async Task UnavailableLanguagePreferenceSurvivesUnrelatedSettingsEdits()
    {
        using var f = new Fixture();
        var store = new SettingsStore(f.Services.DataDirectory);
        await store.SaveAsync(new(Language: "ja", OnboardingCompleted: true), default);
        try
        {
            await f.Shell.InitializeAsync(); f.Shell.Navigate(PageKind.Settings);
            var settings = Assert.IsType<SettingsViewModel>(f.Shell.CurrentPage);
            Assert.Equal("en", Localizer.Current.Language);
            Assert.Equal("ja", settings.SelectedLanguage.Code);
            settings.AllowAutomaticUpdate = true;
            await settings.SaveCommand.ExecuteAsync();
            Assert.False(f.Services.Operations.IsError);
            Assert.Equal("ja", f.Services.Session.Settings.Language);
        }
        finally { Localizer.Current.SetLanguage("en"); }
    }

    private sealed class CatalogDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qm-localization-" + Guid.NewGuid().ToString("N"));
        public CatalogDirectory() => Directory.CreateDirectory(Path);
        public void Write(string language, Dictionary<string,string> strings) => File.WriteAllText(System.IO.Path.Combine(Path, language + ".json"),
            JsonSerializer.Serialize(new { language, name = language, strings }));
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
