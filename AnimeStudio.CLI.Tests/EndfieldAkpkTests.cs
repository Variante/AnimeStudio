using System.Buffers.Binary;
using System.Text;
using AnimeStudio.Endfield;

internal static class EndfieldAkpkTests
{
    public static void Run()
    {
        TestBankPayloadIsDecryptedAndFramed();
        TestSoundPayloadAndMetadata();
        TestUnsupportedVersionFailsClosed();
        TestTruncatedSectorFailsClosed();
    }

    private static void TestBankPayloadIsDecryptedAndFramed()
    {
        const uint bankId = 0x12345678;
        var plainBank = new byte[] { (byte)'B', (byte)'K', (byte)'H', (byte)'D', 0, 0, 0, 0 };
        var encryptedBank = (byte[])plainBank.Clone();
        EndfieldAudioCrypto.DecryptVfs(encryptedBank, 0, encryptedBank.Length, bankId, 0);
        var package = EndfieldAkpkPackage.Parse(BuildPackage(
            bankId, encryptedBank, bank: true, sound: false));
        if (package.BankCount != 1 || package.Entries.Count != 0)
        {
            throw new InvalidOperationException("AKPK bank fixture did not produce one framed bank and no media");
        }
    }

    private static void TestSoundPayloadAndMetadata()
    {
        var wem = Encoding.ASCII.GetBytes("RIFF");
        var package = EndfieldAkpkPackage.Parse(BuildPackage(0x42, wem, bank: false, sound: true));
        if (package.SoundCount != 1 || package.Entries.Count != 1 || package.HeaderSize != 68)
        {
            throw new InvalidOperationException("AKPK sound fixture metadata mismatch");
        }
        var decoded = package.GetWemData(package.Entries[0]);
        if (!decoded.SequenceEqual(wem))
        {
            throw new InvalidOperationException("AKPK sound fixture bytes changed");
        }
        var pluginPackage = EndfieldAkpkPackage.Parse(BuildPackage(0x43, Encoding.ASCII.GetBytes("PLUG"), bank: false, sound: true));
        if (!pluginPackage.GetWemData(pluginPackage.Entries[0]).SequenceEqual(Encoding.ASCII.GetBytes("PLUG")))
        {
            throw new InvalidOperationException("AKPK PLUG fixture must not be media-decrypted");
        }
    }

    private static void TestUnsupportedVersionFailsClosed()
    {
        var data = BuildPackage(0x42, Encoding.ASCII.GetBytes("RIFF"), bank: false, sound: true);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 2);
        AssertThrows(() => EndfieldAkpkPackage.Parse(data), "unsupported AKPK version");
    }

    private static void TestTruncatedSectorFailsClosed()
    {
        var data = BuildPackage(0x42, Encoding.ASCII.GetBytes("RIFF"), bank: false, sound: true);
        Array.Resize(ref data, 76);
        AssertThrows(() => EndfieldAkpkPackage.Parse(data), "out of bounds");
    }

    private static byte[] BuildPackage(uint id, byte[] payload, bool bank, bool sound)
    {
        const int languageStart = 28;
        const int bankStart = 48;
        const int externalStart = 76;
        const int payloadOffset = 80;
        var data = new byte[payloadOffset + payload.Length];
        var soundStart = bank ? 72 : 52;
        data[0] = (byte)'A'; data[1] = (byte)'K'; data[2] = (byte)'P'; data[3] = (byte)'K';
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 68);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), bank ? 24U : 4U);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20, 4), sound ? 24U : 4U);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24, 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(languageStart, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(languageStart + 4, 4), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(languageStart + 8, 4), 0);
        Encoding.ASCII.GetBytes("sfx\0").CopyTo(data, languageStart + 12);
        if (bank)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(bankStart, 4), 1);
            WriteEntry(data, bankStart + 4, id, payload.Length, payloadOffset);
        }
        if (sound)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(soundStart, 4), 1);
            WriteEntry(data, soundStart + 4, id, payload.Length, payloadOffset);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(externalStart, 4), 0);
        payload.CopyTo(data, payloadOffset);
        return data;
    }

    private static void WriteEntry(byte[] data, int offset, uint id, int size, int payloadOffset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), id);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 4, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 8, 4), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 12, 4), (uint)payloadOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 16, 4), 0);
    }

    private static void AssertThrows(Action action, string expected)
    {
        try { action(); }
        catch (Exception e) when (e.Message.Contains(expected, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException($"AKPK fixture expected diagnostic containing '{expected}'");
    }
}
