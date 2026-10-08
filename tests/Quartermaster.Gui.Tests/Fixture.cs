using System.Buffers.Binary;
using System.IO.Compression;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Shared;

[assembly: AvaloniaTestApplication(typeof(Quartermaster.Gui.Tests.TestAppBuilder))]

namespace Quartermaster.Gui.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

internal sealed class FakeDialogs : IDialogService
{
    public Func<Onboarding.SetupViewModel, Task<Onboarding.SetupOutcome>>? OnboardingHandler { get; set; }
    public int OnboardingPrompts { get; private set; }
    public Task<Onboarding.SetupOutcome> ShowOnboardingAsync(Onboarding.SetupViewModel model, CancellationToken ct = default)
    { OnboardingPrompts++; return OnboardingHandler?.Invoke(model) ?? Task.FromResult(Onboarding.SetupOutcome.SkipTour); }
    public AppUpdateChoice AppUpdateAnswer { get; set; } = AppUpdateChoice.Cancel;
    public List<string> AppUpdatePrompts { get; } = [];
    public Task<AppUpdateChoice> PromptAppUpdateAsync(string version, string notes, CancellationToken ct = default)
    { AppUpdatePrompts.Add(version); return Task.FromResult(AppUpdateAnswer); }
    public Quartermaster.Gui.Mods.ModImportRequest? ModImport { get; set; }
    public Quartermaster.Providers.Clients.ProviderFile? ModFile { get; set; }
    public bool CancelModFile { get; set; }
    public Task<Quartermaster.Gui.Mods.ModImportRequest?> RequestModImportAsync() => Task.FromResult(ModImport);
    public Quartermaster.Gui.Mods.LocalModImportOptions? ImportOptions { get; set; } = new();
    public List<string> ImportConfirmations { get; } = [];
    public Task<Quartermaster.Gui.Mods.LocalModImportOptions?> ConfirmModImportAsync(string source)
    { ImportConfirmations.Add(source); return Task.FromResult(ImportOptions); }
    public Task<Quartermaster.Providers.Clients.ProviderFile?> ChooseModFileAsync(Quartermaster.Providers.Clients.ProviderMod mod) => Task.FromResult(CancelModFile ? null : ModFile ?? mod.Files.FirstOrDefault(file => file.IsPrimary) ?? mod.Files.FirstOrDefault());
    public string? ZipPath { get; set; }
    public string? FolderPath { get; set; }
    public string? SavePath { get; set; }
    public string? SuggestedSaveName { get; private set; }
    public Task<string?> SaveModZipAsync(string suggestedName)
    { SuggestedSaveName = suggestedName; return Task.FromResult(SavePath); }
    public bool Confirm { get; set; } = true;
    public Queue<bool> ConfirmationAnswers { get; } = new();
    public string? InputText { get; set; }
    public bool CreateProfileFromFile { get; set; }
    public Task<ProfileCreationRequest?> RequestProfileCreationAsync() => Task.FromResult(
        CreateProfileFromFile ? new ProfileCreationRequest(FromFile: true) : InputText is null ? null : new ProfileCreationRequest(Name: InputText));
    public string? InitialInputText { get; private set; }
    public Task<string?> RequestTextAsync(string title, string prompt, string acceptLabel, string? initialValue = null)
    { InitialInputText = initialValue; return Task.FromResult(InputText); }
    public List<(string Title, string Message, string AcceptLabel, string CancelLabel)> Confirmations { get; } = [];
    public Task<string?> PickModZipAsync() => Task.FromResult(ZipPath);
    public Task<string?> PickProfileZipAsync() => Task.FromResult(ZipPath);
    public Task<string?> SaveProfileZipAsync(string suggestedName)
    { SuggestedSaveName = suggestedName; return Task.FromResult(SavePath); }
    public Task<string?> PickFolderAsync(string title) => Task.FromResult(FolderPath);
    public Task<bool> ConfirmAsync(string title, string message, string acceptLabel, string cancelLabel = "Cancel")
    { Confirmations.Add((title, message, acceptLabel, cancelLabel)); return Task.FromResult(ConfirmationAnswers.TryDequeue(out var answer) ? answer : Confirm); }
}

