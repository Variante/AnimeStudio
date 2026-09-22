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
        TestType2BodyRareShapesFollowTheEngine();
        TestType7BodyFramesReuseTheSharedNodeGroups();
        TestType7BodyFramesFailClosed();
        TestType9BodyFramesLayers();
        TestType9BodyFramesFailClosed();
        TestMusicSegmentBodiesFrameMarkersWithNames();
        TestMusicTrackBodiesFrameSourcesPlaylistAndSwitch();
        TestMusicSwitchAndRanSeqBodiesFrameTransitionRules();
        TestMusicBodiesFailClosed();
        TestEffectDeviceModulatorAndDialogueBodiesFrame();
        TestEffectDeviceModulatorAndDialogueBodiesFailClosed();
        TestType5BodyFramesFrameTwoIndependentVectors();
        TestType5BodyFramesFailClosed();
        TestType14BodyFramesBothBranches();
        TestType14BodyFramesFailClosed();
        TestMusicHeadReferencesFollowTheDiscriminantByte();
        TestType08BodiesFrameOrAreNamed();
        TestType08TailRecordsAreLocatedFromTheEnd();
        TestType08TailHeadWordsAreClassifiedAgainstAControl();
        TestType12SharesType08Layout();
        TestType08HeadWordIsNullOrResolved();
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

    private static void TestType2BodyRareShapesFollowTheEngine()
    {
        // These shapes never occur in the current corpus, so they are pinned from the
        // Wwise 2023.1.17 SDK deserializer rather than from a shipped bank.

        // Group B (NodeInitialMetadataParams) entries are six bytes each.
        var groupB = BuildType2Body(groupBEntries: 2);
        var groupBResult = FrameType2Fixture(groupB);
        if (groupBResult.Type2Body.ExactCount != 1
            || groupBResult.Type2Body.GroupCounts["groupBEntries"] != 2)
        {
            throw new InvalidOperationException("nonempty group B was not consumed as six-byte slots");
        }

        // SetPositioningParams returns as soon as bit 0 is clear, so selector 0x02
        // (listener-relative routing without override) reads nothing further.
        var lowBitOnly = BuildType2Body();
        lowBitOnly[14 + 4 + 9 + 2] = 0x01;
        if (FrameType2Fixture(lowBitOnly).Type2Body.ExactCount != 1)
        {
            throw new InvalidOperationException("group E selector 0x01 body did not consume exactly");
        }
        var highBitOnly = BuildType2Body();
        highBitOnly[14 + 4 + 9 + 2] = 0x02;
        if (FrameType2Fixture(highBitOnly).Type2Body.ExactCount != 1)
        {
            throw new InvalidOperationException("group E selector 0x02 body did not consume exactly");
        }

        // e3DPositionType 3 carries no automation block, exactly like 0.
        var branch = BuildType2Body(groupESelector: 0x03);
        branch[14 + 4 + 9 + 2] = 0x63;
        var branchResult = FrameType2Fixture(branch);
        if (branchResult.Type2Body.ExactCount != 1
            || branchResult.Type2Body.SelectorCounts["groupEBranch_3"] != 1)
        {
            throw new InvalidOperationException("group E branch 3 did not consume exactly");
        }

        // Group H counts are seven-bit continuation values: a two-byte encoding of
        // zero properties must frame like the one-byte encoding does.
        var varintCount = BuildType2Body();
        const int groupHPropCountOffset = 14 + 4 + 9 + 2 + 1 + 1 + 4 + 6;
        var widened = new byte[varintCount.Length + 1];
        Array.Copy(varintCount, 0, widened, 0, groupHPropCountOffset);
        widened[groupHPropCountOffset] = 0x80;
        widened[groupHPropCountOffset + 1] = 0x00;
        Array.Copy(
            varintCount,
            groupHPropCountOffset + 1,
            widened,
            groupHPropCountOffset + 2,
            varintCount.Length - groupHPropCountOffset - 1);
        if (FrameType2Fixture(widened).Type2Body.ExactCount != 1)
        {
            throw new InvalidOperationException("two-byte group H property count was not consumed");
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

        // A unit whose head byte 6 has the high bit set carries three bytes after the
        // head rather than two, with the record count in the middle. One unit in the
        // corpus is shaped that way, and what establishes it is not the closure but
        // that its eight records then all read as curve records.
        static byte[] WideGapUnit(int records)
        {
            var bytes = new List<byte> { 0x00, 0x01, 0x00 };
            var head = new byte[12];
            head[5] = 0x04;
            head[6] = 0x84;
            bytes.AddRange(head);
            bytes.AddRange(new byte[] { 0x00, (byte)records, 0x00 });
            bytes.AddRange(new byte[records * 12]);
            bytes.AddRange(new byte[] { 0x00, 0x00 });
            return bytes.ToArray();
        }

        var wideGap = Frame(Build(
            new byte[] { 0x1B }, 0x15, 11, 0, trailerBytes: WideGapUnit(3)));
        if (wideGap.ExactCount != 1 || wideGap.GroupCounts["tailBlockRecords"] != 3)
        {
            throw new InvalidOperationException("type 0x08 refused a wide-gap tail unit");
        }
        if (wideGap.SelectorCounts["tailBlockUnitGap_wide"] != 1)
        {
            throw new InvalidOperationException("type 0x08 did not record the wide gap");
        }
        // Clearing the high bit must make the same bytes stop framing, or the flag is
        // not what selects the gap.
        var narrowed = WideGapUnit(3);
        narrowed[3 + 6] = 0x04;
        if (Frame(Build(new byte[] { 0x1B }, 0x15, 11, 0, trailerBytes: narrowed)).ExactCount != 0)
        {
            throw new InvalidOperationException("type 0x08 framed a wide gap without its flag");
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
        byte groupBEntries = 0,
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
        writer.Write(groupBEntries);
        writer.Write(new byte[groupBEntries * 6]);
        writer.Write(new byte[9]);
        writer.Write(groupCEntries);
        writer.Write(new byte[groupCEntries * 5]);
        writer.Write(groupDEntries);
        writer.Write(new byte[groupDEntries * 9]);
        writer.Write(groupESelector);
        if ((groupESelector & 0x03) == 0x03)
        {
            writer.Write((byte)0);
            if (((groupESelector >> 5) & 0x03) is 1 or 2)
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

        // Most-significant group first: five groups whose leading group carries
        // bit 4 reach 2^32, which no 32-bit parameter id can hold.
        var overflowing = BuildType2Body(
            groupIEntries: 1,
            groupIKey: new byte[] { 0x90, 0x80, 0x80, 0x80, 0x00 },
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

        // Selector bit 1 alone reads nothing further: the engine checks bit 0 first.
        var highBitOnly = BuildType2Body(childEntries: 1, writePrefix: false);
        highBitOnly[4 + 9 + 2] = 0x02;
        if (FrameType7Fixture(highBitOnly).Type7Body.ExactCount != 1)
        {
            throw new InvalidOperationException("group E selector 0x02 body did not consume exactly");
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

    private static void TestType9BodyFramesLayers()
    {
        // CAkLayerCntr: node groups, a counted child vector, counted layers each with
        // their own RTPC curve list and counted associations, and one trailing byte.
        var minimal = BuildType9Body();
        var rich = BuildType9Body(
            childEntries: 2,
            layers: new[]
            {
                (curveEntries: (ushort)1, curvePoints: (ushort)2, assocPoints: new uint[] { 3, 0 }),
                (curveEntries: (ushort)0, curvePoints: (ushort)0, assocPoints: new uint[] { 1 }),
            },
            tail: 1,
            groupHStates: 1,
            groupHStateElements: 2);
        var structure = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(
                0x9000,
                BuildBnk((0x09, 0x9001U, minimal), (0x09, 0x9002U, rich))))
            .BnkStructures[0];
        var expectedBytes = (uint)(minimal.Length + rich.Length);
        if (structure.Type9Body.FrameCount != 2
            || structure.Type9Body.ExactCount != 2
            || structure.Type9Body.UnsupportedCount != 0
            || structure.Type9Body.FailedCount != 0
            || structure.Type9Body.BodyBytes != expectedBytes
            || structure.Type9Body.ExactCursorBytes != expectedBytes
            || structure.Type9Body.MinExactBytes != 40)
        {
            throw new InvalidOperationException("type 0x09 body frame census mismatch");
        }
        var groups = structure.Type9Body.GroupCounts;
        if (groups["childEntries"] != 2
            || groups["layerEntries"] != 2
            || groups["layerAssocEntries"] != 3
            || groups["layerAssocPoints"] != 4
            || groups["groupIEntries"] != 1
            || groups["groupIPoints"] != 2
            || groups["groupHStateElements"] != 2)
        {
            throw new InvalidOperationException("type 0x09 group inventory mismatch");
        }
        var selectors = structure.Type9Body.SelectorCounts;
        if (selectors["type09Tail_00"] != 1
            || selectors["type09Tail_01"] != 1
            || selectors["groupIKeyWidth_1"] != 1)
        {
            throw new InvalidOperationException("type 0x09 selector inventory mismatch");
        }
    }

    private static void TestType9BodyFramesFailClosed()
    {
        var body = BuildType9Body(
            childEntries: 1,
            layers: new[] { (curveEntries: (ushort)0, curvePoints: (ushort)0, assocPoints: new uint[] { 1 }) });

        var truncated = body[..^1];
        if (!FrameType9Fixture(truncated).Type9Body.FailureCounts.ContainsKey("truncated_type09Tail"))
        {
            throw new InvalidOperationException("truncated type 0x09 tail was not rejected");
        }

        var trailing = new byte[body.Length + 1];
        body.CopyTo(trailing, 0);
        if (!FrameType9Fixture(trailing).Type9Body.FailureCounts.ContainsKey("trailing_bytes"))
        {
            throw new InvalidOperationException("trailing type 0x09 byte was not rejected");
        }

        // The association count sits after the node frame (31), the child count and
        // child (8), the layer count (4), the layer id (4), its empty curve count (2)
        // and the RTPC id and type (5).
        const int assocCountOffset = 31 + 8 + 4 + 4 + 2 + 5;
        var overrun = (byte[])body.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(overrun.AsSpan(assocCountOffset, 4), 0x7FFFFFFF);
        if (!FrameType9Fixture(overrun).Type9Body.FailureCounts.ContainsKey("range_layerAssocEntries"))
        {
            throw new InvalidOperationException("type 0x09 association overrun was not rejected");
        }

        var wrongVersion = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0x9400, BuildBnk(149, (0x09, 0x9401U, body))));
        if (wrongVersion.BnkStructures[0].Type9Body.UnsupportedCount != 1
            || !wrongVersion.BnkStructures[0].Type9Body.UnsupportedCategories
                .ContainsKey("unsupported_bank_version"))
        {
            throw new InvalidOperationException("non-current bank version was not held unsupported");
        }
    }

    private static byte[] BuildMusicNodeParams(uint childEntries = 0, uint stingerEntries = 0, byte flags = 0)
    {
        var node = BuildType2Body(writePrefix: false);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(flags);
        writer.Write(node);
        writer.Write(childEntries);
        writer.Write(new byte[checked((int)childEntries * 4)]);
        writer.Write(new byte[23]); // AkMeterInfo.
        writer.Write(stingerEntries);
        writer.Write(new byte[checked((int)stingerEntries * 24)]);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildMusicTransitionRules((uint sources, uint destinations, bool transObject)[] rules)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((uint)rules.Length);
        foreach (var (sources, destinations, transObject) in rules)
        {
            writer.Write(sources);
            writer.Write(new byte[checked((int)sources * 4)]);
            writer.Write(destinations);
            writer.Write(new byte[checked((int)destinations * 4)]);
            writer.Write(new byte[21]); // Source rule.
            writer.Write(new byte[26]); // Destination rule.
            writer.Write((byte)(transObject ? 1 : 0));
            if (transObject)
            {
                writer.Write(new byte[30]);
            }
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildType0ABody(string[]? markerNames = null, uint childEntries = 0, uint stingerEntries = 0)
    {
        markerNames ??= Array.Empty<string>();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(BuildMusicNodeParams(childEntries, stingerEntries));
        writer.Write(1234.5); // fDuration.
        writer.Write((uint)markerNames.Length);
        foreach (var name in markerNames)
        {
            writer.Write(0x1111U);
            writer.Write(0.0);
            writer.Write(Encoding.ASCII.GetBytes(name));
            writer.Write((byte)0);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildType0BBody(
        uint[]? sourcePluginIds = null,
        int sourceParamBytes = 0,
        uint playlistEntries = 0,
        uint subTracks = 0,
        uint[]? clipPointCounts = null,
        byte trackType = 0,
        uint switchAssocEntries = 0)
    {
        sourcePluginIds ??= Array.Empty<uint>();
        clipPointCounts ??= Array.Empty<uint>();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((byte)0); // uFlags.
        writer.Write((uint)sourcePluginIds.Length);
        foreach (var pluginId in sourcePluginIds)
        {
            writer.Write(pluginId);
            writer.Write(new byte[10]);
            if ((pluginId & 0x0F) == 2)
            {
                writer.Write((uint)sourceParamBytes);
                writer.Write(new byte[sourceParamBytes]);
            }
        }
        writer.Write(playlistEntries);
        writer.Write(new byte[checked((int)playlistEntries * 44)]);
        if (playlistEntries > 0)
        {
            writer.Write(subTracks); // Read by the engine only with a nonempty playlist.
        }
        writer.Write((uint)clipPointCounts.Length);
        foreach (var points in clipPointCounts)
        {
            writer.Write(new byte[8]);
            writer.Write(points);
            writer.Write(new byte[checked((int)points * 12)]);
        }
        writer.Write(BuildType2Body(writePrefix: false));
        writer.Write(trackType);
        if (trackType == 3)
        {
            writer.Write(new byte[9]);
            writer.Write(switchAssocEntries);
            writer.Write(new byte[checked((int)switchAssocEntries * 4)]);
            writer.Write(new byte[32]);
        }
        writer.Write(100); // iLookAheadTime.
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildType0CBody(
        (uint sources, uint destinations, bool transObject)[]? rules = null,
        uint arguments = 0,
        uint treeBytes = 0)
    {
        rules ??= Array.Empty<(uint, uint, bool)>();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(BuildMusicNodeParams());
        writer.Write(BuildMusicTransitionRules(rules));
        writer.Write((byte)1); // bIsContinuePlayback.
        writer.Write(arguments);
        writer.Write(new byte[checked((int)arguments * 5)]);
        writer.Write(treeBytes);
        writer.Write((byte)0); // uMode.
        writer.Write(new byte[checked((int)treeBytes)]);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildType0DBody(
        (uint sources, uint destinations, bool transObject)[]? rules = null,
        uint playlistEntries = 0)
    {
        rules ??= Array.Empty<(uint, uint, bool)>();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(BuildMusicNodeParams());
        writer.Write(BuildMusicTransitionRules(rules));
        writer.Write(playlistEntries);
        writer.Write(new byte[checked((int)playlistEntries * 30)]);
        writer.Flush();
        return stream.ToArray();
    }

    private static EndfieldBnkStructure FrameMusicFixture(byte objectType, byte[] body)
    {
        return EndfieldAkpkPackage
            .Parse(BuildEncryptedBankPackage((uint)(0xA000 + objectType), BuildBnk((objectType, 0xA001U, body))))
            .BnkStructures[0];
    }

    private static void TestMusicSegmentBodiesFrameMarkersWithNames()
    {
        var minimal = BuildType0ABody();
        var rich = BuildType0ABody(new[] { "Entry", "", "Exit Cue" }, childEntries: 2, stingerEntries: 1);
        var structure = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0xA100, BuildBnk((0x0A, 0xA101U, minimal), (0x0A, 0xA102U, rich))))
            .BnkStructures[0];
        var census = structure.Type0ABody;
        if (census.ExactCount != 2 || census.FailedCount != 0 || census.MinExactBytes != 75
            || census.GroupCounts["markerEntries"] != 3
            || census.GroupCounts["markerNameBytes"] != 13
            || census.GroupCounts["childEntries"] != 2
            || census.GroupCounts["stingerEntries"] != 1
            || census.SelectorCounts["musicFlags_00"] != 2)
        {
            throw new InvalidOperationException("type 0x0A body frame census mismatch");
        }
    }

    private static void TestMusicTrackBodiesFrameSourcesPlaylistAndSwitch()
    {
        var minimal = BuildType0BBody();
        var rich = BuildType0BBody(
            sourcePluginIds: new[] { 0x00040001U, 0x00940002U },
            sourceParamBytes: 6,
            playlistEntries: 2,
            subTracks: 1,
            clipPointCounts: new uint[] { 3, 0 },
            trackType: 3,
            switchAssocEntries: 2);
        var structure = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0xA200, BuildBnk((0x0B, 0xA201U, minimal), (0x0B, 0xA202U, rich))))
            .BnkStructures[0];
        var census = structure.Type0BBody;
        if (census.ExactCount != 2 || census.FailedCount != 0 || census.MinExactBytes != 49
            || census.GroupCounts["sourceEntries"] != 2
            || census.GroupCounts["sourceParamBytes"] != 6
            || census.GroupCounts["playlistEntries"] != 2
            || census.GroupCounts["subTrackCount"] != 1
            || census.GroupCounts["clipAutomationEntries"] != 2
            || census.GroupCounts["clipAutomationPoints"] != 3
            || census.GroupCounts["switchAssocEntries"] != 2
            || census.SelectorCounts["trackType_03"] != 1
            || census.SelectorCounts["trackType_00"] != 1)
        {
            throw new InvalidOperationException("type 0x0B body frame census mismatch");
        }
    }

    private static void TestMusicSwitchAndRanSeqBodiesFrameTransitionRules()
    {
        var rules = new[] { (1U, 2U, true), (0U, 0U, false) };
        var switchBody = BuildType0CBody(rules, arguments: 2, treeBytes: 24);
        var switchCensus = FrameMusicFixture(0x0C, switchBody).Type0CBody;
        if (switchCensus.ExactCount != 1
            || switchCensus.GroupCounts["transitionRuleEntries"] != 2
            || switchCensus.GroupCounts["transitionRuleSourceEntries"] != 1
            || switchCensus.GroupCounts["transitionRuleDestinationEntries"] != 2
            || switchCensus.GroupCounts["transitionObjectEntries"] != 1
            || switchCensus.GroupCounts["decisionArgumentEntries"] != 2
            || switchCensus.GroupCounts["decisionTreeBytes"] != 24
            || switchCensus.SelectorCounts["transitionObject_01"] != 1
            || switchCensus.SelectorCounts["transitionObject_00"] != 1
            || switchCensus.SelectorCounts["continuePlayback_01"] != 1
            || switchCensus.SelectorCounts["decisionMode_00"] != 1)
        {
            throw new InvalidOperationException("type 0x0C body frame census mismatch");
        }
        if (FrameMusicFixture(0x0C, BuildType0CBody()).Type0CBody.MinExactBytes != 77)
        {
            throw new InvalidOperationException("type 0x0C minimal body length mismatch");
        }

        var ranSeq = FrameMusicFixture(0x0D, BuildType0DBody(rules, playlistEntries: 3)).Type0DBody;
        if (ranSeq.ExactCount != 1
            || ranSeq.GroupCounts["playlistEntries"] != 3
            || ranSeq.GroupCounts["transitionRuleEntries"] != 2)
        {
            throw new InvalidOperationException("type 0x0D body frame census mismatch");
        }
        if (FrameMusicFixture(0x0D, BuildType0DBody()).Type0DBody.MinExactBytes != 71)
        {
            throw new InvalidOperationException("type 0x0D minimal body length mismatch");
        }
    }

    private static void TestMusicBodiesFailClosed()
    {
        var unterminated = BuildType0ABody(new[] { "Cue" });
        unterminated = unterminated[..^1];
        if (!FrameMusicFixture(0x0A, unterminated).Type0ABody.FailureCounts.ContainsKey("unterminated_markerName"))
        {
            throw new InvalidOperationException("unterminated marker name was not rejected");
        }

        var tree = BuildType0CBody(treeBytes: 12);
        BinaryPrimitives.WriteUInt32LittleEndian(tree.AsSpan(tree.Length - 12 - 5, 4), 0x7FFFFFF4);
        if (!FrameMusicFixture(0x0C, tree).Type0CBody.FailureCounts.ContainsKey("range_decisionTreeBytes"))
        {
            throw new InvalidOperationException("decision tree overrun was not rejected");
        }
        if (!FrameMusicFixture(0x0C, BuildType0CBody(treeBytes: 10)).Type0CBody.FailureCounts.ContainsKey("decisionTree_not_whole_nodes"))
        {
            throw new InvalidOperationException("partial decision tree node was not rejected");
        }

        var trailing = new byte[BuildType0DBody().Length + 1];
        BuildType0DBody().CopyTo(trailing, 0);
        if (!FrameMusicFixture(0x0D, trailing).Type0DBody.FailureCounts.ContainsKey("trailing_bytes"))
        {
            throw new InvalidOperationException("trailing type 0x0D byte was not rejected");
        }

        var truncated = BuildType0BBody()[..^1];
        if (!FrameMusicFixture(0x0B, truncated).Type0BBody.FailureCounts.ContainsKey("truncated_lookAheadTime"))
        {
            throw new InvalidOperationException("truncated type 0x0B look-ahead was not rejected");
        }

        var wrongVersion = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0xA400, BuildBnk(149, (0x0A, 0xA401U, BuildType0ABody()))));
        if (!wrongVersion.BnkStructures[0].Type0ABody.UnsupportedCategories.ContainsKey("unsupported_bank_version"))
        {
            throw new InvalidOperationException("non-current bank version was not held unsupported");
        }
    }

    private static byte[] BuildPropertyBundles(byte props = 0, byte ranged = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(props);
        writer.Write(new byte[props * 5]);
        writer.Write(ranged);
        writer.Write(new byte[ranged * 9]);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildGroupI(ushort entries = 0, ushort points = 0, byte[]? key = null)
    {
        key ??= new byte[] { 0x00 };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(entries);
        for (var i = 0; i < entries; i++)
        {
            writer.Write(new byte[6]);
            writer.Write(key);
            writer.Write(new byte[5]);
            writer.Write(points);
            writer.Write(new byte[points * 12]);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildFxBody(
        uint paramBytes = 0,
        byte media = 0,
        ushort curves = 0,
        ushort values = 0,
        byte[]? valueKey = null,
        byte? deviceSlots = null)
    {
        valueKey ??= new byte[] { 0x00 };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(0x00640003U); // fxID.
        writer.Write(paramBytes);
        writer.Write(new byte[checked((int)paramBytes)]);
        writer.Write(media);
        writer.Write(new byte[media * 5]);
        writer.Write(BuildGroupI(curves));
        writer.Write((byte)0); // StateChunk: no properties.
        writer.Write((byte)0); // StateChunk: no groups.
        writer.Write(values);
        for (var i = 0; i < values; i++)
        {
            writer.Write(valueKey);
            writer.Write(new byte[5]);
        }
        if (deviceSlots.HasValue)
        {
            writer.Write(deviceSlots.Value);
            if (deviceSlots.Value > 0)
            {
                writer.Write((byte)0);
                writer.Write(new byte[deviceSlots.Value * 6]);
            }
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildModulatorBody(byte props = 0, byte ranged = 0, ushort curves = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(BuildPropertyBundles(props, ranged));
        writer.Write(BuildGroupI(curves));
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildDialogueBody(uint arguments = 0, uint treeBytes = 0, byte props = 0, byte ranged = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((byte)100); // uProbability.
        writer.Write(arguments);
        writer.Write(new byte[checked((int)arguments * 5)]);
        writer.Write(treeBytes);
        writer.Write((byte)0); // uMode.
        writer.Write(new byte[checked((int)treeBytes)]);
        writer.Write(BuildPropertyBundles(props, ranged));
        writer.Flush();
        return stream.ToArray();
    }

    private static void TestEffectDeviceModulatorAndDialogueBodiesFrame()
    {
        var fx = FrameMusicFixture(0x10, BuildFxBody(paramBytes: 12, media: 2, curves: 1, values: 2, valueKey: new byte[] { 0x81, 0x30 })).Type10Body;
        if (fx.ExactCount != 1
            || fx.GroupCounts["fxParamBytes"] != 12
            || fx.GroupCounts["fxMediaEntries"] != 2
            || fx.GroupCounts["groupIEntries"] != 1
            || fx.GroupCounts["fxPropertyEntries"] != 2
            || fx.GroupCounts["deviceEffectEntries"] != 0)
        {
            throw new InvalidOperationException("type 0x10 body frame census mismatch");
        }
        if (FrameMusicFixture(0x11, BuildFxBody()).Type11Body.MinExactBytes != 15)
        {
            throw new InvalidOperationException("type 0x11 minimal body length mismatch");
        }

        var device = FrameMusicFixture(0x15, BuildFxBody(deviceSlots: 2)).Type15Body;
        if (device.ExactCount != 1 || device.GroupCounts["deviceEffectEntries"] != 2)
        {
            throw new InvalidOperationException("type 0x15 body frame census mismatch");
        }
        if (FrameMusicFixture(0x15, BuildFxBody(deviceSlots: 0)).Type15Body.MinExactBytes != 16)
        {
            throw new InvalidOperationException("type 0x15 minimal body length mismatch");
        }

        var lfo = FrameMusicFixture(0x13, BuildModulatorBody(props: 3, ranged: 1, curves: 1)).Type13Body;
        if (lfo.ExactCount != 1
            || lfo.GroupCounts["groupCEntries"] != 3
            || lfo.GroupCounts["groupDEntries"] != 1
            || lfo.GroupCounts["groupIEntries"] != 1)
        {
            throw new InvalidOperationException("type 0x13 body frame census mismatch");
        }
        if (FrameMusicFixture(0x14, BuildModulatorBody()).Type14ModBody.MinExactBytes != 4
            || FrameMusicFixture(0x16, BuildModulatorBody()).Type22Body.MinExactBytes != 4)
        {
            throw new InvalidOperationException("modulator minimal body length mismatch");
        }

        var dialogue = FrameMusicFixture(0x0F, BuildDialogueBody(arguments: 2, treeBytes: 36, props: 1)).Type0FBody;
        if (dialogue.ExactCount != 1
            || dialogue.GroupCounts["decisionArgumentEntries"] != 2
            || dialogue.GroupCounts["decisionTreeBytes"] != 36
            || dialogue.GroupCounts["groupCEntries"] != 1
            || dialogue.SelectorCounts["decisionMode_00"] != 1)
        {
            throw new InvalidOperationException("type 0x0F body frame census mismatch");
        }
        if (FrameMusicFixture(0x0F, BuildDialogueBody()).Type0FBody.MinExactBytes != 12)
        {
            throw new InvalidOperationException("type 0x0F minimal body length mismatch");
        }
    }

    private static void TestEffectDeviceModulatorAndDialogueBodiesFailClosed()
    {
        var overrun = BuildFxBody(paramBytes: 4);
        BinaryPrimitives.WriteUInt32LittleEndian(overrun.AsSpan(4, 4), 0x7FFFFFFF);
        if (!FrameMusicFixture(0x10, overrun).Type10Body.FailureCounts.ContainsKey("range_fxParamBytes"))
        {
            throw new InvalidOperationException("effect parameter overrun was not rejected");
        }
        var trailing = new byte[BuildModulatorBody().Length + 1];
        if (!FrameMusicFixture(0x13, trailing).Type13Body.FailureCounts.ContainsKey("trailing_bytes"))
        {
            throw new InvalidOperationException("trailing modulator byte was not rejected");
        }
        var truncated = BuildDialogueBody(treeBytes: 12)[..^2];
        if (!FrameMusicFixture(0x0F, truncated).Type0FBody.FailureCounts.ContainsKey("truncated_groupDCount")
            && !FrameMusicFixture(0x0F, truncated).Type0FBody.FailureCounts.ContainsKey("truncated_groupCCount"))
        {
            throw new InvalidOperationException("truncated dialogue event was not rejected");
        }
        var wrongVersion = EndfieldAkpkPackage.Parse(
            BuildEncryptedBankPackage(0xA500, BuildBnk(149, (0x15, 0xA501U, BuildFxBody(deviceSlots: 0)))));
        if (!wrongVersion.BnkStructures[0].Type15Body.UnsupportedCategories.ContainsKey("unsupported_bank_version"))
        {
            throw new InvalidOperationException("non-current bank version was not held unsupported");
        }
    }

    private static EndfieldBnkStructure FrameType9Fixture(byte[] body)
    {
        return EndfieldAkpkPackage
            .Parse(BuildEncryptedBankPackage(0x9300, BuildBnk((0x09, 0x9301U, body))))
            .BnkStructures[0];
    }

    private static byte[] BuildType9Body(
        uint childEntries = 0,
        (ushort curveEntries, ushort curvePoints, uint[] assocPoints)[]? layers = null,
        byte tail = 0,
        byte groupHStates = 0,
        ushort groupHStateElements = 1)
    {
        var node = BuildType2Body(
            groupHStates: groupHStates,
            groupHStateElements: groupHStateElements,
            writePrefix: false);
        layers ??= Array.Empty<(ushort, ushort, uint[])>();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(node);
        writer.Write(childEntries);
        writer.Write(new byte[checked((int)childEntries * 4)]);
        writer.Write((uint)layers.Length);
        foreach (var (curveEntries, curvePoints, assocPoints) in layers)
        {
            writer.Write(0x1234U); // ulLayerID.
            writer.Write(curveEntries);
            for (var i = 0; i < curveEntries; i++)
            {
                writer.Write(new byte[6]);
                writer.Write((byte)0); // One-byte ParamID.
                writer.Write(new byte[5]);
                writer.Write(curvePoints);
                writer.Write(new byte[curvePoints * 12]);
            }
            writer.Write(new byte[5]); // rtpcID and rtpcType.
            writer.Write((uint)assocPoints.Length);
            foreach (var points in assocPoints)
            {
                writer.Write(0x5678U); // ulAssociatedChildID.
                writer.Write(points);
                writer.Write(new byte[checked((int)points * 12)]);
            }
        }
        writer.Write(tail);
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
