using Quartermaster.Core.Patching;
using global::System.Buffers.Binary;
using global::System.IO.Compression;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Importing;
using Quartermaster.Library.Storage;

namespace Quartermaster.Interop.Tests;

internal sealed class Fixture : IDisposable
{
    public const string Archive = "9ba626afa44a3aa3";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "quartermaster-interop-" + Guid.NewGuid().ToString("N"));
    public string App => Path.Combine(Root, "app");
    public string Game => Path.Combine(Root, "game");
    public ModContentStore Contents { get; }
    public JsonLibraryStore Store { get; }
    public LibraryService Library { get; }
    public Fixture()
    {
        Directory.CreateDirectory(Game); File.WriteAllBytes(Path.Combine(Game, Archive), []);
        Contents = new(App); Store = new(App); Library = new(Store, Contents);
    }
    public string Source(string name, ulong resource = 1, string archive = Archive, int slot = 7)
    {
        var directory = Path.Combine(Root, name); Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, $"{archive}.patch_{slot}"), Patch(resource));
        File.WriteAllBytes(Path.Combine(directory, $"{archive}.patch_{slot}.stream"), [1, 2, 3]);
        return directory;
    }
    public string Zip(string folder, string name = "mod.zip")
    {
        var path = Path.Combine(Root, name); ZipFile.CreateFromDirectory(folder, path); return path;
    }
    public static byte[] Patch(ulong resource = 1)
    {
        var data = new byte[196];
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
        void U64(int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(at), value);
        U32(0, 4026531857); U32(4, 1); U32(8, 1);
        U64(80, 0x1122334455667788); U64(88, 1); U64(104, resource); U64(112, 0x1122334455667788); U64(120, 192); U32(160, 4);
        data[192] = (byte)resource; return data;
    }
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
