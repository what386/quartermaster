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
    public string? ZipPath { get; set; }
    public string? FolderPath { get; set; }
    public bool Confirm { get; set; } = true;
    public string? InputText { get; set; }
    public Task<string?> RequestTextAsync(string title, string prompt, string acceptLabel) => Task.FromResult(InputText);
    public List<(string Title, string Message)> Confirmations { get; } = [];
    public Task<string?> PickModZipAsync() => Task.FromResult(ZipPath);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult(FolderPath);
    public Task<bool> ConfirmAsync(string title, string message, string acceptLabel)
    { Confirmations.Add((title, message)); return Task.FromResult(Confirm); }
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
    public Fixture(bool discoverGame = true)
    {
        Directory.CreateDirectory(Game); File.WriteAllBytes(Path.Combine(Game, Archive), []);
        Services = new(Data, Dialogs, () => discoverGame ? [Game] : [], () => Launches++);
        Shell = new(Services);
    }
    public string Source(string name, ulong resource = 1)
    {
        var folder = Path.Combine(Root, name); Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, Archive + ".patch_7"), Patch(resource));
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
    private static byte[] Patch(ulong resource)
    {
        var data = new byte[196];
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
        void U64(int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(at), value);
        U32(0, 4026531857); U32(4, 1); U32(8, 1); U64(80, 0x1122334455667788); U64(88, 1);
        U64(104, resource); U64(112, 0x1122334455667788); U64(120, 192); U32(160, 4); data[192] = (byte)resource;
        return data;
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
