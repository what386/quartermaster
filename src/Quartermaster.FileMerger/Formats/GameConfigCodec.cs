using SpaceWizards.Sodium;

namespace Quartermaster.FileMerger;

/// <summary>Authenticated NaCl sealed-box encoding used by generated game config files.</summary>
public static class GameConfigCodec
{
    // Game format keys, documented by SHODAN Stat Editor's offline/gamedata.lua.
    private static readonly byte[] PublicKey = Convert.FromHexString("2dc9aa7c30e6eed5f7fd0e0e95c7287299b126f79520cc41de750982ee7ff50c");
    private static readonly byte[] SecretKey = Convert.FromHexString("a0296b81310b58f013d8a9b343a1c292008003dfd9ab15fcf351543048b49b64");
    public const int SealBytes = 48;

    public static byte[] Decrypt(ReadOnlySpan<byte> content)
    {
        if (content.Length < SealBytes) throw new InvalidDataException("A sealed game config is too short.");
        var plain = new byte[content.Length - SealBytes];
        if (!CryptoBox.SealOpen(plain, content, PublicKey, SecretKey))
            throw new InvalidDataException("Game config authentication failed; the file is corrupt or uses an unsupported format.");
        return plain;
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plain) => CryptoBox.Seal(plain, PublicKey);

    /// <summary>Seals edited LDLD data with a correction trailer that preserves the game's expected hash.</summary>
    public static byte[] EncryptPreservingHash(ReadOnlySpan<byte> plain, ulong gameHash)
    {
        var document = ConfigDocument.Parse(plain.ToArray());
        return Encrypt(ConfigHash.Correct(document, document.Bytes, gameHash));
    }
}
