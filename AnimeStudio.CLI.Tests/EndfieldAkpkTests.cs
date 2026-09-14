using System.Buffers.Binary;
using System.Text;
using AnimeStudio.Endfield;

internal static class EndfieldAkpkTests
{
    public static void Run()
    {
        TestBankPayloadIsDecryptedAndFramed();
        TestHircObjectFramingAndUnknownTypeArePreserved();
        TestType2SourcePrefixIsBounded();
        TestType3ActionFramesConsumeSupportedBodiesExactly();
        TestType3ActionFramesFailClosed();
        TestMalformedHircFailsClosed();
        TestSoundPayloadAndMetadata();
        TestUnsupportedVersionFailsClosed();
        TestTruncatedSectorFailsClosed();
    }

    private static void TestHircObjectFramingAndUnknownTypeArePreserved()
    {
        var bank = BuildBnk(
            (0xFE, 0x1001U, new byte[] { 1, 2, 3 }),
            (0x01, 0x1002U, Array.Empty<byte>()));
        var package = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x12345678, bank));
        if (package.BnkStructures.Count != 1)
        {
            throw new InvalidOperationException("HIRC fixture did not produce one BNK structure");
        }
        var structure = package.BnkStructures[0];
        if (structure.HircObjectCount != 2
            || structure.HircObjectTypeCounts[0xFE] != 1
            || structure.HircObjectTypeCounts[0x01] != 1
            || structure.Sections.Count != 2)
        {
            throw new InvalidOperationException("HIRC fixture framing/type census mismatch");
        }
    }

    private static void TestMalformedHircFailsClosed()
    {
        var truncatedBankHeader = BuildBnk((0xFE, 0x2000U, Array.Empty<byte>()));
        BinaryPrimitives.WriteUInt32LittleEndian(truncatedBankHeader.AsSpan(4, 4), 3);
        AssertThrows(
            () => EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x2000, truncatedBankHeader)),
            "BKHD version field truncated");

        var truncated = BuildBnk((0xFE, 0x2001U, new byte[] { 1 }));
        // BKHD is 12 bytes; HIRC object size begins at byte 25.
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(25, 4), 99);
        AssertThrows(
            () => EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x2000, truncated)),
            "HIRC object range out of HIRC");

        var trailing = BuildBnk((0xFE, 0x2002U, new byte[] { 1 }));
        BinaryPrimitives.WriteUInt32LittleEndian(trailing.AsSpan(16, 4),
            BinaryPrimitives.ReadUInt32LittleEndian(trailing.AsSpan(16, 4)) + 1);
        Array.Resize(ref trailing, trailing.Length + 1);
        trailing[^1] = 0x7F;
        AssertThrows(
            () => EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x2000, trailing)),
            "HIRC cursor mismatch");

        var impossibleCount = BuildBnk((0xFE, 0x2003U, Array.Empty<byte>()));
        // BKHD is 12 bytes; HIRC body and its count begin at byte 20.
        BinaryPrimitives.WriteUInt32LittleEndian(impossibleCount.AsSpan(20, 4), uint.MaxValue);
        AssertThrows(
            () => EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x2000, impossibleCount)),
            "HIRC object count exceeds HIRC");
    }

    private static void TestType2SourcePrefixIsBounded()
    {
        var source = new byte[18];
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(0, 4), 0x00040001);
        source[4] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(5, 4), 0x1234);
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(9, 4), 0x20);
        source[13] = 0x80;
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(14, 4), 0);
        var package = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0x3000, BuildBnk((0x02, 0x3001U, source))));
        var structure = package.BnkStructures[0];
        if (structure.Type2PrefixCount != 1
            || structure.Type2PluginTypeCounts[1] != 1
            || structure.Type2PrefixBytes != 14
            || structure.Type2OpaqueTailBytes != 4)
        {
            throw new InvalidOperationException("type 0x02 source-prefix census mismatch");
        }

        var truncated = new byte[13];
        AssertThrows(
            () => EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x3000, BuildBnk((0x02, 0x3002U, truncated)))),
            "type 0x02 source prefix truncated");

        var overrun = new byte[18];
        BinaryPrimitives.WriteUInt32LittleEndian(overrun.AsSpan(0, 4), 0x00040002);
        BinaryPrimitives.WriteUInt32LittleEndian(overrun.AsSpan(14, 4), 99);
        AssertThrows(
            () => EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x3000, BuildBnk((0x02, 0x3003U, overrun)))),
            "type 0x02 source plugin range out of object");
    }

    private static void TestType3ActionFramesConsumeSupportedBodiesExactly()
    {
        var operations = new ushort[]
        {
            0x0100, 0x0200, 0x0300, 0x0400, 0x0600, 0x0700,
            0x0800, 0x0900, 0x0A00, 0x0B00, 0x0C00, 0x0D00,
            0x0E00, 0x0F00, 0x1000, 0x1100, 0x1200, 0x1300,
            0x1400, 0x1900, 0x1A00, 0x1B00, 0x1E00, 0x1F00,
            0x2000, 0x2100, 0x2200, 0x3000, 0x3100, 0x3200,
            0x3300, 0x3400, 0x3500, 0x3600, 0x3700, 0x6100,
        };
        var objects = operations
            .Select((operation, index) => (
                Type: (byte)0x03,
                Id: checked((uint)(0x4000 + index)),
                Body: BuildActionBody(operation)))
            .ToArray();
        var package = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0x4000, BuildBnk(objects)));
        var structure = package.BnkStructures[0];
        var expectedBytes = (uint)objects.Sum(item => item.Body.Length);
        if (structure.Type3ActionFrameCount != (uint)operations.Length
            || structure.Type3ActionExactCount != (uint)operations.Length
            || structure.Type3ActionUnsupportedCount != 0
            || structure.Type3ActionFailedCount != 0
            || structure.Type3ActionBodyBytes != expectedBytes
            || structure.Type3ActionExactCursorBytes != expectedBytes
            || structure.Type3ActionOperationCounts.Count != operations.Length)
        {
            throw new InvalidOperationException("type 0x03 supported operation cursor census mismatch");
        }
    }

    private static void TestType3ActionFramesFailClosed()
    {
        var truncatedHeader = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0x4100, BuildBnk((0x03, 0x4101U, Array.Empty<byte>()))))
            .BnkStructures[0];
        if (truncatedHeader.Type3ActionFailedCount != 1
            || !truncatedHeader.Type3ActionFailureCounts.ContainsKey("truncated_action_type"))
        {
            throw new InvalidOperationException("truncated type 0x03 action header was not rejected");
        }

        var malformedCountBody = new byte[]
        {
            0x00, 0x10, 0, 0, 0, 0, 0, // Action type, fixed header
            2, // Two property IDs are declared but neither fits.
        };
        var malformedCount = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0x4100, BuildBnk((0x03, 0x4102U, malformedCountBody))))
            .BnkStructures[0];
        if (!malformedCount.Type3ActionFailureCounts.ContainsKey("truncated_scalar_property_ids"))
        {
            throw new InvalidOperationException("malformed type 0x03 property count was not rejected");
        }

        var overrunExceptions = BuildActionBody(0x0100);
        overrunExceptions[^1] = 1;
        var exceptionOverrun = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0x4100, BuildBnk((0x03, 0x4103U, overrunExceptions))))
            .BnkStructures[0];
        if (!exceptionOverrun.Type3ActionFailureCounts.ContainsKey("exception_rows_out_of_range"))
        {
            throw new InvalidOperationException("out-of-range type 0x03 exception count was not rejected");
        }

        var validMultiByteExceptions = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(
                0x4100,
                BuildBnk((0x03, 0x4107U, BuildActionBodyWithExceptionRows(0x0100, new byte[] { 0x80, 0x01 }, 128)))))
            .BnkStructures[0];
        if (validMultiByteExceptions.Type3ActionExactCount != 1
            || validMultiByteExceptions.Type3ActionFailureCounts.Count != 0)
        {
            throw new InvalidOperationException("valid multi-byte type 0x03 exception count was not framed exactly");
        }

        var exceptionCountOverflow = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(
                0x4100,
                BuildBnk((0x03, 0x4108U, BuildActionBodyWithExceptionRows(
                    0x0100,
                    new byte[] { 0x80, 0x80, 0x80, 0x80, 0x10 },
                    0)))))
            .BnkStructures[0];
        if (!exceptionCountOverflow.Type3ActionFailureCounts.ContainsKey("exception_count_overflow"))
        {
            throw new InvalidOperationException("overflowing type 0x03 exception count was not rejected");
        }

        var unterminatedExceptionCount = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(
                0x4100,
                BuildBnk((0x03, 0x4109U, BuildActionBodyWithExceptionRows(
                    0x0100,
                    new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80 },
                    0)))))
            .BnkStructures[0];
        if (!unterminatedExceptionCount.Type3ActionFailureCounts.ContainsKey("unterminated_exception_count"))
        {
            throw new InvalidOperationException("unterminated five-byte type 0x03 exception count was not rejected");
        }

        var trailingBody = BuildActionBody(0x1000).Append((byte)0x7F).ToArray();
        var trailing = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0x4100, BuildBnk((0x03, 0x4104U, trailingBody))))
            .BnkStructures[0];
        if (!trailing.Type3ActionFailureCounts.ContainsKey("unexpected_trailing_bytes"))
        {
            throw new InvalidOperationException("trailing type 0x03 action byte was not rejected");
        }

        var unsupported = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0x4100, BuildBnk((0x03, 0x4105U, BuildActionBody(0x0500)))))
            .BnkStructures[0];
        if (unsupported.Type3ActionUnsupportedCount != 1
            || !unsupported.Type3ActionFailureCounts.ContainsKey("unsupported_operation"))
        {
            throw new InvalidOperationException("unknown type 0x03 operation was not kept unsupported");
        }

        var wrongVersion = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0x4100, BuildBnk(151, (0x03, 0x4106U, BuildActionBody(0x1000)))))
            .BnkStructures[0];
        if (wrongVersion.Type3ActionUnsupportedCount != 1
            || !wrongVersion.Type3ActionFailureCounts.ContainsKey("unsupported_bank_version"))
        {
            throw new InvalidOperationException("non-v150 type 0x03 action was not kept unsupported");
        }
    }

    private static byte[] BuildActionBody(ushort operation)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(operation);
        writer.Write(0U);
        writer.Write((byte)0);
        writer.Write((byte)0); // Scalar property count.
        writer.Write((byte)0); // Ranged property count.
        if (operation == 0x0400)
        {
            writer.Write(new byte[9]);
        }
        else if (operation is 0x1200 or 0x1900 or 0x6100)
        {
            writer.Write(new byte[8]);
        }
        else if (operation is 0x1300 or 0x1400)
        {
            writer.Write(new byte[15]);
            writer.Write((byte)0); // Empty exception list.
        }
        else if (operation is 0x0100 or 0x0200 or 0x0300)
        {
            writer.Write(new byte[2]);
            writer.Write((byte)0);
        }
        else if (operation is 0x0600 or 0x0700)
        {
            writer.Write((byte)0);
            writer.Write((byte)0);
        }
        else if (operation is 0x0800 or 0x0900 or 0x0A00 or 0x0B00 or 0x0C00
            or 0x0D00 or 0x0E00 or 0x0F00 or 0x2000 or 0x3000)
        {
            writer.Write(new byte[14]);
            writer.Write((byte)0);
        }
        else if (operation is 0x3100 or 0x3200)
        {
            writer.Write(new byte[7]);
            writer.Write((byte)0);
        }
        else if (operation is 0x3300 or 0x3400 or 0x3500 or 0x3600 or 0x3700)
        {
            writer.Write(new byte[2]);
            writer.Write((byte)0);
        }
        else if (operation is 0x1E00)
        {
            writer.Write(new byte[14]);
            writer.Write((byte)0);
        }
        else if (operation is 0x2200)
        {
            writer.Write((byte)0);
            writer.Write((byte)0);
        }
        return stream.ToArray();
    }

    private static byte[] BuildActionBodyWithExceptionRows(ushort operation, byte[] encodedCount, int rowCount)
    {
        var body = BuildActionBody(operation);
        if (body.Length == 0 || body[^1] != 0)
        {
            throw new InvalidOperationException("fixture action body does not end with an empty exception count");
        }
        return body[..^1]
            .Concat(encodedCount)
            .Concat(new byte[checked(rowCount * 5)])
            .ToArray();
    }

    private static void TestBankPayloadIsDecryptedAndFramed()
    {
        const uint bankId = 0x12345678;
        var plainBank = new byte[]
        {
            (byte)'B', (byte)'K', (byte)'H', (byte)'D',
            4, 0, 0, 0,
            150, 0, 0, 0,
        };
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

    private static byte[] BuildBnk(params (byte Type, uint Id, byte[] Body)[] objects)
    {
        return BuildBnk(150, objects);
    }

    private static byte[] BuildBnk(uint bankVersion, params (byte Type, uint Id, byte[] Body)[] objects)
    {
        using var hircStream = new MemoryStream();
        using (var hircWriter = new BinaryWriter(hircStream, Encoding.UTF8, true))
        {
            hircWriter.Write((uint)objects.Length);
            foreach (var item in objects)
            {
                hircWriter.Write(item.Type);
                hircWriter.Write(checked((uint)(4 + item.Body.Length)));
                hircWriter.Write(item.Id);
                hircWriter.Write(item.Body);
            }
        }

        using var bankStream = new MemoryStream();
        using (var writer = new BinaryWriter(bankStream, Encoding.UTF8, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("BKHD"));
            writer.Write(4U);
            writer.Write(bankVersion);
            writer.Write(Encoding.ASCII.GetBytes("HIRC"));
            writer.Write(checked((uint)hircStream.Length));
            writer.Write(hircStream.ToArray());
        }
        return bankStream.ToArray();
    }

    private static byte[] BuildEncryptedBankPackage(uint bankId, byte[] bank)
    {
        var encrypted = (byte[])bank.Clone();
        EndfieldAudioCrypto.DecryptVfs(encrypted, 0, encrypted.Length, bankId, 0);
        return BuildPackage(bankId, encrypted, bank: true, sound: false);
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
