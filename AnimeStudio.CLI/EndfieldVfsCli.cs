using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AnimeStudio.Endfield;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    public static class EndfieldVfsCli
    {
        private const int MaxFailureDiagnostics = 8;
        private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
        {
            "dump",
            "audio",
            "audio-audit",
            "stream",
            "vfs-index",
            "vfsindex",
            "vfs-audit",
            "vfs-profile",
            "vfs-inner-audit",
            "list",
            "extend-data",
            "extenddata",
        };

        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (!LooksLikeVfsInvocation(args))
            {
                return false;
            }

            try
            {
                if (args.Skip(1).Any(IsHelp))
                {
                    PrintCommandHelp(args[0]);
                    return true;
                }

                switch (args[0].ToLowerInvariant())
                {
                    case "list":
                        RunList();
                        return true;
                    case "dump":
                        RunDump(ParseVfsOptions(args, "./output"));
                        return true;
                    case "vfs-index":
                    case "vfsindex":
                        RunVfsIndex(ParseVfsOptions(args, "./vfs_index.json"));
                        return true;
                    case "vfs-audit":
                        EndfieldVfsAudit.Run(args);
                        return true;
                    case "vfs-profile":
                        RunVfsProfile(ParseVfsOptions(args, "./vfs_profile_ledger.jsonl.gz"));
                        return true;
                    case "vfs-inner-audit":
                        RunVfsInnerAudit(ParseVfsOptions(args, "./vfs_inner_audit_ledger.jsonl.gz"));
                        return true;
                    case "audio":
                        EndfieldAudioCli.Run(args);
                        return true;
                    case "audio-audit":
                        exitCode = EndfieldAudioCli.RunAudit(args);
                        return true;
                    case "stream":
                        RunStream(ParseVfsOptions(args, ""));
                        return true;
                    case "extend-data":
                    case "extenddata":
                        RunExtendData(ParseExtendDataOptions(args));
                        return true;
                }
            }
            catch (HelpRequestedException)
            {
                return true;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Error: {e.Message}");
                exitCode = 1;
                return true;
            }

            return false;
        }

        private static bool LooksLikeVfsInvocation(string[] args)
        {
            if (args.Length == 0 || !Commands.Contains(args[0]))
            {
                return false;
            }

            if (string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase) || args.Length == 1)
            {
                return true;
            }

            if (string.Equals(args[0], "extend-data", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[0], "extenddata", StringComparison.OrdinalIgnoreCase))
            {
                return args.Skip(1).Any(arg =>
                    IsHelp(arg) ||
                    arg == "-i" ||
                    arg == "--input" ||
                    arg.StartsWith("--input=", StringComparison.Ordinal));
            }

            if (string.Equals(args[0], "vfs-audit", StringComparison.OrdinalIgnoreCase))
            {
                return args.Skip(1).Any(arg =>
                    IsHelp(arg) || arg == "-s" || arg == "--streaming-assets"
                    || arg.StartsWith("--streaming-assets=", StringComparison.Ordinal));
            }

            return args.Skip(1).Any(arg =>
                IsHelp(arg) ||
                arg == "-s" ||
                arg == "--streaming-assets" ||
                arg.StartsWith("--streaming-assets=", StringComparison.Ordinal)
            );
        }

        private static void RunList()
        {
            Console.WriteLine("Available block types:");
            foreach (var blockType in EndfieldVfsBlockTypes.AllDumpable)
            {
                Console.WriteLine($"  - {blockType.GetName()}");
            }
        }

        private static void RunDump(VfsOptions options)
        {
            var loader = new EndfieldVfsLoader(options.StreamingAssets, options.FallbackAssets);
            foreach (var block in LoadSelectedBlocks(
                loader,
                options,
                blockType => Console.WriteLine($"Dumping {blockType.GetName()} files..."),
                (_, e) => Console.WriteLine($"  Warning: Block {e.HashDirectory} not found, skipping")))
            {
                DumpBlock(loader, block, options.Output, options);
            }
        }

        private static void RunStream(VfsOptions options)
        {
            var loader = new EndfieldVfsLoader(options.StreamingAssets, options.FallbackAssets);
            var emittedCount = 0;
            var errorCount = 0;
            var enforceErrors = false;
            var diagnostics = new List<string>();
            var diagnosticsLock = new object();
            foreach (var selectedFile in EnumerateSelectedFiles(
                loader,
                options,
                (_, e) => Console.Error.WriteLine($"Warning: Block {e.HashDirectory} not found, skipping")))
            {
                try
                {
                    var data = loader.ExtractFileToBytes(
                        selectedFile.BlockType,
                        selectedFile.Chunk,
                        selectedFile.File,
                        options.VerifyMd5 && ShouldFailOnFileErrors(options, selectedFile.BlockType));
                    var payload = new JObject
                    {
                        ["blockType"] = EndfieldVfsBlockTypes.GetName(selectedFile.File.BlockTypeValue),
                        ["blockTypeValue"] = selectedFile.File.BlockTypeValue,
                        ["fileName"] = selectedFile.File.FileName,
                        ["length"] = data.Length,
                        ["dataBase64"] = Convert.ToBase64String(data),
                    };
                    Console.Out.WriteLine(payload.ToString(Formatting.None));
                    emittedCount++;
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref errorCount);
                    if (ShouldFailOnFileErrors(options, selectedFile.BlockType))
                    {
                        enforceErrors = true;
                    }
                    lock (diagnosticsLock)
                    {
                        if (diagnostics.Count < MaxFailureDiagnostics)
                        {
                            diagnostics.Add($"{selectedFile.File.FileName}: {e.Message}");
                        }
                    }
                }
            }
            Console.Error.WriteLine($"Streamed {emittedCount} files");
            if (errorCount > 0 && enforceErrors)
            {
                ThrowFileErrors("stream", errorCount, diagnostics);
            }
        }

        private static void RunExtendData(ExtendDataOptions options)
        {
            if (Directory.Exists(options.Output)
                && Directory.EnumerateFileSystemEntries(options.Output).Any())
            {
                throw new ArgumentException($"output directory must be absent or empty: {options.Output}");
            }

            var sourceBytes = File.ReadAllBytes(options.Input);
            var document = EndfieldCompressData.Decode(sourceBytes);
            Directory.CreateDirectory(options.Output);
            var records = new JArray();
            foreach (var record in document.Records)
            {
                var outputName = $"{record.Index:D6}.json";
                var outputPath = Path.Combine(options.Output, outputName);
                var jsonText = record.Json.ToString(Formatting.Indented).Replace("\r\n", "\n", StringComparison.Ordinal);
                File.WriteAllText(outputPath, jsonText + "\n", new UTF8Encoding(false));
                records.Add(new JObject
                {
                    ["compressedLength"] = record.CompressedLength,
                    ["index"] = record.Index,
                    ["output"] = outputName,
                    ["rootType"] = record.RootType == null ? JValue.CreateNull() : record.RootType,
                    ["sourceOffset"] = record.SourceOffset,
                    ["uncompressedLength"] = record.UncompressedLength,
                });
            }

            var manifest = new JObject
            {
                ["format"] = "animestudio-extend-data-compress",
                ["recordCount"] = document.Records.Count,
                ["records"] = records,
                ["schemaVersion"] = 1,
                ["sourceFileName"] = Path.GetFileName(options.Input),
                ["sourceLength"] = document.SourceLength,
                ["sourceSha256"] = Convert.ToHexString(SHA256.HashData(sourceBytes)),
            };
            File.WriteAllText(
                Path.Combine(options.Output, "manifest.json"),
                manifest.ToString(Formatting.Indented).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n",
                new UTF8Encoding(false));
            Console.WriteLine($"  Done: decoded {document.Records.Count} records -> {options.Output}");
        }

        private static void RunVfsProfile(VfsOptions options)
        {
            var finalLedger = options.Output;
            var finalSummary = string.IsNullOrEmpty(options.SummaryOutput)
                ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(finalLedger)) ?? ".", "vfs_profile_summary.json")
                : options.SummaryOutput;
            if (string.Equals(Path.GetFullPath(finalLedger), Path.GetFullPath(finalSummary), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("vfs-profile ledger and summary outputs must be distinct paths");
            }
            var temporaryLedger = CreateSiblingTemporaryPath(finalLedger);
            var temporarySummary = CreateSiblingTemporaryPath(finalSummary);
            try
            {
                var loader = new EndfieldVfsLoader(options.StreamingAssets, options.FallbackAssets);
                var catalog = loader.DiscoverCatalog();
                var inputs = new List<EndfieldVfsCorpusFile>();
                var presentRawIds = new HashSet<byte>();
                var selectedKnownIds = new HashSet<byte>(options.SelectedBlockTypes().Select(item => (byte)item));
                var sourceUnavailableBlocks = 0;
                var sourceExcludedBlocks = 0;
                var sourceUnavailableChunks = 0;
                var sourceExcludedChunks = 0;

                foreach (var entry in catalog)
                {
                    var info = entry.CanonicalInfo;
                    if (info == null)
                    {
                        // A directory with no parseable metadata is still visible in
                        // summary accounting, but cannot yield logical file rows.
                        if (entry.PrimaryMetadataPath != null || entry.FallbackMetadataPath != null)
                            sourceUnavailableBlocks++;
                        continue;
                    }
                    var rawId = info.BlockTypeValue;
                    presentRawIds.Add(rawId);
                    if (!IsSelectedRawBlock(options, rawId)) continue;
                    var excluded = options.ExcludeDeferredVoice && IsDeferredVoice(rawId);
                    if (excluded) sourceExcludedBlocks++;
                    var metadataVerified = entry.State != EndfieldVfsCatalogState.Conflicting
                        && entry.State != EndfieldVfsCatalogState.ShadowedEmpty;
                    foreach (var chunk in info.Chunks)
                    {
                        var selectedFiles = chunk.Files.Where(file => options.ShouldIncludeFile(file.FileName)).ToList();
                        if (selectedFiles.Count == 0) continue;
                        if (excluded)
                        {
                            sourceExcludedChunks++;
                            foreach (var file in selectedFiles)
                            {
                                inputs.Add(CreateProfileInput(
                                    loader, entry, chunk, file, metadataVerified, "excluded",
                                    "deferred English/Japanese/Korean voice block excluded by default"));
                            }
                            continue;
                        }

                        string chunkPath = null;
                        try
                        {
                            chunkPath = loader.ResolveChunkPath(entry, chunk);
                        }
                        catch (EndfieldVfsChunkNotFoundException)
                        {
                            sourceUnavailableChunks++;
                        }
                        foreach (var file in selectedFiles)
                        {
                            if (chunkPath == null)
                            {
                                inputs.Add(CreateProfileInput(
                                    loader, entry, chunk, file, metadataVerified, "unavailable",
                                    "physical chunk is missing from primary and fallback roots"));
                                continue;
                            }
                            var (chunkSource, _) = ClassifyChunkPath(
                                chunkPath, options.StreamingAssets, options.FallbackAssets);
                            inputs.Add(EndfieldVfsCorpusClassifier.FromLoader(
                                loader, entry, chunk, file, metadataVerified, chunkSource));
                        }
                    }
                }

                foreach (var knownId in selectedKnownIds)
                {
                    if (presentRawIds.Contains(knownId)) continue;
                    if (options.ExcludeDeferredVoice && IsDeferredVoice(knownId))
                    {
                        sourceExcludedBlocks++;
                        continue;
                    }
                    sourceUnavailableBlocks++;
                }

                var summary = EndfieldVfsCorpusClassifier.WriteJsonlGzip(
                    inputs, temporaryLedger, temporarySummary, options.BoundedByteLimit);
                summary.PrimaryAssets = NormalizePath(Path.GetFullPath(options.StreamingAssets));
                summary.FallbackAssets = string.IsNullOrEmpty(options.FallbackAssets)
                    ? string.Empty
                    : NormalizePath(Path.GetFullPath(options.FallbackAssets));
                summary.UnavailableBlockCount = sourceUnavailableBlocks;
                summary.UnavailableChunkCount = sourceUnavailableChunks;
                summary.ExcludedBlockCount = sourceExcludedBlocks;
                summary.ExcludedChunkCount = sourceExcludedChunks;
                using (var ledgerInput = File.OpenRead(temporaryLedger))
                {
                    summary.LedgerSha256 = Convert.ToHexString(SHA256.HashData(ledgerInput));
                }
                summary.RecomputeCompleteness();
                EndfieldVfsCorpusClassifier.WriteSummary(temporarySummary, summary);
                Console.WriteLine(
                    $"  Profiled {summary.FileCount} files; excluded={summary.ExcludedCount}, unavailable={summary.UnavailableCount} " +
                    $"(blocks={summary.UnavailableBlockCount}, chunks={summary.UnavailableChunkCount})");
                // Invalidate the old commit marker, then publish the ledger and
                // terminal summary. Expected corpus failures remain inspectable;
                // the command returns non-zero only after both outputs exist.
                if (File.Exists(finalSummary)) File.Delete(finalSummary);
                File.Move(temporaryLedger, finalLedger, overwrite: true);
                File.Move(temporarySummary, finalSummary, overwrite: true);
                Console.WriteLine($"  Done: VFS profile -> {finalLedger}; terminal summary -> {finalSummary}");
                if (!summary.Complete)
                {
                    throw new EndfieldVfsException(
                        $"vfs-profile failed integrity/reconciliation gates: failures={summary.FailureCount}, " +
                        $"unavailableFiles={summary.UnavailableCount}, unavailableBlocks={summary.UnavailableBlockCount}, " +
                        $"unavailableChunks={summary.UnavailableChunkCount}");
                }
            }
            catch
            {
                TryDeleteTemporaryFile(temporaryLedger);
                TryDeleteTemporaryFile(temporarySummary);
                throw;
            }
        }

        private static void RunVfsInnerAudit(VfsOptions options)
        {
            var unsupportedRequestedTypes = options.UseAllBlockTypes
                ? new List<EndfieldVfsBlockType>()
                : options.BlockTypes
                    .Where(item => item is not EndfieldVfsBlockType.InitialBundle
                        and not EndfieldVfsBlockType.Bundle)
                    .Distinct()
                    .ToList();
            if (unsupportedRequestedTypes.Count != 0)
            {
                throw new ArgumentException(
                    "vfs-inner-audit does not support requested block type(s): " +
                    string.Join(", ", unsupportedRequestedTypes.Select(item => item.GetName())));
            }
            var selectedRawIds = options.UseAllBlockTypes
                ? new HashSet<byte>(new byte[] { (byte)EndfieldVfsBlockType.InitialBundle, (byte)EndfieldVfsBlockType.Bundle })
                : new HashSet<byte>(options.BlockTypes
                    .Where(item => item is EndfieldVfsBlockType.InitialBundle or EndfieldVfsBlockType.Bundle)
                    .Select(item => (byte)item));
            if (selectedRawIds.Count == 0)
            {
                throw new ArgumentException("vfs-inner-audit requires --block-type initial-bundle and/or bundle");
            }

            var finalLedger = options.Output;
            var finalSummary = string.IsNullOrEmpty(options.SummaryOutput)
                ? finalLedger + ".summary.json"
                : options.SummaryOutput;
            if (string.Equals(
                Path.GetFullPath(finalLedger), Path.GetFullPath(finalSummary),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("vfs-inner-audit ledger and summary outputs must be distinct paths");
            }
            var temporaryLedger = CreateSiblingTemporaryPath(finalLedger);
            var temporarySummary = CreateSiblingTemporaryPath(finalSummary);
            var temporaryDirectory = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(finalLedger)) ?? ".",
                $".vfs-inner-audit-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);
            var summary = new JObject
            {
                ["format"] = "animestudio-endfield-vfs-inner-audit",
                ["schemaVersion"] = 1,
                ["primaryAssets"] = NormalizePath(Path.GetFullPath(options.StreamingAssets)),
                ["fallbackAssets"] = string.IsNullOrEmpty(options.FallbackAssets)
                    ? string.Empty
                    : NormalizePath(Path.GetFullPath(options.FallbackAssets)),
                ["selectedBlockTypeValues"] = new JArray(selectedRawIds.OrderBy(item => item)),
                ["fileCount"] = 0,
                ["verifiedCount"] = 0,
                ["failedCount"] = 0,
                ["declaredBytes"] = 0L,
                ["actualBytesRead"] = 0L,
                ["innerNodeCount"] = 0L,
                ["innerBlockCount"] = 0L,
                ["blockFailureCount"] = 0,
            };
            var failureDiagnostics = new JArray();
            try
            {
                var loader = new EndfieldVfsLoader(options.StreamingAssets, options.FallbackAssets);
                var game = GameManager.GetGame(GameType.ArknightsEndfield)
                    ?? throw new InvalidOperationException("Arknights Endfield game profile is unavailable");
                using (var compressedLedger = File.Create(temporaryLedger))
                using (var gzip = new System.IO.Compression.GZipStream(
                    compressedLedger, System.IO.Compression.CompressionLevel.Optimal))
                using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
                {
                    var catalog = loader.DiscoverCatalog()
                        .OrderBy(item => item.HashDirectory, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    summary["catalogEntryCount"] = catalog.Count;
                    var selectedDirectories = selectedRawIds.ToDictionary(
                        item => loader.BlockDirectoryName((EndfieldVfsBlockType)item),
                        item => item,
                        StringComparer.OrdinalIgnoreCase);
                    var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in catalog.Where(item => selectedDirectories.ContainsKey(item.HashDirectory)))
                    {
                        seenDirectories.Add(entry.HashDirectory);
                        var expectedRawId = selectedDirectories[entry.HashDirectory];
                        if (entry.CanonicalInfo == null)
                        {
                            WriteInnerAuditBlockFailure(
                                writer, summary, failureDiagnostics, entry, expectedRawId,
                                "unparseable_metadata", entry.PrimaryError ?? entry.FallbackError ?? "metadata is missing or unparsable");
                            continue;
                        }
                        if (entry.CanonicalInfo.BlockTypeValue != expectedRawId)
                        {
                            WriteInnerAuditBlockFailure(
                                writer, summary, failureDiagnostics, entry, expectedRawId,
                                "block_identity_mismatch",
                                $"directory {entry.HashDirectory} expected block type {expectedRawId}, " +
                                $"metadata declared {entry.CanonicalInfo.BlockTypeValue}");
                            continue;
                        }
                        if (entry.State is EndfieldVfsCatalogState.MissingMetadata or EndfieldVfsCatalogState.Conflicting)
                        {
                            WriteInnerAuditBlockFailure(
                                writer, summary, failureDiagnostics, entry, expectedRawId,
                                entry.State == EndfieldVfsCatalogState.Conflicting
                                    ? "conflicting_metadata" : "unparseable_metadata",
                                entry.PrimaryError ?? entry.FallbackError ?? $"catalog state is {entry.State}");
                            continue;
                        }
                        var info = entry.CanonicalInfo;
                        foreach (var chunk in info.Chunks.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase))
                        {
                            string chunkPath = null;
                            string chunkSource = "missing";
                            try
                            {
                                chunkPath = loader.ResolveChunkPath(entry, chunk);
                                (chunkSource, _) = ClassifyChunkPath(
                                    chunkPath, options.StreamingAssets, options.FallbackAssets);
                            }
                            catch (Exception exception)
                            {
                                foreach (var file in chunk.Files
                                    .Where(item => options.ShouldIncludeFile(item.FileName))
                                    .OrderBy(item => item.FileName, StringComparer.Ordinal))
                                {
                                    WriteInnerAuditFailure(
                                        writer, summary, failureDiagnostics, entry, info, chunk, file,
                                        chunkSource, chunkPath, "missing_chunk", exception.Message);
                                }
                                continue;
                            }

                            foreach (var file in chunk.Files
                                .Where(item => options.ShouldIncludeFile(item.FileName))
                                .OrderBy(item => item.FileName, StringComparer.Ordinal))
                            {
                                AuditInnerFile(
                                    loader, game, writer, summary, failureDiagnostics,
                                    entry, info, chunk, file, chunkSource, chunkPath, temporaryDirectory);
                            }
                        }
                    }
                    foreach (var missing in selectedDirectories
                        .Where(item => !seenDirectories.Contains(item.Key))
                        .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        WriteInnerAuditBlockFailure(
                            writer, summary, failureDiagnostics, null, missing.Value,
                            "missing_block_directory", $"selected block directory {missing.Key} is missing from both roots",
                            missing.Key);
                    }
                }

                summary["failureDiagnostics"] = failureDiagnostics;
                summary["complete"] = (int)summary["failedCount"] == 0
                    && (int)summary["blockFailureCount"] == 0;
                File.WriteAllText(
                    temporarySummary,
                    summary.ToString(Formatting.Indented).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n",
                    new UTF8Encoding(false));
                File.Move(temporaryLedger, finalLedger, overwrite: true);
                File.Move(temporarySummary, finalSummary, overwrite: true);
                Console.WriteLine(
                    $"  Inner-audited {summary["verifiedCount"]}/{summary["fileCount"]} logical files; " +
                    $"failed={summary["failedCount"]}; blockFailures={summary["blockFailureCount"]}; " +
                    $"ledger -> {finalLedger}; summary -> {finalSummary}");
                if (!(bool)summary["complete"])
                {
                    throw new EndfieldVfsException(
                        $"vfs-inner-audit failed for {(int)summary["failedCount"]} logical files and " +
                        $"{(int)summary["blockFailureCount"]} block-level checks");
                }
            }
            catch
            {
                TryDeleteTemporaryFile(temporaryLedger);
                TryDeleteTemporaryFile(temporarySummary);
                throw;
            }
            finally
            {
                TryDeleteTemporaryDirectory(temporaryDirectory);
            }
        }

        private static void AuditInnerFile(
            EndfieldVfsLoader loader,
            Game game,
            TextWriter writer,
            JObject summary,
            JArray failureDiagnostics,
            EndfieldVfsCatalogEntry entry,
            EndfieldVfsBlockMainInfo info,
            EndfieldVfsChunkInfo chunk,
            EndfieldVfsFileInfo file,
            string chunkSource,
            string chunkPath,
            string temporaryDirectory)
        {
            IncrementInnerAudit(summary, "fileCount", 1);
            IncrementInnerAudit(summary, "declaredBytes", file.Length);
            var row = new JObject
            {
                ["recordType"] = "file",
                ["blockTypeValue"] = file.BlockTypeValue,
                ["blockName"] = EndfieldVfsBlockTypes.GetName(file.BlockTypeValue),
                ["hashDirectory"] = entry.HashDirectory,
                ["overlayState"] = FormatInnerAuditOverlayState(entry.State),
                ["overlayStateScope"] = "block_metadata",
                ["virtualPath"] = file.FileName,
                ["chunk"] = chunk.FileName,
                ["source"] = chunkSource,
                ["physicalPath"] = chunkPath == null ? JValue.CreateNull() : NormalizePath(chunkPath),
                ["chunkDeclaredLength"] = chunk.Length,
                ["offset"] = file.Offset,
                ["declaredLength"] = file.Length,
                ["actualBytesRead"] = 0L,
                ["encryption"] = file.UseEncrypt,
                ["declaredFileDataMd5DisplayHex"] = EndfieldVfsFormatting.UInt128Hex(file.FileDataMd5),
                ["declaredFileDataMd5LittleEndianHex"] = EndfieldVfsFormatting.UInt128LittleEndianHex(file.FileDataMd5),
                ["fileDataMd5Verified"] = false,
                ["status"] = "failed",
            };
            Stream payload = null;
            VFSFile inner = null;
            try
            {
                payload = CreateInnerAuditPayloadStream(file.Length, temporaryDirectory);
                var actual = loader.ExtractFileFromPath(
                    chunkPath, chunk, file, payload, verifyMd5: true);
                row["actualBytesRead"] = actual;
                row["fileDataMd5Verified"] = true;
                IncrementInnerAudit(summary, "actualBytesRead", actual);
                payload.Position = 0;
                using (var reader = new FileReader(file.FileName, payload, leaveOpen: true))
                {
                    if (reader.FileType != FileType.VFSFile)
                    {
                        throw new InvalidDataException($"inner signature is {reader.FileType}, expected VFSFile");
                    }
                    inner = new VFSFile(reader, file.FileName, GameType.ArknightsEndfield);
                    row["status"] = "inner_structure_verified";
                    // This decoded custom-header word is used only by the
                    // BlocksInfoAtTheEnd variant.  Current files do not prove
                    // that it is the logical container length.
                    row["decodedHeaderSizeWord"] = inner.m_Header.size;
                    row["headerFlags"] = (uint)inner.m_Header.flags;
                    row["headerCompressedBlocksInfoSize"] = inner.m_Header.compressedBlocksInfoSize;
                    row["headerUncompressedBlocksInfoSize"] = inner.m_Header.uncompressedBlocksInfoSize;
                    row["storageBlockCount"] = inner.BlocksInfo.Count;
                    row["directoryNodeCount"] = inner.DirectoryInfo.Count;
                    row["decodedBlockBytes"] = inner.BlocksInfo.Sum(item => (long)item.uncompressedSize);
                    IncrementInnerAudit(summary, "verifiedCount", 1);
                    IncrementInnerAudit(summary, "innerNodeCount", inner.DirectoryInfo.Count);
                    IncrementInnerAudit(summary, "innerBlockCount", inner.BlocksInfo.Count);
                }
            }
            catch (Exception exception)
            {
                row["diagnostic"] = BoundInnerAuditDiagnostic(exception.Message);
                IncrementInnerAudit(summary, "failedCount", 1);
                if (failureDiagnostics.Count < MaxFailureDiagnostics)
                {
                    failureDiagnostics.Add(new JObject
                    {
                        ["blockTypeValue"] = file.BlockTypeValue,
                        ["virtualPath"] = file.FileName,
                        ["chunk"] = chunk.FileName,
                        ["source"] = chunkSource,
                        ["offset"] = file.Offset,
                        ["expected"] = file.Length,
                        ["actual"] = row["actualBytesRead"],
                        ["diagnostic"] = row["diagnostic"],
                    });
                }
            }
            finally
            {
                if (inner?.fileList != null)
                {
                    foreach (var node in inner.fileList)
                    {
                        node.stream?.Dispose();
                    }
                }
                payload?.Dispose();
            }
            WriteJsonLine(writer, row);
        }

        private static void WriteInnerAuditBlockFailure(
            TextWriter writer,
            JObject summary,
            JArray failureDiagnostics,
            EndfieldVfsCatalogEntry entry,
            byte expectedRawId,
            string status,
            string diagnostic,
            string hashDirectory = null)
        {
            summary["blockFailureCount"] = (int)summary["blockFailureCount"] + 1;
            var bounded = BoundInnerAuditDiagnostic(diagnostic);
            var directory = hashDirectory ?? entry?.HashDirectory ?? string.Empty;
            var row = new JObject
            {
                ["recordType"] = "block_failure",
                ["blockTypeValue"] = expectedRawId,
                ["blockName"] = EndfieldVfsBlockTypes.GetName(expectedRawId),
                ["hashDirectory"] = directory,
                ["overlayState"] = entry == null
                    ? "missing_both" : FormatInnerAuditOverlayState(entry.State),
                ["overlayStateScope"] = "block_metadata",
                ["status"] = status,
                ["primaryMetadataPath"] = entry?.PrimaryMetadataPath == null
                    ? JValue.CreateNull() : NormalizePath(entry.PrimaryMetadataPath),
                ["fallbackMetadataPath"] = entry?.FallbackMetadataPath == null
                    ? JValue.CreateNull() : NormalizePath(entry.FallbackMetadataPath),
                ["primaryError"] = entry?.PrimaryError == null
                    ? JValue.CreateNull() : BoundInnerAuditDiagnostic(entry.PrimaryError),
                ["fallbackError"] = entry?.FallbackError == null
                    ? JValue.CreateNull() : BoundInnerAuditDiagnostic(entry.FallbackError),
                ["diagnostic"] = bounded,
            };
            WriteJsonLine(writer, row);
            if (failureDiagnostics.Count < MaxFailureDiagnostics)
            {
                failureDiagnostics.Add(row.DeepClone());
            }
        }

        private static Stream CreateInnerAuditPayloadStream(long length, string temporaryDirectory)
        {
            const long maxMemoryPayload = 64L * 1024 * 1024;
            if (length < 0)
            {
                throw new InvalidDataException($"negative VFS logical-file length {length}");
            }
            if (length <= maxMemoryPayload)
            {
                return new MemoryStream(checked((int)length));
            }
            var path = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N") + ".bin");
            return new FileStream(
                path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                1024 * 1024, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        }

        private static void WriteInnerAuditFailure(
            TextWriter writer,
            JObject summary,
            JArray failureDiagnostics,
            EndfieldVfsCatalogEntry entry,
            EndfieldVfsBlockMainInfo info,
            EndfieldVfsChunkInfo chunk,
            EndfieldVfsFileInfo file,
            string chunkSource,
            string chunkPath,
            string status,
            string diagnostic)
        {
            IncrementInnerAudit(summary, "fileCount", 1);
            IncrementInnerAudit(summary, "declaredBytes", file.Length);
            IncrementInnerAudit(summary, "failedCount", 1);
            var bounded = BoundInnerAuditDiagnostic(diagnostic);
            WriteJsonLine(writer, new JObject
            {
                ["recordType"] = "file",
                ["blockTypeValue"] = file.BlockTypeValue,
                ["blockName"] = EndfieldVfsBlockTypes.GetName(file.BlockTypeValue),
                ["hashDirectory"] = entry.HashDirectory,
                ["overlayState"] = FormatInnerAuditOverlayState(entry.State),
                ["overlayStateScope"] = "block_metadata",
                ["virtualPath"] = file.FileName,
                ["chunk"] = chunk.FileName,
                ["source"] = chunkSource,
                ["physicalPath"] = chunkPath == null ? JValue.CreateNull() : NormalizePath(chunkPath),
                ["chunkDeclaredLength"] = chunk.Length,
                ["offset"] = file.Offset,
                ["declaredLength"] = file.Length,
                ["actualBytesRead"] = 0L,
                ["encryption"] = file.UseEncrypt,
                ["declaredFileDataMd5DisplayHex"] = EndfieldVfsFormatting.UInt128Hex(file.FileDataMd5),
                ["declaredFileDataMd5LittleEndianHex"] = EndfieldVfsFormatting.UInt128LittleEndianHex(file.FileDataMd5),
                ["fileDataMd5Verified"] = false,
                ["status"] = status,
                ["diagnostic"] = bounded,
            });
            if (failureDiagnostics.Count < MaxFailureDiagnostics)
            {
                failureDiagnostics.Add(new JObject
                {
                    ["blockTypeValue"] = file.BlockTypeValue,
                    ["virtualPath"] = file.FileName,
                    ["chunk"] = chunk.FileName,
                    ["source"] = chunkSource,
                    ["offset"] = file.Offset,
                    ["expected"] = file.Length,
                    ["actual"] = 0L,
                    ["diagnostic"] = bounded,
                });
            }
        }

        private static void IncrementInnerAudit(JObject summary, string name, long amount)
        {
            summary[name] = (long)summary[name] + amount;
        }

        private static string BoundInnerAuditDiagnostic(string message)
        {
            var normalized = (message ?? "inner audit failed").Replace('\r', ' ').Replace('\n', ' ');
            return normalized.Length <= 320 ? normalized : normalized[..320] + "...";
        }

        private static string FormatInnerAuditOverlayState(EndfieldVfsCatalogState state) => state switch
        {
            EndfieldVfsCatalogState.PrimaryOnly => "primary_only",
            EndfieldVfsCatalogState.FallbackOnly => "fallback_only",
            EndfieldVfsCatalogState.Identical => "identical",
            EndfieldVfsCatalogState.Replaced => "replaced",
            EndfieldVfsCatalogState.ShadowedEmpty => "shadowed_empty",
            EndfieldVfsCatalogState.Conflicting => "conflicting_metadata",
            _ => "missing_both",
        };

        private static EndfieldVfsCorpusFile CreateProfileInput(
            EndfieldVfsLoader loader,
            EndfieldVfsCatalogEntry entry,
            EndfieldVfsChunkInfo chunk,
            EndfieldVfsFileInfo file,
            bool metadataVerified,
            string status,
            string diagnostic) => new()
            {
                BlockTypeValue = file.BlockTypeValue,
                BlockTypeName = EndfieldVfsBlockTypes.GetName(file.BlockTypeValue),
                VirtualPath = file.FileName,
                ChunkFileName = chunk.FileName,
                ChunkMd5 = EndfieldVfsFormatting.UInt128LittleEndianHex(chunk.Md5Name),
                ChunkContentMd5 = EndfieldVfsFormatting.UInt128LittleEndianHex(chunk.ContentMd5),
                ChunkSource = status == "excluded" ? "excluded" : "missing",
                ChunkLength = chunk.Length,
                Offset = file.Offset,
                Length = file.Length,
                UseEncrypt = file.UseEncrypt,
                MetadataVerified = metadataVerified,
                StatusOverride = status,
                DiagnosticOverride = diagnostic,
            };

        private static bool IsSelectedRawBlock(VfsOptions options, byte rawId) =>
            options.UseAllBlockTypes || options.BlockTypes.Any(item => (byte)item == rawId);

        private static bool IsDeferredVoice(byte rawId) => rawId is 102 or 103 or 104;

        private static IEnumerable<VfsBlockSelection> LoadSelectedBlocks(
            EndfieldVfsLoader loader,
            VfsOptions options,
            Action<EndfieldVfsBlockType> beforeLoad = null,
            Action<EndfieldVfsBlockType, EndfieldVfsBlockNotFoundException> missingBlock = null)
        {
            foreach (var blockType in options.SelectedBlockTypes())
            {
                beforeLoad?.Invoke(blockType);
                EndfieldVfsBlockMainInfo blockInfo;
                try
                {
                    blockInfo = loader.LoadBlockInfo(blockType);
                }
                catch (EndfieldVfsBlockNotFoundException e)
                {
                    missingBlock?.Invoke(blockType, e);
                    continue;
                }

                yield return new VfsBlockSelection(blockType, blockInfo);
            }
        }

        private static IEnumerable<VfsFileSelection> EnumerateSelectedFiles(
            EndfieldVfsLoader loader,
            VfsOptions options,
            Action<EndfieldVfsBlockType, EndfieldVfsBlockNotFoundException> missingBlock = null)
        {
            foreach (var block in LoadSelectedBlocks(loader, options, missingBlock: missingBlock))
            {
                foreach (var chunk in block.Info.Chunks)
                {
                    foreach (var file in SelectedFiles(options, chunk))
                    {
                        yield return new VfsFileSelection(block.BlockType, chunk, file);
                    }
                }
            }
        }

        private static IEnumerable<EndfieldVfsFileInfo> SelectedFiles(VfsOptions options, EndfieldVfsChunkInfo chunk) =>
            chunk.Files.Where(file => IsSelectedFile(options, file));

        private static void DumpBlock(EndfieldVfsLoader loader, VfsBlockSelection block, string output, VfsOptions options)
        {
            var blockType = block.BlockType;
            var blockInfo = block.Info;

            var successCount = 0;
            var errorCount = 0;
            var diagnostics = new List<string>();
            var diagnosticsLock = new object();
            var selectedChunks = blockInfo.Chunks
                .Select(chunk => new VfsChunkSelection(chunk, SelectedFiles(options, chunk).ToList()))
                .ToList();
            var totalFiles = selectedChunks.Sum(chunk => chunk.Files.Count);

            foreach (var selectedChunk in selectedChunks)
            {
                Parallel.ForEach(selectedChunk.Files, file =>
                {
                    try
                    {
                        ProcessDumpFile(
                            loader,
                            blockType,
                            selectedChunk.Chunk,
                            file,
                            output,
                            options.VerifyMd5 && ShouldFailOnFileErrors(options, blockType));
                        Interlocked.Increment(ref successCount);
                    }
                    catch (Exception e)
                    {
                        Interlocked.Increment(ref errorCount);
                        lock (diagnosticsLock)
                        {
                            if (diagnostics.Count < MaxFailureDiagnostics)
                            {
                                diagnostics.Add($"{file.FileName}: {e.Message}");
                            }
                        }
                    }
                });
            }

            Console.WriteLine($"  Done: Extracted {successCount}/{totalFiles} files");
            if (errorCount > 0)
            {
                Console.WriteLine($"  Warning: {errorCount} files failed");
                if (ShouldFailOnFileErrors(options, blockType))
                {
                    ThrowFileErrors("dump", errorCount, diagnostics);
                }
            }
        }

        private static bool IsSelectedFile(VfsOptions options, EndfieldVfsFileInfo file)
        {
            return !string.IsNullOrEmpty(file.FileName)
                && !file.FileName.EndsWith("/")
                && !file.FileName.EndsWith("\\")
                && options.ShouldIncludeFile(file.FileName);
        }

        private static void ProcessDumpFile(
            EndfieldVfsLoader loader,
            EndfieldVfsBlockType blockType,
            EndfieldVfsChunkInfo chunk,
            EndfieldVfsFileInfo file,
            string output,
            bool verifyMd5)
        {
            string outputPath;
            if (blockType == EndfieldVfsBlockType.Table)
            {
                var data = loader.ExtractFileToBytes(blockType, chunk, file, verifyMd5);
                outputPath = EndfieldDumpProcessors.ProcessTableFile(data, output);
            }
            else if (blockType == EndfieldVfsBlockType.Lua)
            {
                var data = loader.ExtractFileToBytes(blockType, chunk, file, verifyMd5);
                outputPath = EndfieldDumpProcessors.ProcessLuaFile(data, file.FileName, output);
            }
            else if (blockType == EndfieldVfsBlockType.Video || blockType == EndfieldVfsBlockType.AuditVideo)
            {
                var data = loader.ExtractFileToBytes(blockType, chunk, file, verifyMd5);
                outputPath = EndfieldDumpProcessors.ProcessVideoFile(data, file.FileName, output);
            }
            else
            {
                outputPath = EndfieldDumpProcessors.ResolveContainedPath(output, file.FileName);
                var parent = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
                loader.ExtractFile(blockType, chunk, file, stream, verifyMd5);
            }
            if (!File.Exists(outputPath))
            {
                throw new IOException($"Failed to create output file: {outputPath}");
            }
        }

        private static void RunVfsIndex(VfsOptions options)
        {
            var finalOutput = options.Output;
            var temporaryOutput = CreateSiblingTemporaryPath(finalOutput);
            options.Output = temporaryOutput;
            options.OutputDisplay = finalOutput;
            try
            {
                if (options.UseJsonLines)
                {
                    RunVfsIndexJsonLines(options);
                }
                else
                {
                    RunVfsIndexJson(options);
                }

                File.Move(temporaryOutput, finalOutput, overwrite: true);
            }
            catch
            {
                TryDeleteTemporaryFile(temporaryOutput);
                throw;
            }
            finally
            {
                options.Output = finalOutput;
                options.OutputDisplay = null;
            }
        }

        private static void RunVfsIndexJson(VfsOptions options)
        {

            var loader = new EndfieldVfsLoader(options.StreamingAssets, options.FallbackAssets);
            var blocks = new JArray();
            var flatFiles = new JArray();
            var missingBlocks = new JArray();
            var totalChunks = 0;
            var totalFiles = 0;
            var totalBytes = 0L;
            var missingChunks = 0;
            var integrityErrorCount = 0;
            var integrityDiagnostics = new List<string>();

            foreach (var block in LoadSelectedBlocks(
                loader,
                options,
                blockType => Console.WriteLine($"Indexing {blockType.GetName()} metadata..."),
                (blockType, e) =>
                {
                    Console.WriteLine($"  Warning: Block {e.HashDirectory} not found, skipping");
                    missingBlocks.Add(new JObject
                    {
                        ["name"] = blockType.GetName(),
                        ["hashDirectory"] = loader.BlockDirectoryName(blockType),
                    });
                }))
            {
                var blockType = block.BlockType;
                var blockInfo = block.Info;
                var blockDirName = loader.BlockDirectoryName(blockType);
                var chunkValues = new JArray();
                var blockFileCount = 0;
                var blockByteCount = 0L;
                var blockMissingChunks = 0;

                foreach (var chunk in blockInfo.Chunks)
                {
                    var chunkFileName = chunk.FileName;
                    var chunkExists = true;
                    string chunkSource;
                    string chunkRelativePath;
                    string chunkAbsolutePath;
                    try
                    {
                        var chunkPath = loader.ResolveChunkPath(blockType, chunk);
                        (chunkSource, chunkRelativePath) = ClassifyChunkPath(chunkPath, options.StreamingAssets, options.FallbackAssets);
                        chunkAbsolutePath = NormalizePath(chunkPath);
                    }
                    catch (EndfieldVfsChunkNotFoundException)
                    {
                        chunkExists = false;
                        chunkSource = "missing";
                        chunkRelativePath = $"{blockDirName}/{chunkFileName}";
                        chunkAbsolutePath = null;
                        blockMissingChunks++;
                        missingChunks++;
                    }

                    var selectedFiles = SelectedFiles(options, chunk).ToList();
                    if (!chunkExists && selectedFiles.Count > 0 && ShouldFailOnFileErrors(options, blockType))
                    {
                        integrityErrorCount++;
                        if (integrityDiagnostics.Count < MaxFailureDiagnostics)
                        {
                            integrityDiagnostics.Add($"{blockType.GetName()}/{chunkFileName}: chunk is missing");
                        }
                    }
                    if (options.VerifyMd5
                        && chunkExists
                        && selectedFiles.Count > 0
                        && ShouldFailOnFileErrors(options, blockType))
                    {
                        VerifySelectedFiles(
                            loader,
                            blockType,
                            chunk,
                            selectedFiles,
                            integrityDiagnostics,
                            ref integrityErrorCount);
                    }

                    var files = new JArray();
                    var chunkFileCount = 0;
                    var chunkByteCount = 0L;
                    foreach (var file in selectedFiles)
                    {
                        files.Add(new JObject
                        {
                            ["blockType"] = EndfieldVfsBlockTypes.GetName(file.BlockTypeValue),
                            ["blockTypeValue"] = file.BlockTypeValue,
                            ["chunkMd5"] = EndfieldVfsFormatting.UInt128Hex(file.FileChunkMd5),
                            ["dataMd5"] = EndfieldVfsFormatting.UInt128Hex(file.FileDataMd5),
                            ["encrypted"] = file.UseEncrypt,
                            ["ivSeed"] = file.IvSeed,
                            ["length"] = file.Length,
                            ["name"] = file.FileName,
                            ["nameHash"] = file.FileNameHash,
                            ["offset"] = file.Offset,
                            ["tag"] = file.FileTag.ToString(),
                        });
                        flatFiles.Add(new JObject
                        {
                            ["blockName"] = blockType.GetName(),
                            ["chunkAbsolutePath"] = chunkAbsolutePath == null ? JValue.CreateNull() : chunkAbsolutePath,
                            ["chunkContentMd5"] = EndfieldVfsFormatting.UInt128Hex(chunk.ContentMd5),
                            ["chunkExists"] = chunkExists,
                            ["chunkFile"] = chunkFileName,
                            ["chunkLength"] = chunk.Length,
                            ["chunkMd5Name"] = EndfieldVfsFormatting.UInt128Hex(chunk.Md5Name),
                            ["chunkRelativePath"] = chunkRelativePath,
                            ["chunkSource"] = chunkSource,
                            ["encrypted"] = file.UseEncrypt,
                            ["fileBlockType"] = EndfieldVfsBlockTypes.GetName(file.BlockTypeValue),
                            ["fileBlockTypeValue"] = file.BlockTypeValue,
                            ["fileChunkMd5"] = EndfieldVfsFormatting.UInt128Hex(file.FileChunkMd5),
                            ["fileDataMd5"] = EndfieldVfsFormatting.UInt128Hex(file.FileDataMd5),
                            ["fileName"] = file.FileName,
                            ["fileNameHash"] = file.FileNameHash,
                            ["fileTag"] = file.FileTag.ToString(),
                            ["hashDirectory"] = blockDirName,
                            ["ivSeed"] = file.IvSeed,
                            ["length"] = file.Length,
                            ["offset"] = file.Offset,
                        });
                        chunkFileCount++;
                        chunkByteCount += file.Length;
                    }

                    blockFileCount += chunkFileCount;
                    blockByteCount += chunkByteCount;

                    chunkValues.Add(new JObject
                    {
                        ["absolutePath"] = chunkAbsolutePath == null ? JValue.CreateNull() : chunkAbsolutePath,
                        ["blockType"] = EndfieldVfsBlockTypes.GetName(chunk.BlockTypeValue),
                        ["blockTypeValue"] = chunk.BlockTypeValue,
                        ["byteCount"] = chunkByteCount,
                        ["contentMd5"] = EndfieldVfsFormatting.UInt128Hex(chunk.ContentMd5),
                        ["exists"] = chunkExists,
                        ["fileCount"] = chunkFileCount,
                        ["fileName"] = chunkFileName,
                        ["files"] = files,
                        ["length"] = chunk.Length,
                        ["md5Name"] = EndfieldVfsFormatting.UInt128Hex(chunk.Md5Name),
                        ["relativePath"] = chunkRelativePath,
                        ["source"] = chunkSource,
                        ["tag"] = chunk.MainTag.ToString(),
                    });
                }

                totalChunks += blockInfo.Chunks.Count;
                totalFiles += blockFileCount;
                totalBytes += blockByteCount;

                blocks.Add(new JObject
                {
                    ["blockType"] = EndfieldVfsBlockTypes.GetName(blockInfo.BlockTypeValue),
                    ["blockTypeValue"] = blockInfo.BlockTypeValue,
                    ["byteCount"] = blockByteCount,
                    ["chunkCount"] = blockInfo.Chunks.Count,
                    ["chunks"] = chunkValues,
                    ["codeVersion"] = blockInfo.CodeVersion,
                    ["declaredChunkBytes"] = blockInfo.GroupChunksLength,
                    ["declaredFileCount"] = blockInfo.GroupFileInfoNum,
                    ["fileCount"] = blockFileCount,
                    ["groupConfigHashName"] = blockInfo.GroupConfigHashName,
                    ["groupConfigName"] = blockInfo.GroupConfigName,
                    ["hashDirectory"] = blockDirName,
                    ["missingChunkCount"] = blockMissingChunks,
                    ["name"] = blockType.GetName(),
                    ["version"] = blockInfo.Version,
                });
            }

            var outputPayload = new JObject
            {
                ["blockFilter"] = options.BlockFilterName,
                ["blocks"] = blocks,
                ["fallbackAssets"] = string.IsNullOrEmpty(options.FallbackAssets) ? JValue.CreateNull() : NormalizePath(options.FallbackAssets),
                ["files"] = flatFiles,
                ["generatedAtEpoch"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["missingBlocks"] = missingBlocks,
                ["schemaVersion"] = 1,
                ["streamingAssets"] = NormalizePath(options.StreamingAssets),
                ["summary"] = new JObject
                {
                    ["blockCount"] = blocks.Count,
                    ["byteCount"] = totalBytes,
                    ["chunkCount"] = totalChunks,
                    ["fileCount"] = totalFiles,
                    ["missingBlockCount"] = missingBlocks.Count,
                    ["missingChunkCount"] = missingChunks,
                },
            };

            var outputParent = Path.GetDirectoryName(options.Output);
            if (!string.IsNullOrEmpty(outputParent))
            {
                Directory.CreateDirectory(outputParent);
            }
            var indexJson = JsonConvert.SerializeObject(outputPayload, Formatting.Indented)
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            File.WriteAllText(options.Output, indexJson, new UTF8Encoding(false));
            Console.WriteLine($"  Done: indexed {totalFiles} files across {totalChunks} chunks -> {options.OutputDisplay ?? options.Output}");
            if (integrityErrorCount > 0)
            {
                ThrowFileErrors("index", integrityErrorCount, integrityDiagnostics);
            }
        }

        private static string CreateSiblingTemporaryPath(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                throw new ArgumentException("index output path is required", nameof(output));
            }

            var fullOutput = Path.GetFullPath(output);
            var parent = Path.GetDirectoryName(fullOutput);
            if (string.IsNullOrEmpty(parent))
            {
                throw new ArgumentException($"index output has no parent directory: {output}", nameof(output));
            }
            Directory.CreateDirectory(parent);
            return Path.Combine(
                parent,
                $".{Path.GetFileName(fullOutput)}.{Guid.NewGuid():N}.tmp");
        }

        private static void TryDeleteTemporaryFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception cleanupException)
            {
                Console.Error.WriteLine($"Warning: failed to clean temporary VFS index {path}: {cleanupException.Message}");
            }
        }

        private static void TryDeleteTemporaryDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception cleanupException)
            {
                Console.Error.WriteLine($"Warning: failed to clean inner-audit temporary directory {path}: {cleanupException.Message}");
            }
        }

        private static bool ShouldFailOnFileErrors(VfsOptions options, EndfieldVfsBlockType blockType) =>
            !(options.UseAllBlockTypes && blockType is
                EndfieldVfsBlockType.AudioEnglish or
                EndfieldVfsBlockType.AudioJapanese or
                EndfieldVfsBlockType.AudioKorean);

        private static void ThrowFileErrors(string operation, int errorCount, List<string> diagnostics)
        {
            foreach (var diagnostic in diagnostics)
            {
                Console.Error.WriteLine($"Error: {operation} failed: {diagnostic}");
            }
            var suffix = errorCount > diagnostics.Count
                ? $"; {errorCount - diagnostics.Count} additional failures omitted"
                : string.Empty;
            throw new EndfieldVfsException(
                $"{operation} failed for {errorCount} selected files{suffix}");
        }

        private static void VerifySelectedFiles(
            EndfieldVfsLoader loader,
            EndfieldVfsBlockType blockType,
            EndfieldVfsChunkInfo chunk,
            List<EndfieldVfsFileInfo> files,
            List<string> diagnostics,
            ref int errorCount)
        {
            try
            {
                loader.VerifyChunkContentMd5(blockType, chunk);
            }
            catch (Exception exception)
            {
                RecordIntegrityFailure(
                    $"{blockType.GetName()}/{chunk.FileName}",
                    exception,
                    diagnostics,
                    ref errorCount);
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    loader.ExtractFileToBytes(blockType, chunk, file, verifyMd5: true);
                }
                catch (Exception exception)
                {
                    RecordIntegrityFailure(
                        $"{blockType.GetName()}/{chunk.FileName}/{file.FileName}",
                        exception,
                        diagnostics,
                        ref errorCount);
                }
            }
        }

        private static void RecordIntegrityFailure(
            string item,
            Exception exception,
            List<string> diagnostics,
            ref int errorCount)
        {
            errorCount++;
            if (diagnostics.Count < MaxFailureDiagnostics)
            {
                diagnostics.Add($"{item}: {exception.Message}");
            }
        }

        private static void RunVfsIndexJsonLines(VfsOptions options)
        {
            var outputParent = Path.GetDirectoryName(options.Output);
            if (!string.IsNullOrEmpty(outputParent))
            {
                Directory.CreateDirectory(outputParent);
            }

            var loader = new EndfieldVfsLoader(options.StreamingAssets, options.FallbackAssets);
            var totalBlocks = 0;
            var totalChunks = 0;
            var totalFiles = 0;
            var totalBytes = 0L;
            var missingBlocks = 0;
            var missingChunks = 0;
            var integrityErrorCount = 0;
            var integrityDiagnostics = new List<string>();

            using var writer = new StreamWriter(options.Output, false, new UTF8Encoding(false), 64 * 1024)
            {
                NewLine = "\n",
            };
            WriteJsonLine(writer, new JObject
            {
                ["recordType"] = "header",
                ["format"] = "animestudio-vfs-index",
                ["encoding"] = "jsonl",
                ["schemaVersion"] = 1,
                ["blockFilter"] = options.BlockFilterName,
                ["fallbackAssets"] = string.IsNullOrEmpty(options.FallbackAssets) ? JValue.CreateNull() : NormalizePath(options.FallbackAssets),
                ["generatedAtEpoch"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["streamingAssets"] = NormalizePath(options.StreamingAssets),
            });

            foreach (var block in LoadSelectedBlocks(
                loader,
                options,
                blockType => Console.WriteLine($"Indexing {blockType.GetName()} metadata..."),
                (blockType, e) =>
                {
                    Console.WriteLine($"  Warning: Block {e.HashDirectory} not found, skipping");
                    WriteJsonLine(writer, new JObject
                    {
                        ["recordType"] = "missingBlock",
                        ["name"] = blockType.GetName(),
                        ["hashDirectory"] = loader.BlockDirectoryName(blockType),
                    });
                    missingBlocks++;
                }))
            {
                var blockType = block.BlockType;
                var blockInfo = block.Info;
                var blockName = blockType.GetName();
                var blockDirName = loader.BlockDirectoryName(blockType);
                totalBlocks++;

                WriteJsonLine(writer, new JObject
                {
                    ["recordType"] = "block",
                    ["blockType"] = EndfieldVfsBlockTypes.GetName(blockInfo.BlockTypeValue),
                    ["blockTypeValue"] = blockInfo.BlockTypeValue,
                    ["chunkCount"] = blockInfo.Chunks.Count,
                    ["codeVersion"] = blockInfo.CodeVersion,
                    ["declaredChunkBytes"] = blockInfo.GroupChunksLength,
                    ["declaredFileCount"] = blockInfo.GroupFileInfoNum,
                    ["groupConfigHashName"] = blockInfo.GroupConfigHashName,
                    ["groupConfigName"] = blockInfo.GroupConfigName,
                    ["hashDirectory"] = blockDirName,
                    ["name"] = blockName,
                    ["version"] = blockInfo.Version,
                });

                foreach (var chunk in blockInfo.Chunks)
                {
                    var chunkFileName = chunk.FileName;
                    var chunkMd5Name = EndfieldVfsFormatting.UInt128Hex(chunk.Md5Name);
                    var chunkId = $"{blockName}/{chunkMd5Name}";
                    var chunkExists = true;
                    string chunkSource;
                    string chunkRelativePath;
                    string chunkAbsolutePath;
                    try
                    {
                        var chunkPath = loader.ResolveChunkPath(blockType, chunk);
                        (chunkSource, chunkRelativePath) = ClassifyChunkPath(chunkPath, options.StreamingAssets, options.FallbackAssets);
                        chunkAbsolutePath = NormalizePath(chunkPath);
                    }
                    catch (EndfieldVfsChunkNotFoundException)
                    {
                        chunkExists = false;
                        chunkSource = "missing";
                        chunkRelativePath = $"{blockDirName}/{chunkFileName}";
                        chunkAbsolutePath = null;
                        missingChunks++;
                    }

                    var selectedFiles = SelectedFiles(options, chunk).ToList();
                    if (!chunkExists && selectedFiles.Count > 0 && ShouldFailOnFileErrors(options, blockType))
                    {
                        integrityErrorCount++;
                        if (integrityDiagnostics.Count < MaxFailureDiagnostics)
                        {
                            integrityDiagnostics.Add($"{blockType.GetName()}/{chunkFileName}: chunk is missing");
                        }
                    }
                    if (options.VerifyMd5
                        && chunkExists
                        && selectedFiles.Count > 0
                        && ShouldFailOnFileErrors(options, blockType))
                    {
                        VerifySelectedFiles(
                            loader,
                            blockType,
                            chunk,
                            selectedFiles,
                            integrityDiagnostics,
                            ref integrityErrorCount);
                    }
                    var chunkByteCount = selectedFiles.Sum(file => (long)file.Length);
                    WriteJsonLine(writer, new JObject
                    {
                        ["recordType"] = "chunk",
                        ["absolutePath"] = chunkAbsolutePath == null ? JValue.CreateNull() : chunkAbsolutePath,
                        ["blockName"] = blockName,
                        ["blockType"] = EndfieldVfsBlockTypes.GetName(chunk.BlockTypeValue),
                        ["blockTypeValue"] = chunk.BlockTypeValue,
                        ["byteCount"] = chunkByteCount,
                        ["chunkId"] = chunkId,
                        ["contentMd5"] = EndfieldVfsFormatting.UInt128Hex(chunk.ContentMd5),
                        ["exists"] = chunkExists,
                        ["fileCount"] = selectedFiles.Count,
                        ["fileName"] = chunkFileName,
                        ["hashDirectory"] = blockDirName,
                        ["length"] = chunk.Length,
                        ["md5Name"] = chunkMd5Name,
                        ["relativePath"] = chunkRelativePath,
                        ["source"] = chunkSource,
                        ["tag"] = chunk.MainTag.ToString(),
                    });

                    foreach (var file in selectedFiles)
                    {
                        var fileName = NormalizePath(file.FileName);
                        WriteJsonLine(writer, new JObject
                        {
                            ["recordType"] = "file",
                            ["blockName"] = blockName,
                            ["chunkId"] = chunkId,
                            ["encrypted"] = file.UseEncrypt,
                            ["fileBlockType"] = EndfieldVfsBlockTypes.GetName(file.BlockTypeValue),
                            ["fileBlockTypeValue"] = file.BlockTypeValue,
                            ["fileChunkMd5"] = EndfieldVfsFormatting.UInt128Hex(file.FileChunkMd5),
                            ["fileDataMd5"] = EndfieldVfsFormatting.UInt128Hex(file.FileDataMd5),
                            ["fileName"] = fileName,
                            ["fileNameHash"] = file.FileNameHash,
                            ["fileTag"] = file.FileTag.ToString(),
                            ["ivSeed"] = file.IvSeed,
                            ["length"] = file.Length,
                            ["logicalId"] = $"{blockName}/{fileName.TrimStart('/')}",
                            ["offset"] = file.Offset,
                        });
                    }

                    totalChunks++;
                    totalFiles += selectedFiles.Count;
                    totalBytes += chunkByteCount;
                }
            }

            WriteJsonLine(writer, new JObject
            {
                ["recordType"] = "summary",
                ["blockCount"] = totalBlocks,
                ["byteCount"] = totalBytes,
                ["chunkCount"] = totalChunks,
                ["fileCount"] = totalFiles,
                ["missingBlockCount"] = missingBlocks,
                ["missingChunkCount"] = missingChunks,
            });
            Console.WriteLine($"  Done: indexed {totalFiles} files across {totalChunks} chunks -> {options.OutputDisplay ?? options.Output}");
            if (integrityErrorCount > 0)
            {
                ThrowFileErrors("index", integrityErrorCount, integrityDiagnostics);
            }
        }

        private static void WriteJsonLine(TextWriter writer, JObject payload)
        {
            writer.WriteLine(payload.ToString(Formatting.None));
        }

        private static (string source, string relativePath) ClassifyChunkPath(string path, string streamingAssets, string fallbackAssets)
        {
            var primaryVfs = Path.Combine(streamingAssets, EndfieldVfsLoader.VfsDirectoryName);
            if (TryRelativePath(path, primaryVfs, out var primaryRelative))
            {
                return ("primary", primaryRelative);
            }

            if (!string.IsNullOrEmpty(fallbackAssets))
            {
                var fallbackVfs = Path.Combine(fallbackAssets, EndfieldVfsLoader.VfsDirectoryName);
                if (TryRelativePath(path, fallbackVfs, out var fallbackRelative))
                {
                    return ("fallback", fallbackRelative);
                }
            }

            return ("unknown", NormalizePath(path));
        }

        private static bool TryRelativePath(string path, string root, out string relative)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var pathFull = Path.GetFullPath(path);
            if (pathFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                relative = NormalizePath(pathFull[rootFull.Length..]);
                return true;
            }

            relative = null;
            return false;
        }

        private static string NormalizePath(string path) => path.Replace('\\', '/');

        private static ExtendDataOptions ParseExtendDataOptions(string[] args)
        {
            if (args.Length > 1 && IsHelp(args[1]))
            {
                PrintCommandHelp(args[0]);
                throw new HelpRequestedException();
            }

            var options = new ExtendDataOptions
            {
                Output = "./extend_data",
            };
            for (var i = 1; i < args.Length; i++)
            {
                var token = args[i];
                if (IsHelp(token))
                {
                    PrintCommandHelp(args[0]);
                    throw new HelpRequestedException();
                }

                string value = null;
                var equalsIndex = token.IndexOf('=');
                if (equalsIndex > 0)
                {
                    value = token[(equalsIndex + 1)..];
                    token = token[..equalsIndex];
                }

                switch (token)
                {
                    case "-i":
                    case "--input":
                        options.Input = value ?? NextValue(args, ref i, token);
                        break;
                    case "-o":
                    case "--output":
                        options.Output = value ?? NextValue(args, ref i, token);
                        break;
                    default:
                        throw new ArgumentException($"unexpected argument: {token}");
                }
            }

            if (string.IsNullOrEmpty(options.Input))
            {
                throw new ArgumentException("--input is required");
            }
            return options;
        }

        private static VfsOptions ParseVfsOptions(string[] args, string defaultOutput)
        {
            if (args.Length > 1 && IsHelp(args[1]))
            {
                PrintCommandHelp(args[0]);
                throw new HelpRequestedException();
            }

            var options = new VfsOptions
            {
                Output = defaultOutput,
            };

            for (var i = 1; i < args.Length; i++)
            {
                var token = args[i];
                if (IsHelp(token))
                {
                    PrintCommandHelp(args[0]);
                    throw new HelpRequestedException();
                }

                string value = null;
                var equalsIndex = token.IndexOf('=');
                if (equalsIndex > 0)
                {
                    value = token[(equalsIndex + 1)..];
                    token = token[..equalsIndex];
                }

                switch (token)
                {
                    case "-s":
                    case "--streaming-assets":
                        options.StreamingAssets = value ?? NextValue(args, ref i, token);
                        break;
                    case "--fallback-assets":
                        options.FallbackAssets = value ?? NextValue(args, ref i, token);
                        break;
                    case "-o":
                    case "--output":
                        options.Output = value ?? NextValue(args, ref i, token);
                        break;
                    case "-b":
                    case "--block-type":
                        var rawBlock = value ?? NextValue(args, ref i, token);
                        if (string.Equals(rawBlock, "all", StringComparison.OrdinalIgnoreCase))
                        {
                            options.SelectAllBlockTypes();
                        }
                        else if (EndfieldVfsBlockTypes.TryParseCliValue(rawBlock, out var blockType))
                        {
                            options.AddBlockType(blockType);
                        }
                        else
                        {
                            throw new ArgumentException($"invalid block type: {rawBlock}");
                        }
                        break;
                    case "--file-regex":
                        options.AddFileRegex(value ?? NextValue(args, ref i, token));
                        break;
                    case "--verify-md5":
                        if (value != null)
                        {
                            throw new ArgumentException("--verify-md5 does not take a value");
                        }
                        if (string.Equals(args[0], "vfs-profile", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new ArgumentException("--verify-md5 is not supported by vfs-profile; use vfs-audit for MD5 certification");
                        }
                        options.VerifyMd5 = true;
                        break;
                    case "--bounded-bytes":
                        if (!string.Equals(args[0], "vfs-profile", StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("--bounded-bytes is only valid for vfs-profile");
                        if (!int.TryParse(value ?? NextValue(args, ref i, token), out var boundedBytes)
                            || boundedBytes < 1 || boundedBytes > 4096)
                            throw new ArgumentException("--bounded-bytes must be an integer between 1 and 4096");
                        options.BoundedByteLimit = boundedBytes;
                        break;
                    case "--summary-json":
                        if (!string.Equals(args[0], "vfs-profile", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(args[0], "vfs-inner-audit", StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("--summary-json is only valid for vfs-profile or vfs-inner-audit");
                        options.SummaryOutput = value ?? NextValue(args, ref i, token);
                        break;
                    case "--include-deferred-voice":
                        if (!string.Equals(args[0], "vfs-profile", StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("--include-deferred-voice is only valid for vfs-profile");
                        if (value != null) throw new ArgumentException("--include-deferred-voice does not take a value");
                        options.ExcludeDeferredVoice = false;
                        break;
                    case "--jsonl":
                        if (!string.Equals(args[0], "vfs-index", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(args[0], "vfsindex", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new ArgumentException("--jsonl is only valid for vfs-index");
                        }
                        if (value != null)
                        {
                            throw new ArgumentException("--jsonl does not take a value");
                        }
                        options.UseJsonLines = true;
                        break;
                    default:
                        throw new ArgumentException($"unexpected argument: {token}");
                }
            }

            if (string.IsNullOrEmpty(options.StreamingAssets))
            {
                throw new ArgumentException("--streaming-assets is required");
            }

            return options;
        }

        private static bool IsHelp(string value) => value == "-h" || value == "--help" || value == "/?";

        private static string NextValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{option} requires a value");
            }
            index++;
            return args[index];
        }

        private static void PrintCommandHelp(string command)
        {
            const string executable = "AnimeStudio.CLI.exe";
            const string blockTypeValues = "[default: all] [possible values: all, initial-audio, initial-bundle, initial-extend-data, bundle-manifest, i-fix-patch, audit-streaming, audit-dynamic-streaming, audit-iv, audit-audio, audit-video, bundle, audio, video, iv, streaming, dynamic-streaming, lua, table, json-data, extend-data, hotfix-audio, terrain, audio-chinese, audio-english, audio-japanese, audio-korean]";

            switch (command.ToLowerInvariant())
            {
                case "dump":
                    PrintHelpLines(
                        $"Usage: {executable} dump [OPTIONS] --streaming-assets <STREAMING_ASSETS>",
                        "",
                        "Options:",
                        "  -s, --streaming-assets <STREAMING_ASSETS>",
                        "          ",
                        "      --fallback-assets <FALLBACK_ASSETS>",
                        "          ",
                        "  -o, --output <OUTPUT>",
                        "          [default: ./output]",
                        "  -b, --block-type <BLOCK_TYPE>",
                        $"          {blockTypeValues}",
                        "          May be repeated to dump multiple block types.",
                        "      --file-regex <REGEX>",
                        "          Only dump files whose VFS filename matches the regex. May be repeated.",
                        "      --verify-md5",
                        "          Verify raw chunk ContentMd5 and decrypted file DataMd5 values.",
                        "  -h, --help",
                        "          Print help");
                    break;
                case "stream":
                    PrintHelpLines(
                        $"Usage: {executable} stream [OPTIONS] --streaming-assets <STREAMING_ASSETS>",
                        "",
                        "Options:",
                        "  -s, --streaming-assets <STREAMING_ASSETS>",
                        "          ",
                        "      --fallback-assets <FALLBACK_ASSETS>",
                        "          ",
                        "  -b, --block-type <BLOCK_TYPE>",
                        $"          {blockTypeValues}",
                        "          May be repeated to stream multiple block types.",
                        "      --file-regex <REGEX>",
                        "          Only stream files whose VFS filename matches the regex. May be repeated.",
                        "      --verify-md5",
                        "          Verify raw chunk ContentMd5 and decrypted file DataMd5 values.",
                        "  -h, --help",
                        "          Print help");
                    break;
                case "vfs-index":
                case "vfsindex":
                    PrintHelpLines(
                        $"Usage: {executable} vfs-index [OPTIONS] --streaming-assets <STREAMING_ASSETS>",
                        "",
                        "Options:",
                        "  -s, --streaming-assets <STREAMING_ASSETS>",
                        "          ",
                        "      --fallback-assets <FALLBACK_ASSETS>",
                        "          ",
                        "  -o, --output <OUTPUT>",
                        "          [default: ./vfs_index.json]",
                        "  -b, --block-type <BLOCK_TYPE>",
                        $"          {blockTypeValues}",
                        "          May be repeated to index multiple block types.",
                        "      --file-regex <REGEX>",
                        "          Only index files whose VFS filename matches the regex. May be repeated.",
                        "      --verify-md5",
                        "          Verify raw chunk ContentMd5 and decrypted file DataMd5 values.",
                        "      --jsonl",
                        "          Write compact newline-delimited records for streaming consumers.",
                        "  -h, --help",
                        "          Print help");
                    break;
                case "audio":
                    PrintHelpLines(
                        $"Usage: {executable} audio [OPTIONS] --streaming-assets <STREAMING_ASSETS>",
                        "",
                        "Options:",
                        "  -s, --streaming-assets <STREAMING_ASSETS>",
                        "          ",
                        "      --fallback-assets <FALLBACK_ASSETS>",
                        "          ",
                        "  -o, --output <OUTPUT>",
                        "          [default: ./output]",
                        "      --shared-output <OUTPUT>",
                        "          Route shared Audio/InitAudio/AuditAudio blocks separately from language voice.",
                        "  -l, --language <LANGUAGE>",
                        "          [default: all] [possible values: all, chinese, english, japanese, korean]",
                        "  -f, --format <FORMAT>",
                        "          [default: flac] [possible values: flac, wav, wem]",
                        "  -b, --block <BLOCK>",
                        "          [default: all] [possible values: all, voice, audio, initial-audio, audit-audio, hotfix-audio]",
                        "  -j, --jobs <JOBS>",
                        "          Maximum concurrent audio conversions. [default: min(8, logical processors)]",
                        "  -h, --help",
                        "          Print help");
                    break;
                case "list":
                    PrintHelpLines(
                        $"Usage: {executable} list",
                        "",
                        "Options:",
                        "  -h, --help  Print help");
                    break;
                case "extend-data":
                case "extenddata":
                    PrintHelpLines(
                        $"Usage: {executable} extend-data --input <COMPRESS_DATA_BIN> [OPTIONS]",
                        "",
                        "Decode ExtendData/Main/CompressData.bin into numbered JSON records.",
                        "The operation is opt-in and does not affect normal VFS dumps.",
                        "",
                        "Options:",
                        "  -i, --input <COMPRESS_DATA_BIN>",
                        "          Required path to the compressed ExtendData file.",
                        "  -o, --output <OUTPUT>",
                        "          [default: ./extend_data]",
                        "  -h, --help",
                        "          Print help");
                    break;
                case "audio-audit":
                    PrintHelpLines(
                        $"Usage: {executable} audio-audit [OPTIONS] --streaming-assets <PRIMARY_ASSETS>",
                        "",
                        "Certify AKPK/Wwise outer package, sector, bank, and media-table framing.",
                        "English, Japanese, and Korean voice blocks are excluded.",
                        "",
                        "Options:",
                        "  -s, --streaming-assets <PRIMARY_ASSETS>",
                        "      --fallback-assets <FALLBACK_ASSETS>",
                        "  -o, --output <OUTPUT>",
                        "          [default: ./akpk_audit.json]",
                        "  -b, --block <BLOCK>",
                        "          Repeatable: audio, initial-audio, audit-audio, hotfix-audio, audio-chinese",
                        "  -h, --help",
                        "          Print help");
                    break;
                case "vfs-audit":
                    PrintHelpLines(
                        $"Usage: {executable} vfs-audit [OPTIONS] --streaming-assets <PRIMARY_ASSETS>",
                        "",
                        "Certify every physical VFS catalog file and logical file boundary without writing payloads.",
                        "English, Japanese, and Korean voice blocks are explicitly excluded; AuditAudio is in scope.",
                        "",
                        "Options:",
                        "  -s, --streaming-assets <PRIMARY_ASSETS>",
                        "          Primary VFS root (Persistent may be supplied here).",
                        "      --fallback-assets <FALLBACK_ASSETS>",
                        "          Optional fallback VFS root (StreamingAssets may be supplied here).",
                        "      --summary-json <OUTPUT>",
                        "          [default: ./vfs_audit_summary.json]",
                        "      --ledger-jsonl-gz <OUTPUT>",
                        "          [default: ./vfs_audit_ledger.jsonl.gz]",
                        "      --report-md <OUTPUT>",
                        "          [default: ./vfs_audit_report.md]",
                        "      --block-hash <8-HEX-DIRECTORY>",
                        "          Repeatable focused-validation filter; omit for the required full catalog audit.",
                        "  -h, --help",
                        "          Print help");
                    break;
                case "vfs-profile":
                    PrintHelpLines(
                        $"Usage: {executable} vfs-profile [OPTIONS] --streaming-assets <PRIMARY_ASSETS>",
                        "",
                        "Stream bounded structural observations for every selected logical VFS file.",
                        "English, Japanese, and Korean voice blocks are excluded by default and counted in the summary.",
                        "The summary is moved last as the successful publication marker.",
                        "",
                        "Options:",
                        "  -s, --streaming-assets <PRIMARY_ASSETS>",
                        "          Primary VFS root.",
                        "      --fallback-assets <FALLBACK_ASSETS>",
                        "          Optional fallback VFS root for missing metadata/chunks.",
                        "  -o, --output <OUTPUT>",
                        "          [default: ./vfs_profile_ledger.jsonl.gz]",
                        "      --summary-json <OUTPUT>",
                        "          [default: alongside ledger as vfs_profile_summary.json]",
                        "  -b, --block-type <BLOCK_TYPE>",
                        $"          {blockTypeValues}",
                        "          May be repeated to profile multiple block types.",
                        "      --file-regex <REGEX>",
                        "          Only profile files whose VFS filename matches the regex. May be repeated.",
                        "      --bounded-bytes <N>",
                        "          Prefix/suffix bound (1..4096). [default: 64]",
                        "      --include-deferred-voice",
                        "          Include English, Japanese, and Korean voice blocks (normally excluded).",
                        "  -h, --help",
                        "          Print help");
                    break;
                case "vfs-inner-audit":
                    PrintHelpLines(
                        $"Usage: {executable} vfs-inner-audit [OPTIONS] --streaming-assets <PRIMARY_ASSETS>",
                        "",
                        "Strictly audit nested Endfield VFS containers in InitBundle and Bundle logical files.",
                        "The command verifies inner header/block framing, exact decompression, directory bounds, names, overlap, duplicates, and exact node reads.",
                        "It does not parse Unity object payload semantics.",
                        "",
                        "Options:",
                        "  -s, --streaming-assets <PRIMARY_ASSETS>",
                        "          Primary VFS root (Persistent may be supplied here).",
                        "      --fallback-assets <FALLBACK_ASSETS>",
                        "          Optional fallback VFS root (StreamingAssets may be supplied here).",
                        "  -o, --output <OUTPUT>",
                        "          [default: ./vfs_inner_audit_ledger.jsonl.gz]",
                        "      --summary-json <OUTPUT>",
                        "          [default: <OUTPUT>.summary.json]",
                        "  -b, --block-type <BLOCK_TYPE>",
                        "          Repeatable; only initial-bundle and bundle are accepted.",
                        "      --file-regex <REGEX>",
                        "          Repeatable logical-path filter used for deterministic focused audits.",
                        "  -h, --help",
                        "          Print help");
                    break;
            }
        }

        private static void PrintHelpLines(params string[] lines)
        {
            foreach (var line in lines)
            {
                Console.WriteLine(line);
            }
        }

        private sealed class VfsOptions
        {
            public string StreamingAssets { get; set; }
            public string FallbackAssets { get; set; }
            public string Output { get; set; }
            public string OutputDisplay { get; set; }
            public bool VerifyMd5 { get; set; }
            public bool UseJsonLines { get; set; }
            public int BoundedByteLimit { get; set; } = EndfieldVfsCorpusClassifier.DefaultBoundedByteLimit;
            public string SummaryOutput { get; set; }
            public bool ExcludeDeferredVoice { get; set; } = true;
            public List<EndfieldVfsBlockType> BlockTypes { get; } = new();
            public List<Regex> FileRegexes { get; } = new();
            public bool UseAllBlockTypes { get; private set; } = true;
            public string BlockFilterName { get; private set; } = "All";

            public IEnumerable<EndfieldVfsBlockType> SelectedBlockTypes() =>
                UseAllBlockTypes || BlockTypes.Count == 0 ? EndfieldVfsBlockTypes.AllDumpable : BlockTypes;

            public bool ShouldIncludeFile(string fileName)
            {
                if (FileRegexes.Count == 0)
                {
                    return true;
                }
                var normalized = NormalizePath(fileName);
                return FileRegexes.Any(regex => regex.IsMatch(normalized));
            }

            public void AddFileRegex(string pattern)
            {
                FileRegexes.Add(new Regex(pattern, RegexOptions.IgnoreCase));
            }

            public void SelectAllBlockTypes()
            {
                UseAllBlockTypes = true;
                BlockTypes.Clear();
                BlockFilterName = "All";
            }

            public void AddBlockType(EndfieldVfsBlockType blockType)
            {
                if (UseAllBlockTypes)
                {
                    UseAllBlockTypes = false;
                    BlockTypes.Clear();
                }

                if (!BlockTypes.Contains(blockType))
                {
                    BlockTypes.Add(blockType);
                }
                BlockFilterName = string.Join(", ", BlockTypes.Select(item => item.GetName()));
            }
        }

        private sealed class ExtendDataOptions
        {
            public string Input { get; set; }
            public string Output { get; set; }
        }

        private sealed class VfsChunkSelection
        {
            public VfsChunkSelection(EndfieldVfsChunkInfo chunk, List<EndfieldVfsFileInfo> files)
            {
                Chunk = chunk;
                Files = files;
            }

            public EndfieldVfsChunkInfo Chunk { get; }
            public List<EndfieldVfsFileInfo> Files { get; }
        }

        private sealed class VfsBlockSelection
        {
            public VfsBlockSelection(EndfieldVfsBlockType blockType, EndfieldVfsBlockMainInfo info)
            {
                BlockType = blockType;
                Info = info;
            }

            public EndfieldVfsBlockType BlockType { get; }
            public EndfieldVfsBlockMainInfo Info { get; }
        }

        private sealed class VfsFileSelection
        {
            public VfsFileSelection(EndfieldVfsBlockType blockType, EndfieldVfsChunkInfo chunk, EndfieldVfsFileInfo file)
            {
                BlockType = blockType;
                Chunk = chunk;
                File = file;
            }

            public EndfieldVfsBlockType BlockType { get; }
            public EndfieldVfsChunkInfo Chunk { get; }
            public EndfieldVfsFileInfo File { get; }
        }

        private sealed class HelpRequestedException : Exception
        {
        }
    }
}
