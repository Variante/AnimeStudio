using System.Collections.Specialized;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text;
using AnimeStudio;
using AnimeStudio.CLI;
using AnimeStudio.Endfield;

static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length >= 3 && string.Equals(args[0], "lua-sweep", StringComparison.OrdinalIgnoreCase))
        {
            return RunLuaSweep(args[1], args[2]);
        }
        TestEnemySettlementBattleGraphPayload();
        TestObservedEnemySettlementBattleGraphVariants();
        TestEnemySettlementBattleGraphPayloadRejectsTrailingBytes();
        TestObservedEnemyCastSkillResponsePayload();
        TestTruncatedEnemyCastSkillResponseReportsExactCursor();
        TestObservedEnemySimpleAttackPayloads();
        TestObservedEnemyCheckGameplayTagPayload();
        TestObservedUIToggleSetValuePayload();
        TestExactGuideRemainingPayloads();
        TestExactGuideCameraBlendPayloads();
        TestExactCameraControlLockEnemyPayloads();
        TestExactAbilitySystemForEnemyPartPayload();
        TestExactEnemyPartsRootPayload();
        TestManagedReferenceRegistryValidation();
        TestManagedReferenceRegistryTypeTreeGate();
        TestValidationFailureRegistryRecovery();
        TestAbilitySystemModeWeaponVisibilityProfile();
        TestAbilitySystemSkillDataBundleExactSerializedLayout();
        TestLineFollowerSerializedTypeTreeLayout();
        TestEndfieldVfsTerrainTypeRegistry();
        TestEndfieldVfsUnknownTypePreservation();
        TestEndfieldVfsIntegrityGuards();
        TestEndfieldVfsCatalogInvariants();
        TestEndfieldVfsMd5Verification();
        TestEndfieldVfsAuditSyntheticFixtures();
        StreamExtensionsTests.Run();
        VFSDirectoryInfoTests.Run();
        VFSFileType5Tests.Run();
        VFSInnerStructureTests.Run();
        TestEndfieldCompressDataRecords();
        TestEndfieldCompressDataRejectsMalformedContainers();
        TestEndfieldCompressDataCliOutput();
        TestEndfieldLuaDecoderObservedWrappers();
        TestEndfieldLuaDecoderRejectsMalformedFrames();
        TestEndfieldUsmInspectionAndFramingGuards();
        EndfieldSparkBufferTests.Run();
        Console.WriteLine("Managed-reference and VFS recovery tests passed.");
        return 0;
    }

    private static int RunLuaSweep(string inputPath, string outputPath)
    {
        var raw = File.ReadAllBytes(inputPath);
        var text = raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE
            ? System.Text.Encoding.Unicode.GetString(raw, 2, raw.Length - 2)
            : System.Text.Encoding.UTF8.GetString(raw);
        var rows = new List<Dictionary<string, object?>>();
        var wrapperCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var statusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var failureCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var totalSourceBytes = 0L;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var jsonLine = line.TrimEnd('\r').TrimStart();
            if (!jsonLine.StartsWith("{", StringComparison.Ordinal))
            {
                continue;
            }
            using var document = JsonDocument.Parse(jsonLine);
            var root = document.RootElement;
            var fileName = root.GetProperty("fileName").GetString() ?? string.Empty;
            var source = Convert.FromBase64String(root.GetProperty("dataBase64").GetString() ?? string.Empty);
            totalSourceBytes += source.Length;
            var row = new Dictionary<string, object?>
            {
                ["fileName"] = fileName,
                ["sourceLength"] = source.Length,
                ["declaredLength"] = root.TryGetProperty("length", out var declared) ? declared.GetInt64() : null,
            };
            try
            {
                var result = EndfieldLuaDecoder.Decode(source, fileName);
                var variant = result.WrapperVariant == EndfieldLuaWrapperVariant.Base64Xxtea
                    ? "base64_xxtea"
                    : "plain_utf8";
                Increment(wrapperCounts, variant);
                Increment(statusCounts, result.TerminalStatus);
                row["wrapperVariant"] = variant;
                row["terminalStatus"] = result.TerminalStatus;
                row["sourceSha256"] = result.SourceSha256;
                row["decodedSha256"] = result.DecodedSha256;
                row["decodedLength"] = result.DecodedBytes.Length;
                row["cipherLength"] = result.CipherBytes.Length;
                if (result.CipherBytes.Length != 0)
                {
                    row["cipherSha256"] = result.CipherSha256;
                }
                if (result.LexicalIndex is not null)
                {
                    row["lexical"] = new Dictionary<string, object?>
                    {
                        ["valid"] = result.LexicalIndex.IsValid,
                        ["tokenCount"] = result.LexicalIndex.TokenCount,
                        ["identifierCount"] = result.LexicalIndex.IdentifierCount,
                        ["callCount"] = result.LexicalIndex.CallCount,
                        ["stringCount"] = result.LexicalIndex.StringCount,
                    };
                }
            }
            catch (EndfieldLuaDecodeException e)
            {
                Increment(failureCounts, e.Code);
                row["terminalStatus"] = "failed";
                row["failure"] = new Dictionary<string, object?>
                {
                    ["code"] = e.Code,
                    ["offset"] = e.Offset,
                    ["message"] = e.Message,
                };
            }
            rows.Add(row);
        }

        var report = new Dictionary<string, object?>
        {
            ["schemaVersion"] = "lua-vfs-certification-v1",
            ["source"] = new Dictionary<string, object?>
            {
                ["streamJsonl"] = inputPath,
                ["sourceEncoding"] = raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE ? "utf-16le" : "utf-8",
            },
            ["totals"] = new Dictionary<string, object?>
            {
                ["inputRows"] = rows.Count,
                ["sourceBytes"] = totalSourceBytes,
                ["decodedRows"] = rows.Count(row => row.ContainsKey("decodedSha256")),
                ["failures"] = rows.Count(row => row.ContainsKey("failure")),
            },
            ["wrapperVariants"] = wrapperCounts,
            ["terminalStatuses"] = statusCounts,
            ["failuresByCode"] = failureCounts,
            ["rows"] = rows,
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var parent = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        File.WriteAllText(outputPath, json, new UTF8Encoding(false));
        Console.WriteLine($"Lua certification: {rows.Count} rows, {wrapperCounts.GetValueOrDefault("base64_xxtea")} Base64+XXTEA, {wrapperCounts.GetValueOrDefault("plain_utf8")} plain UTF-8, {rows.Count(row => row.ContainsKey("failure"))} failures");
        return rows.Any(row => row.ContainsKey("failure")) ? 1 : 0;
    }

    private static void Increment(Dictionary<string, int> counts, string key)
    {
        counts[key] = counts.GetValueOrDefault(key) + 1;
    }

    private static void TestEndfieldLuaDecoderObservedWrappers()
    {
        // Exact bytes from the installed VFS corpus. The outer payload is
        // UTF-8 Base64; the 8-byte ciphertext decrypts to one LF byte plus
        // the XXTEA logical-length word.
        var observed = EndfieldLuaDecoder.Decode(
            System.Text.Encoding.UTF8.GetBytes("opkmRFNE6wo="),
            "Data/LuaScripts/Const/DialogConst.lua");
        AssertEqual(EndfieldLuaWrapperVariant.Base64Xxtea, observed.WrapperVariant, "observed Lua wrapper");
        AssertBytesEqual(new byte[] { 0x0A }, observed.DecodedBytes, "observed Lua plaintext");
        AssertEqual("decoded_lua_lexical_index", observed.TerminalStatus, "observed Lua terminal status");
        if (observed.LexicalIndex is null || !observed.LexicalIndex.IsValid)
        {
            throw new InvalidOperationException("observed Lua source must pass lexical validation");
        }
        AssertEqual(0, observed.LexicalIndex.TokenCount, "empty observed Lua token count");

        var markdown = EndfieldLuaDecoder.Decode(
            System.Text.Encoding.UTF8.GetBytes("# ChangePortableDeviceCtrl\r\n\r\n说明\n"),
            "Data/LuaScripts/UI/Panels/ChangePortableDevice/ChangePortableDeviceCtrl.md");
        AssertEqual(EndfieldLuaWrapperVariant.PlainUtf8, markdown.WrapperVariant, "observed plain wrapper");
        AssertBytesEqual(
            System.Text.Encoding.UTF8.GetBytes("# ChangePortableDeviceCtrl\r\n\r\n说明\n"),
            markdown.DecodedBytes,
            "plain wrapper preserves bytes");
        AssertEqual("plain_utf8_non_lua", markdown.TerminalStatus, "plain wrapper terminal status");
        if (markdown.CipherBytes.Length != 0 || markdown.LexicalIndex is not null)
        {
            throw new InvalidOperationException("plain wrapper must not claim XXTEA or Lua semantics");
        }
    }

    private static void TestEndfieldLuaDecoderRejectsMalformedFrames()
    {
        AssertThrowsWithMessage<EndfieldLuaDecodeException>(
            () => EndfieldLuaDecoder.Decode(
                System.Text.Encoding.UTF8.GetBytes("not!"),
                "Data/LuaScripts/Bad.lua"),
            "lua.wrapper.base64.character",
            "strict Lua wrapper rejects invalid Base64 characters");
        AssertThrowsWithMessage<EndfieldLuaDecodeException>(
            () => EndfieldLuaDecoder.Decode(
                System.Text.Encoding.UTF8.GetBytes("AAAA"),
                "Data/LuaScripts/Short.lua"),
            "lua.wrapper.xxtea.frame",
            "strict Lua wrapper rejects short XXTEA frames");

        var corruptedCipher = Convert.FromBase64String("opkmRFNE6wo=");
        corruptedCipher[0] ^= 0x80;
        AssertThrowsWithMessage<EndfieldLuaDecodeException>(
            () => EndfieldLuaDecoder.Decode(
                System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(corruptedCipher)),
                "Data/LuaScripts/Corrupt.lua"),
            "lua.xxtea.",
            "strict Lua wrapper rejects wrong-key/corrupt output");

        var lexical = EndfieldLuaLexicalScanner.Scan("return broken(");
        AssertEqual(false, lexical.IsValid, "lexical negative fixture status");
        AssertEqual("lua.lexical.unclosed_delimiter", lexical.DiagnosticCode, "lexical negative fixture diagnostic");
        AssertEqual(13, lexical.DiagnosticOffset, "lexical negative fixture offset");
    }

    private static void TestEndfieldUsmInspectionAndFramingGuards()
    {
        var valid = BuildSyntheticUsm(
            BuildUsmBlock("CRID", 0, payloadLength: 0),
            BuildUsmBlock("@SFV", 0, payloadLength: 16));
        var inspection = EndfieldUsmConverter.Inspect(valid);
        AssertEqual(valid.Length, inspection.ByteLength, "USM inspected byte length");
        AssertEqual(2, inspection.BlockCount, "USM inspected block count");
        AssertEqual(1, inspection.BlockCounts["@SFV"], "USM video block count");
        AssertEqual((byte)0, inspection.VideoStreamIds[0], "USM video stream identity");

        var unknown = (byte[])valid.Clone();
        unknown[32] = (byte)'Z';
        AssertThrowsWithMessage<EndfieldVfsException>(
            () => EndfieldUsmConverter.Inspect(unknown),
            "unknown block id",
            "USM rejects unknown outer block");

        var missingFirstCrid = BuildSyntheticUsm(
            BuildUsmBlock("@SFV", 0, payloadLength: 0),
            BuildUsmBlock("CRID", 0, payloadLength: 0));
        AssertThrowsWithMessage<EndfieldVfsException>(
            () => EndfieldUsmConverter.Inspect(missingFirstCrid),
            "first block must be CRID",
            "USM requires CRID as the first outer block");

        var truncated = valid[..^1];
        AssertThrowsWithMessage<EndfieldVfsException>(
            () => EndfieldUsmConverter.Inspect(truncated),
            "overruns input",
            "USM rejects short final block");

        var invalidHeader = BuildSyntheticUsm(
            BuildUsmBlock("CRID", 0, payloadLength: 20, headerSize: 4),
            BuildUsmBlock("@SFV", 0, payloadLength: 0));
        AssertThrowsWithMessage<EndfieldVfsException>(
            () => EndfieldUsmConverter.Inspect(invalidHeader),
            "invalid header size",
            "USM rejects undersized block header");

        var unobservedHeader = BuildSyntheticUsm(
            BuildUsmBlock("CRID", 0, payloadLength: 4, headerSize: 20),
            BuildUsmBlock("@SFV", 0, payloadLength: 0));
        AssertThrowsWithMessage<EndfieldVfsException>(
            () => EndfieldUsmConverter.Inspect(unobservedHeader),
            "expected=24",
            "USM rejects unobserved block header size");

        var multipleVideoStreams = BuildSyntheticUsm(
            BuildUsmBlock("CRID", 0, payloadLength: 0),
            BuildUsmBlock("@SFV", 0, payloadLength: 0),
            BuildUsmBlock("@SFV", 1, payloadLength: 0));
        AssertThrowsWithMessage<EndfieldVfsException>(
            () => EndfieldUsmConverter.Inspect(multipleVideoStreams),
            "multiple video streams",
            "USM rejects silently merged video streams");

        var multipleAudioStreams = BuildSyntheticUsm(
            BuildUsmBlock("CRID", 0, payloadLength: 0),
            BuildUsmBlock("@SFV", 0, payloadLength: 0),
            BuildUsmBlock("@SFA", 0, payloadLength: 0),
            BuildUsmBlock("@SFA", 1, payloadLength: 0));
        AssertThrowsWithMessage<EndfieldVfsException>(
            () => EndfieldUsmConverter.Inspect(multipleAudioStreams),
            "multiple audio streams",
            "USM rejects silently merged audio streams");
    }

    private static byte[] BuildSyntheticUsm(params byte[][] blocks)
    {
        using var output = new MemoryStream();
        foreach (var block in blocks)
        {
            output.Write(block);
        }
        return output.ToArray();
    }

    private static byte[] BuildUsmBlock(string id, byte streamId, int payloadLength, int headerSize = 24)
    {
        var body = new byte[headerSize + payloadLength];
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(0, 2), checked((ushort)headerSize));
        body[4] = streamId;
        var idBytes = Encoding.ASCII.GetBytes(id);
        using var output = new MemoryStream();
        output.Write(idBytes);
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, checked((uint)body.Length));
        output.Write(size);
        output.Write(body);
        return output.ToArray();
    }

    private static void TestEndfieldVfsTerrainTypeRegistry()
    {
        AssertEqual((byte)22, (byte)EndfieldVfsBlockType.Terrain, "Terrain VFS type ID");
        AssertEqual("Terrain", EndfieldVfsBlockType.Terrain.GetName(), "Terrain VFS type name");
        AssertEqual(
            "F84BF5E6",
            EndfieldVfsHash.VfsBlockHash("Terrain", EndfieldVfsKeys.UnityHashSecret),
            "Terrain VFS block hash");
        if (!EndfieldVfsBlockTypes.TryParseCliValue("terrain", out var parsed)
            || parsed != EndfieldVfsBlockType.Terrain)
        {
            throw new InvalidOperationException("Terrain must be selectable by the VFS CLI parser");
        }
        if (!EndfieldVfsBlockTypes.AllDumpable.Contains(EndfieldVfsBlockType.Terrain))
        {
            throw new InvalidOperationException("Terrain must be listed among dumpable VFS types");
        }
    }

    private static void TestEndfieldVfsUnknownTypePreservation()
    {
        const byte blockTypeValue = 222;
        const byte chunkTypeValue = blockTypeValue;
        const byte fileTypeValue = blockTypeValue;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(4); // current installed metadata code version
            writer.Write(24764758); // current populated-block metadata version
            writer.Write((ushort)0);
            writer.Write(0L);
            writer.Write(1);
            writer.Write(4L);
            writer.Write(blockTypeValue);
            writer.Write(1); // chunk count
            writer.Write(new byte[16]);
            writer.Write(new byte[16]);
            writer.Write(4L);
            writer.Write(chunkTypeValue);
            writer.Write(0); // MainTag for code version 4
            writer.Write(1); // file count
            writer.Write((ushort)1);
            writer.Write((byte)'x');
            writer.Write(0L);
            writer.Write(new byte[16]);
            writer.Write(new byte[16]);
            writer.Write(0L);
            writer.Write(4L);
            writer.Write(fileTypeValue);
            writer.Write((byte)0); // not encrypted
            writer.Write(0); // FileTag for code version 4
        }

        var parsed = EndfieldVfsLoader.ParseBlockInfo(stream.ToArray(), verifyCrc: false);
        AssertEqual(EndfieldVfsBlockType.Raw, parsed.BlockType, "unknown block enum compatibility");
        AssertEqual(blockTypeValue, parsed.BlockTypeValue, "unknown block ID preservation");
        AssertEqual($"Unknown({blockTypeValue})", EndfieldVfsBlockTypes.GetName(parsed.BlockTypeValue), "unknown block display name");
        AssertEqual(chunkTypeValue, parsed.Chunks[0].BlockTypeValue, "unknown chunk ID preservation");
        AssertEqual(fileTypeValue, parsed.Chunks[0].Files[0].BlockTypeValue, "unknown file ID preservation");
        AssertEqual($"Unknown({fileTypeValue})", EndfieldVfsBlockTypes.GetName(parsed.Chunks[0].Files[0].BlockTypeValue), "unknown file display name");
    }

    private static void TestEndfieldVfsIntegrityGuards()
    {
        AssertThrows<EndfieldVfsException>(
            () => EndfieldVfsLoader.ParseBlockInfo(new byte[] { 11, 0 }, verifyCrc: false),
            "VFS rejects truncated metadata");
        var truncatedMetadata = BuildVfsMetadata(writer =>
        {
            writer.Write(1); // chunk count
            WriteVfsChunk(writer, 1, ("truncated", 0, 1));
        });
        AssertThrows<EndfieldVfsException>(
            () => EndfieldVfsLoader.ParseBlockInfo(truncatedMetadata[..^1], verifyCrc: false),
            "VFS rejects truncation inside a file record");
        AssertThrows<EndfieldVfsException>(
            () => EndfieldVfsLoader.ParseBlockInfo(BuildVfsMetadata(writer => writer.Write(1)), verifyCrc: false),
            "VFS rejects a chunk count larger than remaining metadata");
        AssertThrows<EndfieldVfsException>(
            () => EndfieldVfsLoader.ParseBlockInfo(BuildVfsMetadata(writer => writer.Write(int.MaxValue)), verifyCrc: false),
            "VFS rejects a chunk count overflow");
        AssertThrows<EndfieldVfsException>(
            () => EndfieldVfsLoader.ParseBlockInfo(
                BuildVfsMetadata(writer =>
                {
                    writer.Write(1); // chunk count
                    WriteVfsChunk(writer, -1);
                }),
                verifyCrc: false),
            "VFS rejects a negative chunk length");
        AssertThrows<EndfieldVfsException>(
            () => EndfieldVfsLoader.ParseBlockInfo(
                BuildVfsMetadata(writer =>
                {
                    writer.Write(1); // chunk count
                    WriteVfsChunk(writer, 4, ("out-of-range", 3, 2));
                }),
                verifyCrc: false),
            "VFS rejects a file range beyond its chunk");
        AssertThrows<EndfieldVfsException>(
            () => EndfieldVfsLoader.ParseBlockInfo(
                BuildVfsMetadata(writer =>
                {
                    writer.Write(1); // chunk count
                    WriteVfsChunk(writer, 8, ("first", 0, 5), ("overlap", 4, 2));
                }),
                verifyCrc: false),
            "VFS rejects overlapping file ranges");
        AssertThrows<EndfieldVfsException>(
            () => EndfieldVfsLoader.ParseBlockInfo(
                BuildVfsMetadata(writer =>
                {
                    writer.Write(1); // chunk count
                    WriteVfsChunk(writer, 4, ("negative", 0, -1));
                }),
                verifyCrc: false),
            "VFS rejects a negative file length");
        AssertThrows<EndfieldVfsException>(
            () => EndfieldVfsLoader.ParseBlockInfo(
                BuildVfsMetadata(writer =>
                {
                    writer.Write(1); // chunk count
                    WriteVfsChunkHeader(writer, 0, fileCount: int.MaxValue);
                }),
                verifyCrc: false),
            "VFS rejects a file count overflow");

        AssertThrows<EndfieldVfsException>(
            () => EndfieldVfsLoader.CopyRange(
                new MemoryStream(new byte[] { 1, 2, 3 }),
                new MemoryStream(),
                4,
                cipher: null),
            "VFS rejects a short chunk range read");

        var root = Path.Combine(Path.GetTempPath(), $"animestudio-vfs-integrity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var loader = new EndfieldVfsLoader(root);
            var missingChunk = new EndfieldVfsChunkInfo
            {
                BlockType = EndfieldVfsBlockType.Terrain,
                BlockTypeValue = (byte)EndfieldVfsBlockType.Terrain,
                Length = 1,
            };
            AssertThrows<EndfieldVfsChunkNotFoundException>(
                () => loader.ResolveChunkPath(EndfieldVfsBlockType.Terrain, missingChunk),
                "VFS reports a missing chunk");

            var contained = EndfieldDumpProcessors.ResolveContainedPath(root, "Table/ok.json");
            if (!contained.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("VFS output containment accepted an unexpected path");
            }
            AssertThrows<EndfieldVfsException>(
                () => EndfieldDumpProcessors.ResolveContainedPath(root, "../escape.json"),
                "VFS rejects output path traversal");

            WriteEncryptedVfsBlockWithMissingChunk(root);
            AssertCliFailsForSelectedVfsFile(
                new[] { "dump", "--streaming-assets", root, "--block-type", "Terrain", "--output", Path.Combine(root, "dump") },
                "VFS dump fails when a selected chunk is missing");
            AssertCliFailsForSelectedVfsFile(
                new[] { "stream", "--streaming-assets", root, "--block-type", "Terrain" },
                "VFS stream fails when a selected chunk is missing");
            AssertCliFailsForSelectedVfsFile(
                new[] { "vfs-index", "--streaming-assets", root, "--block-type", "Terrain", "--output", Path.Combine(root, "index.json") },
                "VFS index fails when a selected chunk is missing");
            var failedIndexPath = Path.Combine(root, "index.json");
            if (File.Exists(failedIndexPath)
                || Directory.GetFiles(root, ".index.json.*.tmp", SearchOption.TopDirectoryOnly).Length != 0)
            {
                throw new InvalidOperationException("failed VFS index must not publish or retain a temporary output");
            }

            var failedJsonlPath = Path.Combine(root, "index.jsonl");
            AssertCliFailsForSelectedVfsFile(
                new[] { "vfs-index", "--jsonl", "--streaming-assets", root, "--block-type", "Terrain", "--output", failedJsonlPath },
                "VFS JSONL index fails when a selected chunk is missing");
            if (File.Exists(failedJsonlPath)
                || Directory.GetFiles(root, ".index.jsonl.*.tmp", SearchOption.TopDirectoryOnly).Length != 0)
            {
                throw new InvalidOperationException("failed JSONL VFS index must not publish or retain a temporary output");
            }

            var blockName = EndfieldVfsHash.VfsBlockHash("Terrain", EndfieldVfsKeys.UnityHashSecret);
            var chunkPath = Path.Combine(
                root,
                "VFS",
                blockName,
                "00000000000000000000000000000000.chk");
            File.WriteAllBytes(chunkPath, new byte[] { 0x42 });
            var jsonlPath = Path.Combine(root, "index.jsonl");
            AssertCliFailsForSelectedVfsFile(
                new[] { "dump", "--verify-md5", "--streaming-assets", root, "--block-type", "Terrain", "--output", Path.Combine(root, "verify-dump") },
                "VFS dump verifies selected chunk and file MD5 values");
            AssertCliFailsForSelectedVfsFile(
                new[] { "stream", "--verify-md5", "--streaming-assets", root, "--block-type", "Terrain" },
                "VFS stream verifies selected chunk and file MD5 values");
            AssertCliFailsForSelectedVfsFile(
                new[] { "vfs-index", "--verify-md5", "--streaming-assets", root, "--block-type", "Terrain", "--output", Path.Combine(root, "verify-index.json") },
                "VFS index verifies selected chunk and file MD5 values");
            if (!EndfieldVfsCli.TryRun(
                    new[] { "vfs-index", "--streaming-assets", root, "--block-type", "Terrain", "--output", failedIndexPath },
                    out var successfulIndexExit)
                || successfulIndexExit != 0
                || !File.Exists(failedIndexPath)
                || Directory.GetFiles(root, ".index.json.*.tmp", SearchOption.TopDirectoryOnly).Length != 0)
            {
                throw new InvalidOperationException("successful VFS index must atomically publish its final output");
            }

            if (!EndfieldVfsCli.TryRun(
                    new[] { "vfs-index", "--jsonl", "--streaming-assets", root, "--block-type", "Terrain", "--output", jsonlPath },
                    out var successfulJsonlExit)
                || successfulJsonlExit != 0
                || !File.Exists(jsonlPath)
                || Directory.GetFiles(root, ".index.jsonl.*.tmp", SearchOption.TopDirectoryOnly).Length != 0)
            {
                throw new InvalidOperationException("successful JSONL VFS index must atomically publish its final output");
            }

            var voiceRoot = Path.Combine(Path.GetTempPath(), $"animestudio-vfs-voice-{Guid.NewGuid():N}");
            var auditAudioRoot = Path.Combine(Path.GetTempPath(), $"animestudio-vfs-audit-audio-{Guid.NewGuid():N}");
            Directory.CreateDirectory(voiceRoot);
            Directory.CreateDirectory(auditAudioRoot);
            try
            {
                WriteEncryptedVfsBlockWithMissingChunk(voiceRoot, EndfieldVfsBlockType.AudioEnglish);
                if (!EndfieldVfsCli.TryRun(
                        new[] { "stream", "--verify-md5", "--streaming-assets", voiceRoot },
                        out var voiceExit)
                    || voiceExit != 0)
                {
                    throw new InvalidOperationException("default-all verification must exclude English voice failures");
                }

                WriteEncryptedVfsBlockWithMissingChunk(auditAudioRoot, EndfieldVfsBlockType.AuditAudio);
                AssertCliFailsForSelectedVfsFile(
                    new[] { "stream", "--verify-md5", "--streaming-assets", auditAudioRoot },
                    "default-all verification must fail for unavailable AuditAudio");
            }
            finally
            {
                if (Directory.Exists(voiceRoot))
                {
                    Directory.Delete(voiceRoot, recursive: true);
                }
                if (Directory.Exists(auditAudioRoot))
                {
                    Directory.Delete(auditAudioRoot, recursive: true);
                }
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void TestEndfieldVfsCatalogInvariants()
    {
        var valid = BuildVfsMetadata(2, 8, writer =>
        {
            writer.Write(1); // chunk count
            WriteVfsChunk(writer, 8, ("first", 0, 5), ("second", 5, 3));
        });
        var parsed = EndfieldVfsLoader.ParseBlockInfo(valid, verifyCrc: false);
        AssertEqual(2, parsed.GroupFileInfoNum, "VFS group file count invariant");
        AssertEqual(8L, parsed.GroupChunksLength, "VFS group chunk length invariant");

        AssertThrowsWithMessage<EndfieldVfsException>(
            () => EndfieldVfsLoader.ParseBlockInfo(
                BuildVfsMetadata(1, 8, writer =>
                {
                    writer.Write(1);
                    WriteVfsChunk(writer, 8, ("first", 0, 5), ("second", 5, 3));
                }),
                verifyCrc: false),
            "group_file_info_num",
            "VFS rejects a group file count mismatch");
        AssertThrowsWithMessage<EndfieldVfsException>(
            () => EndfieldVfsLoader.ParseBlockInfo(
                BuildVfsMetadata(2, 7, writer =>
                {
                    writer.Write(1);
                    WriteVfsChunk(writer, 8, ("first", 0, 5), ("second", 5, 3));
                }),
                verifyCrc: false),
            "group_chunks_length",
            "VFS rejects a group chunk length mismatch");
        AssertThrowsWithMessage<EndfieldVfsException>(
            () => EndfieldVfsLoader.ParseBlockInfo(
                BuildVfsMetadata(2, 2, writer =>
                {
                    writer.Write(1);
                    WriteVfsChunkWithTypes(writer, 2, (byte)EndfieldVfsBlockType.Terrain, 21, ("first", 0, 1), ("second", 1, 1));
                }),
                verifyCrc: false),
            "does not match block type",
            "VFS rejects a file type inconsistent with its block");
        AssertThrowsWithMessage<EndfieldVfsException>(
            () => EndfieldVfsLoader.ParseBlockInfo(
                BuildVfsMetadata(1, 1, writer =>
                {
                    writer.Write(1);
                    WriteVfsChunkWithTypes(writer, 1, 21, 21, ("first", 0, 1));
                }),
                verifyCrc: false),
            "chunk 0 type",
            "VFS rejects a chunk type inconsistent with its block");
        AssertThrowsWithMessage<EndfieldVfsException>(
            () => EndfieldVfsLoader.ParseBlockInfo(
                BuildVfsMetadata(2, 2, writer =>
                {
                    writer.Write(1);
                    WriteVfsChunk(writer, 2, ("duplicate", 0, 1), ("duplicate", 1, 1));
                }),
                verifyCrc: false),
            "duplicate virtual filename",
            "VFS rejects duplicate virtual filenames");

        using var invalidUtf8 = new MemoryStream();
        using (var writer = new BinaryWriter(invalidUtf8, System.Text.Encoding.UTF8, true))
        {
            writer.Write(11);
            writer.Write((ushort)1);
            writer.Write((byte)0xff);
            writer.Write(0L);
            writer.Write(0);
            writer.Write(0L);
            writer.Write((byte)EndfieldVfsBlockType.Terrain);
            writer.Write(0); // chunk count
        }
        AssertThrowsWithMessage<EndfieldVfsException>(
            () => EndfieldVfsLoader.ParseBlockInfo(invalidUtf8.ToArray(), verifyCrc: false),
            "invalid UTF-8",
            "VFS rejects invalid UTF-8 metadata strings");
    }

    private static void TestEndfieldVfsMd5Verification()
    {
        var root = Path.Combine(Path.GetTempPath(), $"animestudio-vfs-md5-{Guid.NewGuid():N}");
        var blockName = EndfieldVfsHash.VfsBlockHash("Terrain", EndfieldVfsKeys.UnityHashSecret);
        var blockDirectory = Path.Combine(root, "VFS", blockName);
        var chunkName = "00000000000000000000000000000000.chk";
        var chunkPath = Path.Combine(blockDirectory, chunkName);
        Directory.CreateDirectory(blockDirectory);
        try
        {
            var payload = new byte[] { 0x42 };
            File.WriteAllBytes(chunkPath, payload);
            var digest = UInt128FromLittleEndian(System.Security.Cryptography.MD5.HashData(payload));
            var chunk = new EndfieldVfsChunkInfo
            {
                Md5Name = 0,
                ContentMd5 = digest,
                Length = payload.Length,
                BlockType = EndfieldVfsBlockType.Terrain,
                BlockTypeValue = (byte)EndfieldVfsBlockType.Terrain,
            };
            var file = new EndfieldVfsFileInfo
            {
                FileName = "payload.bin",
                Offset = 0,
                Length = payload.Length,
                FileDataMd5 = digest,
                BlockType = EndfieldVfsBlockType.Terrain,
                BlockTypeValue = (byte)EndfieldVfsBlockType.Terrain,
            };
            var loader = new EndfieldVfsLoader(root);

            loader.VerifyChunkContentMd5(EndfieldVfsBlockType.Terrain, chunk);
            var verifiedPayload = loader.ExtractFileToBytes(EndfieldVfsBlockType.Terrain, chunk, file, verifyMd5: true);
            if (!payload.SequenceEqual(verifiedPayload))
            {
                throw new InvalidOperationException("VFS verified payload differed from the source bytes");
            }

            // The second check uses the cached physical digest, even after the source changes.
            File.WriteAllBytes(chunkPath, new byte[] { 0x43 });
            loader.VerifyChunkContentMd5(EndfieldVfsBlockType.Terrain, chunk);
            AssertThrowsWithMessage<EndfieldVfsException>(
                () => loader.ExtractFileToBytes(EndfieldVfsBlockType.Terrain, chunk, file, verifyMd5: true),
                "DataMd5 for",
                "VFS rejects a mismatched decrypted file digest");

            var badChunk = new EndfieldVfsChunkInfo
            {
                Md5Name = chunk.Md5Name,
                ContentMd5 = 0,
                Length = chunk.Length,
                BlockType = chunk.BlockType,
                BlockTypeValue = chunk.BlockTypeValue,
            };
            AssertThrowsWithMessage<EndfieldVfsException>(
                () => loader.VerifyChunkContentMd5(EndfieldVfsBlockType.Terrain, badChunk),
                "ContentMd5 for",
                "VFS rejects a mismatched raw chunk digest");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void TestEndfieldVfsAuditSyntheticFixtures()
    {
        TestEndfieldVfsAuditCurrentMetadataAndDeterminism();

        var cases = new (string Name, Action<string> Setup, string Expected)[]
        {
            ("crc", root =>
            {
                PrepareAuditCatalog(root);
                WriteAuditMetadata(root, EndfieldVfsBlockType.Terrain,
                    BuildAuditMetadata(EndfieldVfsBlockType.Terrain, writer => writer.Write(0)), corruptCrc: true);
            }, "CRC mismatch"),
            ("unsupported-version", root =>
            {
                PrepareAuditCatalog(root);
                WriteAuditMetadata(root, EndfieldVfsBlockType.Terrain,
                    BuildUnsupportedAuditMetadata());
            }, "unsupported code version"),
            ("count-overflow", root =>
            {
                PrepareAuditCatalog(root);
                WriteAuditMetadata(root, EndfieldVfsBlockType.Terrain,
                    BuildAuditMetadata(EndfieldVfsBlockType.Terrain, writer => writer.Write(int.MaxValue)));
            }, "chunk_count count"),
            ("bounds", root =>
            {
                PrepareAuditCatalog(root);
                var metadata = BuildAuditMetadata(EndfieldVfsBlockType.Terrain, writer =>
                {
                    writer.Write(1);
                    WriteAuditChunkHeader(writer, 4, 1);
                    WriteAuditFile(writer, "out-of-range", 3, 2, 0, 0, EndfieldVfsBlockType.Terrain);
                });
                WriteAuditMetadata(root, EndfieldVfsBlockType.Terrain, metadata);
            }, "invalid file range"),
            ("overlap", root =>
            {
                PrepareAuditCatalog(root);
                var metadata = BuildAuditMetadata(EndfieldVfsBlockType.Terrain, writer =>
                {
                    writer.Write(1);
                    WriteAuditChunkHeader(writer, 8, 2);
                    WriteAuditFile(writer, "first", 0, 5, 0, 0, EndfieldVfsBlockType.Terrain);
                    WriteAuditFile(writer, "overlap", 4, 2, 0, 0, EndfieldVfsBlockType.Terrain);
                });
                WriteAuditMetadata(root, EndfieldVfsBlockType.Terrain, metadata);
            }, "overlapping file ranges"),
            ("duplicate-path", root =>
            {
                PrepareAuditCatalog(root);
                var metadata = BuildAuditMetadata(EndfieldVfsBlockType.Terrain, writer =>
                {
                    writer.Write(1);
                    WriteAuditChunkHeader(writer, 2, 2);
                    WriteAuditFile(writer, "duplicate", 0, 1, 0, 0, EndfieldVfsBlockType.Terrain);
                    WriteAuditFile(writer, "duplicate", 1, 1, 0, 0, EndfieldVfsBlockType.Terrain);
                });
                WriteAuditMetadata(root, EndfieldVfsBlockType.Terrain, metadata);
            }, "duplicate virtual filename"),
            ("normalized-duplicate-path", root =>
            {
                PrepareAuditCatalog(root);
                var physical = new byte[2];
                var chunkDigest = UInt128FromLittleEndian(System.Security.Cryptography.MD5.HashData(physical));
                var fileDigest = UInt128FromLittleEndian(System.Security.Cryptography.MD5.HashData(new byte[1]));
                var metadata = BuildAuditMetadata(EndfieldVfsBlockType.Terrain, 2, 2, writer =>
                {
                    writer.Write(1);
                    WriteAuditChunkHeader(writer, 2, 2, chunkDigest);
                    WriteAuditFile(writer, "same/path.bin", 0, 1, 0, fileDigest, EndfieldVfsBlockType.Terrain);
                    WriteAuditFile(writer, "same\\path.bin", 1, 1, 0, fileDigest, EndfieldVfsBlockType.Terrain);
                });
                WriteAuditMetadata(root, EndfieldVfsBlockType.Terrain, metadata);
                var blockDirectory = Path.Combine(root, "VFS",
                    EndfieldVfsHash.VfsBlockHash(EndfieldVfsBlockType.Terrain.GetName(), EndfieldVfsKeys.UnityHashSecret));
                File.WriteAllBytes(Path.Combine(blockDirectory, "00000000000000000000000000000000.chk"), physical);
            }, "duplicate_logical_path"),
            ("short-read", root =>
            {
                PrepareAuditCatalog(root);
                var payload = new byte[] { 0x41 };
                WriteAuditBlock(root, EndfieldVfsBlockType.Terrain, "short.bin", payload,
                    declaredChunkLength: 2);
            }, "chunk length mismatch"),
            ("chunk-hash-mismatch", root =>
            {
                PrepareAuditCatalog(root);
                var payload = new byte[] { 0x41, 0x42 };
                WriteAuditBlock(root, EndfieldVfsBlockType.Terrain, "hash.bin", payload,
                    chunkContentMd5: 0);
            }, "chunk ContentMd5 mismatch"),
            ("file-hash-mismatch", root =>
            {
                PrepareAuditCatalog(root);
                var payload = new byte[] { 0x41, 0x42 };
                WriteAuditBlock(root, EndfieldVfsBlockType.Terrain, "hash.bin", payload,
                    fileDataMd5: 0);
            }, "file_data_md5_mismatch"),
            ("file-chunk-identity-mismatch", root =>
            {
                PrepareAuditCatalog(root);
                var payload = new byte[] { 0x41, 0x42 };
                WriteAuditBlock(root, EndfieldVfsBlockType.Terrain, "identity.bin", payload,
                    fileChunkMd5: 1);
            }, "file_chunk_identity_mismatch"),
            ("missing-chunk", root =>
            {
                PrepareAuditCatalog(root);
                var payload = new byte[] { 0x41, 0x42 };
                WriteAuditBlock(root, EndfieldVfsBlockType.Terrain, "missing.bin", payload,
                    writeChunk: false);
            }, "missing_both"),
            ("path-traversal", root =>
            {
                PrepareAuditCatalog(root);
                var payload = new byte[] { 0x41, 0x42 };
                WriteAuditBlock(root, EndfieldVfsBlockType.Terrain, "../escape.bin", payload);
            }, "unsafe_logical_path"),
            ("overlay-conflict", root =>
            {
                var fallback = Path.Combine(root, "fallback");
                PrepareAuditCatalog(root);
                PrepareAuditCatalog(fallback);
                WriteAuditBlock(root, EndfieldVfsBlockType.Terrain, "primary.bin", new byte[] { 0x41 });
                WriteAuditBlock(fallback, EndfieldVfsBlockType.Terrain, "fallback.bin", new byte[] { 0x42 });
            }, "overlay_conflict"),
        };

        foreach (var testCase in cases)
        {
            var root = Path.Combine(Path.GetTempPath(), $"animestudio-vfs-audit-{testCase.Name}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                testCase.Setup(root);
                var fallback = testCase.Name == "overlay-conflict"
                    ? Path.Combine(root, "fallback")
                    : null;
                var artifacts = RunAudit(root, testCase.Name, fallback, expectSuccess: false);
                var ledger = ReadGzipText(artifacts.Ledger);
                var summary = File.ReadAllText(artifacts.Summary);
                if (!ledger.Contains(testCase.Expected, StringComparison.Ordinal)
                    && !summary.Contains(testCase.Expected, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"vfs-audit {testCase.Name}: expected ledger diagnostic {testCase.Expected}");
                }
                AssertAuditPublished(artifacts, testCase.Name);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        TestEndfieldVfsAuditUnknownIdAndExcludedVoice();
    }

    private static void TestEndfieldVfsAuditCurrentMetadataAndDeterminism()
    {
        var root = Path.Combine(Path.GetTempPath(), $"animestudio-vfs-audit-positive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            PrepareAuditCatalog(root);
            WriteAuditBlock(root, EndfieldVfsBlockType.Terrain, "terrain.bin", new byte[] { 0x10, 0x20 });
            WriteEncryptedAuditBlock(root, EndfieldVfsBlockType.JsonData, "encrypted.bin",
                new byte[] { 0x31, 0x32, 0x33 }, 0x1020304050607080L);
            var first = RunAudit(root, "first", fallback: null, expectSuccess: true);
            var second = RunAudit(root, "second", fallback: null, expectSuccess: true);

            AssertBytesEqual(File.ReadAllBytes(first.Summary), File.ReadAllBytes(second.Summary),
                "vfs-audit summary is deterministic");
            AssertBytesEqual(File.ReadAllBytes(first.Ledger), File.ReadAllBytes(second.Ledger),
                "vfs-audit ledger is deterministic");
            AssertBytesEqual(File.ReadAllBytes(first.Report), File.ReadAllBytes(second.Report),
                "vfs-audit report is deterministic");
            AssertAuditPublished(first, "current metadata format");
            var summary = File.ReadAllText(first.Summary);
            if (!summary.Contains("\"failureCount\": 0", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("valid current-format vfs-audit fixture unexpectedly failed");
            }
            var ledger = ReadGzipText(first.Ledger);
            if (!ledger.Contains("\"status\":\"verified\"", StringComparison.Ordinal)
                || !ledger.Contains("\"blockName\":\"Terrain\"", StringComparison.Ordinal)
                || !ledger.Contains("\"encrypted\":true", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("valid current-format vfs-audit row was not verified");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void TestEndfieldVfsAuditUnknownIdAndExcludedVoice()
    {
        var root = Path.Combine(Path.GetTempPath(), $"animestudio-vfs-audit-special-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            PrepareAuditCatalog(root);
            const byte unknownType = 222;
            const string unknownName = "SyntheticUnknown";
            var unknownHash = EndfieldVfsHash.VfsBlockHash(unknownName, EndfieldVfsKeys.UnityHashSecret);
            WriteAuditMetadata(root, unknownHash,
                BuildAuditMetadata(unknownName, unknownType, writer => writer.Write(0)));
            WriteAuditBlock(root, EndfieldVfsBlockType.AudioEnglish, "voice/excluded.ogg", new byte[] { 0x42 });

            var artifacts = RunAudit(root, "special", fallback: null, expectSuccess: true);
            var ledger = ReadGzipText(artifacts.Ledger);
            if (!ledger.Contains("\"blockName\":\"Unknown(222)\"", StringComparison.Ordinal)
                || !ledger.Contains("\"blockTypeValue\":222", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("unknown VFS block ID was not preserved by vfs-audit");
            }
            var excludedNeedle = "\"status\":\"excluded_voice\"";
            if (!ledger.Contains(excludedNeedle, StringComparison.Ordinal)
                || !ledger.Contains("voice/excluded.ogg", StringComparison.Ordinal)
                || !ledger.Contains("\"code\":\"excluded_voice\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("excluded English voice file did not emit its exact excluded row");
            }
            AssertAuditPublished(artifacts, "unknown/excluded voice");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static (string Summary, string Ledger, string Report) RunAudit(
        string root,
        string suffix,
        string? fallback,
        bool expectSuccess)
    {
        var summary = Path.Combine(root, $"audit-{suffix}-summary.json");
        var ledger = Path.Combine(root, $"audit-{suffix}-ledger.jsonl.gz");
        var report = Path.Combine(root, $"audit-{suffix}-report.md");
        var args = new List<string>
        {
            "vfs-audit",
            "--streaming-assets", root,
            "--summary-json", summary,
            "--ledger-jsonl-gz", ledger,
            "--report-md", report,
        };
        if (!string.IsNullOrEmpty(fallback))
        {
            args.Add("--fallback-assets");
            args.Add(fallback);
        }

        if (!EndfieldVfsCli.TryRun(args.ToArray(), out var exitCode))
        {
            throw new InvalidOperationException($"vfs-audit {suffix} was not recognized by the CLI");
        }
        if ((exitCode == 0) != expectSuccess)
        {
            throw new InvalidOperationException(
                $"vfs-audit {suffix}: expected success={expectSuccess}, got exit code {exitCode}");
        }
        return (summary, ledger, report);
    }

    private static void AssertAuditPublished((string Summary, string Ledger, string Report) artifacts, string label)
    {
        foreach (var path in new[] { artifacts.Summary, artifacts.Ledger, artifacts.Report })
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                throw new InvalidOperationException($"{label}: vfs-audit did not publish {path}");
            }
        }
        var parent = Path.GetDirectoryName(artifacts.Summary)
            ?? throw new InvalidOperationException("vfs-audit output has no parent");
        foreach (var output in new[] { artifacts.Summary, artifacts.Ledger, artifacts.Report })
        {
            var prefix = "." + Path.GetFileName(output) + ".";
            if (Directory.GetFiles(parent, prefix + "*.tmp", SearchOption.TopDirectoryOnly).Length != 0)
            {
                throw new InvalidOperationException($"{label}: vfs-audit left a temporary file for {output}");
            }
        }
    }

    private static string ReadGzipText(string path)
    {
        using var input = File.OpenRead(path);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, new System.Text.UTF8Encoding(false));
        return reader.ReadToEnd();
    }

    private static void PrepareAuditCatalog(string root)
    {
        foreach (var blockType in EndfieldVfsBlockTypes.AllDumpable)
        {
            WriteAuditMetadata(root, blockType,
                BuildAuditMetadata(blockType, writer => writer.Write(0)));
        }
    }

    private static void WriteAuditBlock(
        string root,
        EndfieldVfsBlockType blockType,
        string fileName,
        byte[] payload,
        long? declaredChunkLength = null,
        UInt128? chunkContentMd5 = null,
        UInt128? fileDataMd5 = null,
        UInt128? fileChunkMd5 = null,
        bool writeChunk = true)
    {
        var chunkName = "00000000000000000000000000000000.chk";
        var dataMd5 = UInt128FromLittleEndian(System.Security.Cryptography.MD5.HashData(payload));
        var chunkMd5 = chunkContentMd5 ?? dataMd5;
        var metadata = BuildAuditMetadata(blockType, 1, declaredChunkLength ?? payload.Length, writer =>
        {
            writer.Write(1);
            WriteAuditChunkHeader(writer, declaredChunkLength ?? payload.Length, 1, chunkMd5, blockType);
            WriteAuditFile(writer, fileName, 0, payload.Length, fileChunkMd5 ?? 0, fileDataMd5 ?? dataMd5, blockType);
        });
        WriteAuditMetadata(root, blockType, metadata);
        if (writeChunk)
        {
            var blockDirectory = Path.Combine(root, "VFS",
                EndfieldVfsHash.VfsBlockHash(blockType.GetName(), EndfieldVfsKeys.UnityHashSecret));
            Directory.CreateDirectory(blockDirectory);
            File.WriteAllBytes(Path.Combine(blockDirectory, chunkName), payload);
        }
    }

    private static void WriteEncryptedAuditBlock(
        string root,
        EndfieldVfsBlockType blockType,
        string fileName,
        byte[] decodedPayload,
        long ivSeed)
    {
        var physicalPayload = (byte[])decodedPayload.Clone();
        var nonce = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(nonce.AsSpan(0, 4), EndfieldVfsLoader.VfsProtoVersion);
        BinaryPrimitives.WriteInt64LittleEndian(nonce.AsSpan(4, 8), ivSeed);
        var cipher = new EndfieldChaCha20(EndfieldVfsKeys.ChaChaKey, nonce, 1);
        cipher.ApplyKeystream(physicalPayload);
        var dataMd5 = UInt128FromLittleEndian(System.Security.Cryptography.MD5.HashData(decodedPayload));
        var contentMd5 = UInt128FromLittleEndian(System.Security.Cryptography.MD5.HashData(physicalPayload));
        var metadata = BuildAuditMetadata(blockType, 1, physicalPayload.Length, writer =>
        {
            writer.Write(1);
            WriteAuditChunkHeader(writer, physicalPayload.Length, 1, contentMd5, blockType);
            WriteAuditFile(writer, fileName, 0, physicalPayload.Length, 0, dataMd5, blockType,
                encrypted: true, ivSeed: ivSeed);
        });
        WriteAuditMetadata(root, blockType, metadata);
        var blockDirectory = Path.Combine(root, "VFS",
            EndfieldVfsHash.VfsBlockHash(blockType.GetName(), EndfieldVfsKeys.UnityHashSecret));
        File.WriteAllBytes(Path.Combine(blockDirectory, "00000000000000000000000000000000.chk"), physicalPayload);
    }

    private static byte[] BuildAuditMetadata(EndfieldVfsBlockType blockType, Action<BinaryWriter> body) =>
        BuildAuditMetadata(blockType, 0, 0, body);

    private static byte[] BuildAuditMetadata(
        EndfieldVfsBlockType blockType,
        int groupFileCount,
        long groupChunksLength,
        Action<BinaryWriter> body) =>
        BuildAuditMetadata(blockType.GetName(), (byte)blockType, groupFileCount, groupChunksLength, body);

    private static byte[] BuildAuditMetadata(string groupConfigName, byte blockTypeValue, Action<BinaryWriter> body)
        => BuildAuditMetadata(groupConfigName, blockTypeValue, 0, 0, body);

    private static byte[] BuildUnsupportedAuditMetadata()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        writer.Write(5);
        writer.Write(24764758);
        return stream.ToArray();
    }

    private static byte[] BuildAuditMetadata(
        string groupConfigName,
        byte blockTypeValue,
        int groupFileCount,
        long groupChunksLength,
        Action<BinaryWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(4); // current installed metadata code version
            writer.Write(24764758); // current populated-block metadata version
            var name = System.Text.Encoding.UTF8.GetBytes(groupConfigName);
            writer.Write((ushort)name.Length);
            writer.Write(name);
            var directoryValue = uint.Parse(
                EndfieldVfsHash.VfsBlockHash(groupConfigName, EndfieldVfsKeys.UnityHashSecret),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture);
            writer.Write((long)unchecked((int)BinaryPrimitives.ReverseEndianness(directoryValue)));
            writer.Write(groupFileCount);
            writer.Write(groupChunksLength);
            writer.Write(blockTypeValue);
            body(writer);
            writer.Write(new byte[] { 0xE4, 0xC2, 0x96, 0x6A, 0, 0, 0, 0, 0, 0 });
        }
        return stream.ToArray();
    }

    private static void WriteAuditMetadata(
        string root,
        EndfieldVfsBlockType blockType,
        byte[] metadata,
        bool corruptCrc = false)
    {
        var hash = EndfieldVfsHash.VfsBlockHash(blockType.GetName(), EndfieldVfsKeys.UnityHashSecret);
        WriteAuditMetadata(root, hash, metadata, corruptCrc);
    }

    private static void WriteAuditMetadata(
        string root,
        string hashDirectory,
        byte[] metadata,
        bool corruptCrc = false)
    {
        var blockDirectory = Path.Combine(root, "VFS", hashDirectory);
        Directory.CreateDirectory(blockDirectory);
        var withCrc = new byte[metadata.Length + 4];
        Buffer.BlockCopy(metadata, 0, withCrc, 0, metadata.Length);
        var crc = unchecked((int)EndfieldCrc32.Compute(metadata));
        if (corruptCrc) crc ^= 1;
        Buffer.BlockCopy(BitConverter.GetBytes(crc), 0, withCrc, metadata.Length, 4);
        var nonce = new byte[12];
        var encrypted = (byte[])withCrc.Clone();
        var cipher = new EndfieldChaCha20(EndfieldVfsKeys.ChaChaKey, nonce, 1);
        cipher.ApplyKeystream(encrypted);
        var blockBytes = new byte[nonce.Length + encrypted.Length];
        Buffer.BlockCopy(nonce, 0, blockBytes, 0, nonce.Length);
        Buffer.BlockCopy(encrypted, 0, blockBytes, nonce.Length, encrypted.Length);
        File.WriteAllBytes(Path.Combine(blockDirectory, $"{hashDirectory}.blc"), blockBytes);
    }

    private static void WriteAuditChunkHeader(
        BinaryWriter writer,
        long chunkLength,
        int fileCount,
        UInt128 chunkContentMd5 = default,
        EndfieldVfsBlockType blockType = EndfieldVfsBlockType.Terrain)
    {
        WriteUInt128LittleEndian(writer, 0); // chunk MD5 name
        WriteUInt128LittleEndian(writer, chunkContentMd5);
        writer.Write(chunkLength);
        writer.Write((byte)blockType);
        writer.Write(0); // MainTag for code version 4
        writer.Write(fileCount);
    }

    private static void WriteAuditFile(
        BinaryWriter writer,
        string fileName,
        long offset,
        long length,
        UInt128 chunkMd5,
        UInt128 dataMd5,
        EndfieldVfsBlockType blockType,
        bool encrypted = false,
        long ivSeed = 0)
    {
        var name = System.Text.Encoding.UTF8.GetBytes(fileName);
        writer.Write((ushort)name.Length);
        writer.Write(name);
        writer.Write(unchecked((long)EndfieldVfsHash.Hash64(
            System.Text.Encoding.UTF8.GetBytes(fileName), EndfieldVfsKeys.UnityHashSecret, 0)));
        WriteUInt128LittleEndian(writer, chunkMd5);
        WriteUInt128LittleEndian(writer, dataMd5);
        writer.Write(offset);
        writer.Write(length);
        writer.Write((byte)blockType);
        writer.Write(encrypted ? (byte)1 : (byte)0);
        if (encrypted) writer.Write(ivSeed);
        writer.Write(0); // FileTag for code version 4
    }

    private static void WriteUInt128LittleEndian(BinaryWriter writer, UInt128 value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[..8], (ulong)value);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], (ulong)(value >> 64));
        writer.Write(bytes);
    }

    private static UInt128 UInt128FromLittleEndian(byte[] bytes)
    {
        var low = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0, 8));
        var high = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8, 8));
        return ((UInt128)high << 64) | low;
    }

    private static void WriteEncryptedVfsBlockWithMissingChunk(string root) =>
        WriteEncryptedVfsBlockWithMissingChunk(root, EndfieldVfsBlockType.Terrain);

    private static void WriteEncryptedVfsBlockWithMissingChunk(string root, EndfieldVfsBlockType blockType)
    {
        var blockName = EndfieldVfsHash.VfsBlockHash(blockType.GetName(), EndfieldVfsKeys.UnityHashSecret);
        var blockDirectory = Path.Combine(root, "VFS", blockName);
        Directory.CreateDirectory(blockDirectory);

        var metadata = BuildVfsMetadata((byte)blockType, 1, 1, writer =>
        {
            writer.Write(1); // chunk count
            WriteVfsChunkWithTypes(writer, 1, (byte)blockType, (byte)blockType, ("missing.bin", 0, 1));
        });
        var withCrc = new byte[metadata.Length + 4];
        Buffer.BlockCopy(metadata, 0, withCrc, 0, metadata.Length);
        Buffer.BlockCopy(BitConverter.GetBytes((int)EndfieldCrc32.Compute(metadata)), 0, withCrc, metadata.Length, 4);

        var nonce = new byte[12];
        var encrypted = (byte[])withCrc.Clone();
        var cipher = new EndfieldChaCha20(EndfieldVfsKeys.ChaChaKey, nonce, 1);
        cipher.ApplyKeystream(encrypted);
        var blockBytes = new byte[nonce.Length + encrypted.Length];
        Buffer.BlockCopy(nonce, 0, blockBytes, 0, nonce.Length);
        Buffer.BlockCopy(encrypted, 0, blockBytes, nonce.Length, encrypted.Length);
        File.WriteAllBytes(Path.Combine(blockDirectory, $"{blockName}.blc"), blockBytes);
    }

    private static void AssertCliFailsForSelectedVfsFile(string[] args, string label)
    {
        if (!EndfieldVfsCli.TryRun(args, out var exitCode) || exitCode == 0)
        {
            throw new InvalidOperationException($"{label}: expected a nonzero exit code, got {exitCode}");
        }
    }

    private static byte[] BuildVfsMetadata(Action<BinaryWriter> body) =>
        BuildVfsMetadata((byte)EndfieldVfsBlockType.Terrain, 0, 0, body);

    private static byte[] BuildVfsMetadata(int groupFileCount, long groupChunksLength, Action<BinaryWriter> body)
        => BuildVfsMetadata((byte)EndfieldVfsBlockType.Terrain, groupFileCount, groupChunksLength, body);

    private static byte[] BuildVfsMetadata(
        byte blockTypeValue,
        int groupFileCount,
        long groupChunksLength,
        Action<BinaryWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(11); // current metadata format: code version is implicit
            writer.Write((ushort)0);
            writer.Write(0L);
            writer.Write(groupFileCount);
            writer.Write(groupChunksLength);
            writer.Write(blockTypeValue);
            body(writer);
        }
        return stream.ToArray();
    }

    private static void WriteVfsChunk(BinaryWriter writer, long chunkLength, params (string Name, long Offset, long Length)[] files)
    {
        WriteVfsChunkWithTypes(writer, chunkLength, (byte)EndfieldVfsBlockType.Terrain, (byte)EndfieldVfsBlockType.Terrain, files);
    }

    private static void WriteVfsChunkWithTypes(
        BinaryWriter writer,
        long chunkLength,
        byte chunkTypeValue,
        byte fileTypeValue,
        params (string Name, long Offset, long Length)[] files)
    {
        WriteVfsChunkHeader(writer, chunkLength, files.Length, chunkTypeValue);
        foreach (var file in files)
        {
            var name = System.Text.Encoding.UTF8.GetBytes(file.Name);
            writer.Write((ushort)name.Length);
            writer.Write(name);
            writer.Write(0L); // file name hash
            writer.Write(new byte[16]); // file chunk MD5
            writer.Write(new byte[16]); // file data MD5
            writer.Write(file.Offset);
            writer.Write(file.Length);
            writer.Write(fileTypeValue);
            writer.Write((byte)0); // not encrypted
        }
    }

    private static void WriteVfsChunkHeader(BinaryWriter writer, long chunkLength, int fileCount) =>
        WriteVfsChunkHeader(writer, chunkLength, fileCount, (byte)EndfieldVfsBlockType.Terrain);

    private static void WriteVfsChunkHeader(BinaryWriter writer, long chunkLength, int fileCount, byte chunkTypeValue)
    {
        writer.Write(new byte[16]); // chunk MD5 name
        writer.Write(new byte[16]); // chunk content MD5
        writer.Write(chunkLength);
        writer.Write(chunkTypeValue);
        writer.Write(fileCount);
    }

    private static void TestEndfieldCompressDataRecords()
    {
        var first = "{\"$type\":\"NodeCanvas.BehaviourTrees.BehaviourTree\",\"name\":\"first\"}";
        var second = "{\"type\":\"NodeCanvas.BehaviourTrees.BehaviourTree\",\"name\":\"second\",\"nodes\":[]}";
        var container = BuildCompressData(first, second);
        var decoded = EndfieldCompressData.Decode(container);

        AssertEqual(2, decoded.Records.Count, "CompressData record count");
        AssertEqual(4 + 2 * 4, decoded.Records[0].SourceOffset, "CompressData first offset");
        AssertEqual(first, decoded.Records[0].JsonText, "CompressData first text");
        AssertEqual("NodeCanvas.BehaviourTrees.BehaviourTree", decoded.Records[0].RootType, "CompressData first root type");
        AssertEqual("NodeCanvas.BehaviourTrees.BehaviourTree", decoded.Records[1].RootType, "CompressData type-field root type");
        AssertEqual("second", decoded.Records[1].Json["name"]?.ToObject<string>(), "CompressData second JSON");
        AssertEqual(decoded.Records[1].SourceOffset, decoded.Records[0].SourceOffset + 8 + decoded.Records[0].CompressedLength, "CompressData adjacent records");
    }

    private static void TestEndfieldCompressDataRejectsMalformedContainers()
    {
        var valid = BuildCompressData("{\"$type\":\"Tree\"}", "{\"$type\":\"Tree2\"}");

        var firstOffsetInvalid = (byte[])valid.Clone();
        WriteUInt32(firstOffsetInvalid, 4, 0);
        AssertThrows<EndfieldCompressDataException>(
            () => EndfieldCompressData.Decode(firstOffsetInvalid),
            "CompressData rejects offset-table gaps");

        var offsetsNotIncreasing = (byte[])valid.Clone();
        WriteUInt32(offsetsNotIncreasing, 8, ReadUInt32(offsetsNotIncreasing, 4));
        AssertThrows<EndfieldCompressDataException>(
            () => EndfieldCompressData.Decode(offsetsNotIncreasing),
            "CompressData rejects non-monotonic offsets");

        var recordLengthMismatch = BuildCompressData("{\"$type\":\"Tree\"}");
        var recordOffset = ReadUInt32(recordLengthMismatch, 4);
        WriteUInt32(recordLengthMismatch, (int)recordOffset, ReadUInt32(recordLengthMismatch, (int)recordOffset) + 1);
        AssertThrows<EndfieldCompressDataException>(
            () => EndfieldCompressData.Decode(recordLengthMismatch),
            "CompressData rejects compressed-length gaps");

        var decodedLengthMismatch = BuildCompressData("{\"$type\":\"Tree\"}");
        recordOffset = ReadUInt32(decodedLengthMismatch, 4);
        WriteUInt32(decodedLengthMismatch, (int)recordOffset + 4, ReadUInt32(decodedLengthMismatch, (int)recordOffset + 4) + 1);
        AssertThrows<EndfieldCompressDataException>(
            () => EndfieldCompressData.Decode(decodedLengthMismatch),
            "CompressData rejects decoded-length mismatch");

        AssertThrows<EndfieldCompressDataException>(
            () => EndfieldCompressData.Decode(BuildContainer((new byte[] { 0x00 }, 2u))),
            "CompressData rejects malformed Brotli");
        AssertThrows<EndfieldCompressDataException>(
            () => EndfieldCompressData.Decode(BuildContainer((CompressBrotli(new byte[] { 0x7b }), 1u))),
            "CompressData rejects odd UTF-16LE");
        AssertThrows<EndfieldCompressDataException>(
            () => EndfieldCompressData.Decode(BuildContainer((CompressBrotli(new byte[] { 0x00, 0xd8 }), 2u))),
            "CompressData rejects invalid UTF-16LE");
        AssertThrows<EndfieldCompressDataException>(
            () => EndfieldCompressData.Decode(BuildCompressData("not-json")),
            "CompressData rejects invalid JSON");
        AssertThrows<EndfieldCompressDataException>(
            () => EndfieldCompressData.Decode(BuildCompressData("{\"$type\":\"Tree\"}//comment")),
            "CompressData rejects JSON comments");
        AssertThrows<EndfieldCompressDataException>(
            () => EndfieldCompressData.Decode(BuildCompressData("{\"$type\":\"Tree\"}{\"extra\":true}")),
            "CompressData rejects trailing JSON");
        AssertThrows<EndfieldCompressDataException>(
            () => EndfieldCompressData.Decode(BuildContainer((CompressBrotli(System.Text.Encoding.Unicode.GetBytes("{\"$type\":\"Tree\"}")).Concat(new byte[] { 0 }).ToArray(), 32u))),
            "CompressData rejects trailing Brotli bytes");
        AssertThrows<EndfieldCompressDataException>(
            () => EndfieldCompressData.Decode(new byte[] { 0, 0, 0, 0, 1 }),
            "CompressData rejects empty-container trailing bytes");
    }

    private static void TestEndfieldCompressDataCliOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), $"animestudio-compress-data-test-{Guid.NewGuid():N}");
        var output = Path.Combine(root, "decoded");
        var input = Path.Combine(root, "CompressData.bin");
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(output); // An existing empty directory is allowed.
            File.WriteAllBytes(input, BuildCompressData("{\"$type\":\"Tree\",\"id\":1}"));
            if (!EndfieldVfsCli.TryRun(
                    new[] { "extend-data", "--input", input, "--output", output },
                    out var exitCode)
                || exitCode != 0)
            {
                throw new InvalidOperationException($"CompressData CLI failed with exit code {exitCode}");
            }

            AssertEqual(true, File.Exists(Path.Combine(output, "000000.json")), "CompressData CLI numbered output");
            var manifest = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")));
            AssertEqual(1, manifest["recordCount"]?.ToObject<int>(), "CompressData CLI manifest count");
            AssertEqual("000000.json", manifest["records"]?[0]?["output"]?.ToObject<string>(), "CompressData CLI manifest output");
            AssertEqual(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(input))),
                manifest["sourceSha256"]?.ToObject<string>(),
                "CompressData CLI source hash");

            if (!EndfieldVfsCli.TryRun(
                    new[] { "extend-data", "--input", input, "--output", output },
                    out var rerunExitCode)
                || rerunExitCode == 0)
            {
                throw new InvalidOperationException("CompressData CLI must reject a non-empty output directory");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static byte[] BuildCompressData(params string[] jsonTexts) =>
        BuildContainer(jsonTexts.Select(text =>
            (CompressBrotli(System.Text.Encoding.Unicode.GetBytes(text)), (uint)System.Text.Encoding.Unicode.GetByteCount(text))).ToArray());

    private static byte[] BuildContainer(params (byte[] compressed, uint uncompressedLength)[] records)
    {
        var firstOffset = 4 + records.Length * 4;
        var offsets = new int[records.Length];
        var nextOffset = firstOffset;
        for (var i = 0; i < records.Length; i++)
        {
            offsets[i] = nextOffset;
            nextOffset += 8 + records[i].compressed.Length;
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(records.Length);
            foreach (var offset in offsets)
            {
                writer.Write(offset);
            }
            foreach (var record in records)
            {
                writer.Write(record.compressed.Length);
                writer.Write(record.uncompressedLength);
                writer.Write(record.compressed);
            }
        }
        return stream.ToArray();
    }

    private static byte[] CompressBrotli(byte[] data)
    {
        using var stream = new MemoryStream();
        using (var brotli = new BrotliStream(stream, CompressionLevel.Optimal, true))
        {
            brotli.Write(data, 0, data.Length);
        }
        return stream.ToArray();
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        BitConverter.ToUInt32(data, offset);

    private static void WriteUInt32(byte[] data, int offset, uint value) =>
        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, data, offset, sizeof(uint));

    private static void AssertThrows<T>(Action action, string label) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"{label}: expected {typeof(T).Name}, got {exception.GetType().Name}", exception);
        }

        throw new InvalidOperationException($"{label}: expected {typeof(T).Name}");
    }

    private static void AssertThrowsWithMessage<T>(Action action, string expectedFragment, string label)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            if (exception.Message.IndexOf(expectedFragment, StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException(
                    $"{label}: expected diagnostic containing '{expectedFragment}', got '{exception.Message}'");
            }
            return;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"{label}: expected {typeof(T).Name}, got {exception.GetType().Name}", exception);
        }

        throw new InvalidOperationException($"{label}: expected {typeof(T).Name}");
    }

    private static void TestObservedEnemySettlementBattleGraphVariants()
    {
        var payloads = new[]
        {
            Words(
                0x3e99999a, 0x00000000, 0xb8509cb1, 0xc4707c7f,
                0x1b7ecf33, 0xf6d09833, 0x40a00000, 0x40a00000,
                0x00000000, 0x40a00000, 0x40a00000, 0x42700000,
                0x41200000, 0x00000000, 0x00000000),
            Words(
                0x3e99999a, 0x00000000, 0x00000000, 0x00000000,
                0x1b7ecf33, 0xf6d09833, 0x40a00000, 0x40a00000,
                0x00000002, 0x40a00000, 0x40a00000, 0x42700000,
                0x41200000, 0x00000000, 0x00000001, 0x00000000,
                0xdee80001, 0x4c7f6e70, 0x00000000, 0xdee80003,
                0x4c7f6e70),
            Words(
                0x3e99999a, 0x00000000, 0x0a98a2a3, 0xc0181181,
                0x1b7ecf33, 0xf6d09833, 0x41000000, 0x41000000,
                0x00000002, 0x40a00000, 0x40a00000, 0x42700000,
                0x41200000, 0x00000000, 0x00000001, 0x00000000,
                0x6fd402eb, 0x3a466e2c, 0x00000001, 0xa220020e,
                0x3a466e36, 0x6fd402ec, 0x3a466e2c),
            Words(
                0x3e99999a, 0x00000000, 0x00000000, 0x00000000,
                0x1b7ecf33, 0xf6d09833, 0x41000000, 0x41000000,
                0x00000000, 0x40a00000, 0x40a00000, 0x42700000,
                0x41200000, 0x00000000, 0x00000001, 0x00000000,
                0xec7c0018, 0x396d0d54, 0x00000003, 0x4d880a68,
                0x268b1c4b, 0x44c40003, 0x721ee060, 0xec7c0019,
                0x396d0d54, 0x4d880a69, 0x268b1c4b),
        };

        for (var i = 0; i < payloads.Length; i++)
        {
            var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
                "EnemySettlementBattleGraph/EnemySettlementBattleGraphData",
                "Beyond.Gameplay.AI",
                "Gameplay.Beyond",
                payloads[i]);
            AssertFlag(decoded, "$decoded", true, $"observed settlement payload variant {i + 1}");
        }
    }

    private static void TestEnemySettlementBattleGraphPayload()
    {
        var payload = Words(
            0x3e99999a,
            0x00000000,
            0x00000000,
            0x00000000,
            0x1b7ecf33,
            0xf6d09833,
            0x00000000,
            0x00000000,
            0x00000000,
            0x41200000,
            0x40a00000,
            0x42700000,
            0x41200000,
            0x00000000,
            0x00000000);
        var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "EnemySettlementBattleGraph/EnemySettlementBattleGraphData",
            "Beyond.Gameplay.AI",
            "Gameplay.Beyond",
            payload);

        AssertFlag(decoded, "$decoded", true, "settlement payload decoded");
        AssertEqual(
            "Beyond.Gameplay.AI.EnemySettlementBattleGraph/EnemySettlementBattleGraphData",
            decoded["layout"] as string,
            "settlement layout");
        AssertEqual(0.3f, Convert.ToSingle(decoded["baseInterval"]), "base interval");
        AssertEqual(10f, Convert.ToSingle(decoded["onHitTimeout"]), "on-hit timeout");
        AssertEqual(5f, Convert.ToSingle(decoded["sightRadius"]), "sight radius");
        AssertEqual(60f, Convert.ToSingle(decoded["sightAngle"]), "sight angle");
        AssertEqual(10f, Convert.ToSingle(decoded["leaveDis"]), "leave distance");
    }

    private static void TestEnemySettlementBattleGraphPayloadRejectsTrailingBytes()
    {
        var valid = Words(
            0x3e99999a,
            0, 0, 0,
            0x1b7ecf33,
            0xf6d09833,
            0, 0, 0,
            0x41200000,
            0x40a00000,
            0x42700000,
            0x41200000,
            0, 0);
        var payload = valid.Concat(new byte[4]).ToArray();
        var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "EnemySettlementBattleGraph/EnemySettlementBattleGraphData",
            "Beyond.Gameplay.AI",
            "Gameplay.Beyond",
            payload);

        AssertFlag(decoded, "$decoded", false, "trailing bytes must reject exact layout");
        AssertFlag(decoded, "$heuristic", true, "rejected layout remains heuristic");
    }

    private static void TestObservedEnemyCastSkillResponsePayload()
    {
        // Exact 56-byte registry payload from BB_eny_0077_agshield,
        // RefId 2669506000975823080 in the pinned export.
        var payload = Words(
            0x3dcccccd,
            0x00000021,
            0x5f796e65,
            0x37373030,
            0x7367615f,
            0x6c656968,
            0x6b735f64,
            0x306c6c69,
            0x75735f31,
            0x65656363,
            0x00000064,
            0x00000003,
            0x00000001,
            0x00000000);
        var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "EnemyCastSkillResponse/EnemyCastSkillResponseData",
            "Beyond.Gameplay.AI",
            "Gameplay.Beyond",
            payload);

        AssertFlag(decoded, "$decoded", true, "observed cast-skill response payload decoded");
        AssertEqual(0.1f, Convert.ToSingle(decoded["baseInterval"]), "cast-skill response base interval");
        AssertEqual(
            "eny_0077_agshield_skill01_succeed",
            decoded["skillId"] as string,
            "cast-skill response skill id");
        AssertEqual(true, Convert.ToBoolean(decoded["interruptSkill"]), "cast-skill response interrupt flag");
        AssertEqual(false, Convert.ToBoolean(decoded["waitFinish"]), "cast-skill response wait-finish flag");
    }

    private static void TestTruncatedEnemyCastSkillResponseReportsExactCursor()
    {
        var payload = Words(
            0x3dcccccd,
            0x00000021,
            0x5f796e65,
            0x37373030,
            0x7367615f,
            0x6c656968,
            0x6b735f64,
            0x306c6c69,
            0x75735f31,
            0x65656363,
            0x00000064,
            0x00000003,
            0x00000001);
        var failure = Exporter.DecodeEnemyCastSkillResponseFailureForTesting(payload);
        AssertEqual(true, Convert.ToBoolean(failure["cursorAvailable"]), "failure cursor is available");
        AssertEqual(payload.Length, Convert.ToInt32(failure["relativeCursor"]), "failure relative cursor");
        AssertEqual("waitFinish", failure["activeField"] as string, "failure active field");
        AssertEqual("interruptSkill", failure["lastCompletedField"] as string, "failure last completed field");
        AssertEqual(4, Convert.ToInt32(failure["requestedBytes"]), "failure requested bytes");
    }

    private static void TestObservedUIToggleSetValuePayload()
    {
        // Exact 120-byte registry payload from guide_group_make_battle_turret_ct,
        // RefId 7093480650259824651 in the pinned export.
        var payload = Words(
            0x0000000f,
            0x00000008,
            0x31653133,
            0x39386336,
            0x00000000,
            0x00000000,
            0x00000000,
            0x00000001,
            0x00000001,
            0x00000010,
            0x00000000,
            0x00000000,
            0x0000002f,
            0x42636146,
            0x646c6975,
            0x7473694c,
            0x656c6553,
            0x61507463,
            0x2f6c656e,
            0x6e69614d,
            0x6e6f432f,
            0x746e6574,
            0x7079542f,
            0x676f5465,
            0x00656c67,
            0xffffffff,
            0x00000000,
            0x00000000,
            0x00000001,
            0xffffffff);
        var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "UIToggleSetValue",
            "Beyond.Gameplay.Actions",
            "Gameplay.Beyond",
            payload);

        AssertFlag(decoded, "$decoded", true, "observed UI toggle payload decoded");
        var actionBase = decoded["actionBase"] as OrderedDictionary
            ?? throw new InvalidOperationException("UI toggle action base missing");
        AssertEqual("31e16c89", actionBase["key"] as string, "UI toggle action key");
        var togglePath = decoded["_togglePath"] as OrderedDictionary
            ?? throw new InvalidOperationException("UI toggle path parameter missing");
        AssertEqual(
            "FacBuildListSelectPanel/Main/Content/TypeToggle",
            togglePath["value"] as string,
            "UI toggle path value");
        var isOn = decoded["_isOn"] as OrderedDictionary
            ?? throw new InvalidOperationException("UI toggle bool parameter missing");
        AssertEqual(true, Convert.ToBoolean(isOn["value"]), "UI toggle enabled value");
    }

    private static void TestExactGuideRemainingPayloads()
    {
        var portableDevice = Exporter.DecodeManagedReferencePayloadForTesting(
            "CheckIsPortableDeviceActive",
            "Beyond.Gameplay",
            "Gameplay.Beyond",
            Words(
                0x00000008, 0x33336665, 0x36326263, 0x00000000,
                0x00000001, 0x00000001, 0x00000000, 0x00000000,
                0x0000001d, 0x6d657469, 0x7665645f, 0x5f656369,
                0x6c6c6162, 0x5f6e6f6f, 0x79636572, 0x5f656c63,
                0x00000031, 0xffffffff));
        AssertFlag(portableDevice, "$decoded", true, "portable-device condition decoded");
        var itemId = portableDevice["_itemId"] as OrderedDictionary
            ?? throw new InvalidOperationException("portable-device item parameter missing");
        AssertEqual("item_device_balloon_recycle_1", itemId["value"] as string, "portable-device item ID");

        var depotPanel = Exporter.DecodeManagedReferencePayloadForTesting(
            "OnDomainDepotMainPanelOpen",
            "Beyond.Gameplay.Conditions",
            "Gameplay.Beyond",
            Words(
                0x00000008, 0x37623961, 0x31623165, 0x00000000,
                0x00000001, 0x00000001, 0x00000000, 0x00000000,
                0x00000008, 0x616d6f64, 0x325f6e69, 0xffffffff,
                0x00000000, 0x00000000, 0x00000000, 0xffffffff));
        AssertFlag(depotPanel, "$decoded", true, "domain-depot panel condition decoded");
        var domainId = depotPanel["_domainId"] as OrderedDictionary
            ?? throw new InvalidOperationException("domain-depot ID parameter missing");
        AssertEqual("domain_2", domainId["value"] as string, "domain-depot ID");
        var waitAnimation = depotPanel["_waitAnimationIn"] as OrderedDictionary
            ?? throw new InvalidOperationException("domain-depot animation parameter missing");
        AssertEqual(false, Convert.ToBoolean(waitAnimation["value"]), "domain-depot wait-animation flag");

        var blueprintTab = Exporter.DecodeManagedReferencePayloadForTesting(
            "OnFacBlueprintTabOpen",
            "Beyond.Gameplay.Conditions",
            "Gameplay.Beyond",
            Words(
                0x00000008, 0x34366431, 0x32616539, 0x00000000,
                0x00000001, 0x00000001, 0x00000000, 0x00000000,
                0x00000001, 0xffffffff));
        AssertFlag(blueprintTab, "$decoded", true, "blueprint-tab condition decoded");
        var tabType = blueprintTab["_tabType"] as OrderedDictionary
            ?? throw new InvalidOperationException("blueprint-tab parameter missing");
        var tabTypeValue = tabType["value"] as OrderedDictionary
            ?? throw new InvalidOperationException("blueprint-tab value missing");
        AssertEqual(1, Convert.ToInt32(tabTypeValue["value"]), "blueprint-tab type");

        AssertGuideActionBaseOnlyDecoded(
            "ClearManualCraftFilterRecord",
            Words(
                0x0000000f, 0x00000008, 0x36336130, 0x39613238,
                0x00000000, 0x00000000, 0x00000000, 0x00000001,
                0x00000001, 0xffffffff));
        AssertGuideActionBaseOnlyDecoded(
            "FacResetBlueprintFilter",
            Words(
                0x00000011, 0x00000008, 0x39656364, 0x31396562,
                0x00000000, 0x00000000, 0x00000000, 0x00000001,
                0x00000001, 0xffffffff));

        var scrollCellArea = Exporter.DecodeManagedReferencePayloadForTesting(
            "GenUIScrollListCellArea",
            "Beyond.Gameplay.Actions",
            "Gameplay.Beyond",
            Words(
                0x00000005, 0x00000008, 0x36316635, 0x32376630,
                0x00000000, 0x00000000, 0x00000000, 0x00000001,
                0x00000001, 0xffffffff, 0x00000000, 0x00000000,
                0x00000049, 0x65766e49, 0x726f746e, 0x6e615079,
                0x432f6c65, 0x65746e6f, 0x492f746e, 0x426d6574,
                0x6f4e6761, 0x492f6564, 0x426d6574, 0x462f6761,
                0x426c6c75, 0x74492f67, 0x61426d65, 0x6e6f4367,
                0x746e6574, 0x6574492f, 0x73694c6d, 0x00000074,
                0xffffffff, 0x00000000, 0x00000000, 0x00000023,
                0xffffffff, 0x00000000, 0x00000000, 0x00000026,
                0xffffffff));
        AssertFlag(scrollCellArea, "$decoded", true, "scroll-cell action decoded");
        var listPath = scrollCellArea["_listPath"] as OrderedDictionary
            ?? throw new InvalidOperationException("scroll-cell list path missing");
        AssertEqual(
            "InventoryPanel/Content/ItemBagNode/ItemBag/FullBg/ItemBagContent/ItemList",
            listPath["value"] as string,
            "scroll-cell list path");
        AssertGuideParamIntValue(scrollCellArea, "_startIndex", 35, "scroll-cell start index");
        AssertGuideParamIntValue(scrollCellArea, "_endIndex", 38, "scroll-cell end index");
    }

    private static void AssertGuideActionBaseOnlyDecoded(string className, byte[] payload)
    {
        var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
            className,
            "Beyond.Gameplay.Actions",
            "Gameplay.Beyond",
            payload);
        AssertFlag(decoded, "$decoded", true, $"{className} action decoded");
        _ = decoded["actionBase"] as OrderedDictionary
            ?? throw new InvalidOperationException($"{className} action base missing");
    }

    private static void AssertGuideParamIntValue(
        OrderedDictionary decoded,
        string fieldName,
        int expected,
        string label
    )
    {
        var parameter = decoded[fieldName] as OrderedDictionary
            ?? throw new InvalidOperationException($"{fieldName} parameter missing");
        var value = parameter["value"] as OrderedDictionary
            ?? throw new InvalidOperationException($"{fieldName} value missing");
        AssertEqual(expected, Convert.ToInt32(value["value"]), label);
    }

    private static void TestExactGuideCameraBlendPayloads()
    {
        var blendToPayload = Convert.FromBase64String(
            "FwAAAAgAAABmNzExZjU4NQAAAAAAAAAAAAAAAAEAAAABAAAA/////wAAAAAAAAAAWJ2YxCpZh0MlLb1C/////wAAAAAAAAAAhoNAQbYDpEMAAAAA/////wAAAAAAAAAAAAAAAP////8AAAAAAAAAAAAAAAD/////AAAAAAAAAAAAAHBC/////wAAAAAAAAAAAAAgQP////8AAAAAAAAAAAEAAAD/////AAAAAAAAAAAAAAAA/////wAAAAAAAAAAAABAQP////8AAAAAAAAAAAAAAAD/////AAAAAAAAAAABAAAA/////wAAAAAAAAAAAAAAAP////8AAAAAAAAAAAAAAAD/////AAAAAAAAAAAAAAAA/////wAAAAAAAAAAAAAAAP////8AAAAAAAAAAAAAAAD/////");
        var blendTo = Exporter.DecodeManagedReferencePayloadForTesting(
            "BlendToCameraTransformWithoutBack",
            "Beyond.Gameplay.Actions",
            "Gameplay.Beyond",
            blendToPayload);
        AssertFlag(blendTo, "$decoded", true, "camera blend-to action decoded");
        AssertFlag(blendTo, "exactTypeTreeDecoded", true, "camera blend-to exact marker");
        var alternativePoses = blendTo["_alternativeCameraPoses"] as OrderedDictionary
            ?? throw new InvalidOperationException("camera alternative-pose parameter missing");
        var poseValues = alternativePoses["value"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("camera alternative-pose list missing");
        AssertEqual(0, poseValues.Count, "camera alternative-pose count");
        var blendToCurve = blendTo["blendCurveKey"] as OrderedDictionary
            ?? throw new InvalidOperationException("camera blend-to curve parameter missing");
        AssertEqual(string.Empty, blendToCurve["value"] as string, "camera blend-to curve key");

        var blendOut = Exporter.DecodeManagedReferencePayloadForTesting(
            "BlendOutFromCamera",
            "Beyond.Gameplay.Actions",
            "Gameplay.Beyond",
            Convert.FromBase64String(
                "GAAAAAgAAAA4YzZhYWJmMgAAAAAAAAAAAAAAAAEAAAABAAAA/////wAAAAAAAAAAAAAAQP////8AAAAAAAAAAAAAAAD/////AAAAAAAAAAABAAAA/////wAAAAAAAAAAAAAAAP////8AAAAAAAAAAAAAAAD/////AAAAAAAAAAAAAAAA/////w=="));
        AssertFlag(blendOut, "$decoded", true, "camera blend-out action decoded");
        var blendOutCurve = blendOut["blendCurveKey"] as OrderedDictionary
            ?? throw new InvalidOperationException("camera blend-out curve parameter missing");
        AssertEqual(string.Empty, blendOutCurve["value"] as string, "camera blend-out curve key");
        AssertGuideParamIntValue(blendOut, "_resetType", 0, "camera blend-out reset type");

        AssertNotExactAndVisiblyIncomplete(
            Exporter.DecodeManagedReferencePayloadForTesting(
                "BlendToCameraTransformWithoutBack",
                "Beyond.Gameplay.Actions",
                "Gameplay.Beyond",
                AppendWord(blendToPayload, 0)),
            "camera blend-to trailing bytes");
    }

    private static void TestExactCameraControlLockEnemyPayloads()
    {
        var payloads = new[]
        {
            Convert.FromBase64String(
                "AQAAAAAAAAABAAAAAQAAAAAA8MEAAPBBAAAAAM3MDD8AAAA/mpkZP5qZmT4DAAAAzcxMPgIAAAAAAAAAZmbmPmILtjpiC7Y6AAAAAAAAAACrqqo+//8zQzMzMz9iC7Y6Ygu2OgAAAACrqqo+AAAAAAIAAAACAAAABAAAAAIAAAAAAAAAAAAAAKeSFkCnkhZAAAAAAAAAAAA4WhA9AACAPwAAgD9JJGQ+SSRkPgAAAADA9mY9AAAAAAIAAAACAAAABAAAAJqZmT4AAABAAACAPwcAAAACAAAAAAAAAAAAAAAAAABAAAAAQAAAAAAAAAAAAAAAAAAAgD8AAIA/AAAAAAAAAAAAAAAAAAAAAAAAAAACAAAAAgAAAAQAAAABAAAAAACAPwIAAAAAAAAAzWqqPgAAAEBOY/07AAAAAAAAAAAAAAAA0kVwQoEeTD8AAAAAAAAAAAAAAAAAAAAAAAAAAAIAAAACAAAABAAAAA=="),
            Convert.FromBase64String(
                "AQAAAAEAAAAAAAAAAAAAAAAA8MEAAPBBAAAAAM3MDD8AAAA/mpkZP5qZGT8DAAAAzcxMPgIAAAAAAAAAZmbmPmILtjpiC7Y6AAAAAAAAAACrqqo+//8zQzMzMz9iC7Y6Ygu2OgAAAACrqqo+AAAAAAIAAAACAAAABAAAAAIAAAAAAAAAAAAAAKeSFkCnkhZAAAAAAAAAAAA4WhA9AACAPwAAgD9JJGQ+SSRkPgAAAADA9mY9AAAAAAIAAAACAAAABAAAAJqZmT4AAABAAACAPwAAAAAAAAAAAgAAAAIAAAAEAAAAAAAAAM3MTD4CAAAAAAAAAM3MTD4AAAAAAAAAAAAAAAAAAAAAAAAAAAAANEMAAAA/AAAAAAAAAAAAAAAAAAAAAAAAAAACAAAAAgAAAAQAAAA="),
        };

        for (var i = 0; i < payloads.Length; i++)
        {
            var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
                "CameraControlLockEnemyConfig",
                "Beyond.Gameplay.View",
                "Gameplay.Beyond",
                payloads[i]);
            AssertFlag(decoded, "$decoded", true, $"lock-enemy camera payload {i + 1} decoded");
            AssertFlag(decoded, "exactTypeTreeDecoded", true, $"lock-enemy camera payload {i + 1} exact marker");
            AssertEqual(true, Convert.ToBoolean(decoded["limitToInputType"]), $"lock-enemy camera payload {i + 1} input limit");
            var durationCurve = decoded["changeDurationByDeltaYaw"] as OrderedDictionary
                ?? throw new InvalidOperationException("lock-enemy duration curve missing");
            var durationKeys = durationCurve["keyframes"] as List<OrderedDictionary>
                ?? throw new InvalidOperationException("lock-enemy duration keyframes missing");
            AssertEqual(2, durationKeys.Count, $"lock-enemy camera payload {i + 1} duration key count");
            var enteringCurve = decoded["enteringCurve"] as OrderedDictionary
                ?? throw new InvalidOperationException("lock-enemy entering curve missing");
            var enteringKeys = enteringCurve["keyframes"] as List<OrderedDictionary>
                ?? throw new InvalidOperationException("lock-enemy entering keyframes missing");
            AssertEqual(i == 0 ? 2 : 0, enteringKeys.Count, $"lock-enemy camera payload {i + 1} entering key count");
        }

        var invalidBool = (byte[])payloads[0].Clone();
        Buffer.BlockCopy(BitConverter.GetBytes(2), 0, invalidBool, 0, sizeof(int));
        AssertNotExactAndVisiblyIncomplete(
            Exporter.DecodeManagedReferencePayloadForTesting(
                "CameraControlLockEnemyConfig",
                "Beyond.Gameplay.View",
                "Gameplay.Beyond",
                invalidBool),
            "lock-enemy camera invalid bool");
    }

    private static void TestExactAbilitySystemForEnemyPartPayload()
    {
        // Exact 940-byte registry payload from data_eny_0081_ruanyi in the pinned export.
        var payload940 = Convert.FromBase64String(
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAEAAACgAAAAAAAAAAAAAAABAAAAAAAAAAEAAAAAAMhCAQAAAAAAoEEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAPwAAgD8AAIA/AQAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAQAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/AAAAPwAAgD8AAAAAAABoQgAAgD8AAAAAAAAAAAAAAAAA" +
                "AEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AQAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAABAAAAAAAAAAAAAAABAAAAAAAAAAAAAAABAAAAAAAAAAAA" +
                "AAAAAAAA/////wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAEAAAAAAAAAAgAAAAEAAAAAAAAAAMB5RBQAAAAAAAAAAMB5RAEAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAMhB" +
                "AAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAQAAAAAAAACXAAAAAQAAAA==");
        var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "AbilitySystemForEnemyPartData",
            "Beyond.Gameplay.Core",
            "Gameplay.Beyond",
            payload940);

        AssertFlag(
            decoded,
            "$decoded",
            true,
            $"enemy-part ability payload decoded ({decoded["exactTypeTreeDecodeFailure"]})");
        AssertFlag(decoded, "exactTypeTreeDecoded", true, "enemy-part ability exact marker");
        AssertEqual(
            "all inherited AbilitySystemData and derived enemy-part fields consumed",
            decoded["observedPayloadStatus"] as string,
            "enemy-part ability exact status");
        AssertEqual(true, Convert.ToBoolean(decoded["defaultEnabled"]), "enemy-part ability enabled flag");
        AssertEqual(false, Convert.ToBoolean(decoded["asIndividualInExcludeTargetProcessor"]), "enemy-part exclusion flag");
        var attributes = decoded["partAttributes"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("enemy-part attributes missing");
        AssertEqual(2, attributes.Count, "enemy-part attribute count");
        var firstType = attributes[0]["attributeType"] as OrderedDictionary
            ?? throw new InvalidOperationException("enemy-part first attribute type missing");
        var secondType = attributes[1]["attributeType"] as OrderedDictionary
            ?? throw new InvalidOperationException("enemy-part second attribute type missing");
        AssertEqual(1, Convert.ToInt32(firstType["value"]), "enemy-part first attribute type");
        AssertEqual(20, Convert.ToInt32(secondType["value"]), "enemy-part second attribute type");
        AssertEqual(999f, Convert.ToSingle(attributes[0]["value"]), "enemy-part first attribute value");
        AssertEqual(999f, Convert.ToSingle(attributes[1]["value"]), "enemy-part second attribute value");

        // Exact 928-byte registry payload from the exhaustive Persistent sweep.
        var variant928 = Exporter.DecodeManagedReferencePayloadForTesting(
            "AbilitySystemForEnemyPartData",
            "Beyond.Gameplay.Core",
            "Gameplay.Beyond",
            Convert.FromBase64String(
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIA/AAAAQAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAPwAAgD8AAIA/AQAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAQAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/AAAAPwAAgD8AAAAAAABoQgAAgD8AAAAAAAAAAAAAAAAA" +
                "AEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AQAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAABAAAAAAAAAAAAAAABAAAAAAAAAAAAAAABAAAAAAAAAAAA" +
                "AAAAAAAA/////wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAABAAAAAAAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAQAAAAEAAAABAAAAAABIQgAAAAAAAAAAAQAAAJgAAAAAAAAAAAAAAAAAAAAAAAdDAAAAAAAAAAABAAAA" +
                "AAAAAAAAAAABAAAAAAAAAAAAAACYAAAAAAAAAA=="));
        AssertEqual(
            "all inherited AbilitySystemData and derived enemy-part fields consumed",
            variant928["observedPayloadStatus"] as string,
            $"928-byte enemy-part exact status ({variant928["exactTypeTreeDecodeFailure"]})");
        AssertFlag(variant928, "exactTypeTreeDecoded", true, "928-byte enemy-part exact marker");
        var healthType = variant928["healthType"] as OrderedDictionary
            ?? throw new InvalidOperationException("928-byte enemy-part health type missing");
        AssertEqual(1, Convert.ToInt32(healthType["value"]), "928-byte enemy-part health type value");
        AssertEqual("unresolved", healthType["nameStatus"] as string, "928-byte enemy-part health type semantic status");

        AssertNotExactAndVisiblyIncomplete(
            Exporter.DecodeManagedReferencePayloadForTesting(
                "AbilitySystemForEnemyPartData",
                "Beyond.Gameplay.Core",
                "Gameplay.Beyond",
                AppendWord(payload940, 0)),
            "enemy-part ability trailing bytes");
    }

    private static void TestExactEnemyPartsRootPayload()
    {
        // Exact 92-byte registry payload from data_eny_0081_ruanyi in the pinned export.
        var payload = Convert.FromBase64String(
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAgAAACXAAAAmAAAAJkAAACaAAAAmwAAAJwAAAA0AAAANAAAAA4AAABCaXAwMDFf" +
                "Ul9UaGlnaAAAAQAAANiH14o=");
        var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "EnemyPartsRootComponentData",
            "Beyond.Gameplay.Core",
            "Gameplay.Beyond",
            payload);

        AssertFlag(decoded, "$decoded", true, "enemy-parts root payload decoded");
        AssertFlag(decoded, "exactTypeTreeDecoded", true, "enemy-parts root exact marker");
        AssertEqual(
            "all EnemyPartsRootComponentData TypeTree fields consumed",
            decoded["observedPayloadStatus"] as string,
            "enemy-parts root exact status");
        var mountPointData = decoded["mountPointData"] as OrderedDictionary
            ?? throw new InvalidOperationException("enemy-parts root mount-point dictionary missing");
        var mountPointEntries = mountPointData["entries"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("enemy-parts root mount-point entries missing");
        AssertEqual(0, mountPointEntries.Count, "enemy-parts root mount-point count");
        AssertEqual(true, Convert.ToBoolean(decoded["snapMountPointToSurface"]), "enemy-parts root snap flag");
        var snapMountPoints = decoded["needToSnapMountPoints"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("enemy-parts root snap mount-points missing");
        AssertEqual(8, snapMountPoints.Count, "enemy-parts root snap mount-point count");
        AssertEqual(151, Convert.ToInt32(snapMountPoints[0]["value"]), "enemy-parts root first snap mount-point");
        AssertEqual("Bip001_R_Thigh", decoded["partName"] as string, "enemy-parts root part name");
        var partTags = decoded["partTags"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("enemy-parts root tags missing");
        AssertEqual(1, partTags.Count, "enemy-parts root tag count");
        var tagId = partTags[0]["tagId"] as OrderedDictionary
            ?? throw new InvalidOperationException("enemy-parts root tag ID missing");
        AssertEqual("0x8ad787d8", tagId["hex"] as string, "enemy-parts root tag ID");

        var invalidBool = (byte[])payload.Clone();
        Buffer.BlockCopy(BitConverter.GetBytes(2), 0, invalidBool, 24, sizeof(int));
        AssertNotExactAndVisiblyIncomplete(
            Exporter.DecodeManagedReferencePayloadForTesting(
                "EnemyPartsRootComponentData",
                "Beyond.Gameplay.Core",
                "Gameplay.Beyond",
                invalidBool),
            "enemy-parts root invalid bool");

        AssertNotExactAndVisiblyIncomplete(
            Exporter.DecodeManagedReferencePayloadForTesting(
                "EnemyPartsRootComponentData",
                "Beyond.Gameplay.Core",
                "Gameplay.Beyond",
                payload[..^4]),
            "enemy-parts root truncated payload");
    }

    private static void TestObservedEnemySimpleAttackPayloads()
    {
        // Exact 56-byte registry payload from BB_eny_0029_lbmob_defend.
        var longPayload = Words(
            0x3dcccccd,
            0x00000024,
            0x5f796e65,
            0x39323030,
            0x6d626c5f,
            0x615f626f,
            0x63617474,
            0x726f636b,
            0x65735f65,
            0x656c7474,
            0x746e656d,
            0x40400000,
            0x00000000,
            0x00000000);
        var longDecoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "EnemySimpleAttackBehavior/EnemySimpleAttackBehaviorData",
            "Beyond.Gameplay.AI",
            "Gameplay.Beyond",
            longPayload);
        AssertFlag(longDecoded, "$decoded", true, "observed long simple-attack payload decoded");
        AssertEqual(
            "eny_0029_lbmob_attackcore_settlement",
            longDecoded["skillId"] as string,
            "long simple-attack skill id");
        AssertEqual(3f, Convert.ToSingle(longDecoded["skillRange"]), "long simple-attack range");
        AssertEqual(false, Convert.ToBoolean(longDecoded["changeCD"]), "long simple-attack change-CD flag");
        AssertEqual(0f, Convert.ToSingle(longDecoded["cd"]), "long simple-attack CD");

        // Exact 44-byte nonzero-tail payload from BB_eny_0117_klhound_cardefend.
        var nonzeroTailPayload = Words(
            0x3dcccccd,
            0x00000017,
            0x5f796e65,
            0x37313130,
            0x686c6b5f,
            0x646e756f,
            0x696b735f,
            0x00316c6c,
            0x40000000,
            0x00000001,
            0x40a00000);
        var nonzeroTailDecoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "EnemySimpleAttackBehavior/EnemySimpleAttackBehaviorData",
            "Beyond.Gameplay.AI",
            "Gameplay.Beyond",
            nonzeroTailPayload);
        AssertFlag(nonzeroTailDecoded, "$decoded", true, "observed nonzero-tail simple-attack payload decoded");
        AssertEqual(
            "eny_0117_klhound_skill1",
            nonzeroTailDecoded["skillId"] as string,
            "nonzero-tail simple-attack skill id");
        AssertEqual(2f, Convert.ToSingle(nonzeroTailDecoded["skillRange"]), "nonzero-tail simple-attack range");
        AssertEqual(true, Convert.ToBoolean(nonzeroTailDecoded["changeCD"]), "nonzero-tail simple-attack change-CD flag");
        AssertEqual(5f, Convert.ToSingle(nonzeroTailDecoded["cd"]), "nonzero-tail simple-attack CD");
    }

    private static void TestObservedEnemyCheckGameplayTagPayload()
    {
        // Exact 20-byte registry payload from BB_eny_0075_lbroshan.
        var payload = Words(
            0x00000000,
            0x00000000,
            0x00000001,
            0x00000001,
            0x9df293d9);
        var decoded = Exporter.DecodeManagedReferencePayloadForTesting(
            "EnemyCheckGameplayTag/EnemyCheckGameplayTagData",
            "Beyond.Gameplay.AI",
            "Gameplay.Beyond",
            payload);

        AssertFlag(decoded, "$decoded", true, "observed gameplay-tag check payload decoded");
        var tagInfo = decoded["tagInfo"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("gameplay-tag check list missing");
        AssertEqual(1, tagInfo.Count, "gameplay-tag check count");
        AssertEqual(true, Convert.ToBoolean(tagInfo[0]["invert"]), "gameplay-tag check invert flag");
        var tag = tagInfo[0]["tag"] as OrderedDictionary
            ?? throw new InvalidOperationException("gameplay-tag check value missing");
        var tagId = tag["tagId"] as OrderedDictionary
            ?? throw new InvalidOperationException("gameplay-tag check ID missing");
        AssertEqual("0x9df293d9", tagId["hex"] as string, "gameplay-tag check ID");
    }

    private static void TestManagedReferenceRegistryValidation()
    {
        var valid = BuildRegistryType(BuildEntry(
            1,
            "EnemySettlementBattleGraph/EnemySettlementBattleGraphData",
            "Beyond.Gameplay.AI",
            "Gameplay.Beyond"));
        AssertEqual(
            true,
            Exporter.TryValidateManagedReferenceRegistry(valid, out var validDiagnostic),
            $"valid registry: {validDiagnostic?["reason"]}");

        var corrupt = BuildRegistryType(BuildEntry(
            9626766541,
            "",
            "\u0002",
            "eny_0046_lbshamman_attackcore_settlement"));
        AssertEqual(
            false,
            Exporter.TryValidateManagedReferenceRegistry(corrupt, out var corruptDiagnostic),
            "corrupt registry rejection");
        AssertEqual("invalidTypeHeader", corruptDiagnostic["reason"] as string, "corrupt registry diagnostic");

        var nullSentinel = BuildRegistryType(BuildEntry(-1, "", "", ""));
        AssertEqual(
            true,
            Exporter.TryValidateManagedReferenceRegistry(nullSentinel, out _),
            "null sentinel registry");
    }

    private static void TestManagedReferenceRegistryTypeTreeGate()
    {
        var ordinaryReferences = new TypeTree
        {
            m_Nodes = new List<TypeTreeNode>
            {
                new("Example", "Base", 0, false),
                new("BipedReferences", "references", 1, false),
                new("PPtr<Transform>", "root", 2, false),
                new("int", "m_FileID", 3, false),
                new("SInt64", "m_PathID", 3, false),
            },
        };
        AssertEqual(
            false,
            Exporter.IsFinalTopLevelTypeTreeField(
                ordinaryReferences,
                "references",
                "ManagedReferencesRegistry"),
            "ordinary references field is not a managed registry");

        var managedRegistry = new TypeTree
        {
            m_Nodes = new List<TypeTreeNode>
            {
                new("Example", "Base", 0, false),
                new("int", "value", 1, false),
                new("ManagedReferencesRegistry", "references", 1, false),
                new("int", "version", 2, false),
                new("vector", "RefIds", 2, false),
            },
        };
        AssertEqual(
            true,
            Exporter.IsFinalTopLevelTypeTreeField(
                managedRegistry,
                "references",
                "ManagedReferencesRegistry"),
            "final managed registry field recognized");

        managedRegistry.m_Nodes.Add(new TypeTreeNode("int", "trailing", 1, false));
        AssertEqual(
            false,
            Exporter.IsFinalTopLevelTypeTreeField(
                managedRegistry,
                "references",
                "ManagedReferencesRegistry"),
            "non-final managed registry field rejected");
    }

    private static void TestValidationFailureRegistryRecovery()
    {
        var rawData = Convert.FromBase64String(
            "AAAAAAAAAAAAAAAAAQAAAAEAAADP1NXnFRjhgB8AAABCQl9lbnlfMDA0Nl9sYnNoYW1tYW5fY2FyZGVmZW5kAAAAAAASAAAA5aGU6Ziy546p5rOV5oCq54mpAAAAAAAAXV9+cUetnYAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAAC4zx2wAQAAAAEHFGlae401AQAAAKWVSHcBAAAAAQAAAKUDIAtmORkGAgAAAAIAAAClAyALZjkZBj8AAABFbmVteVNldHRsZW1lbnRCYXR0bGVCZWhhdmlvci9FbmVteVNldHRsZW1lbnRCYXR0bGVCZWhhdmlvckRhdGEAEgAAAEJleW9uZC5HYW1lcGxheS5BSQAADwAAAEdhbWVwbGF5LkJleW9uZADNzMw9AgAAAAAAAAABAAAAAgAAACgAAABlbnlfMDA0Nl9sYnNoYW1tYW5fYXR0YWNrY29yZV9zZXR0bGVtZW50AACgQCoAAABlbnlfMDA0Nl9sYnNoYW1tYW5fYXR0YWNrcGxheWVyX3NldHRsZW1lbnQAAAAAIEEBBxRpWnuNNTkAAABFbmVteVNldHRsZW1lbnRCYXR0bGVHcmFwaC9FbmVteVNldHRsZW1lbnRCYXR0bGVHcmFwaERhdGEAAAASAAAAQmV5b25kLkdhbWVwbGF5LkFJAAAPAAAAR2FtZXBsYXkuQmV5b25kAJqZmT4AAAAAAAAAAAAAAAAzz34bM5jQ9gAAoEAAAKBAAAAAAAAAoEAAAKBAAABwQgAAIEEAAAAAAAAAAA==");
        var references = Exporter.RecoverManagedReferencesForTesting(
            rawData,
            168,
            439445549081428901,
            3858876083966576385);
        AssertFlag(references, "$decoded", true, "validation-failure registry fully decoded");
        var entries = references["RefIds"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("recovered validation-failure entries missing");
        AssertEqual(2, entries.Count, "recovered validation-failure entry count");
        AssertEqual(439445549081428901L, Convert.ToInt64(entries[0]["rid"]), "first recovered registry RID");
        AssertEqual(3858876083966576385L, Convert.ToInt64(entries[1]["rid"]), "second recovered registry RID");
        var secondType = entries[1]["type"] as OrderedDictionary
            ?? throw new InvalidOperationException("second recovered registry type missing");
        AssertEqual(
            "EnemySettlementBattleGraph/EnemySettlementBattleGraphData",
            secondType["class"] as string,
            "second recovered registry class");
    }

    private static void TestAbilitySystemModeWeaponVisibilityProfile()
    {
        var observedEmptyProfile = Exporter.DecodeAbilitySystemModeConfigForTesting(
            BuildAbilitySystemModeConfigPayload(false));
        var observedMode = GetOnlyMode(observedEmptyProfile);
        AssertEqual("Patrol", observedMode["modeId"] as string, "observed mode id");
        AssertEqual(
            false,
            Convert.ToBoolean(observedMode["overrideWeaponVisibilityProfile"]),
            "observed weapon visibility override");
        var observedProfile = observedMode["weaponVisibilityProfile"] as OrderedDictionary
            ?? throw new InvalidOperationException("observed weapon visibility profile missing");
        var observedSlots = observedProfile["slots"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("observed weapon visibility slots missing");
        AssertEqual(
            0,
            observedSlots.Count,
            "observed empty weapon visibility slots");

        var populatedProfile = Exporter.DecodeAbilitySystemModeConfigForTesting(
            BuildAbilitySystemModeConfigPayload(true, (2, true, false)));
        var populatedMode = GetOnlyMode(populatedProfile);
        var profile = populatedMode["weaponVisibilityProfile"] as OrderedDictionary
            ?? throw new InvalidOperationException("populated weapon visibility profile missing");
        var slots = profile["slots"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("populated weapon visibility slots missing");
        AssertEqual(1, slots.Count, "populated weapon visibility slot count");
        AssertEqual(2, Convert.ToInt32(slots[0]["weaponIndex"]), "weapon visibility slot index");
        AssertEqual(true, Convert.ToBoolean(slots[0]["showWhenIdle"]), "weapon visible while idle");
        AssertEqual(false, Convert.ToBoolean(slots[0]["showWhenFight"]), "weapon hidden while fighting");
    }

    private static void TestAbilitySystemSkillDataBundleExactSerializedLayout()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            for (var i = 0; i < 6; i++)
            {
                writer.Write(0);
            }
            WriteAlignedAsciiString(writer, "normal_skill");
            WriteAlignedAsciiString(writer, "ultimate_skill");
            WriteAlignedAsciiString(writer, "plunge_start");
            WriteAlignedAsciiString(writer, "plunge_end");
            WriteAlignedAsciiString(writer, "dodge_skill");
            writer.Write(2);
            writer.Write(1);
            writer.Write(9);
            writer.Write(0);
            writer.Write(1);
            writer.Write(0);
            writer.Write(1);
            writer.Write(1);
            writer.Write(1);
            WriteAlignedAsciiString(writer, "combo_power");
            writer.Write(12.5d);
            WriteAlignedAsciiString(writer, "ready");
            writer.Write(1);
            WriteAlignedAsciiString(writer, "combo_skill");
            WriteAlignedAsciiString(writer, "combo_node");
            writer.Write(1);
            writer.Write(3);
            writer.Write(1);
            WriteAlignedAsciiString(writer, "normal_skill");
            WriteAlignedAsciiString(writer, "HUD_ENEMY");
            writer.Write(1);
            WriteAlignedAsciiString(writer, "normal_skill");
            writer.Write(1);
            writer.Write(4);
        }

        var decoded = Exporter.DecodeAbilitySystemSkillDataBundleForTesting(stream.ToArray());
        AssertEqual(true, Convert.ToBoolean(decoded["enableComboSkillBlackboard"]), "combo blackboard enabled");
        AssertEqual("combo_skill", decoded["comboSkillId"] as string, "combo skill id");
        AssertEqual("HUD_ENEMY", decoded["hudPanelName"] as string, "HUD panel name");
        var blackboard = decoded["comboSkillBlackboard"] as OrderedDictionary
            ?? throw new InvalidOperationException("combo skill blackboard missing");
        AssertEqual(1, Convert.ToInt32(blackboard["count"]), "combo skill blackboard count");
        var conditions = decoded["comboSkillConditions"] as OrderedDictionary
            ?? throw new InvalidOperationException("combo skill conditions missing");
        AssertEqual(1, Convert.ToInt32(conditions["count"]), "combo skill condition count");
        var conditionEntries = conditions["entries"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("combo skill condition entries missing");
        AssertEqual(true, Convert.ToBoolean(conditionEntries[0]["comboSkillConditionImmediately"]), "combo skill condition immediate flag");
        var overrides = decoded["activeSkillTypeOverrides"] as OrderedDictionary
            ?? throw new InvalidOperationException("active skill type overrides missing");
        var entries = overrides["entries"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("active skill type override entries missing");
        AssertEqual(1, entries.Count, "active skill type override count");
        AssertEqual(4, Convert.ToInt32(entries[0]["value"]), "active skill type override value");
    }

    private static void TestLineFollowerSerializedTypeTreeLayout()
    {
        // Exact 100-byte payload from the pinned StreamingAssets corpus. The
        // TypeTree row's nominal byteSize is 26, but its two aligned UInt8
        // fields make the observed serialized stride 32 bytes.
        var payload = Convert.FromBase64String(
            "AwAAAAAAAACULMCGLCZLuwAAAACXAAAAAAAAAJgAAAACAAAAAAAAAJQsiVQIU1PKAAAAAJcAAAAAAAAAmAAAAAIAAAAAAAAAlCzs+EDebQkAAAAAlwAAAAAAAACYAAAAAgAAAA==");
        var typeTree = new TypeTree
        {
            m_Nodes = new List<TypeTreeNode>
            {
                new() { m_Level = 0, m_Type = "LineFollower", m_Name = "Base", m_ByteSize = -1, m_MetaFlag = 0x8000 },
                new() { m_Level = 1, m_Type = "LineFollowerData", m_Name = "data", m_ByteSize = -1, m_MetaFlag = 0x8000 },
                new() { m_Level = 2, m_Type = "Array", m_Name = "Array", m_ByteSize = -1, m_TypeFlags = 1, m_MetaFlag = 0x8000 },
                new() { m_Level = 3, m_Type = "int", m_Name = "size", m_ByteSize = 4 },
                new() { m_Level = 3, m_Type = "LineFollowerData", m_Name = "data", m_ByteSize = 26, m_MetaFlag = 0x8000 },
                new() { m_Level = 4, m_Type = "PPtr<$LineRenderer>", m_Name = "line", m_ByteSize = 12 },
                new() { m_Level = 5, m_Type = "int", m_Name = "m_FileID", m_ByteSize = 4, m_MetaFlag = 0x800001 },
                new() { m_Level = 5, m_Type = "SInt64", m_Name = "m_PathID", m_ByteSize = 8, m_MetaFlag = 0x800001 },
                new() { m_Level = 4, m_Type = "UInt8", m_Name = "useConfigSourceMountPoint", m_ByteSize = 1, m_MetaFlag = 0x4100 },
                new() { m_Level = 4, m_Type = "int", m_Name = "source", m_ByteSize = 4 },
                new() { m_Level = 4, m_Type = "UInt8", m_Name = "useConfigTargetMountPoint", m_ByteSize = 1, m_MetaFlag = 0x4100 },
                new() { m_Level = 4, m_Type = "int", m_Name = "target", m_ByteSize = 4 },
                new() { m_Level = 4, m_Type = "int", m_Name = "positionNum", m_ByteSize = 4 },
            },
            m_StringBuffer = Array.Empty<byte>(),
        };

        var decoded = TypeTreeHelper.ReadTypePayload(typeTree, payload, 0, payload.Length, out var bytesRead);
        AssertEqual((long)payload.Length, bytesRead, "LineFollower TypeTree bytes consumed");
        var rows = decoded["data"] as List<object>
            ?? throw new InvalidOperationException("LineFollower TypeTree rows missing");
        AssertEqual(3, rows.Count, "LineFollower TypeTree row count");
    }

    private static OrderedDictionary GetOnlyMode(OrderedDictionary modeConfig)
    {
        var modes = modeConfig["modes"] as List<OrderedDictionary>
            ?? throw new InvalidOperationException("decoded mode list missing");
        AssertEqual(1, modes.Count, "mode count");
        return modes[0];
    }

    private static byte[] BuildAbilitySystemModeConfigPayload(
        bool overrideWeaponVisibilityProfile,
        params (int WeaponIndex, bool ShowWhenIdle, bool ShowWhenFight)[] slots)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1);
        WriteAlignedAsciiString(writer, "Patrol");
        writer.Write(1);
        WriteAlignedAsciiString(writer, "default");
        WriteAlignedAsciiString(writer, "");
        writer.Write(1);
        writer.Write(1);
        WriteAlignedAsciiString(writer, "common_enemy_passive_patrol");
        writer.Write(1);
        writer.Write(1f);
        writer.Write(1);
        writer.Write(360f);
        writer.Write(0);
        writer.Write(1);
        writer.Write(0);
        writer.Write(0);
        writer.Write(1);
        WriteAlignedAsciiString(writer, "isWalk");
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        WriteAlignedAsciiString(writer, "");
        writer.Write(0);
        WriteAlignedAsciiString(writer, "");
        writer.Write(0);
        writer.Write(overrideWeaponVisibilityProfile ? 1 : 0);
        writer.Write(slots.Length);
        foreach (var slot in slots)
        {
            writer.Write(slot.WeaponIndex);
            writer.Write(slot.ShowWhenIdle ? 1 : 0);
            writer.Write(slot.ShowWhenFight ? 1 : 0);
        }
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        return stream.ToArray();
    }

    private static void WriteAlignedAsciiString(BinaryWriter writer, string value)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
        while ((writer.BaseStream.Position & 3) != 0)
        {
            writer.Write((byte)0);
        }
    }

    private static OrderedDictionary BuildRegistryType(params OrderedDictionary[] entries)
    {
        return new OrderedDictionary
        {
            {
                "references",
                new OrderedDictionary
                {
                    { "version", 2 },
                    { "RefIds", entries.ToList() },
                }
            },
        };
    }

    private static OrderedDictionary BuildEntry(long rid, string className, string namespaceName, string assemblyName)
    {
        return new OrderedDictionary
        {
            { "rid", rid },
            {
                "type",
                new OrderedDictionary
                {
                    { "class", className },
                    { "ns", namespaceName },
                    { "asm", assemblyName },
                }
            },
            { "data", new OrderedDictionary() },
        };
    }

    private static byte[] Words(params uint[] words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
        if (!BitConverter.IsLittleEndian)
        {
            for (var offset = 0; offset < bytes.Length; offset += sizeof(uint))
            {
                Array.Reverse(bytes, offset, sizeof(uint));
            }
        }
        return bytes;
    }

    private static byte[] AppendWord(byte[] payload, int value)
    {
        var result = new byte[payload.Length + sizeof(int)];
        Buffer.BlockCopy(payload, 0, result, 0, payload.Length);
        Buffer.BlockCopy(BitConverter.GetBytes(value), 0, result, payload.Length, sizeof(int));
        return result;
    }

    private static void AssertNotExactAndVisiblyIncomplete(OrderedDictionary dictionary, string label)
    {
        AssertFlag(dictionary, "exactTypeTreeDecoded", false, $"{label} exact marker");
        var visiblyIncomplete = new[] { "$partial", "$unparsed", "$heuristic" }
            .Any(key => dictionary.Contains(key) && dictionary[key] is bool flag && flag);
        AssertEqual(true, visiblyIncomplete, $"{label} visible incomplete marker");
    }

    private static void AssertFlag(OrderedDictionary dictionary, string key, bool expected, string label)
    {
        var actual = dictionary.Contains(key) && dictionary[key] is bool flag && flag;
        AssertEqual(expected, actual, label);
    }

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
        }
    }

    private static void AssertBytesEqual(byte[] expected, byte[] actual, string label)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"{label}: byte sequences differ");
        }
    }
}
