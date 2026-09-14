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
        TestType4U32VectorFramesClassifyExactAndOpaqueBodies();
        TestType2BodyFramesConsumeSupportedBodiesExactly();
        TestType2BodyFramesFailClosed();
        TestType2BodyAmbiguousShapesStayUnsupported();
        TestType7BodyFramesReuseTheSharedNodeGroups();
        TestType7BodyFramesFailClosed();
        TestType5BodyFramesFrameTwoIndependentVectors();
        TestType5BodyFramesFailClosed();
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

    private static void TestType2BodyFramesConsumeSupportedBodiesExactly()
    {
        var minimal = BuildType2Body();
        var rich = BuildType2Body(
            groupAFlag: 1,
            groupAEntries: 2,
            groupCEntries: 3,
            groupDEntries: 1,
            groupESelector: 0x23,
            groupEVertices: 2,
            groupEItems: 1,
            groupFSelector: 0x0A,
            groupHProps: 2,
            groupHStates: 1,
            groupIEntries: 1,
            groupIPoints: 2);
        var package = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(
                0x6000,
                BuildBnk((0x02, 0x6001U, minimal), (0x02, 0x6002U, rich))));
        var structure = package.BnkStructures[0];
        var expectedBytes = (uint)(minimal.Length + rich.Length);
        if (structure.Type2Body.FrameCount != 2
            || structure.Type2Body.ExactCount != 2
            || structure.Type2Body.UnsupportedCount != 0
            || structure.Type2Body.FailedCount != 0
            || structure.Type2Body.BodyBytes != expectedBytes
            || structure.Type2Body.ExactCursorBytes != expectedBytes
            || structure.Type2Body.NonExactBytes != 0)
        {
            throw new InvalidOperationException("type 0x02 body frame census mismatch");
        }
        if (structure.Type2Body.MinExactBytes != (uint)minimal.Length
            || structure.Type2Body.MaxExactBytes != (uint)rich.Length)
        {
            throw new InvalidOperationException("type 0x02 body exact-length range mismatch");
        }
        if (structure.Type2Body.GroupCounts["groupAEntries"] != 2
            || structure.Type2Body.GroupCounts["groupCEntries"] != 3
            || structure.Type2Body.GroupCounts["groupDEntries"] != 1
            || structure.Type2Body.GroupCounts["groupEVertices"] != 2
            || structure.Type2Body.GroupCounts["groupEItems"] != 1
            || structure.Type2Body.GroupCounts["groupHProps"] != 2
            || structure.Type2Body.GroupCounts["groupHGroups"] != 1
            || structure.Type2Body.GroupCounts["groupHStates"] != 1
            || structure.Type2Body.GroupCounts["groupIEntries"] != 1
            || structure.Type2Body.GroupCounts["groupIPoints"] != 2)
        {
            throw new InvalidOperationException("type 0x02 body anonymous group inventory mismatch");
        }
        if (structure.Type2Body.SelectorCounts["groupESelector_23"] != 1
            || structure.Type2Body.SelectorCounts["groupEBranch_1"] != 1
            || structure.Type2Body.SelectorCounts["groupFSelector_0A"] != 1)
        {
            throw new InvalidOperationException("type 0x02 body selector inventory mismatch");
        }

        // No type 0x02 body in the current corpus carries a continued group I key, so
        // without this fixture the shared variable-size read would be unpinned on the
        // type 0x02 path and a regression there would be invisible.
        var continuedKey = BuildType2Body(groupIEntries: 1, groupIKey: new byte[] { 0x82, 0x30 });
        var continuedResult = FrameType2Fixture(continuedKey);
        if (continuedResult.Type2Body.ExactCount != 1
            || continuedResult.Type2Body.GroupCounts["groupIKeyBytes"] != 2
            || continuedResult.Type2Body.SelectorCounts["groupIKeyWidth_2"] != 1)
        {
            throw new InvalidOperationException("continued type 0x02 group I key was not consumed");
        }

        // The plug-in source prefix is length-prefixed and must be inside the cursor.
        var pluginBody = BuildType2Body(pluginId: 0x00650002, pluginParameterBytes: 6);
        var pluginPackage = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0x6100, BuildBnk((0x02, 0x6101U, pluginBody))));
        if (pluginPackage.BnkStructures[0].Type2Body.ExactCount != 1
            || pluginPackage.BnkStructures[0].Type2Body.ExactCursorBytes != (uint)pluginBody.Length)
        {
            throw new InvalidOperationException("type 0x02 plug-in source body did not consume exactly");
        }
    }

    private static void TestType2BodyFramesFailClosed()
    {
        var body = BuildType2Body();

        var truncated = FrameType2Fixture(body[..^1]);
        if (truncated.Type2Body.FailedCount != 1
            || !truncated.Type2Body.FailureCounts.ContainsKey("truncated_groupICount")
            || truncated.Type2Body.NonExactBytes != (uint)(body.Length - 1))
        {
            throw new InvalidOperationException("truncated type 0x02 body did not fail closed");
        }

        var trailing = FrameType2Fixture(body.Concat(new byte[] { 0x7F }).ToArray());
        if (trailing.Type2Body.FailedCount != 1
            || !trailing.Type2Body.FailureCounts.ContainsKey("trailing_bytes"))
        {
            throw new InvalidOperationException("trailing type 0x02 bytes were not reported");
        }

        // Malformed count: an entry count that cannot fit in the remaining body.
        var malformed = (byte[])body.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(malformed.Length - 2, 2), 0x0FFF);
        var malformedResult = FrameType2Fixture(malformed);
        if (malformedResult.Type2Body.FailedCount != 1
            || !malformedResult.Type2Body.FailureCounts.ContainsKey("range_groupIEntries"))
        {
            throw new InvalidOperationException("malformed type 0x02 entry count was not range-checked");
        }

        var pointOverrun = BuildType2Body(groupIEntries: 1, groupIPoints: 1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            pointOverrun.AsSpan(pointOverrun.Length - 2 - 12, 2), 0x0FFF);
        if (!FrameType2Fixture(pointOverrun).Type2Body.FailureCounts.ContainsKey("range_groupIPoints"))
        {
            throw new InvalidOperationException("malformed type 0x02 point count was not range-checked");
        }

        const int groupECountOffsetFromEnd = 39;

        var vertexOverrun = BuildType2Body(groupESelector: 0x23, groupEVertices: 1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            vertexOverrun.AsSpan(vertexOverrun.Length - groupECountOffsetFromEnd, 4),
            0x4000_0000U);
        if (!FrameType2Fixture(vertexOverrun).Type2Body.FailureCounts.ContainsKey("range_groupEVertices"))
        {
            throw new InvalidOperationException("group E vertex count was not range-checked");
        }

        var itemOverrun = BuildType2Body(groupESelector: 0x23, groupEItems: 1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            itemOverrun.AsSpan(itemOverrun.Length - groupECountOffsetFromEnd, 4),
            0x4000_0000U);
        if (!FrameType2Fixture(itemOverrun).Type2Body.FailureCounts.ContainsKey("range_groupEItems"))
        {
            throw new InvalidOperationException("group E item count was not range-checked");
        }

        var wrongVersion = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0x6300, BuildBnk(149, (0x02, 0x6301U, body))));
        if (wrongVersion.BnkStructures[0].Type2Body.UnsupportedCount != 1
            || !wrongVersion.BnkStructures[0].Type2Body.UnsupportedCategories
                .ContainsKey("unsupported_bank_version"))
        {
            throw new InvalidOperationException("non-current bank version was not held unsupported");
        }
    }

    private static void TestType2BodyAmbiguousShapesStayUnsupported()
    {
        // The current corpus never carries a nonempty group B vector, so its element
        // width is unresolved and any nonempty vector must stay unsupported.
        var groupB = BuildType2Body();
        groupB[14 + 3] = 1;
        var groupBResult = FrameType2Fixture(groupB);
        if (groupBResult.Type2Body.UnsupportedCount != 1
            || !groupBResult.Type2Body.UnsupportedCategories.ContainsKey("unsupported_groupB_nonempty"))
        {
            throw new InvalidOperationException("nonempty group B was not held unsupported");
        }

        // Type 0x07 bodies carry group E selector 0x01 with no extension, which rules
        // out a bit-0 predicate. Selector 0x02 is still unobserved anywhere, so bit-1-only
        // and both-bits-set remain indistinguishable and must stay unsupported.
        var lowBitOnly = BuildType2Body();
        lowBitOnly[14 + 4 + 9 + 2] = 0x01;
        var lowBitResult = FrameType2Fixture(lowBitOnly);
        if (lowBitResult.Type2Body.ExactCount != 1 || lowBitResult.Type2Body.UnsupportedCount != 0)
        {
            throw new InvalidOperationException("group E selector 0x01 body did not consume exactly");
        }

        var ambiguous = BuildType2Body();
        ambiguous[14 + 4 + 9 + 2] = 0x02;
        var ambiguousResult = FrameType2Fixture(ambiguous);
        if (ambiguousResult.Type2Body.UnsupportedCount != 1
            || !ambiguousResult.Type2Body.UnsupportedCategories
                .ContainsKey("unsupported_groupE_selector"))
        {
            throw new InvalidOperationException("group E selector 0x02 was not held unsupported");
        }

        // Branch selector 3 never occurs in the current corpus.
        var branch = BuildType2Body(groupESelector: 0x03);
        branch[14 + 4 + 9 + 2] = 0x63;
        var branchResult = FrameType2Fixture(branch);
        if (branchResult.Type2Body.UnsupportedCount != 1
            || !branchResult.Type2Body.UnsupportedCategories.ContainsKey("unsupported_groupE_branch"))
        {
            throw new InvalidOperationException("unobserved group E branch was not held unsupported");
        }
    }

    private static EndfieldBnkStructure FrameType2Fixture(byte[] body)
    {
        return EndfieldAkpkPackage
            .Parse(BuildEncryptedBankPackage(0x6200, BuildBnk((0x02, 0x6201U, body))))
            .BnkStructures[0];
    }

    private static byte[] BuildType2Body(
        uint pluginId = 0x00040001,
        int pluginParameterBytes = 0,
        byte groupAFlag = 0,
        byte groupAEntries = 0,
        byte groupCEntries = 0,
        byte groupDEntries = 0,
        byte groupESelector = 0,
        uint groupEVertices = 0,
        uint groupEItems = 0,
        byte groupFSelector = 0,
        byte groupHProps = 0,
        byte groupHStates = 0,
        ushort groupHStateElements = 1,
        ushort groupIEntries = 0,
        ushort groupIPoints = 0,
        byte[]? groupIKey = null,
        uint? childEntries = null,
        bool writePrefix = true)
    {
        groupIKey ??= new byte[] { 0x00 };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        if (writePrefix)
        {
            writer.Write(pluginId);
            writer.Write(new byte[10]);
            if ((pluginId & 0x0F) == 2)
            {
                writer.Write(checked((uint)pluginParameterBytes));
                writer.Write(new byte[pluginParameterBytes]);
            }
        }
        writer.Write(groupAFlag);
        writer.Write(groupAEntries);
        if (groupAEntries > 0)
        {
            writer.Write((byte)0);
            writer.Write(new byte[groupAEntries * 6]);
        }
        writer.Write((byte)0); // Group B flag.
        writer.Write((byte)0); // Group B count.
        writer.Write(new byte[9]);
        writer.Write(groupCEntries);
        writer.Write(new byte[groupCEntries * 5]);
        writer.Write(groupDEntries);
        writer.Write(new byte[groupDEntries * 9]);
        writer.Write(groupESelector);
        if ((groupESelector & 0x03) == 0x03)
        {
            writer.Write((byte)0);
            if (((groupESelector >> 5) & 0x03) != 0)
            {
                writer.Write(new byte[5]);
                writer.Write(groupEVertices);
                writer.Write(new byte[checked((int)groupEVertices * 16)]);
                writer.Write(groupEItems);
                writer.Write(new byte[checked((int)groupEItems * 20)]);
            }
        }
        writer.Write(groupFSelector);
        if ((groupFSelector & 0x08) != 0)
        {
            writer.Write(new byte[16]);
        }
        writer.Write(new byte[4]);
        writer.Write(new byte[6]);
        writer.Write(groupHProps);
        writer.Write(new byte[groupHProps * 3]);
        writer.Write((byte)(groupHStates > 0 ? 1 : 0));
        if (groupHStates > 0)
        {
            writer.Write(new byte[5]);
            writer.Write(groupHStates);
            for (var state = 0; state < groupHStates; state++)
            {
                writer.Write(new byte[4]); // State key.
                writer.Write(groupHStateElements);
                writer.Write(new byte[groupHStateElements * 6]);
            }
        }
        writer.Write(groupIEntries);
        for (var i = 0; i < groupIEntries; i++)
        {
            writer.Write(new byte[6]);
            writer.Write(groupIKey); // One anonymous variable-size key.
            writer.Write(new byte[5]);
            writer.Write(groupIPoints);
            writer.Write(new byte[groupIPoints * 12]);
        }
        if (childEntries.HasValue)
        {
            writer.Write(childEntries.Value);
            writer.Write(new byte[checked((int)childEntries.Value * 4)]);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static void TestType7BodyFramesReuseTheSharedNodeGroups()
    {
        // A group H state carries its own counted element vector. Every state in the
        // current type 0x02, 0x05 and 0x07 corpora holds exactly one element, so a
        // multi-element state must be pinned by fixture or the width is untested.
        // Two elements is the shape that actually disproved the fixed width, so pin it
        // first and by name; three exercises the general case.
        var twoElementState = BuildType2Body(
            groupHStates: 1,
            groupHStateElements: 2,
            childEntries: 0,
            writePrefix: false);
        var twoElement = FrameType7Fixture(twoElementState);
        if (twoElement.Type7Body.ExactCount != 1
            || twoElement.Type7Body.GroupCounts["groupHStateElements"] != 2
            || twoElement.Type7Body.SelectorCounts["groupHStateWidth_18"] != 1)
        {
            throw new InvalidOperationException("two-element group H state was not consumed");
        }

        var multiElementState = BuildType2Body(
            groupHStates: 1,
            groupHStateElements: 3,
            childEntries: 0,
            writePrefix: false);
        var multiElement = FrameType7Fixture(multiElementState);
        if (multiElement.Type7Body.ExactCount != 1
            || multiElement.Type7Body.GroupCounts["groupHStates"] != 1
            || multiElement.Type7Body.GroupCounts["groupHStateElements"] != 3
            || multiElement.Type7Body.SelectorCounts["groupHStateWidth_24"] != 1)
        {
            throw new InvalidOperationException("multi-element group H state was not consumed");
        }

        // Wide states must bucket instead of minting a key per observed width.
        var wideState = BuildType2Body(
            groupHStates: 1,
            groupHStateElements: 12,
            childEntries: 0,
            writePrefix: false);
        if (FrameType7Fixture(wideState).Type7Body.SelectorCounts["groupHStateWidth_over_54"] != 1)
        {
            throw new InvalidOperationException("wide group H state was not bucketed");
        }

        // A lane that frames no state at all must still publish the element row.
        if (!FrameType7Fixture(BuildType2Body(childEntries: 0, writePrefix: false))
            .Type7Body.GroupCounts.ContainsKey("groupHStateElements"))
        {
            throw new InvalidOperationException("group H element counter was not seeded");
        }

        var emptyState = BuildType2Body(
            groupHStates: 1,
            groupHStateElements: 0,
            childEntries: 0,
            writePrefix: false);
        if (FrameType7Fixture(emptyState).Type7Body.SelectorCounts["groupHStateWidth_6"] != 1)
        {
            throw new InvalidOperationException("empty group H state was not consumed");
        }

        // Type 0x07 opens with the same node groups as type 0x02 and closes with one
        // counted vector of four-byte anonymous references.
        var minimal = BuildType2Body(childEntries: 0, writePrefix: false);
        var rich = BuildType2Body(
            groupAFlag: 1,
            groupAEntries: 1,
            groupCEntries: 2,
            groupESelector: 0x03,
            groupFSelector: 0x08,
            groupHProps: 1,
            groupHStates: 1,
            groupIEntries: 1,
            groupIPoints: 1,
            childEntries: 3,
            writePrefix: false);
        var package = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(
                0x7000,
                BuildBnk((0x07, 0x7001U, minimal), (0x07, 0x7002U, rich))));
        var structure = package.BnkStructures[0];
        var expectedBytes = (uint)(minimal.Length + rich.Length);
        if (structure.Type7Body.FrameCount != 2
            || structure.Type7Body.ExactCount != 2
            || structure.Type7Body.UnsupportedCount != 0
            || structure.Type7Body.FailedCount != 0
            || structure.Type7Body.BodyBytes != expectedBytes
            || structure.Type7Body.ExactCursorBytes != expectedBytes
            || structure.Type7Body.NonExactBytes != 0
            || structure.Type7Body.MinExactBytes != (uint)minimal.Length
            || structure.Type7Body.MaxExactBytes != (uint)rich.Length)
        {
            throw new InvalidOperationException("type 0x07 body frame census mismatch");
        }
        if (minimal.Length != 35)
        {
            throw new InvalidOperationException(
                $"type 0x07 minimum frame is {minimal.Length}, not the published 35");
        }
        if (structure.Type7Body.GroupCounts["childEntries"] != 3
            || structure.Type7Body.GroupCounts["groupAEntries"] != 1
            || structure.Type7Body.GroupCounts["groupIPoints"] != 1)
        {
            throw new InvalidOperationException("type 0x07 anonymous group inventory mismatch");
        }

        // A continued variable-size group I key must be consumed, not assumed one byte.
        var continuedKey = BuildType2Body(
            groupIEntries: 1,
            groupIKey: new byte[] { 0x82, 0x30 },
            childEntries: 1,
            writePrefix: false);
        var continued = EndfieldAkpkPackage
            .Parse(BuildEncryptedBankPackage(0x7100, BuildBnk((0x07, 0x7101U, continuedKey))))
            .BnkStructures[0];
        if (continued.Type7Body.ExactCount != 1
            || continued.Type7Body.GroupCounts["groupIKeyBytes"] != 2
            || continued.Type7Body.SelectorCounts["groupIKeyWidth_2"] != 1)
        {
            throw new InvalidOperationException("continued group I key was not consumed");
        }

        // Selector 0x01 carries no extension; the corpus disproves a bit-0 predicate.
        var lowSelector = BuildType2Body(groupESelector: 0x01, childEntries: 1, writePrefix: false);
        if (EndfieldAkpkPackage
            .Parse(BuildEncryptedBankPackage(0x7200, BuildBnk((0x07, 0x7201U, lowSelector))))
            .BnkStructures[0].Type7Body.ExactCount != 1)
        {
            throw new InvalidOperationException("group E selector 0x01 body did not consume exactly");
        }
    }

    private static void TestType7BodyFramesFailClosed()
    {
        var body = BuildType2Body(childEntries: 1, writePrefix: false);

        // The tail is a four-byte count plus one four-byte entry; cut into the count.
        var truncated = FrameType7Fixture(body[..^6]);
        if (truncated.Type7Body.FailedCount != 1
            || !truncated.Type7Body.FailureCounts.ContainsKey("truncated_childCount"))
        {
            throw new InvalidOperationException("truncated type 0x07 child count did not fail closed");
        }

        var shortVector = FrameType7Fixture(body[..^1]);
        if (shortVector.Type7Body.FailedCount != 1
            || !shortVector.Type7Body.FailureCounts.ContainsKey("range_childEntries"))
        {
            throw new InvalidOperationException("short type 0x07 child vector did not fail closed");
        }

        var trailing = FrameType7Fixture(body.Concat(new byte[] { 0x5A }).ToArray());
        if (trailing.Type7Body.FailedCount != 1
            || !trailing.Type7Body.FailureCounts.ContainsKey("trailing_bytes"))
        {
            throw new InvalidOperationException("trailing type 0x07 bytes were not reported");
        }

        var malformed = (byte[])body.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(malformed.Length - 8, 4), 0x4000_0000U);
        if (!FrameType7Fixture(malformed).Type7Body.FailureCounts.ContainsKey("range_childEntries"))
        {
            throw new InvalidOperationException("type 0x07 child count was not range-checked");
        }

        // A fifth key byte that still continues must be rejected, never silently truncated.
        var unterminated = BuildType2Body(
            groupIEntries: 1,
            groupIKey: new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80 },
            childEntries: 1,
            writePrefix: false);
        if (!FrameType7Fixture(unterminated).Type7Body.FailureCounts.ContainsKey("unterminated_groupIKey"))
        {
            throw new InvalidOperationException("unterminated group I key was not rejected");
        }

        // A fifth byte that both continues and exceeds 32 bits must report the
        // continuation first, so the two guards cannot be reordered unnoticed.
        var unterminatedAndWide = BuildType2Body(
            groupIEntries: 1,
            groupIKey: new byte[] { 0x80, 0x80, 0x80, 0x80, 0x90 },
            childEntries: 1,
            writePrefix: false);
        var wideResult = FrameType7Fixture(unterminatedAndWide);
        if (!wideResult.Type7Body.FailureCounts.ContainsKey("unterminated_groupIKey")
            || wideResult.Type7Body.FailureCounts.ContainsKey("overflow_groupIKey"))
        {
            throw new InvalidOperationException("continuation must outrank overflow on the fifth key byte");
        }

        var overflowing = BuildType2Body(
            groupIEntries: 1,
            groupIKey: new byte[] { 0x80, 0x80, 0x80, 0x80, 0x10 },
            childEntries: 1,
            writePrefix: false);
        if (!FrameType7Fixture(overflowing).Type7Body.FailureCounts.ContainsKey("overflow_groupIKey"))
        {
            throw new InvalidOperationException("overflowing group I key was not rejected");
        }

        // In a prefix-free body with one group H state: 4 flag/count bytes, 9 scalars,
        // empty group C and D counts, the two selectors, group F's scalar, group G,
        // the prop count, the group count, the 5-byte group header and the state count
        // put the state key at 35 and its element count at 39.
        const int stateKeyOffset = 35;
        const int stateElementCountOffset = 39;

        var stateOverrun = BuildType2Body(groupHStates: 1, childEntries: 0, writePrefix: false);
        BinaryPrimitives.WriteUInt16LittleEndian(
            stateOverrun.AsSpan(stateElementCountOffset, 2), 0x0FFF);
        if (!FrameType7Fixture(stateOverrun).Type7Body.FailureCounts.ContainsKey("range_groupHStateElements"))
        {
            throw new InvalidOperationException("group H state element count was not range-checked");
        }

        var truncatedState = BuildType2Body(groupHStates: 1, childEntries: 0, writePrefix: false);
        if (!FrameType7Fixture(truncatedState[..(stateKeyOffset + 2)])
            .Type7Body.FailureCounts.ContainsKey("truncated_groupHStateKey"))
        {
            throw new InvalidOperationException("truncated group H state key was not rejected");
        }

        if (!FrameType7Fixture(truncatedState[..(stateElementCountOffset + 1)])
            .Type7Body.FailureCounts.ContainsKey("truncated_groupHStateElementCount"))
        {
            throw new InvalidOperationException("truncated group H element count was not rejected");
        }

        // A key cut off at EOF must fail closed. The group I entry-count precheck only
        // reserves the 14-byte minimum per entry, so this is reachable only once an
        // earlier points-bearing entry has eaten the slack: two entries reserve 28
        // bytes, the first consumes 26, and the second is cut off right after its head.
        var twoEntries = BuildType2Body(
            groupIEntries: 2,
            groupIPoints: 1,
            childEntries: 0,
            writePrefix: false);
        const int nodeGroupsThroughGroupICount = 31;
        const int firstEntryBytes = 6 + 1 + 5 + 2 + 12;
        var truncatedKey = twoEntries[..(nodeGroupsThroughGroupICount + firstEntryBytes + 6)];
        if (!FrameType7Fixture(truncatedKey).Type7Body.FailureCounts.ContainsKey("truncated_groupIKey"))
        {
            throw new InvalidOperationException("truncated group I key was not rejected");
        }

        // Selector bit 1 alone is unobserved, so the branch predicate stays ambiguous.
        var ambiguous = BuildType2Body(childEntries: 1, writePrefix: false);
        ambiguous[4 + 9 + 2] = 0x02;
        var ambiguousResult = FrameType7Fixture(ambiguous);
        if (ambiguousResult.Type7Body.UnsupportedCount != 1
            || !ambiguousResult.Type7Body.UnsupportedCategories.ContainsKey("unsupported_groupE_selector"))
        {
            throw new InvalidOperationException("ambiguous group E selector was not held unsupported");
        }

        var wrongVersion = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0x7400, BuildBnk(149, (0x07, 0x7401U, body))));
        if (wrongVersion.BnkStructures[0].Type7Body.UnsupportedCount != 1
            || !wrongVersion.BnkStructures[0].Type7Body.UnsupportedCategories
                .ContainsKey("unsupported_bank_version"))
        {
            throw new InvalidOperationException("non-current bank version was not held unsupported");
        }
    }

    private static void TestType5BodyFramesFrameTwoIndependentVectors()
    {
        // Type 0x05 adds a fixed opaque block and two independently counted vectors.
        var minimal = BuildType5Body();
        var rich = BuildType5Body(
            groupCEntries: 2,
            groupESelector: 0x03,
            groupFSelector: 0x08,
            referenceEntries: 3,
            recordEntries: 2);
        var structure = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(
                0x8000,
                BuildBnk((0x05, 0x8001U, minimal), (0x05, 0x8002U, rich))))
            .BnkStructures[0];
        var expectedBytes = (uint)(minimal.Length + rich.Length);
        if (structure.Type5Body.FrameCount != 2
            || structure.Type5Body.ExactCount != 2
            || structure.Type5Body.UnsupportedCount != 0
            || structure.Type5Body.FailedCount != 0
            || structure.Type5Body.BodyBytes != expectedBytes
            || structure.Type5Body.ExactCursorBytes != expectedBytes
            || structure.Type5Body.NonExactBytes != 0
            || structure.Type5Body.MinExactBytes != (uint)minimal.Length)
        {
            throw new InvalidOperationException("type 0x05 body frame census mismatch");
        }
        // The two counts are independent, so the census must not conflate them.
        if (structure.Type5Body.GroupCounts["referenceEntries"] != 3
            || structure.Type5Body.GroupCounts["recordEntries"] != 2
            || structure.Type5Body.GroupCounts["referenceRecordCountMismatch"] != 1)
        {
            throw new InvalidOperationException("type 0x05 vector inventory mismatch");
        }
        // Pin the published minimum so the gate's floor cannot drift from the framer.
        if (minimal.Length != 61)
        {
            throw new InvalidOperationException(
                $"type 0x05 minimum frame is {minimal.Length}, not the published 61");
        }
    }

    private static void TestType5BodyFramesFailClosed()
    {
        var body = BuildType5Body(referenceEntries: 1, recordEntries: 1);

        // The tail is: 4-byte reference count, one 4-byte reference, a 2-byte record
        // count and one 8-byte record.
        var truncated = FrameType5Fixture(body[..^15]);
        if (truncated.Type5Body.FailedCount != 1
            || !truncated.Type5Body.FailureCounts.ContainsKey("truncated_referenceCount"))
        {
            throw new InvalidOperationException("truncated type 0x05 reference count did not fail closed");
        }

        var trailing = FrameType5Fixture(body.Concat(new byte[] { 0x3C }).ToArray());
        if (trailing.Type5Body.FailedCount != 1
            || !trailing.Type5Body.FailureCounts.ContainsKey("trailing_bytes"))
        {
            throw new InvalidOperationException("trailing type 0x05 bytes were not reported");
        }

        var referenceOverrun = (byte[])body.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(
            referenceOverrun.AsSpan(referenceOverrun.Length - 18, 4), 0x4000_0000U);
        if (!FrameType5Fixture(referenceOverrun).Type5Body.FailureCounts.ContainsKey("range_referenceEntries"))
        {
            throw new InvalidOperationException("type 0x05 reference count was not range-checked");
        }

        var recordOverrun = (byte[])body.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(
            recordOverrun.AsSpan(recordOverrun.Length - 10, 2), 0x0FFF);
        if (!FrameType5Fixture(recordOverrun).Type5Body.FailureCounts.ContainsKey("range_recordEntries"))
        {
            throw new InvalidOperationException("type 0x05 record count was not range-checked");
        }

        var missingHeader = FrameType5Fixture(body[..(body.Length - 18 - 12)]);
        if (!missingHeader.Type5Body.FailureCounts.ContainsKey("truncated_suffixHeader"))
        {
            throw new InvalidOperationException("truncated type 0x05 fixed block did not fail closed");
        }

        var wrongVersion = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0x8300, BuildBnk(149, (0x05, 0x8301U, body))));
        if (wrongVersion.BnkStructures[0].Type5Body.UnsupportedCount != 1
            || !wrongVersion.BnkStructures[0].Type5Body.UnsupportedCategories
                .ContainsKey("unsupported_bank_version"))
        {
            throw new InvalidOperationException("non-current bank version was not held unsupported");
        }
    }

    private static EndfieldBnkStructure FrameType5Fixture(byte[] body)
    {
        return EndfieldAkpkPackage
            .Parse(BuildEncryptedBankPackage(0x8200, BuildBnk((0x05, 0x8201U, body))))
            .BnkStructures[0];
    }

    private static byte[] BuildType5Body(
        byte groupCEntries = 0,
        byte groupESelector = 0,
        byte groupFSelector = 0,
        uint referenceEntries = 0,
        ushort recordEntries = 0)
    {
        var node = BuildType2Body(
            groupCEntries: groupCEntries,
            groupESelector: groupESelector,
            groupFSelector: groupFSelector,
            writePrefix: false);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(node);
        writer.Write(new byte[24]); // Fixed opaque block.
        writer.Write(referenceEntries);
        writer.Write(new byte[checked((int)referenceEntries * 4)]);
        writer.Write(recordEntries);
        writer.Write(new byte[recordEntries * 8]);
        writer.Flush();
        return stream.ToArray();
    }

    private static EndfieldBnkStructure FrameType7Fixture(byte[] body)
    {
        return EndfieldAkpkPackage
            .Parse(BuildEncryptedBankPackage(0x7300, BuildBnk((0x07, 0x7301U, body))))
            .BnkStructures[0];
    }

    private static void TestType4U32VectorFramesClassifyExactAndOpaqueBodies()
    {
        var exactVector = new byte[9];
        exactVector[0] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(exactVector.AsSpan(1, 4), 0x5001);
        BinaryPrimitives.WriteUInt32LittleEndian(exactVector.AsSpan(5, 4), 0x5002);
        var exact = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(
                0x5000,
                BuildBnk(
                    (0x04, 0x5101U, exactVector),
                    (0x04, 0x5102U, new byte[] { 0 }))));
        var exactStructure = exact.BnkStructures[0];
        if (exactStructure.Type4U32VectorFrameCount != 2
            || exactStructure.Type4U32VectorExactCount != 2
            || exactStructure.Type4U32VectorUnsupportedCount != 0
            || exactStructure.Type4U32VectorFailedCount != 0
            || exactStructure.Type4U32VectorEntryCount != 2
            || exactStructure.Type4U32VectorBodyBytes != 10
            || exactStructure.Type4U32VectorPrefixBytes != 10
            || exactStructure.Type4U32VectorExactCursorBytes != 10)
        {
            throw new InvalidOperationException("type 0x04 exact vector frame census mismatch");
        }

        var truncated = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(
                0x5200,
                BuildBnk((0x04, 0x5201U, new byte[] { 2, 1, 2, 3, 4 }))))
            .BnkStructures[0];
        if (truncated.Type4U32VectorFrameCount != 1
            || truncated.Type4U32VectorFailedCount != 1
            || !truncated.Type4U32VectorFailureCounts.ContainsKey("truncated_entries")
            || truncated.Type4U32VectorFailureCounts["truncated_entries"] != 1
            || truncated.Type4U32VectorFailedBodyBytes != 5)
        {
            throw new InvalidOperationException("truncated type 0x04 vector was not rejected");
        }

        var empty = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(
                0x5300,
                BuildBnk((0x04, 0x5301U, Array.Empty<byte>()))))
            .BnkStructures[0];
        if (empty.Type4U32VectorFailedCount != 1
            || !empty.Type4U32VectorFailureCounts.ContainsKey("truncated_count"))
        {
            throw new InvalidOperationException("empty type 0x04 body did not fail closed");
        }

        var trailing = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(
                0x5400,
                BuildBnk((0x04, 0x5401U, new byte[] { 1, 0x34, 0x12, 0, 0, 0xEE }))))
            .BnkStructures[0];
        if (trailing.Type4U32VectorUnsupportedCount != 1
            || trailing.Type4U32VectorExactCount != 0
            || !trailing.Type4U32VectorUnsupportedCategories.ContainsKey("opaque_tail_after_candidate_vector")
            || trailing.Type4U32VectorUnsupportedCategories["opaque_tail_after_candidate_vector"] != 1
            || trailing.Type4U32VectorPrefixBytes != 5
            || trailing.Type4U32VectorOpaqueTailBytes != 1
            || trailing.Type4U32VectorBodyBytes != 6
            || trailing.Type4U32VectorExactCursorBytes != 0)
        {
            throw new InvalidOperationException("type 0x04 trailing bytes were not kept opaque");
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
