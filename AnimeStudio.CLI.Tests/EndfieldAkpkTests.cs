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
        TestType14BodyFramesBothBranches();
        TestType14BodyFramesFailClosed();
        TestMusicHeadReferencesFollowTheDiscriminantByte();
        TestType11SourceRecordsAreCounted();
        TestType11TailEntriesAreCounted();
        TestType22BodyFramesReuseGroupI();
        TestType11BodiesFrame();
        TestType08BodiesFrameOrAreNamed();
        TestType08TailRecordsAreLocatedFromTheEnd();
        TestType08TailHeadWordsAreClassifiedAgainstAControl();
        TestType12SharesType08Layout();
        TestType08HeadWordIsNullOrResolved();
        TestType17FramesOrFencesTheTiedWidth();
        TestSmallTypesFrameExactly();
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


    private static byte[] BuildType14Body(
        byte headByte = 0,
        byte optionalBlockFlag = 0,
        (byte selector, ushort elements)[]? entries = null,
        ushort terminator = 0,
        int extraTrailingBytes = 0,
        int truncateBy = 0)
    {
        entries ??= new[] { ((byte)0x02, (ushort)2) };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(headByte);
        writer.Write(optionalBlockFlag);
        // Remainder of the fixed twenty-one byte head.
        writer.Write(new byte[19]);
        if (optionalBlockFlag == 1)
        {
            writer.Write(new byte[20]);
        }
        writer.Write((byte)entries.Length);
        foreach (var (selector, elements) in entries)
        {
            writer.Write(selector);
            writer.Write(elements);
            writer.Write(new byte[elements * 12]);
        }
        writer.Write(terminator);
        writer.Flush();
        var body = stream.ToArray();
        if (extraTrailingBytes > 0)
        {
            body = body.Concat(new byte[extraTrailingBytes]).ToArray();
        }
        if (truncateBy > 0)
        {
            body = body[..^truncateBy];
        }
        return body;
    }


    private static byte[] BuildMusicBody(byte discriminant, uint target, int length = 40)
    {
        var body = new byte[length];
        body[2] = discriminant;
        var offset = discriminant == 0 ? 9 : 5;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset, 4), target);
        return body;
    }






    private static void TestSmallTypesFrameExactly()
    {
        static byte[] TwoBlocks(byte first, byte second)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(first);
            writer.Write(new byte[first]);              // keys
            writer.Write(new byte[first * 4]);          // four-byte values
            writer.Write(second);
            writer.Write(new byte[second]);             // keys
            writer.Write(new byte[second * 8]);         // eight-byte values
            writer.Write(new byte[2]);
            writer.Flush();
            return stream.ToArray();
        }

        var pair = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7800, BuildBnk(
            ((byte)0x13, 0x7801U, TwoBlocks(3, 0)),
            ((byte)0x14, 0x7802U, TwoBlocks(7, 2))))).BnkStructures[0];
        if (pair.SmallTypes.Exact != 2 || pair.SmallTypes.Failed != 0
            || pair.SmallTypes.BodiesWithSecondBlock != 1
            || pair.SmallTypes.SecondBlockEntries != 2
            || pair.SmallTypes.BodiesByType["type13"] != 1
            || pair.SmallTypes.BodiesByType["type14"] != 1)
        {
            throw new InvalidOperationException("small types did not frame exactly");
        }

        // The second block's values are eight bytes, not four. A four-byte reading
        // would leave bytes over, so this is what pins the width.
        var wrongWidth = TwoBlocks(1, 1);
        if (EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7800, BuildBnk(
                ((byte)0x13, 0x7803U, wrongWidth[..^4])))).BnkStructures[0]
                .SmallTypes.Exact != 0)
        {
            throw new InvalidOperationException("small types accepted a four-byte second value");
        }

        // Numeric type 0x15 uses the 0x10/0x11 header plus eight bytes.
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write((ushort)7);
            writer.Write((ushort)0xAE);
            writer.Write(12U);
            writer.Write(new byte[12]);
            writer.Write(new byte[8]);
        }
        var header = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7800, BuildBnk(
            ((byte)0x15, 0x7804U, stream.ToArray())))).BnkStructures[0];
        if (header.SmallTypes.Exact != 1 || header.SmallTypes.BodiesByType["type15"] != 1)
        {
            throw new InvalidOperationException("type 0x15 did not frame exactly");
        }

        if (!EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7800, BuildBnk(
                ((byte)0x13, 0x7805U, TwoBlocks(1, 1).Concat(new byte[] { 0 }).ToArray()))))
                .BnkStructures[0].SmallTypes.FailureCounts.ContainsKey("trailing_bytes"))
        {
            throw new InvalidOperationException("small types accepted trailing bytes");
        }
    }

    private static void TestType17FramesOrFencesTheTiedWidth()
    {
        static byte[] Build(ushort flag, ushort runCount, int sectionBytes)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write((ushort)3);
            writer.Write((ushort)0x69);
            writer.Write((uint)sectionBytes);
            writer.Write(new byte[sectionBytes]);
            writer.Write((byte)0);
            writer.Write((ushort)0);          // group I: no entries
            writer.Write(flag);
            if (flag == 0)
            {
                writer.Write(runCount);
                writer.Write(new byte[runCount * 6]);
            }
            writer.Flush();
            return stream.ToArray();
        }

        var exact = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7700, BuildBnk(
            ((byte)0x11, 0x7701U, Build(0, 2, 8))))).BnkStructures[0];
        if (exact.Type17.Exact != 1 || exact.Type17.RunElements != 2
            || exact.Type17.Failed != 0 || exact.Type17.Fenced != 0
            || exact.Type17.BodiesByType["type11"] != 1)
        {
            throw new InvalidOperationException("type 0x11 did not frame a clear body");
        }

        // A set flag means two block widths fit and nothing separates them, so the
        // body must be fenced rather than framed on a guess -- and fencing is not a
        // failure, which the counters have to keep distinct.
        var tied = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7700, BuildBnk(
            ((byte)0x11, 0x7702U, Build(1, 0, 8))))).BnkStructures[0];
        if (tied.Type17.Fenced != 1 || tied.Type17.Exact != 0
            || tied.Type17.Failed != 0
            || tied.Type17.FenceReasons["tiedOptionalBlockWidth"] != 1)
        {
            throw new InvalidOperationException("type 0x11 mishandled the tied width");
        }


        // Type 0x10 shares the grammar, so it goes through the same reader; only its
        // 0x7F variant is fenced, under its own reason rather than the tied-width one.
        var shared = Build(0, 2, 8);
        var sharedOk = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7700, BuildBnk(
            ((byte)0x10, 0x7705U, shared)))).BnkStructures[0];
        if (sharedOk.Type17.Exact != 1 || sharedOk.Type17.BodiesByType["type10"] != 1)
        {
            throw new InvalidOperationException("type 0x10 did not reuse the 0x11 grammar");
        }

        var variant = Build(0, 2, 8);
        variant[2] = 0x7F;
        var fenced = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7700, BuildBnk(
            ((byte)0x10, 0x7706U, variant)))).BnkStructures[0];
        if (fenced.Type17.Fenced != 1 || fenced.Type17.Exact != 0
            || fenced.Type17.FenceReasons["type10_variant7F"] != 1)
        {
            throw new InvalidOperationException("type 0x10 did not fence its 0x7F variant");
        }

        // The same third byte on type 0x11 must not be fenced: the variant belongs to
        // 0x10 alone, and a rule applied to the wrong type would silently lose bodies.
        var notAVariant = Build(0, 2, 8);
        notAVariant[2] = 0x7F;
        if (EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7700, BuildBnk(
                ((byte)0x11, 0x7707U, notAVariant)))).BnkStructures[0].Type17.Exact != 1)
        {
            throw new InvalidOperationException("type 0x11 was fenced by a type 0x10 rule");
        }

        var overrun = Build(0, 1, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(overrun.AsSpan(4, 4), 9999);
        var bad = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7700, BuildBnk(
            ((byte)0x11, 0x7703U, overrun)))).BnkStructures[0];
        if (!bad.Type17.FailureCounts.ContainsKey("range_section"))
        {
            throw new InvalidOperationException("type 0x11 accepted an impossible section size");
        }

        var trailing = Build(0, 1, 8).Concat(new byte[] { 0x00 }).ToArray();
        if (!EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7700, BuildBnk(
                ((byte)0x11, 0x7704U, trailing)))).BnkStructures[0]
                .Type17.FailureCounts.ContainsKey("trailing_bytes"))
        {
            throw new InvalidOperationException("type 0x11 accepted trailing bytes");
        }
    }

    private static void TestType08HeadWordIsNullOrResolved()
    {
        const uint target = 0x7601U;
        var targetBody = BuildType2Body();
        static byte[] Head(uint word)
        {
            var body = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), word);
            return body;
        }

        var named = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7600, BuildBnk(
            ((byte)0x02, target, targetBody),
            ((byte)0x08, 0x7602U, Head(target))))).BnkStructures[0];
        if (named.Type08Head.Bodies != 1 || named.Type08Head.Resolved != 1)
        {
            throw new InvalidOperationException("type 0x08 head word did not resolve");
        }

        // Null is a real outcome for this type, not a failure.
        var nulled = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7600, BuildBnk(
            ((byte)0x02, target, targetBody),
            ((byte)0x08, 0x7603U, Head(0))))).BnkStructures[0];
        if (nulled.Type08Head.Null != 1 || nulled.Type08Head.Resolved != 0
            || nulled.Type08Head.Unresolved != 0)
        {
            throw new InvalidOperationException("type 0x08 mistook a null for a reference");
        }

        // A non-null word naming nothing is the case the claim forbids, so it must be
        // visible rather than folded into either side.
        var stranger = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7600, BuildBnk(
            ((byte)0x02, target, targetBody),
            ((byte)0x08, 0x7604U, Head(0xDEADBEEF))))).BnkStructures[0];
        if (stranger.Type08Head.Unresolved != 1 || stranger.Type08Head.Null != 0)
        {
            throw new InvalidOperationException("type 0x08 hid an unresolved head word");
        }

        var tiny = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7600, BuildBnk(
            ((byte)0x08, 0x7605U, new byte[2])))).BnkStructures[0];
        if (tiny.Type08Head.TooShort != 1)
        {
            throw new InvalidOperationException("type 0x08 dropped a short body");
        }
    }

    private static void TestType22BodyFramesReuseGroupI()
    {
        static byte[] Build(byte[] keys, uint[] values, ushort groupIEntries, ushort points)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write((byte)keys.Length);
            writer.Write(keys);
            foreach (var value in values)
            {
                writer.Write(value);
            }
            writer.Write((byte)0);
            writer.Write(groupIEntries);
            for (var i = 0; i < groupIEntries; i++)
            {
                writer.Write(new byte[6]);
                writer.Write((byte)0x13);
                writer.Write(new byte[5]);
                writer.Write(points);
                writer.Write(new byte[points * 12]);
            }
            writer.Flush();
            return stream.ToArray();
        }

        // Keys and values are parallel runs, so one key and one value is nine bytes:
        // count, key, value, the anonymous byte, and an empty group I count.
        var empty = FrameType22Fixture(Build(new byte[] { 0x11 }, new uint[] { 1 }, 0, 0));
        if (empty.Type22Body.ExactCount != 1
            || empty.Type22Body.ExactCursorBytes != 9
            || empty.Type22Body.GroupCounts["propertyEntries"] != 1
            || empty.Type22Body.SelectorCounts["propertyKey_11"] != 1)
        {
            throw new InvalidOperationException("type 0x16 empty body did not frame exactly");
        }

        var withGroupI = FrameType22Fixture(
            Build(new byte[] { 0x00, 0x11 }, new uint[] { 1, 2 }, 1, 1));
        if (withGroupI.Type22Body.ExactCount != 1
            || withGroupI.Type22Body.GroupCounts["groupIEntries"] != 1
            || withGroupI.Type22Body.GroupCounts["groupIPoints"] != 1
            || withGroupI.Type22Body.SelectorCounts["groupIKeyWidth_1"] != 1)
        {
            throw new InvalidOperationException("type 0x16 did not reuse group I");
        }

        // A property count that cannot fit both runs must be rejected on its own
        // terms rather than consuming whatever bytes remain.
        var overrun = Build(new byte[] { 0x00 }, new uint[] { 1 }, 0, 0);
        overrun[0] = 0xFF;
        if (!FrameType22Fixture(overrun).Type22Body.FailureCounts.ContainsKey("range_properties"))
        {
            throw new InvalidOperationException("type 0x16 accepted an impossible property count");
        }

        var trailing = Build(new byte[] { 0x00 }, new uint[] { 1 }, 0, 0)
            .Concat(new byte[] { 0x00 }).ToArray();
        if (!FrameType22Fixture(trailing).Type22Body.FailureCounts.ContainsKey("trailing_bytes"))
        {
            throw new InvalidOperationException("type 0x16 accepted trailing bytes");
        }

        var truncated = Build(new byte[] { 0x00 }, new uint[] { 1 }, 0, 0);
        if (FrameType22Fixture(truncated[..^1]).Type22Body.ExactCount != 0)
        {
            throw new InvalidOperationException("type 0x16 accepted a truncated body");
        }
    }

    // Numeric type 0x0B's body frame: every run in it is a count the body declares,
    // so a synthetic body built from those counts must close, and a body whose
    // counts are wrong must not.
    private static void TestType11BodiesFrame()
    {
        static byte[] Element(int runs, int recordsPerRun, byte trailerFlag)
        {
            var bytes = new List<byte> { (byte)runs, 0, 0, 0, 0 };
            for (var run = 0; run < runs; run++)
            {
                var header = new byte[12];
                header[7] = (byte)recordsPerRun;
                bytes.AddRange(header);
                bytes.AddRange(new byte[recordsPerRun * 12]);
            }
            bytes.AddRange(new byte[12]);
            bytes.Add(trailerFlag);
            bytes.AddRange(new byte[19 + 5 * trailerFlag - 1]);
            return bytes.ToArray();
        }

        static byte[] Body(int sources, int entries, int elementsPerEntry, int runs,
            int recordsPerRun, byte trailerFlag)
        {
            var bytes = new List<byte> { 0x00 };
            bytes.AddRange(BitConverter.GetBytes((uint)sources));
            bytes.AddRange(new byte[sources * 14]);
            bytes.AddRange(BitConverter.GetBytes((uint)entries));
            for (var entry = 0; entry < entries; entry++)
            {
                var header = new byte[48];
                BitConverter.GetBytes((uint)elementsPerEntry).CopyTo(header, 44);
                bytes.AddRange(header);
                for (var element = 0; element < elementsPerEntry; element++)
                {
                    bytes.AddRange(Element(runs, recordsPerRun, trailerFlag));
                }
            }
            bytes.AddRange(BitConverter.GetBytes(100u));
            return bytes.ToArray();
        }

        static EndfieldHircBodyFrameResult Frame(byte[] body) =>
            EndfieldAkpkPackage.FrameType11Body(body, 150);

        // The shapes the corpus actually contains: no runs, one run, several runs,
        // several elements, several entries, and each observed trailer flag.
        foreach (var body in new[]
        {
            Body(1, 1, 1, 0, 0, 0),
            Body(1, 1, 1, 1, 2, 0),
            Body(2, 1, 1, 2, 3, 1),
            Body(1, 1, 3, 1, 1, 0),
            Body(1, 3, 1, 1, 1, 2),
            Body(0, 0, 0, 0, 0, 0),
        })
        {
            if (Frame(body).Status != "exact")
            {
                throw new InvalidOperationException(
                    $"type 0x0B refused a body built from its own counts: {Frame(body).Category}");
            }
        }

        // A trailer flag above the highest observed one is refused rather than
        // assumed to continue the 19 + 5 * flag line.
        var wildFlag = Body(1, 1, 1, 1, 1, 0);
        // The flag sits 23 bytes from the end: its own byte, the 18 trailer bytes a
        // flag of zero asks for, and the 4-byte terminator.
        wildFlag[wildFlag.Length - 23] = 0x09;
        if (Frame(wildFlag).Status == "exact")
        {
            throw new InvalidOperationException("type 0x0B accepted an unobserved trailer flag");
        }
        // A missing terminator is refused before any entry is walked.
        var noTerminator = Body(1, 1, 1, 1, 1, 0);
        noTerminator[noTerminator.Length - 4] = 0x63;
        if (Frame(noTerminator).Category != "terminator_is_not_the_observed_value")
        {
            throw new InvalidOperationException("type 0x0B framed a body with no terminator");
        }
        // Trailing bytes between the last entry and the terminator are a failure, not
        // something to absorb -- and the two kinds are named apart, because 104 bodies
        // leave a short run of zeros there and 218 leave real content.
        var trailingContent = Body(1, 1, 1, 1, 1, 0).ToList();
        trailingContent.InsertRange(trailingContent.Count - 4, new byte[] { 1, 2, 3, 4, 5, 6 });
        if (Frame(trailingContent.ToArray()).Category != "entries_do_not_reach_the_terminator")
        {
            throw new InvalidOperationException("type 0x0B absorbed content before its terminator");
        }
        var trailingZeros = Body(1, 1, 1, 1, 1, 0).ToList();
        trailingZeros.InsertRange(trailingZeros.Count - 4, new byte[6]);
        if (Frame(trailingZeros.ToArray()).Category != "trailing_zero_run_before_the_terminator")
        {
            throw new InvalidOperationException("type 0x0B did not name a trailing zero run");
        }
        // A count that cannot fit is refused rather than clamped.
        var wildEntries = Body(1, 1, 1, 1, 1, 0);
        wildEntries[19] = 0xFF;
        if (Frame(wildEntries).Status == "exact")
        {
            throw new InvalidOperationException("type 0x0B accepted an impossible entry count");
        }
        // A truncated body fails at the part that ran out, not silently.
        var full = Body(1, 1, 1, 2, 2, 1);
        for (var cut = 10; cut < full.Length; cut += 7)
        {
            if (Frame(full[..cut]).Status == "exact")
            {
                throw new InvalidOperationException("type 0x0B framed a truncated body");
            }
        }
    }

    private static void TestType08BodiesFrameOrAreNamed()
    {
        // A reference, the counted key/value block, a one-entry list whose key sizes
        // its value, the nine-byte signature, a zero word, a counted entry run and
        // five zero bytes.
        static byte[] Build(byte[] keys, byte secondKey, int secondWidth, byte entries,
                            byte secondCount = 1, uint afterSignature = 0,
                            int trailer = 5, byte[]? signature = null,
                            byte[]? trailerBytes = null, byte[]? afterSignatureBytes = null)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(0x1234U);
            writer.Write((byte)keys.Length);
            writer.Write(keys);
            for (var i = 0; i < keys.Length; i++)
            {
                writer.Write(0U);
            }
            writer.Write(secondCount);
            writer.Write(secondKey);
            writer.Write(new byte[secondWidth]);
            writer.Write(signature ?? new byte[] { 0x02, 0xE8, 0x03, 0x00, 0x00, 0x00, 0x00, 0xC0, 0xC2 });
            writer.Write(afterSignature);
            if (afterSignatureBytes != null)
            {
                writer.Write(afterSignatureBytes);
            }
            writer.Write(entries);
            if (entries != 0)
            {
                writer.Write(new byte[entries * 6 + 1]);
            }
            writer.Write(trailerBytes ?? new byte[trailer]);
            writer.Flush();
            return stream.ToArray();
        }

        static EndfieldHircBodyCensus Frame(byte[] body) => EndfieldAkpkPackage
            .Parse(BuildEncryptedBankPackage(0x7600, BuildBnk(((byte)0x08, 0x7601U, body))))
            .BnkStructures[0].Type08Body;

        var short08 = Frame(Build(new byte[] { 0x1B, 0x3F }, 0x15, 11, 0));
        if (short08.ExactCount != 1
            || short08.SelectorCounts["secondListKey_15"] != 1
            || short08.GroupCounts["propertyEntries"] != 2
            || short08.GroupCounts["entryRunElements"] != 0)
        {
            throw new InvalidOperationException("type 0x08 short body did not frame exactly");
        }

        // The other observed key is sixteen bytes wider, and the entry run carries one
        // extra byte only when its count is nonzero. Both are layout, not tolerance.
        var long08 = Frame(Build(new byte[] { 0x1B }, 0x1D, 27, 3));
        if (long08.ExactCount != 1 || long08.GroupCounts["entryRunElements"] != 3)
        {
            throw new InvalidOperationException("type 0x08 long body did not frame exactly");
        }

        // A key whose width is not observed is held unsupported rather than walked
        // with a guessed width, because guessing one would frame arbitrary bytes.
        var unknownKey = Frame(Build(new byte[] { 0x1B }, 0x44, 11, 0));
        if (unknownKey.ExactCount != 0
            || unknownKey.UnsupportedCategories["unsupported_second_list_key"] != 1)
        {
            throw new InvalidOperationException("type 0x08 accepted an unobserved list key");
        }

        // The nine bytes before the zero word are a field, not a signature: numeric
        // type 0x08 carries three different 32-bit values there and numeric type 0x12 a
        // fourth. A body whose nine bytes differ must frame, and the value must be
        // published so the variation stays visible instead of being asserted away.
        var otherMiddle = Frame(Build(
            new byte[] { 0x1B }, 0x15, 11, 0,
            signature: new byte[] { 0x02, 0xF4, 0x01, 0x00, 0x00, 0x00, 0x00, 0xC0, 0xC2 }));
        if (otherMiddle.ExactCount != 1
            || otherMiddle.SelectorCounts["middleBlock_02_500"] != 1)
        {
            throw new InvalidOperationException("type 0x08 refused a different middle block");
        }

        // Each of the remaining refusals must be reported under its own reason: a
        // single pooled failure would hide which part of the layout the body broke.
        // The word after the middle block is a count, not a constant. It reads as
        // zero in 396 of the 412 bodies of both types, which is exactly why it looked
        // like one; a body declaring elements must frame, not fail.
        var middleRun = Frame(Build(
            new byte[] { 0x1B }, 0x15, 11, 0, afterSignature: 2, afterSignatureBytes: new byte[36]));
        if (middleRun.ExactCount != 1 || middleRun.GroupCounts["middleRunElements"] != 2)
        {
            throw new InvalidOperationException("type 0x08 refused a nonempty middle run");
        }
        // A count that cannot fit is refused rather than clamped to the bytes present.
        var wildRun = Frame(Build(new byte[] { 0x1B }, 0x15, 11, 0, afterSignature: 9));
        if (wildRun.FailureCounts["range_middle_run"] != 1)
        {
            throw new InvalidOperationException("type 0x08 accepted an impossible middle run");
        }
        // A body that neither ends on the five zero bytes nor carries a whole tail
        // block is refused under the tail block's own reason, not the trailer's: which
        // part of the layout failed has to stay visible.
        var badTrailer = Frame(Build(new byte[] { 0x1B }, 0x15, 11, 0, trailer: 9));
        if (badTrailer.FailureCounts["tail_block_does_not_end_the_body"] != 1)
        {
            throw new InvalidOperationException("type 0x08 framed a body with the wrong trailer");
        }
        // A unit count above the observed ceiling is still refused. Zero is not: a
        // block with no units at all carries only its closing section, and ten bodies
        // are shaped that way.
        var wildUnits = Frame(Build(
            new byte[] { 0x1B }, 0x15, 11, 0,
            trailerBytes: new byte[] { 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00 }));
        if (wildUnits.FailureCounts["range_tail_block_units"] != 1)
        {
            throw new InvalidOperationException("type 0x08 accepted an impossible unit count");
        }

        // The tail block: a zero byte, a unit count, a zero byte, then that many units
        // of twelve head bytes plus their own counted twelve-byte records, then a zero
        // sixteen-bit word. A block with several units is what shows the count is a
        // count -- a single-unit block is indistinguishable from a fixed head.
        static byte[] TailBlock(int units, int recordsPerUnit)
        {
            var bytes = new byte[3 + units * (12 + 2 + recordsPerUnit * 12) + 2];
            bytes[1] = (byte)units;
            for (var i = 0; i < units; i++)
            {
                var at = 3 + i * (12 + 2 + recordsPerUnit * 12);
                bytes[at + 5] = 0x02;
                bytes[at + 6] = (byte)i;
                bytes[at + 11] = 0x02;
                bytes[at + 12] = (byte)recordsPerUnit;
            }
            return bytes;
        }

        // The section that closes a tail block. When its entry count is zero the
        // section is absent and one byte closes the body -- those two bytes are what
        // the previous reading hardcoded as a fixed closing word, which is why it
        // framed the 64 bodies whose section is absent and none of the 18 whose is
        // not.
        static byte[] TailSection(int entries, int wideEntries, int records, int valuesPerRecord)
        {
            var body = new List<byte> { 0x00, 0x00, 0x00, (byte)(entries + wideEntries) };
            for (var i = 0; i < entries; i++)
            {
                body.AddRange(new byte[] { (byte)i, 0x02, 0x01 });
            }
            for (var i = 0; i < wideEntries; i++)
            {
                body.AddRange(new byte[] { 0x82, 0x30, 0x04, 0x00 });
            }
            body.AddRange(new byte[] { 0x01, 0x11, 0x22, 0x33, 0x44, 0x00, (byte)records });
            for (var i = 0; i < records; i++)
            {
                body.AddRange(new byte[] { 0x01, 0x02, 0x03, 0x04, (byte)valuesPerRecord, 0x00 });
                body.AddRange(new byte[valuesPerRecord * 2]);
                body.AddRange(new byte[valuesPerRecord * 4]);
            }
            return body.ToArray();
        }

        var section = Frame(Build(
            new byte[] { 0x1B }, 0x15, 11, 0, trailerBytes: TailSection(5, 0, 4, 1)));
        if (section.ExactCount != 1 || section.GroupCounts["tailSectionRecords"] != 4)
        {
            throw new InvalidOperationException("type 0x08 refused a tail section");
        }
        // The entry width is chosen by the high bit of the entry's first byte. A body
        // mixing both widths is what shows the rule is a rule and not a constant.
        var wideSection = Frame(Build(
            new byte[] { 0x1B }, 0x15, 11, 0, trailerBytes: TailSection(5, 2, 3, 1)));
        if (wideSection.ExactCount != 1 || wideSection.GroupCounts["tailSectionWideEntries"] != 2)
        {
            throw new InvalidOperationException("type 0x08 refused a wide tail section entry");
        }
        // The record carries as many 16-bit values as it declares, then that many
        // floats. Every record but two declares one, which is exactly why the record
        // looked like a fixed twelve bytes.
        var twoValues = Frame(Build(
            new byte[] { 0x1B }, 0x15, 11, 0, trailerBytes: TailSection(1, 0, 1, 2)));
        if (twoValues.ExactCount != 1 || twoValues.GroupCounts["tailSectionRecordValues"] != 2)
        {
            throw new InvalidOperationException("type 0x08 refused a two-value tail record");
        }
        // A record declaring no values, or more than were ever observed, is refused
        // rather than read as an empty or enormous run.
        var zeroValues = TailSection(1, 0, 1, 1);
        zeroValues[zeroValues.Length - 8] = 0x00;
        if (Frame(Build(new byte[] { 0x1B }, 0x15, 11, 0, trailerBytes: zeroValues))
                .FailureCounts["range_tail_section_record_values"] != 1)
        {
            throw new InvalidOperationException("type 0x08 accepted a record with no values");
        }
        // A section whose records run past the body is refused, not truncated.
        var truncated = TailSection(2, 0, 2, 1);
        if (Frame(Build(
                new byte[] { 0x1B }, 0x15, 11, 0,
                trailerBytes: truncated[..(truncated.Length - 6)]))
                .ExactCount != 0)
        {
            throw new InvalidOperationException("type 0x08 framed a truncated tail section");
        }

        var oneUnit = Frame(Build(new byte[] { 0x1B }, 0x15, 11, 0, trailerBytes: TailBlock(1, 1)));
        var threeUnits = Frame(Build(new byte[] { 0x1B }, 0x15, 11, 0, trailerBytes: TailBlock(3, 2)));
        if (oneUnit.ExactCount != 1
            || oneUnit.GroupCounts["tailBlockUnits"] != 1
            || oneUnit.GroupCounts["tailBlockRecords"] != 1
            || threeUnits.ExactCount != 1
            || threeUnits.GroupCounts["tailBlockUnits"] != 3
            || threeUnits.GroupCounts["tailBlockRecords"] != 6
            || threeUnits.SelectorCounts["tailBlockUnitSelector_020202"] != 1)
        {
            throw new InvalidOperationException("type 0x08 did not frame its tail block units");
        }

        // The bytes that are zero in every body of both types are checked; a body that
        // breaks one is refused rather than framed with a shape that does not apply.
        var brokenPrefix = TailBlock(1, 1);
        brokenPrefix[0] = 9;
        var refusedPrefix = Frame(Build(
            new byte[] { 0x1B }, 0x15, 11, 0, trailerBytes: brokenPrefix));
        if (refusedPrefix.FailureCounts["tail_block_prefix_is_not_the_observed_shape"] != 1)
        {
            throw new InvalidOperationException("type 0x08 framed an unobserved tail-block prefix");
        }
        var brokenUnit = TailBlock(1, 1);
        brokenUnit[3 + 4] = 9;
        var refusedUnit = Frame(Build(
            new byte[] { 0x1B }, 0x15, 11, 0, trailerBytes: brokenUnit));
        if (refusedUnit.FailureCounts["tail_block_unit_is_not_the_observed_shape"] != 1)
        {
            throw new InvalidOperationException("type 0x08 framed an unobserved tail-block unit");
        }
        // The second list's count is published, not constrained. Numeric type 0x08's
        // bodies all declare one and numeric type 0x12's declare three with key 0x0A,
        // so a corpus where each type is uniform cannot make the count a rule.
        var otherCount = Frame(Build(new byte[] { 0x1B }, 0x15, 11, 0, secondCount: 5));
        if (otherCount.ExactCount != 1 || otherCount.SelectorCounts["secondListCount_5"] != 1)
        {
            throw new InvalidOperationException("type 0x08 constrained its second-list count");
        }
    }

    private static void TestType08TailRecordsAreLocatedFromTheEnd()
    {
        // A framed type 0x08 head, then a tail: unexplained bytes, a record count,
        // one byte, that many twelve-byte records, and a zero sixteen-bit word.
        static byte[] Build(byte[] headBytes, byte records, uint code, bool zeroWord = true)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(0x1234U);
            writer.Write((byte)1);
            writer.Write((byte)0x1B);
            writer.Write(0U);
            writer.Write((byte)1);
            writer.Write((byte)0x15);
            writer.Write(new byte[11]);
            writer.Write(new byte[] { 0x02, 0xE8, 0x03, 0x00, 0x00, 0x00, 0x00, 0xC0, 0xC2 });
            writer.Write(0U);
            writer.Write((byte)0);
            writer.Write(headBytes);
            writer.Write(records);
            writer.Write((byte)0);
            for (var i = 0; i < records; i++)
            {
                writer.Write(0.0f);
                writer.Write(1.0f);
                writer.Write(code);
            }
            writer.Write((ushort)(zeroWord ? 0 : 7));
            writer.Flush();
            return stream.ToArray();
        }

        static EndfieldHircType08TailCensus Census(byte[] body) => EndfieldAkpkPackage
            .Parse(BuildEncryptedBankPackage(0x7700, BuildBnk(((byte)0x08, 0x7701U, body))))
            .BnkStructures[0].Type08Tail;

        var located = Census(Build(new byte[15], 2, 4));
        if (located.Tails != 1
            || located.TailsWithAUniqueCount != 1
            || located.Records != 2
            || located.RecordCountCounts["records_2"] != 1
            || located.ThirdFieldCounts["code_4"] != 2
            || located.UnexplainedHeadBytes != 17)
        {
            throw new InvalidOperationException("type 0x08 tail run was not located from the end");
        }

        // Without the closing zero word there is nothing to anchor on, and the census
        // must say so rather than search for a run that fits.
        var noWord = Census(Build(new byte[15], 2, 4, zeroWord: false));
        if (noWord.NoZeroWordAtTheEnd != 1 || noWord.TailsWithAUniqueCount != 0)
        {
            throw new InvalidOperationException("type 0x08 tail anchored without its zero word");
        }

        // A body the reader frames outright has no tail to census, and must not be
        // counted as one that does.
        var framed = Census(Build(Array.Empty<byte>(), 0, 0, zeroWord: true));
        if (framed.Tails + framed.FramedByTheReader != 1)
        {
            throw new InvalidOperationException("type 0x08 tail census lost a body");
        }

        // Two counts that both fit make the alignment arithmetic rather than
        // evidence, so the body must be reported ambiguous and contribute no records.
        var ambiguousHead = new byte[15];
        // A tail of this length also admits a three-record reading, whose count byte
        // would land at offset 3. Making that byte a 3 gives two lengths that both fit.
        ambiguousHead[3] = 3;
        var ambiguous = Census(Build(ambiguousHead, 2, 4));
        if (ambiguous.CountIsAmbiguous != 1 || ambiguous.Records != 0)
        {
            throw new InvalidOperationException("type 0x08 tail accepted an ambiguous count");
        }
    }

    private static void TestType08TailHeadWordsAreClassifiedAgainstAControl()
    {
        // A type 0x08 body whose tail head carries two 32-bit words, one of which can
        // be made to name an object the same bank declares.
        static byte[] Build(uint first, uint second)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(0x1234U);
            writer.Write((byte)1);
            writer.Write((byte)0x1B);
            writer.Write(0U);
            writer.Write((byte)1);
            writer.Write((byte)0x15);
            writer.Write(new byte[11]);
            writer.Write(new byte[] { 0x02, 0xE8, 0x03, 0x00, 0x00, 0x00, 0x00, 0xC0, 0xC2 });
            writer.Write(0U);
            writer.Write((byte)0);
            // The fifteen-byte head: three bytes, a word, three bytes, a word, a byte.
            writer.Write(new byte[3]);
            writer.Write(first);
            writer.Write(new byte[3]);
            writer.Write(second);
            writer.Write((byte)2);
            // The record count. The id above is chosen so no shorter run also fits:
            // a second candidate length would make the tail ambiguous and uncounted.
            writer.Write((byte)1);
            writer.Write((byte)0);
            writer.Write(0.0f);
            writer.Write(1.0f);
            writer.Write(4U);
            writer.Write((ushort)0);
            writer.Flush();
            return stream.ToArray();
        }

        // A sibling numeric type 0x12 object gives the first word something real to
        // name; the second word names nothing, which is the control.
        var named = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7800, BuildBnk(
            ((byte)0x08, 0x7801U, Build(0x11223344U, 0xDEADBEEFU)),
            ((byte)0x12, 0x11223344U, new byte[8])))).Type08TailWords;
        if (named.Heads != 1
            || named.FirstWordSameBank != 1
            || named.SecondWordResolves != 0
            || named.FirstWordTargetTypeCounts["type12"] != 1)
        {
            throw new InvalidOperationException("type 0x08 tail head word was not classified");
        }

        // If the control also names an object the field under test proves nothing, so
        // the census has to report that rather than let the first word stand alone.
        var both = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7800, BuildBnk(
            ((byte)0x08, 0x7801U, Build(0x11223344U, 0x11223344U)),
            ((byte)0x12, 0x11223344U, new byte[8])))).Type08TailWords;
        if (both.SecondWordResolves != 1)
        {
            throw new InvalidOperationException("type 0x08 tail head control was not counted");
        }

        // A word naming nothing in the package is a real outcome, not a failure.
        var outside = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7800, BuildBnk(
            ((byte)0x08, 0x7801U, Build(0xCAFEBABEU, 0xDEADBEEFU))))).Type08TailWords;
        if (outside.FirstWordOutsidePackage != 1 || outside.FirstWordTargetTypeCounts.Count != 0)
        {
            throw new InvalidOperationException("type 0x08 tail head word outside the package was miscounted");
        }
    }

    private static void TestType12SharesType08Layout()
    {
        // Reference, counted key/value block, a second list whose key sizes its value,
        // nine bytes, a zero word, the counted entry run, five zero bytes.
        static byte[] Build(byte secondKey, int secondWidth, byte secondCount, byte entries,
                            int trailer = 5)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(0x1234U);
            writer.Write((byte)1);
            writer.Write((byte)0x29);
            writer.Write(100.0f);
            writer.Write(secondCount);
            writer.Write(secondKey);
            writer.Write(new byte[secondWidth]);
            // Nine bytes that are a field here, not a constant: a byte, a word and a
            // float. Numeric type 0x08 happens to carry the same values in every body.
            writer.Write((byte)0);
            writer.Write(0U);
            writer.Write(-96.3f);
            writer.Write(0U);
            writer.Write(entries);
            if (entries != 0)
            {
                writer.Write(new byte[entries * 6 + 1]);
            }
            writer.Write(new byte[trailer]);
            writer.Flush();
            return stream.ToArray();
        }

        static EndfieldHircBodyCensus Frame(byte[] body) => EndfieldAkpkPackage
            .Parse(BuildEncryptedBankPackage(0x7900, BuildBnk(((byte)0x12, 0x7901U, body))))
            .BnkStructures[0].Type12Body;

        // The two widths numeric type 0x08 already uses, and the one this type adds.
        var shortKey = Frame(Build(0x15, 11, 1, 0));
        var longKey = Frame(Build(0x1D, 27, 1, 2));
        var keyA = Frame(Build(0x0A, 12, 3, 1));
        if (shortKey.ExactCount != 1 || longKey.ExactCount != 1 || keyA.ExactCount != 1
            || keyA.SelectorCounts["secondListKey_0A"] != 1
            || keyA.SelectorCounts["secondListCount_3"] != 1)
        {
            throw new InvalidOperationException("type 0x12 did not frame its second-list keys");
        }

        // A width that is not the one its key predicts must not frame: the width is a
        // rule, not a tolerance, and accepting a wrong one would frame arbitrary bytes.
        var wrongWidth = Frame(Build(0x15, 12, 1, 0));
        var wrongWidthFailures = 0U;
        foreach (var pair in wrongWidth.FailureCounts)
        {
            wrongWidthFailures = checked(wrongWidthFailures + pair.Value);
        }
        if (wrongWidth.ExactCount != 0 || wrongWidthFailures != 1)
        {
            throw new InvalidOperationException("type 0x12 framed a body with the wrong value width");
        }

        // An unobserved key is held unsupported rather than walked with a guess.
        var unknownKey = Frame(Build(0x44, 11, 1, 0));
        if (unknownKey.ExactCount != 0
            || unknownKey.UnsupportedCategories["unsupported_second_list_key"] != 1)
        {
            throw new InvalidOperationException("type 0x12 accepted an unobserved list key");
        }
    }

    private static EndfieldBnkStructure FrameType22Fixture(byte[] body)
    {
        return EndfieldAkpkPackage
            .Parse(BuildEncryptedBankPackage(0x7500, BuildBnk((0x16, 0x7501U, body))))
            .BnkStructures[0];
    }

    private static void TestType11TailEntriesAreCounted()
    {
        // A source run, a 32-bit tail-entry count, that many variable-width entries
        // whose second word names one of the declared sources, then the terminator.
        static byte[] Build(uint[] entrySourceIds, int[] entryWidths, uint declared, uint terminator)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write((byte)0);
            writer.Write(1U);
            writer.Write(0x00040001U);
            writer.Write((byte)2);
            writer.Write(0x1000U);
            writer.Write(new byte[5]);
            writer.Write(declared);
            for (var i = 0; i < entrySourceIds.Length; i++)
            {
                writer.Write(0U);
                writer.Write(entrySourceIds[i]);
                writer.Write(new byte[entryWidths[i] - 8]);
            }
            writer.Write(terminator);
            writer.Flush();
            return stream.ToArray();
        }

        var one = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7500, BuildBnk(
            ((byte)0x0B, 0x7501U, Build(new[] { 0x1000U }, new[] { 84 }, 1, 100)))))
            .BnkStructures[0].Type11Sources;
        if (one.BodiesWithATail != 1
            || one.TailEntriesDeclared != 1
            || one.TailEntriesEchoed != 1
            || one.TailEchoesMatchTheCount != 1
            || one.TailEchoesExceedTheCount != 0
            || one.FirstTailEntryNamesADeclaredSource != 1
            || one.TailEntryCountCounts["tailEntries_1"] != 1
            || one.FirstTailEntryLeadingWordCounts["lead_00000000"] != 1
            || one.EndsWithTerminator != 1)
        {
            throw new InvalidOperationException("type 0x0B tail entries were not counted");
        }

        // A body declaring zero entries must carry no echoes; that is what stops the
        // count from being confirmed by bodies the reader cannot see into.
        var empty = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7500, BuildBnk(
            ((byte)0x0B, 0x7502U, Build(Array.Empty<uint>(), Array.Empty<int>(), 0, 100)))))
            .BnkStructures[0].Type11Sources;
        if (empty.BodiesWithATail != 1
            || empty.TailEntriesDeclared != 0
            || empty.TailEntriesEchoed != 0
            || empty.FirstTailEntryNamesADeclaredSource != 0
            || empty.TailEntryCountCounts["tailEntries_0"] != 1)
        {
            throw new InvalidOperationException("type 0x0B misread an empty tail");
        }

        // Two entries present but one declared: the surplus echo must be visible
        // rather than truncated away, because a count that under-reports is not one.
        var surplus = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7500, BuildBnk(
            ((byte)0x0B, 0x7503U, Build(new[] { 0x1000U, 0x1000U }, new[] { 16, 16 }, 1, 100)))))
            .BnkStructures[0].Type11Sources;
        if (surplus.TailEchoesExceedTheCount != 1 || surplus.TailEntriesEchoed != 1)
        {
            throw new InvalidOperationException("type 0x0B hid a surplus tail echo");
        }

        // A count past its bound is refused outright instead of walked.
        var wild = Build(new[] { 0x1000U }, new[] { 84 }, 1, 100);
        BinaryPrimitives.WriteUInt32LittleEndian(wild.AsSpan(19, 4), 70000);
        var refused = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7500, BuildBnk(
            ((byte)0x0B, 0x7504U, wild)))).BnkStructures[0].Type11Sources;
        if (refused.TailCountOutOfRange != 1 || refused.BodiesWithATail != 0)
        {
            throw new InvalidOperationException("type 0x0B accepted an impossible tail count");
        }

        // A terminator other than the observed one is counted under its own key, so a
        // body that breaks the claim is visible instead of being folded into it.
        var other = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7500, BuildBnk(
            ((byte)0x0B, 0x7505U, Build(new[] { 0x1000U }, new[] { 84 }, 1, 7)))))
            .BnkStructures[0].Type11Sources;
        if (other.EndsWithTerminator != 0 || other.TerminatorCounts["end_00000007"] != 1)
        {
            throw new InvalidOperationException("type 0x0B folded an unexpected terminator away");
        }
    }

    private static void TestType11SourceRecordsAreCounted()
    {
        // One byte, a 32-bit record count, then that many fourteen-byte records.
        static byte[] Build(uint records, uint plugin, byte streamType)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write((byte)0);
            writer.Write(records);
            for (var i = 0U; i < records; i++)
            {
                writer.Write(plugin);
                writer.Write(streamType);
                writer.Write(0x1000U + i);
                writer.Write(new byte[5]);
            }
            writer.Flush();
            return stream.ToArray();
        }

        var two = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7400, BuildBnk(
            ((byte)0x0B, 0x7401U, Build(2, 0x00040001U, 2))))).BnkStructures[0];
        if (two.Type11Sources.Bodies != 1
            || two.Type11Sources.BodiesWithRecords != 1
            || two.Type11Sources.Records != 2
            || two.Type11Sources.PluginIdCounts["plugin_00040001"] != 2
            || two.Type11Sources.StreamTypeCounts["streamType_02"] != 2
            || two.Type11Sources.RecordCountCounts["records_2"] != 1)
        {
            throw new InvalidOperationException("type 0x0B source records were not counted");
        }

        // A body declaring no records is still a body, and must not count as one
        // carrying records.
        var none = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7400, BuildBnk(
            ((byte)0x0B, 0x7402U, Build(0, 0, 0))))).BnkStructures[0];
        if (none.Type11Sources.Bodies != 1
            || none.Type11Sources.BodiesWithRecords != 0
            || none.Type11Sources.Records != 0)
        {
            throw new InvalidOperationException("type 0x0B miscounted an empty record run");
        }

        // A count that cannot fit is reported, never clamped to the bytes present.
        var overrun = Build(1, 0x00040001U, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(overrun.AsSpan(1, 4), 9999);
        var bad = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7400, BuildBnk(
            ((byte)0x0B, 0x7403U, overrun)))).BnkStructures[0];
        if (bad.Type11Sources.RecordsOutOfRange != 1 || bad.Type11Sources.Records != 0)
        {
            throw new InvalidOperationException("type 0x0B accepted an impossible record count");
        }

        var tiny = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7400, BuildBnk(
            ((byte)0x0B, 0x7404U, new byte[3])))).BnkStructures[0];
        if (tiny.Type11Sources.TooShort != 1)
        {
            throw new InvalidOperationException("type 0x0B dropped a short body");
        }
    }

    private static void TestMusicHeadReferencesFollowTheDiscriminantByte()
    {
        // A bank holding one type 0x02 object plus music bodies that name it. The
        // offset must come from byte 2, so a body whose word sits at the other offset
        // must not resolve by accident.
        const uint target = 0x7301U;
        var targetBody = BuildType2Body();

        var atNine = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7300, BuildBnk(
            ((byte)0x02, target, targetBody),
            ((byte)0x0A, 0x7302U, BuildMusicBody(0, target))))).BnkStructures[0];
        if (atNine.MusicHeadReferences.Bodies != 1
            || atNine.MusicHeadReferences.Resolved != 1
            || atNine.MusicHeadReferences.OffsetCounts["offset_9"] != 1
            || atNine.MusicHeadReferences.DiscriminantCounts["byte2_00"] != 1
            || atNine.MusicHeadReferences.BodiesByType["type0A"] != 1)
        {
            throw new InvalidOperationException("music head reference at offset 9 did not resolve");
        }

        var atFive = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7300, BuildBnk(
            ((byte)0x02, target, targetBody),
            ((byte)0x0D, 0x7303U, BuildMusicBody(1, target))))).BnkStructures[0];
        if (atFive.MusicHeadReferences.Resolved != 1
            || atFive.MusicHeadReferences.OffsetCounts["offset_5"] != 1
            || atFive.MusicHeadReferences.BodiesByType["type0D"] != 1)
        {
            throw new InvalidOperationException("music head reference at offset 5 did not resolve");
        }

        // Byte 2 says offset 9, but the word is written at 5. Reading the selected
        // offset must leave this unresolved rather than finding the value elsewhere.
        var misplaced = BuildMusicBody(1, target);
        misplaced[2] = 0;
        var wrongPlace = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7300, BuildBnk(
            ((byte)0x02, target, targetBody),
            ((byte)0x0A, 0x7304U, misplaced)))).BnkStructures[0];
        if (wrongPlace.MusicHeadReferences.Resolved != 0
            || wrongPlace.MusicHeadReferences.Zero != 1)
        {
            throw new InvalidOperationException("music head reference ignored its discriminant byte");
        }

        // An unobserved discriminant has no branch, so it must be counted as unknown
        // rather than assigned one.
        var unknown = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7300, BuildBnk(
            ((byte)0x02, target, targetBody),
            ((byte)0x0A, 0x7305U, BuildMusicBody(7, target))))).BnkStructures[0];
        if (unknown.MusicHeadReferences.UnknownDiscriminant != 1
            || unknown.MusicHeadReferences.Resolved != 0)
        {
            throw new InvalidOperationException("music head accepted an unobserved discriminant");
        }

        // A word naming nothing in the bank is unresolved, not silently dropped.
        var stranger = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7300, BuildBnk(
            ((byte)0x02, target, targetBody),
            ((byte)0x0A, 0x7306U, BuildMusicBody(0, 0xDEADBEEF))))).BnkStructures[0];
        if (stranger.MusicHeadReferences.Unresolved != 1
            || stranger.MusicHeadReferences.Resolved != 0)
        {
            throw new InvalidOperationException("music head lost an unresolved reference");
        }

        // Numeric type 0x0C shares the head, but some of its bodies open with a
        // different first byte. Those are outside the claim and must be counted as
        // such rather than read with the wrong rule.
        var otherShape = BuildMusicBody(0, target);
        otherShape[0] = 6;
        var shaped = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7300, BuildBnk(
            ((byte)0x02, target, targetBody),
            ((byte)0x0C, 0x7308U, otherShape)))).BnkStructures[0];
        if (shaped.MusicHeadReferences.UnknownHeadShape != 1
            || shaped.MusicHeadReferences.Resolved != 0
            || shaped.MusicHeadReferences.HeadShapeCounts["head_06"] != 1
            || shaped.MusicHeadReferences.BodiesByType["type0C"] != 1)
        {
            throw new InvalidOperationException("music head read an unestablished head shape");
        }

        // A body too short to hold the selected word is counted, not skipped.
        var tiny = EndfieldAkpkPackage.Parse(BuildEncryptedBankPackage(0x7300, BuildBnk(
            ((byte)0x02, target, targetBody),
            ((byte)0x0A, 0x7307U, new byte[5])))).BnkStructures[0];
        if (tiny.MusicHeadReferences.TooShort != 1 || tiny.MusicHeadReferences.Bodies != 1)
        {
            throw new InvalidOperationException("music head dropped a short body");
        }
    }

    private static EndfieldBnkStructure FrameType14Fixture(byte[] body)
    {
        return EndfieldAkpkPackage
            .Parse(BuildEncryptedBankPackage(0x6E00, BuildBnk((0x0E, 0x6E01U, body))))
            .BnkStructures[0];
    }

    private static void TestType14BodyFramesBothBranches()
    {
        // The short branch: no optional block. 21 + 1 + 3 + 24 + 2 = 51 bytes.
        var shortBranch = FrameType14Fixture(BuildType14Body());
        if (shortBranch.Type14Body.ExactCount != 1
            || shortBranch.Type14Body.ExactCursorBytes != 51
            || shortBranch.Type14Body.GroupCounts["listElements"] != 2
            || shortBranch.Type14Body.GroupCounts["listEntries"] != 1
            || shortBranch.Type14Body.GroupCounts.ContainsKey("optionalBlock")
            || shortBranch.Type14Body.SelectorCounts["optionalBlockFlag_00"] != 1)
        {
            throw new InvalidOperationException("type 0x0E short branch did not frame exactly");
        }

        // The long branch adds exactly twenty bytes and nothing else.
        var longBranch = FrameType14Fixture(BuildType14Body(headByte: 1, optionalBlockFlag: 1));
        if (longBranch.Type14Body.ExactCount != 1
            || longBranch.Type14Body.ExactCursorBytes != 71
            || longBranch.Type14Body.GroupCounts["optionalBlock"] != 1
            || longBranch.Type14Body.SelectorCounts["optionalBlockFlag_01"] != 1
            || longBranch.Type14Body.SelectorCounts["headByte_01"] != 1)
        {
            throw new InvalidOperationException("type 0x0E long branch did not frame exactly");
        }

        // An empty list is still a complete body: head, a zero count, terminator.
        var empty = FrameType14Fixture(BuildType14Body(entries: Array.Empty<(byte, ushort)>()));
        if (empty.Type14Body.ExactCount != 1 || empty.Type14Body.ExactCursorBytes != 24)
        {
            throw new InvalidOperationException("type 0x0E empty list did not frame exactly");
        }

        // Several entries, so the per-entry header is exercised more than once.
        var many = FrameType14Fixture(BuildType14Body(
            entries: new[] { ((byte)0x00, (ushort)1), ((byte)0x02, (ushort)3), ((byte)0x00, (ushort)0) }));
        if (many.Type14Body.ExactCount != 1
            || many.Type14Body.GroupCounts["listEntries"] != 3
            || many.Type14Body.GroupCounts["listElements"] != 4
            || many.Type14Body.SelectorCounts["listEntrySelector_00"] != 2
            || many.Type14Body.SelectorCounts["listEntrySelector_02"] != 1)
        {
            throw new InvalidOperationException("type 0x0E multi-entry list did not frame exactly");
        }
    }

    private static void TestType14BodyFramesFailClosed()
    {
        // The branch byte decides the prefix length, so an unobserved value cannot be
        // framed at all. Guessing either branch would silently mis-frame the body.
        var unknownFlag = BuildType14Body();
        unknownFlag[1] = 2;
        if (!FrameType14Fixture(unknownFlag).Type14Body.FailureCounts
                .ContainsKey("unknown_optionalBlockFlag"))
        {
            throw new InvalidOperationException("type 0x0E accepted an unknown branch byte");
        }

        // A count that overruns the body must be rejected on its own terms rather than
        // clamped to whatever bytes happen to remain.
        var overrun = BuildType14Body();
        overrun[22] = 0xFF;
        overrun[23] = 0xFF;
        if (!FrameType14Fixture(overrun).Type14Body.FailureCounts.ContainsKey("range_listElements"))
        {
            throw new InvalidOperationException("type 0x0E accepted an out-of-range element count");
        }

        // Trailing bytes and truncation are both failures, not partial successes.
        if (!FrameType14Fixture(BuildType14Body(extraTrailingBytes: 1)).Type14Body
                .FailureCounts.ContainsKey("trailing_bytes"))
        {
            throw new InvalidOperationException("type 0x0E accepted trailing bytes");
        }
        if (FrameType14Fixture(BuildType14Body(truncateBy: 1)).Type14Body.ExactCount != 0)
        {
            throw new InvalidOperationException("type 0x0E accepted a truncated body");
        }
        if (FrameType14Fixture(new byte[20]).Type14Body.FailureCounts.Count == 0)
        {
            throw new InvalidOperationException("type 0x0E accepted a body shorter than its head");
        }

        // The two closing bytes read as zero everywhere. A nonzero value means content
        // this frame does not describe, so it must fail rather than be ignored.
        if (!FrameType14Fixture(BuildType14Body(terminator: 1)).Type14Body
                .FailureCounts.ContainsKey("nonzero_listTerminator"))
        {
            throw new InvalidOperationException("type 0x0E accepted a nonzero terminator");
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