internal sealed class Fixture : IDisposable
{
    public const string Archive = "9ba626afa44a3aa3";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "quartermaster-gui-test-" + Guid.NewGuid().ToString("N"));
    public string Data => Path.Combine(Root, "library");
    public string Game => Path.Combine(Root, "game");
    public FakeDialogs Dialogs { get; } = new();
    public AppServices Services { get; }
    public MainWindowViewModel Shell { get; }
    public int Launches { get; private set; }
    public List<Uri> BrowserRequests { get; } = [];
    private readonly Action<Uri>? browserCallback;
    public Fixture(bool discoverGame = true, Action<Uri>? openBrowser = null, HttpClient? nexusApi = null, HttpClient? nexusDownloads = null, HttpClient? githubApi = null, HttpClient? githubDownloads = null, IDialogService? dialogs = null)
    {
        browserCallback = openBrowser;
        Directory.CreateDirectory(Game); File.WriteAllBytes(Path.Combine(Game, Archive), Patch(1));
        Services = new(Data, dialogs ?? Dialogs, () => discoverGame ? [Game] : [], () => Launches++, RecordBrowserRequest, nexusApi, nexusDownloads, githubApi, githubDownloads);
        Shell = new(Services);
    }
    private void RecordBrowserRequest(Uri uri)
    {
        BrowserRequests.Add(uri);
        browserCallback?.Invoke(uri);
    }
    public AppServices ReopenServices() => new(Data, Dialogs, () => [], () => Launches++, RecordBrowserRequest);
    public string Source(string name, ulong resource = 1)
    {
        var folder = Path.Combine(Root, name); Directory.CreateDirectory(folder);
        // Distinct test mods may collide on the same resource without containing identical patch data.
        File.WriteAllBytes(Path.Combine(folder, Archive + ".patch_7"), Patch(resource, 0x1122334455667788,
            global::System.Security.Cryptography.SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(name))));
        File.WriteAllBytes(Path.Combine(folder, Archive + ".patch_7.stream"), [1, 2, 3]);
        return folder;
    }
    public string Zip(string name, ulong resource = 1)
    {
        var source = Source(name, resource); var zip = source + ".zip";
        ZipFile.CreateFromDirectory(source, zip); return zip;
    }
    public string OptionsSource()
    {
        var folder = Path.Combine(Root, "Armor variants"); Directory.CreateDirectory(folder);
        foreach (var (name, resource) in new[] { ("common", 1UL), ("blue", 2UL), ("red", 3UL) })
        {
            var variant = Path.Combine(folder, name); Directory.CreateDirectory(variant);
            File.WriteAllBytes(Path.Combine(variant, Archive + ".patch_0"), Patch(resource));
        }
        File.WriteAllText(Path.Combine(folder, "manifest.json"), """
            {"version":1,"name":"Armor variants","description":"Choose the color of your armor.","options":[
              {"name":"Armor color","include":["common"],"subOptions":[
                {"name":"Blue","include":["blue"]},{"name":"Red","include":["red"]}]}]}
            """);
        return folder;
    }
    public string UnitSource(string name, bool compatible = false, bool missing = false, bool customLod = false)
    {
        const ulong unitType = 16187218042980615487;
        byte[] Unit(int lodSize)
        {
            var unit = new byte[0x80 + lodSize + 16];
            BinaryPrimitives.WriteUInt32LittleEndian(unit.AsSpan(0x2c), 0xA4CD36);
            BinaryPrimitives.WriteUInt32LittleEndian(unit.AsSpan(0x30), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(unit.AsSpan(0x34), (uint)(0x80 + lodSize));
            unit.AsSpan(0x80, lodSize).Fill(0xdd);
            return unit;
        }
        File.WriteAllBytes(Path.Combine(Game, Archive), Patch(1, unitType, Unit(16)));
        var folder = Source(name);
        var modUnit = Unit(compatible && !customLod ? 16 : 8);
        if (!compatible) BinaryPrimitives.WriteUInt32LittleEndian(modUnit.AsSpan(0x2c), 0xA4CD35);
        File.WriteAllBytes(Path.Combine(folder, Archive + ".patch_7"), Patch(missing ? 2UL : 1UL, unitType, modUnit));
        return folder;
    }
    private static byte[] Patch(ulong resource) => Patch(resource, 0x1122334455667788, [(byte)resource, 0, 0, 0]);
    private static byte[] Patch(ulong resource, ulong type, byte[] payload)
    {
        var data = new byte[192 + payload.Length];
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
        void U64(int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(at), value);
        U32(0, 4026531857); U32(4, 1); U32(8, 1); U64(80, type); U64(88, 1);
        U64(104, resource); U64(112, type); U64(120, 192); U32(160, (uint)payload.Length); payload.CopyTo(data, 192);
        return data;
    }
    public void Dispose() { Services.DisposeAsync().AsTask().GetAwaiter().GetResult(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
