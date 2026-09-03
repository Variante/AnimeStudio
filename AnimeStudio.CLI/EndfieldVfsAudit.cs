using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AnimeStudio.Endfield;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    internal static class EndfieldVfsAudit
    {
        private const int MaxDiagnostics = 32;
        private static readonly HashSet<byte> ConditionalMissingAudioTypes = new()
        {
            (byte)EndfieldVfsBlockType.InitialAudio,
            (byte)EndfieldVfsBlockType.AuditAudio,
            (byte)EndfieldVfsBlockType.Audio,
            (byte)EndfieldVfsBlockType.HotfixAudio,
            (byte)EndfieldVfsBlockType.AudioChinese,
            (byte)EndfieldVfsBlockType.AudioEnglish,
            (byte)EndfieldVfsBlockType.AudioJapanese,
            (byte)EndfieldVfsBlockType.AudioKorean,
        };

        public static void Run(string[] args)
        {
            var options = ParseOptions(args);
            var temporary = new[]
            {
                CreateTemporaryPath(options.SummaryJson),
                CreateTemporaryPath(options.LedgerJsonlGz),
                CreateTemporaryPath(options.ReportMd),
            };
            try
            {
                var result = Audit(options, temporary[1]);
                WriteReport(temporary[2], result);
                result.Summary["publication"] = new JObject
                {
                    ["ledgerSha256"] = Sha256File(temporary[1]),
                    ["reportSha256"] = Sha256File(temporary[2]),
                };
                WriteSummary(temporary[0], result.Summary);
                Replace(temporary[1], options.LedgerJsonlGz);
                Replace(temporary[2], options.ReportMd);
                Replace(temporary[0], options.SummaryJson);
                Console.WriteLine($"  Done: audited {result.FileCount} files across {result.ChunkCount} chunks and {result.BlockCount} catalog entries");
                if (result.FailureCount > 0)
                {
                    foreach (var diagnostic in result.Diagnostics)
                    {
                        Console.Error.WriteLine($"Error: vfs-audit: {diagnostic}");
                    }
                    var suffix = result.FailureCount > result.Diagnostics.Count
                        ? $"; {result.FailureCount - result.Diagnostics.Count} additional failures omitted"
                        : string.Empty;
                    throw new EndfieldVfsException($"vfs-audit failed for {result.FailureCount} boundary checks{suffix}");
                }
            }
            catch
            {
                foreach (var path in temporary)
                {
                    TryDelete(path);
                }
                throw;
            }
        }

        private static AuditResult Audit(AuditOptions options, string ledgerPath)
        {
            using var ledgerFile = new FileStream(ledgerPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var ledgerGzip = new GZipStream(ledgerFile, CompressionLevel.SmallestSize);
            using var ledgerWriter = new StreamWriter(ledgerGzip, new UTF8Encoding(false));
            var loader = new EndfieldVfsLoader(options.PrimaryAssets, options.FallbackAssets);
            var entries = loader.DiscoverCatalog().ToList();
            var byHash = new Dictionary<string, EndfieldVfsCatalogEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                byHash[entry.HashDirectory] = entry;
            }
            foreach (var blockType in EndfieldVfsBlockTypes.AllDumpable)
            {
                var hash = loader.BlockDirectoryName(blockType);
                if (!byHash.ContainsKey(hash))
                {
                    byHash.Add(hash, new EndfieldVfsCatalogEntry
                    {
                        HashDirectory = hash,
                        State = EndfieldVfsCatalogState.MissingMetadata,
                    });
                }
            }
            entries = byHash.Values.OrderBy(entry => entry.HashDirectory, StringComparer.OrdinalIgnoreCase).ToList();
            if (options.BlockHashes.Count > 0)
            {
                entries = entries.Where(entry => options.BlockHashes.Contains(entry.HashDirectory)).ToList();
            }

            var sourceFingerprints = InventoryMetadata(options.PrimaryAssets, "primary")
                .Concat(InventoryMetadata(options.FallbackAssets, "fallback"))
                .OrderBy(item => (string)item["role"], StringComparer.Ordinal)
                .ThenBy(item => (string)item["relativePath"], StringComparer.Ordinal)
                .ToList();
            foreach (var fingerprint in sourceFingerprints)
            {
                try
                {
                    var physicalInfo = loader.LoadBlockInfoFromMetadataPath((string)fingerprint["path"]);
                    var decryptedMetadata = EndfieldVfsLoader.ReadDecryptedMetadata((string)fingerprint["path"]);
                    fingerprint["parseStatus"] = "verified";
                    fingerprint["decryptedSha256"] = Convert.ToHexString(SHA256.HashData(decryptedMetadata));
                    fingerprint["blockTypeValue"] = physicalInfo.BlockTypeValue;
                    fingerprint["blockName"] = EndfieldVfsBlockTypes.GetName(physicalInfo.BlockTypeValue);
                    fingerprint["groupConfigName"] = physicalInfo.GroupConfigName;
                    fingerprint["metadataVersion"] = physicalInfo.Version;
                    fingerprint["metadataCodeVersion"] = physicalInfo.CodeVersion;
                    fingerprint["metadataTrailerHex"] = Convert.ToHexString(physicalInfo.MetadataTrailer);
                    fingerprint["metadataCrc32Declared"] = $"{physicalInfo.MetadataCrc32Declared:X8}";
                    fingerprint["metadataCrc32Recomputed"] = $"{physicalInfo.MetadataCrc32Recomputed:X8}";
                }
                catch (Exception exception)
                {
                    fingerprint["parseStatus"] = "failed";
                    fingerprint["parseError"] = exception.Message.Length <= 320
                        ? exception.Message : exception.Message[..320];
                }
            }
            var buildFingerprints = BuildFingerprints(options.PrimaryAssets);
            var inputIdentity = new JObject
            {
                ["primaryAssets"] = NormalizePath(Path.GetFullPath(options.PrimaryAssets)),
                ["fallbackAssets"] = string.IsNullOrEmpty(options.FallbackAssets)
                    ? JValue.CreateNull() : NormalizePath(Path.GetFullPath(options.FallbackAssets)),
                ["metadata"] = new JArray(sourceFingerprints.Select(item => item.DeepClone())),
                ["build"] = buildFingerprints.DeepClone(),
            };
            var inputSetSha256 = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(inputIdentity.ToString(Formatting.None))));
            var blockSummaries = new JArray();
            var chunkSummaries = new JArray();
            var physicalChunkInventory = new JArray(
                InventoryChunks(options.PrimaryAssets, "primary")
                    .Concat(InventoryChunks(options.FallbackAssets, "fallback")));

            var result = new AuditResult
            {
                InputSetSha256 = inputSetSha256,
                BlockSummaries = blockSummaries,
                ChunkSummaries = chunkSummaries,
                PhysicalChunkInventory = physicalChunkInventory,
                LedgerWriter = ledgerWriter,
                Summary = new JObject
                {
                    ["format"] = "animestudio-vfs-boundary-audit",
                    ["schemaVersion"] = 1,
                    ["primaryAssets"] = NormalizePath(options.PrimaryAssets),
                    ["fallbackAssets"] = string.IsNullOrEmpty(options.FallbackAssets) ? JValue.CreateNull() : NormalizePath(options.FallbackAssets),
                    ["conditionalExcludedTypes"] = new JObject
                    {
                        ["audio"] = "any audio block is excluded only when every declared chunk is absent from both primary and fallback roots",
                    },
                    ["inputSetSha256"] = inputSetSha256,
                    ["sourceFingerprints"] = new JArray(sourceFingerprints),
                    ["buildFingerprints"] = buildFingerprints,
                    ["hashAndPathConventions"] = new JObject
                    {
                        ["metadataCrc32"] = "unsigned CRC-32 over decrypted .blc bytes excluding the final little-endian uint32 CRC field",
                        ["md5ByteOrder"] = "metadata UInt128 fields are decoded little-endian; *LittleEndianHex is the 16 stored bytes/conventional digest hex, while *DisplayHex is the numeric UInt128 display",
                        ["chunkFileName"] = "uppercase conventional MD5 bytes (*LittleEndianHex) plus .chk",
                        ["contentMd5"] = "MD5 of the complete raw physical .chk byte stream before per-file decryption",
                        ["fileDataMd5"] = "MD5 of the exact logical file bytes after optional ChaCha20 decryption",
                        ["fileChunkMd5"] = "chunk identity; must equal the containing chunk Md5Name UInt128",
                        ["virtualPath"] = "strict UTF-8 metadata text; forward slash is the ledger display separator; absolute, empty, dot-segment, and traversal paths are rejected",
                        ["sourceSelection"] = "caller-supplied primary metadata; caller-supplied fallback per missing physical chunk (the installed audit passes Persistent first and StreamingAssets second)",
                    },
                    ["physicalMetadata"] = new JArray(sourceFingerprints.Select(item => item.DeepClone())),
                    ["physicalChunkInventory"] = physicalChunkInventory,
                    ["blocks"] = blockSummaries,
                    ["chunks"] = chunkSummaries,
                },
            };
            foreach (var fingerprint in sourceFingerprints.Where(item => !(bool)item["expectedLayout"]))
            {
                Fail(result, Diagnostic("unexpected_metadata_layout", null, null, null,
                    (string)fingerprint["role"], (string)fingerprint["expectedRelativePath"],
                    (string)fingerprint["relativePath"], "physical .blc is not at VFS/<hash>/<hash>.blc",
                    chunkPath: (string)fingerprint["path"]));
            }
            foreach (var fingerprint in sourceFingerprints.Where(item =>
                string.Equals((string)item["parseStatus"], "failed", StringComparison.Ordinal)))
            {
                Fail(result, Diagnostic("physical_metadata_parse_failed", (string)fingerprint["hashDirectory"],
                    null, null, (string)fingerprint["role"], "decryption + supported version + exact footer + CRC",
                    (string)fingerprint["parseError"], "physical .blc did not pass metadata authentication",
                    chunkPath: (string)fingerprint["path"]));
            }
            Emit(result, new JObject
            {
                ["recordType"] = "audit_header",
                ["schemaVersion"] = 1,
                ["inputSetSha256"] = inputSetSha256,
                ["primaryAssets"] = NormalizePath(Path.GetFullPath(options.PrimaryAssets)),
                ["fallbackAssets"] = string.IsNullOrEmpty(options.FallbackAssets)
                    ? JValue.CreateNull() : NormalizePath(Path.GetFullPath(options.FallbackAssets)),
                ["sourceFingerprints"] = new JArray(sourceFingerprints.Select(item => item.DeepClone())),
                ["buildFingerprints"] = buildFingerprints.DeepClone(),
            });
            var logicalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                AuditEntry(loader, entry, result, logicalIds);
            }
            foreach (var physical in result.PhysicalChunkInventory.Cast<JObject>())
            {
                var path = (string)physical["path"];
                physical["selectionStatus"] = result.ReferencedPhysicalChunks.Contains(path)
                    ? "referenced_by_ledger" : "unselected_overlay_or_orphan";
            }
            var terminalReconciled = result.CurrentDeclaredFileCount
                == result.BoundaryVerifiedCount + result.FailedFileCount
                    + result.UnavailableFileCount + result.ExcludedFileCount;
            var availableReconciled = result.AvailableFileCount
                == result.BoundaryVerifiedCount + result.FailedFileCount;
            result.Summary["summary"] = new JObject
            {
                ["blockCount"] = result.BlockCount,
                ["chunkCount"] = result.ChunkCount,
                ["metadataDeclaredChunkCount"] = result.CurrentDeclaredChunkCount,
                ["ledgerFileCount"] = result.FileCount,
                ["metadataDeclaredFileCount"] = result.CurrentDeclaredFileCount,
                ["metadataDeclaredLogicalBytes"] = result.CurrentDeclaredLogicalBytes,
                ["metadataDeclaredChunkBytes"] = result.CurrentDeclaredChunkBytes,
                ["availableFileCount"] = result.AvailableFileCount,
                ["boundaryVerifiedCount"] = result.BoundaryVerifiedCount,
                ["missingFileCount"] = result.MissingFileCount,
                ["unavailableFileCount"] = result.UnavailableFileCount,
                ["excludedFileCount"] = result.ExcludedFileCount,
                ["failedFileCount"] = result.FailedFileCount,
                ["failureCount"] = result.FailureCount,
                ["excludedBlockCount"] = result.ExcludedBlockCount,
                ["missingBlockCount"] = result.MissingBlockCount,
                ["shadowedFallbackFileCount"] = result.ShadowedFallbackCount,
                ["physicalChunkFileCount"] = result.PhysicalChunkInventory.Count,
                ["referencedPhysicalChunkFileCount"] = result.ReferencedPhysicalChunks.Count,
                ["unselectedPhysicalChunkFileCount"] = result.PhysicalChunkInventory.Count
                    - result.ReferencedPhysicalChunks.Count,
                ["terminalCountsReconciled"] = terminalReconciled,
                ["availableCountsReconciled"] = availableReconciled,
                ["allAvailableBoundaryVerified"] = availableReconciled
                    && result.AvailableFileCount == result.BoundaryVerifiedCount,
                ["fullAuditPassed"] = result.FailureCount == 0
                    && terminalReconciled && availableReconciled,
                ["boundaryStatusCounts"] = JObject.FromObject(
                    result.BoundaryStatusCounts.OrderBy(item => item.Key, StringComparer.Ordinal)
                        .ToDictionary(item => item.Key, item => item.Value)),
                ["overlayStateCounts"] = JObject.FromObject(
                    result.OverlayStateCounts.OrderBy(item => item.Key, StringComparer.Ordinal)
                        .ToDictionary(item => item.Key, item => item.Value)),
            };
            result.Summary["diagnostics"] = new JArray(result.StructuredDiagnostics.Select(item => item.DeepClone()));
            result.Summary["unverifiedFiles"] = new JArray(result.UnverifiedFiles.Select(item => item.DeepClone()));
            if (!terminalReconciled || !availableReconciled)
            {
                Fail(result, new JObject
                {
                    ["code"] = "summary_reconciliation_failed",
                    ["expected"] = result.CurrentDeclaredFileCount,
                    ["actual"] = result.BoundaryVerifiedCount + result.FailedFileCount
                        + result.UnavailableFileCount + result.ExcludedFileCount,
                    ["message"] = "file terminal counts do not reconcile with canonical metadata declarations",
                });
                ((JObject)result.Summary["summary"])["failureCount"] = result.FailureCount;
                ((JObject)result.Summary["summary"])["fullAuditPassed"] = false;
                result.Summary["diagnostics"] = new JArray(result.StructuredDiagnostics.Select(item => item.DeepClone()));
            }
            ledgerWriter.Flush();
            return result;
        }

        private static void AuditEntry(
            EndfieldVfsLoader loader,
            EndfieldVfsCatalogEntry entry,
            AuditResult result,
            HashSet<string> logicalIds)
        {
            var info = entry.CanonicalInfo;
            var blockName = info == null
                ? KnownName(loader, entry.HashDirectory)
                : EndfieldVfsBlockTypes.GetName(info.BlockTypeValue);
            var rawType = info?.BlockTypeValue;
            var fallbackFiles = entry.FallbackInfo?.Chunks
                .SelectMany(chunk => chunk.Files)
                .ToDictionary(file => NormalizeVirtualPath(file.FileName), file => file, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, EndfieldVfsFileInfo>(StringComparer.OrdinalIgnoreCase);
            var conditionalMissingAudio = rawType.HasValue
                && ConditionalMissingAudioTypes.Contains(rawType.Value)
                && info != null
                && info.Chunks.Count > 0
                && info.Chunks.All(chunk => IsChunkMissing(loader, entry, chunk));
            var excluded = conditionalMissingAudio;
            var excludedVoice = rawType is (byte)EndfieldVfsBlockType.AudioEnglish
                or (byte)EndfieldVfsBlockType.AudioJapanese
                or (byte)EndfieldVfsBlockType.AudioKorean;
            var excludedStatus = excludedVoice ? "excluded_missing_voice" : "excluded_missing_audio";
            var metadata = new JObject
            {
                ["recordType"] = "block",
                ["inputSetSha256"] = result.InputSetSha256,
                ["status"] = excluded ? excludedStatus : StateName(entry.State),
                ["boundaryStatus"] = excluded ? "excluded" : "not_verified",
                ["overlayState"] = StateName(entry.State),
                ["blockName"] = blockName,
                ["blockTypeValue"] = rawType.HasValue ? rawType.Value : JValue.CreateNull(),
                ["hashDirectory"] = entry.HashDirectory,
                ["catalogState"] = StateName(entry.State),
                ["primaryMetadata"] = entry.PrimaryMetadataPath == null ? JValue.CreateNull() : NormalizePath(entry.PrimaryMetadataPath),
                ["fallbackMetadata"] = entry.FallbackMetadataPath == null ? JValue.CreateNull() : NormalizePath(entry.FallbackMetadataPath),
                ["declaredFileCount"] = info?.GroupFileInfoNum ?? 0,
                ["declaredChunkBytes"] = info?.GroupChunksLength ?? 0,
                ["groupConfigName"] = info == null ? JValue.CreateNull() : info.GroupConfigName,
                ["groupConfigHashName"] = info == null ? JValue.CreateNull() : info.GroupConfigHashName,
                ["metadataVersion"] = info == null ? JValue.CreateNull() : info.Version,
                ["metadataCodeVersion"] = info == null ? JValue.CreateNull() : info.CodeVersion,
                ["metadataTrailerHex"] = info == null ? JValue.CreateNull() : Convert.ToHexString(info.MetadataTrailer),
                ["metadataCrc32Declared"] = info == null ? JValue.CreateNull() : $"{info.MetadataCrc32Declared:X8}",
                ["metadataCrc32Recomputed"] = info == null ? JValue.CreateNull() : $"{info.MetadataCrc32Recomputed:X8}",
                ["primaryIdentity"] = MetadataIdentity(entry.PrimaryInfo, entry.PrimaryMetadataPath),
                ["fallbackIdentity"] = MetadataIdentity(entry.FallbackInfo, entry.FallbackMetadataPath),
                ["diagnostic"] = entry.State == EndfieldVfsCatalogState.MissingMetadata
                    ? Diagnostic("missing_metadata", blockName, null, null, null, null, null,
                        entry.PrimaryError ?? entry.FallbackError ?? "metadata unavailable")
                    : JValue.CreateNull(),
            };
            Emit(result, metadata);
            result.BlockCount++;
            if (info != null)
            {
                result.CurrentDeclaredFileCount += info.GroupFileInfoNum;
                result.CurrentDeclaredChunkCount += info.Chunks.Count;
                result.CurrentDeclaredChunkBytes += info.GroupChunksLength;
                result.CurrentDeclaredLogicalBytes += info.Chunks.SelectMany(chunk => chunk.Files).Sum(file => file.Length);
                result.BlockSummaries.Add(new JObject
                {
                    ["blockName"] = blockName,
                    ["blockTypeValue"] = info.BlockTypeValue,
                    ["hashDirectory"] = entry.HashDirectory,
                    ["overlayState"] = StateName(entry.State),
                    ["metadataVersion"] = info.Version,
                    ["declaredFileCount"] = info.GroupFileInfoNum,
                    ["declaredLogicalBytes"] = info.Chunks.SelectMany(chunk => chunk.Files).Sum(file => file.Length),
                    ["declaredChunkBytes"] = info.GroupChunksLength,
                    ["chunkCount"] = info.Chunks.Count,
                });
            }
            if (excluded)
            {
                result.ExcludedBlockCount++;
                if (info == null)
                {
                    result.MissingBlockCount++;
                    Fail(result, Diagnostic("excluded_missing_metadata", blockName, null, null,
                        entry.HashDirectory, "parseable metadata", null,
                        "excluded block metadata is absent or invalid"));
                    return;
                }
                if (entry.State == EndfieldVfsCatalogState.Conflicting
                    || entry.State == EndfieldVfsCatalogState.ShadowedEmpty)
                {
                    Fail(result, Diagnostic("overlay_conflict", blockName, null, null,
                        entry.HashDirectory, "non-conflicting overlay", StateName(entry.State),
                        "excluded block metadata has an unresolved overlay conflict"));
                }
                if (info != null)
                {
                    foreach (var chunk in info.Chunks)
                    {
                        result.ChunkCount++;
                        AuditExcludedChunk(loader, entry, info, chunk, blockName, result, logicalIds,
                            fallbackFiles, excludedStatus);
                    }
                }
                return;
            }
            if (entry.State == EndfieldVfsCatalogState.MissingMetadata)
            {
                result.MissingBlockCount++;
                Fail(result, Diagnostic("missing_metadata", blockName, null, null, entry.HashDirectory,
                    null, null, $"metadata missing or unparsable (primary: {entry.PrimaryError ?? "none"}; fallback: {entry.FallbackError ?? "none"})"));
                return;
            }
            if (entry.State == EndfieldVfsCatalogState.Conflicting || entry.State == EndfieldVfsCatalogState.ShadowedEmpty)
            {
                Fail(result, Diagnostic("overlay_conflict", blockName, null, null, entry.HashDirectory,
                    StateName(entry.State), "non-conflicting overlay", "primary is canonical and fallback is not merged"));
            }
            if (info == null)
            {
                Fail(result, Diagnostic("missing_canonical_metadata", blockName, null, null,
                    entry.HashDirectory, null, null, "catalog has no canonical metadata"));
                return;
            }
            var blockIdentityError = BlockIdentityError(info, entry.HashDirectory);
            if (blockIdentityError != null)
            {
                Fail(result, Diagnostic("group_directory_hash_mismatch", blockName, null, null,
                    entry.HashDirectory, EndfieldVfsHash.VfsBlockHash(info.GroupConfigName, EndfieldVfsKeys.UnityHashSecret),
                    entry.HashDirectory, blockIdentityError));
            }

            foreach (var chunk in info.Chunks)
            {
                result.ChunkCount++;
                AuditChunk(loader, entry, info, chunk, blockName, result, logicalIds, fallbackFiles);
            }

            if (entry.State == EndfieldVfsCatalogState.Replaced && entry.FallbackInfo != null)
            {
                var primaryNames = new HashSet<string>(
                    info.Chunks.SelectMany(chunk => chunk.Files).Select(file => file.FileName),
                    StringComparer.Ordinal);
                var fallbackEntry = new EndfieldVfsCatalogEntry
                {
                    HashDirectory = entry.HashDirectory,
                    FallbackMetadataPath = entry.FallbackMetadataPath,
                    FallbackInfo = entry.FallbackInfo,
                    CanonicalInfo = entry.FallbackInfo,
                    CanonicalIsPrimary = false,
                    State = EndfieldVfsCatalogState.FallbackOnly,
                };
                foreach (var chunk in entry.FallbackInfo.Chunks)
                {
                    var staleFiles = chunk.Files.Where(file => !primaryNames.Contains(file.FileName)).ToList();
                    if (staleFiles.Count == 0) continue;
                    result.ShadowedFallbackCount += staleFiles.Count;
                    result.ChunkCount++;
                    AuditChunk(loader, fallbackEntry, entry.FallbackInfo, chunk, blockName, result, logicalIds,
                        fallbackFiles, shadowedFallback: true, selectedFiles: staleFiles);
                }
            }
        }

        private static void AuditChunk(
            EndfieldVfsLoader loader,
            EndfieldVfsCatalogEntry entry,
            EndfieldVfsBlockMainInfo info,
            EndfieldVfsChunkInfo chunk,
            string blockName,
            AuditResult result,
            HashSet<string> logicalIds,
            IReadOnlyDictionary<string, EndfieldVfsFileInfo> fallbackFiles,
            bool shadowedFallback = false,
            IReadOnlyList<EndfieldVfsFileInfo> selectedFiles = null)
        {
            var chunkPath = (string)null;
            byte[] rawDigest = null;
            var chunkFailure = (string)null;
            var fileDigests = new Dictionary<EndfieldVfsFileInfo, FileDigest>();
            var actualChunkLength = 0L;
            try
            {
                chunkPath = loader.ResolveChunkPath(entry, chunk);
                actualChunkLength = new FileInfo(chunkPath).Length;
                if (ChunkOverlayState(loader, entry, chunk) == "shadowed_empty")
                {
                    throw new EndfieldVfsException("zero-length primary chunk shadows a non-empty fallback chunk");
                }
                if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(chunkPath)), entry.HashDirectory, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Path.GetFileName(chunkPath), chunk.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new EndfieldVfsException($"physical chunk name/path mismatch: {NormalizePath(chunkPath)} expected {entry.HashDirectory}/{chunk.FileName}");
                }
                rawDigest = ReadChunkOnce(chunkPath, chunk, result, out fileDigests);
                var contentMd5Matches = DigestMatches(chunk.ContentMd5, rawDigest);
                var blockIdentityError = BlockIdentityError(info, entry.HashDirectory)
                    ?? CatalogConflictError(entry);
                if (!contentMd5Matches)
                {
                    chunkFailure = $"chunk ContentMd5 mismatch for {chunk.FileName}";
                    Fail(result, Diagnostic("chunk_content_md5_mismatch", blockName, null, chunk.FileName,
                        ChunkSource(loader, chunkPath), ExpectedDigestHex(chunk.ContentMd5), Convert.ToHexString(rawDigest), chunkFailure,
                        chunkPath: chunkPath));
                }
                EmitChunkRecord(loader, entry, info, chunk, chunkPath, rawDigest, actualChunkLength, result,
                    shadowedFallback, contentMd5Matches && blockIdentityError == null ? "verified" : "failed",
                    blockIdentityError != null ? "block_identity_mismatch" :
                        contentMd5Matches ? "boundary_verified" : "chunk_content_md5_mismatch",
                    blockIdentityError ?? chunkFailure);
                foreach (var file in selectedFiles ?? chunk.Files)
                {
                    var logicalId = $"{info.BlockTypeValue}:{NormalizeVirtualPath(file.FileName)}";
                    var duplicateLogical = !logicalIds.Add(logicalId);
                    result.FileCount++;
                    var fileStatus = shadowedFallback ? "shadowed_fallback" : "verified";
                    var boundaryStatus = shadowedFallback ? "shadowed" : "boundary_verified";
                    var fileFailure = (string)null;
                    object fileExpected = null;
                    object fileActual = null;
                    if (shadowedFallback)
                    {
                        fileFailure = "fallback metadata path is shadowed by newer primary metadata";
                        fileExpected = "current primary declaration";
                        fileActual = "stale fallback-only declaration";
                    }
                    else if (!contentMd5Matches)
                    {
                        fileStatus = "failed";
                        boundaryStatus = "chunk_content_md5_mismatch";
                        fileFailure = chunkFailure;
                        fileExpected = ExpectedDigestHex(chunk.ContentMd5);
                        fileActual = Convert.ToHexString(rawDigest);
                    }
                    else if (blockIdentityError != null)
                    {
                        fileStatus = "failed";
                        boundaryStatus = "block_identity_mismatch";
                        fileFailure = blockIdentityError;
                        fileExpected = "consistent block/group/hash identity";
                        fileActual = entry.HashDirectory;
                    }
                    else if (duplicateLogical)
                    {
                        fileStatus = "failed";
                        boundaryStatus = "duplicate_logical_path";
                        fileFailure = $"duplicate logical path for {file.FileName}";
                        fileExpected = "unique normalized virtual path within numeric block ID";
                        fileActual = NormalizeVirtualPath(file.FileName);
                    }
                    else if (!IsSafeLogicalPath(file.FileName))
                    {
                        fileStatus = "failed";
                        boundaryStatus = "unsafe_logical_path";
                        fileFailure = $"unsafe logical path for {file.FileName}";
                        fileExpected = "relative path with non-empty non-dot segments";
                        fileActual = file.FileName;
                    }
                    else if (file.FileChunkMd5 != chunk.Md5Name)
                    {
                        fileStatus = "failed";
                        boundaryStatus = "file_chunk_identity_mismatch";
                        fileFailure = $"FileChunkMd5 does not equal chunk Md5Name for {file.FileName}";
                        fileExpected = EndfieldVfsFormatting.UInt128LittleEndianHex(chunk.Md5Name);
                        fileActual = EndfieldVfsFormatting.UInt128LittleEndianHex(file.FileChunkMd5);
                    }
                    else if (unchecked((long)EndfieldVfsHash.Hash64(
                        Encoding.UTF8.GetBytes(file.FileName), EndfieldVfsKeys.UnityHashSecret, 0)) != file.FileNameHash)
                    {
                        fileStatus = "failed";
                        boundaryStatus = "file_name_hash_mismatch";
                        fileFailure = $"FileNameHash mismatch for {file.FileName}";
                        fileExpected = EndfieldVfsHash.Hash64(
                            Encoding.UTF8.GetBytes(file.FileName), EndfieldVfsKeys.UnityHashSecret, 0).ToString("X16");
                        fileActual = unchecked((ulong)file.FileNameHash).ToString("X16");
                    }
                    fileDigests.TryGetValue(file, out var fileDigest);
                    if (fileFailure == null && (fileDigest == null || !DigestMatches(file.FileDataMd5, fileDigest.Digest)))
                    {
                        fileStatus = "failed";
                        boundaryStatus = "file_data_md5_mismatch";
                        fileFailure = $"FileDataMd5 mismatch for {file.FileName}";
                        fileExpected = ExpectedDigestHex(file.FileDataMd5);
                        fileActual = fileDigest == null ? null : Convert.ToHexString(fileDigest.Digest);
                    }
                    if (fileFailure != null)
                    {
                        if (!shadowedFallback && contentMd5Matches && blockIdentityError == null)
                        {
                            Fail(result, Diagnostic(boundaryStatus, blockName, file.FileName, chunk.FileName,
                                ChunkSource(loader, chunkPath), fileExpected, fileActual, fileFailure,
                                file.Offset, file.Length, chunkPath));
                        }
                        if (!shadowedFallback)
                        {
                            result.FailedFileCount++;
                        }
                    }
                    else
                    {
                        result.BoundaryVerifiedCount++;
                    }
                    if (!shadowedFallback) result.AvailableFileCount++;
                    var fileOverlayState = FileOverlayState(entry, file, fallbackFiles, shadowedFallback);
                    CountFileStatus(result, boundaryStatus, fileOverlayState);
                    var fileRecord = new JObject
                    {
                        ["recordType"] = "file",
                        ["inputSetSha256"] = result.InputSetSha256,
                        ["status"] = fileStatus,
                        ["boundaryStatus"] = boundaryStatus,
                        ["overlayState"] = fileOverlayState,
                        ["chunkOverlayState"] = ChunkOverlayState(loader, entry, chunk),
                        ["blockName"] = blockName,
                        ["blockTypeValue"] = info.BlockTypeValue,
                        ["hashDirectory"] = entry.HashDirectory,
                        ["chunkFile"] = chunk.FileName,
                        ["fileName"] = file.FileName,
                        ["virtualPath"] = NormalizeVirtualPath(file.FileName),
                        ["offset"] = file.Offset,
                        ["length"] = file.Length,
                        ["actualBytesRead"] = fileDigest?.ActualBytes ?? 0,
                        ["physicalChunkPath"] = NormalizePath(chunkPath),
                        ["physicalChunkSource"] = ChunkSource(loader, chunkPath),
                        ["physicalChunkRoot"] = ChunkRoot(loader, chunkPath),
                        ["metadataProvenance"] = entry.CanonicalIsPrimary ? "primary" : "fallback",
                        ["encrypted"] = file.UseEncrypt,
                        ["ivSeed"] = file.UseEncrypt ? file.IvSeed : JValue.CreateNull(),
                        ["fileNameHashDeclaredSigned"] = file.FileNameHash,
                        ["fileNameHashDeclaredHex"] = unchecked((ulong)file.FileNameHash).ToString("X16"),
                        ["fileNameHashRecomputedHex"] = EndfieldVfsHash.Hash64(
                            Encoding.UTF8.GetBytes(file.FileName), EndfieldVfsKeys.UnityHashSecret, 0).ToString("X16"),
                        ["fileChunkMd5DisplayHex"] = EndfieldVfsFormatting.UInt128Hex(file.FileChunkMd5),
                        ["fileChunkMd5LittleEndianHex"] = EndfieldVfsFormatting.UInt128LittleEndianHex(file.FileChunkMd5),
                        ["declaredFileDataMd5DisplayHex"] = EndfieldVfsFormatting.UInt128Hex(file.FileDataMd5),
                        ["declaredFileDataMd5LittleEndianHex"] = ExpectedDigestHex(file.FileDataMd5),
                        ["recomputedFileDataMd5"] = fileDigest == null
                            ? JValue.CreateNull() : Convert.ToHexString(fileDigest.Digest),
                        ["diagnostic"] = fileFailure == null ? JValue.CreateNull() : Diagnostic(
                            boundaryStatus, blockName, file.FileName, chunk.FileName,
                            ChunkSource(loader, chunkPath), fileExpected, fileActual, fileFailure,
                            file.Offset, file.Length, chunkPath),
                    };
                    RecordUnverified(result, fileRecord);
                    Emit(result, fileRecord);
                }
            }
            catch (Exception exception)
            {
                chunkFailure = exception.Message;
                var missing = exception is EndfieldVfsChunkNotFoundException;
                var code = missing ? "missing_both" : "chunk_not_verified";
                Fail(result, Diagnostic(code, blockName, null, chunk.FileName,
                    chunkPath == null ? "missing_both" : ChunkSource(loader, chunkPath),
                    chunk.Length, actualChunkLength, chunkFailure, chunkPath: chunkPath));
                EmitChunkRecord(loader, entry, info, chunk, chunkPath, rawDigest, actualChunkLength,
                    result, shadowedFallback, "failed", code, chunkFailure);
                if (missing) result.MissingChunkCount++;
                foreach (var file in selectedFiles ?? chunk.Files)
                {
                    var logicalId = $"{info.BlockTypeValue}:{NormalizeVirtualPath(file.FileName)}";
                    var duplicate = !logicalIds.Add(logicalId);
                    result.FileCount++;
                    if (!shadowedFallback)
                    {
                        if (missing)
                        {
                            result.MissingFileCount++;
                            result.UnavailableFileCount++;
                        }
                        else
                        {
                            result.AvailableFileCount++;
                            result.FailedFileCount++;
                        }
                    }
                    var fileOverlayState = FileOverlayState(entry, file, fallbackFiles, shadowedFallback);
                    CountFileStatus(result, duplicate ? "duplicate_logical_path" : code, fileOverlayState);
                    var fileRecord = new JObject
                    {
                        ["recordType"] = "file",
                        ["inputSetSha256"] = result.InputSetSha256,
                        ["status"] = "failed",
                        ["boundaryStatus"] = duplicate ? "duplicate_logical_path" : code,
                        ["overlayState"] = fileOverlayState,
                        ["chunkOverlayState"] = ChunkOverlayState(loader, entry, chunk),
                        ["blockName"] = blockName,
                        ["blockTypeValue"] = info.BlockTypeValue,
                        ["hashDirectory"] = entry.HashDirectory,
                        ["chunkFile"] = chunk.FileName,
                        ["fileName"] = file.FileName,
                        ["virtualPath"] = NormalizeVirtualPath(file.FileName),
                        ["offset"] = file.Offset,
                        ["length"] = file.Length,
                        ["actualBytesRead"] = 0,
                        ["physicalChunkPath"] = chunkPath == null ? JValue.CreateNull() : NormalizePath(chunkPath),
                        ["physicalChunkSource"] = ChunkSource(loader, chunkPath),
                        ["physicalChunkRoot"] = ChunkRoot(loader, chunkPath),
                        ["metadataProvenance"] = entry.CanonicalIsPrimary ? "primary" : "fallback",
                        ["encrypted"] = file.UseEncrypt,
                        ["ivSeed"] = file.UseEncrypt ? file.IvSeed : JValue.CreateNull(),
                        ["fileNameHashDeclaredSigned"] = file.FileNameHash,
                        ["fileNameHashDeclaredHex"] = unchecked((ulong)file.FileNameHash).ToString("X16"),
                        ["fileNameHashRecomputedHex"] = EndfieldVfsHash.Hash64(
                            Encoding.UTF8.GetBytes(file.FileName), EndfieldVfsKeys.UnityHashSecret, 0).ToString("X16"),
                        ["fileChunkMd5DisplayHex"] = EndfieldVfsFormatting.UInt128Hex(file.FileChunkMd5),
                        ["fileChunkMd5LittleEndianHex"] = EndfieldVfsFormatting.UInt128LittleEndianHex(file.FileChunkMd5),
                        ["declaredFileDataMd5DisplayHex"] = EndfieldVfsFormatting.UInt128Hex(file.FileDataMd5),
                        ["declaredFileDataMd5LittleEndianHex"] = ExpectedDigestHex(file.FileDataMd5),
                        ["recomputedFileDataMd5"] = JValue.CreateNull(),
                        ["diagnostic"] = Diagnostic(duplicate ? "duplicate_logical_path" : code,
                            blockName, file.FileName, chunk.FileName,
                            chunkPath == null ? "missing_both" : ChunkSource(loader, chunkPath),
                            file.Length, 0, duplicate ? $"duplicate logical path for {file.FileName}" : chunkFailure,
                            file.Offset, file.Length, chunkPath),
                    };
                    RecordUnverified(result, fileRecord);
                    Emit(result, fileRecord);
                }
            }
        }

        private static void AuditExcludedChunk(
            EndfieldVfsLoader loader,
            EndfieldVfsCatalogEntry entry,
            EndfieldVfsBlockMainInfo info,
            EndfieldVfsChunkInfo chunk,
            string blockName,
            AuditResult result,
            HashSet<string> logicalIds,
            IReadOnlyDictionary<string, EndfieldVfsFileInfo> fallbackFiles,
            string excludedStatus)
        {
            string chunkPath = null;
            long actualLength = 0;
            string code;
            string message;
            try
            {
                chunkPath = loader.ResolveChunkPath(entry, chunk);
                actualLength = new FileInfo(chunkPath).Length;
                if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(chunkPath)), entry.HashDirectory, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Path.GetFileName(chunkPath), chunk.FileName, StringComparison.OrdinalIgnoreCase)
                    || actualLength != chunk.Length)
                {
                    code = "excluded_boundary_mismatch";
                    message = "excluded chunk path identity or physical length does not match metadata";
                    Fail(result, Diagnostic(code, blockName, null, chunk.FileName,
                        ChunkSource(loader, chunkPath), chunk.Length, actualLength, message,
                        chunkPath: chunkPath));
                }
                else
                {
                    code = "excluded_state_changed";
                    message = "audio chunk appeared after the missing-chunk exclusion decision; rerun the audit";
                    Fail(result, Diagnostic(code, blockName, null, chunk.FileName,
                        ChunkSource(loader, chunkPath), "missing from both roots", NormalizePath(chunkPath),
                        message, chunkPath: chunkPath));
                }
            }
            catch (EndfieldVfsChunkNotFoundException)
            {
                code = excludedStatus;
                message = "audio chunk is absent from both primary and fallback roots and is conditionally ignored";
                result.MissingChunkCount++;
            }
            EmitChunkRecord(loader, entry, info, chunk, chunkPath, null, actualLength, result,
                false, "excluded", code, message);
            foreach (var file in chunk.Files)
            {
                var duplicate = !logicalIds.Add($"{info.BlockTypeValue}:{NormalizeVirtualPath(file.FileName)}");
                result.FileCount++;
                result.ExcludedFileCount++;
                if (code is "excluded_missing_voice" or "excluded_missing_audio") result.MissingFileCount++;
                if (duplicate)
                {
                    Fail(result, Diagnostic("duplicate_logical_path", blockName, file.FileName,
                        chunk.FileName, ChunkSource(loader, chunkPath), null, null,
                        $"duplicate logical path for {file.FileName}", file.Offset, file.Length, chunkPath));
                }
                var fileOverlayState = FileOverlayState(entry, file, fallbackFiles, false);
                CountFileStatus(result, duplicate ? "duplicate_logical_path" : code, fileOverlayState);
                var fileRecord = new JObject
                {
                    ["recordType"] = "file",
                    ["inputSetSha256"] = result.InputSetSha256,
                    ["status"] = excludedStatus,
                    ["boundaryStatus"] = duplicate ? "duplicate_logical_path" : code,
                    ["overlayState"] = fileOverlayState,
                    ["chunkOverlayState"] = ChunkOverlayState(loader, entry, chunk),
                    ["blockName"] = blockName,
                    ["blockTypeValue"] = info.BlockTypeValue,
                    ["hashDirectory"] = entry.HashDirectory,
                    ["chunkFile"] = chunk.FileName,
                    ["fileName"] = file.FileName,
                    ["virtualPath"] = NormalizeVirtualPath(file.FileName),
                    ["offset"] = file.Offset,
                    ["length"] = file.Length,
                    ["actualBytesRead"] = 0,
                    ["physicalChunkPath"] = chunkPath == null ? JValue.CreateNull() : NormalizePath(chunkPath),
                    ["physicalChunkSource"] = ChunkSource(loader, chunkPath),
                    ["physicalChunkRoot"] = ChunkRoot(loader, chunkPath),
                    ["metadataProvenance"] = entry.CanonicalIsPrimary ? "primary" : "fallback",
                    ["encrypted"] = file.UseEncrypt,
                    ["ivSeed"] = file.UseEncrypt ? file.IvSeed : JValue.CreateNull(),
                    ["fileNameHashDeclaredSigned"] = file.FileNameHash,
                    ["fileNameHashDeclaredHex"] = unchecked((ulong)file.FileNameHash).ToString("X16"),
                    ["fileNameHashRecomputedHex"] = EndfieldVfsHash.Hash64(
                        Encoding.UTF8.GetBytes(file.FileName), EndfieldVfsKeys.UnityHashSecret, 0).ToString("X16"),
                    ["fileChunkMd5DisplayHex"] = EndfieldVfsFormatting.UInt128Hex(file.FileChunkMd5),
                    ["fileChunkMd5LittleEndianHex"] = EndfieldVfsFormatting.UInt128LittleEndianHex(file.FileChunkMd5),
                    ["declaredFileDataMd5DisplayHex"] = EndfieldVfsFormatting.UInt128Hex(file.FileDataMd5),
                    ["declaredFileDataMd5LittleEndianHex"] = ExpectedDigestHex(file.FileDataMd5),
                    ["recomputedFileDataMd5"] = JValue.CreateNull(),
                    ["diagnostic"] = Diagnostic(duplicate ? "duplicate_logical_path" : code,
                        blockName, file.FileName, chunk.FileName, ChunkSource(loader, chunkPath),
                        file.Length, 0, duplicate ? $"duplicate logical path for {file.FileName}" : message,
                        file.Offset, file.Length, chunkPath),
                };
                RecordUnverified(result, fileRecord);
                Emit(result, fileRecord);
            }
        }

        private static void EmitChunkRecord(
            EndfieldVfsLoader loader,
            EndfieldVfsCatalogEntry entry,
            EndfieldVfsBlockMainInfo info,
            EndfieldVfsChunkInfo chunk,
            string chunkPath,
            byte[] rawDigest,
            long actualChunkLength,
            AuditResult result,
            bool shadowedFallback,
            string status,
            string boundaryStatus,
            string message)
        {
            var logicalBytes = chunk.Files.Sum(file => file.Length);
            Emit(result, new JObject
            {
                ["recordType"] = "chunk",
                ["inputSetSha256"] = result.InputSetSha256,
                ["status"] = shadowedFallback ? "shadowed_fallback" : status,
                ["boundaryStatus"] = shadowedFallback ? "shadowed" : boundaryStatus,
                ["overlayState"] = shadowedFallback ? "shadowed_fallback" : StateName(entry.State),
                ["chunkOverlayState"] = ChunkOverlayState(loader, entry, chunk),
                ["blockName"] = EndfieldVfsBlockTypes.GetName(info.BlockTypeValue),
                ["blockTypeValue"] = info.BlockTypeValue,
                ["hashDirectory"] = entry.HashDirectory,
                ["chunkFile"] = chunk.FileName,
                ["declaredLength"] = chunk.Length,
                ["actualPhysicalLength"] = actualChunkLength,
                ["actualBytesRead"] = rawDigest == null ? 0 : actualChunkLength,
                ["logicalBytes"] = logicalBytes,
                ["gapBytes"] = Math.Max(0, chunk.Length - logicalBytes),
                ["coveredIntervalCount"] = chunk.Files.Count,
                ["overlapBytes"] = 0,
                ["physicalChunkPath"] = chunkPath == null ? JValue.CreateNull() : NormalizePath(chunkPath),
                ["physicalChunkSource"] = ChunkSource(loader, chunkPath),
                ["physicalChunkRoot"] = ChunkRoot(loader, chunkPath),
                ["metadataProvenance"] = entry.CanonicalIsPrimary ? "primary" : "fallback",
                ["chunkMd5NameDisplayHex"] = EndfieldVfsFormatting.UInt128Hex(chunk.Md5Name),
                ["chunkMd5NameLittleEndianHex"] = EndfieldVfsFormatting.UInt128LittleEndianHex(chunk.Md5Name),
                ["declaredContentMd5DisplayHex"] = EndfieldVfsFormatting.UInt128Hex(chunk.ContentMd5),
                ["declaredContentMd5LittleEndianHex"] = ExpectedDigestHex(chunk.ContentMd5),
                ["recomputedContentMd5"] = rawDigest == null ? JValue.CreateNull() : Convert.ToHexString(rawDigest),
                ["diagnostic"] = message == null ? JValue.CreateNull() : Diagnostic(
                    boundaryStatus, EndfieldVfsBlockTypes.GetName(info.BlockTypeValue), null,
                    chunk.FileName, ChunkSource(loader, chunkPath), chunk.Length, actualChunkLength,
                    message, chunkPath: chunkPath),
            });
            result.ChunkSummaries.Add(new JObject
            {
                ["blockName"] = EndfieldVfsBlockTypes.GetName(info.BlockTypeValue),
                ["blockTypeValue"] = info.BlockTypeValue,
                ["hashDirectory"] = entry.HashDirectory,
                ["chunkFile"] = chunk.FileName,
                ["physicalChunkSource"] = ChunkSource(loader, chunkPath),
                ["physicalChunkPath"] = chunkPath == null ? JValue.CreateNull() : NormalizePath(chunkPath),
                ["physicalChunkRoot"] = ChunkRoot(loader, chunkPath),
                ["declaredLength"] = chunk.Length,
                ["actualPhysicalLength"] = actualChunkLength,
                ["logicalBytes"] = logicalBytes,
                ["gapBytes"] = Math.Max(0, chunk.Length - logicalBytes),
                ["intervalCount"] = chunk.Files.Count,
                ["overlapBytes"] = 0,
                ["boundaryStatus"] = shadowedFallback ? "shadowed" : boundaryStatus,
                ["overlayState"] = shadowedFallback ? "shadowed_fallback" : StateName(entry.State),
                ["chunkOverlayState"] = ChunkOverlayState(loader, entry, chunk),
            });
            if (!string.IsNullOrEmpty(chunkPath))
            {
                result.ReferencedPhysicalChunks.Add(NormalizePath(Path.GetFullPath(chunkPath)));
            }
        }

        private static byte[] ReadChunkOnce(
            string path,
            EndfieldVfsChunkInfo chunk,
            AuditResult result,
            out Dictionary<EndfieldVfsFileInfo, FileDigest> fileDigests)
        {
            fileDigests = new Dictionary<EndfieldVfsFileInfo, FileDigest>();
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            if (input.Length != chunk.Length)
            {
                throw new EndfieldVfsException($"chunk length mismatch for {chunk.FileName}: metadata {chunk.Length}, actual {input.Length}");
            }
            using var rawMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var buffer = new byte[64 * 1024];
            long position = 0;
            foreach (var file in chunk.Files.OrderBy(item => item.Offset))
            {
                ReadAndHash(input, rawMd5, buffer, file.Offset - position, null, ref position);
                using var fileMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                EndfieldChaCha20 cipher = null;
                if (file.UseEncrypt)
                {
                    var nonce = new byte[12];
                    BinaryPrimitives.WriteInt32LittleEndian(nonce.AsSpan(0, 4), EndfieldVfsLoader.VfsProtoVersion);
                    BinaryPrimitives.WriteInt64LittleEndian(nonce.AsSpan(4, 8), file.IvSeed);
                    cipher = new EndfieldChaCha20(EndfieldVfsKeys.ChaChaKey, nonce, 1);
                }
                ReadAndHash(input, rawMd5, buffer, file.Length, cipher, ref position, fileMd5);
                fileDigests[file] = new FileDigest(fileMd5.GetHashAndReset(), file.Length);
            }
            ReadAndHash(input, rawMd5, buffer, chunk.Length - position, null, ref position);
            if (position != chunk.Length)
            {
                throw new EndfieldVfsException($"short VFS range read: expected {chunk.Length} bytes, received {position}");
            }
            return rawMd5.GetHashAndReset();
        }

        private static void ReadAndHash(
            Stream input,
            IncrementalHash rawMd5,
            byte[] buffer,
            long length,
            EndfieldChaCha20 cipher,
            ref long position,
            IncrementalHash fileMd5 = null)
        {
            if (length < 0)
            {
                throw new EndfieldVfsException($"invalid sequential range at offset {position}: {length}");
            }
            var remaining = length;
            while (remaining > 0)
            {
                var wanted = (int)Math.Min(buffer.Length, remaining);
                var read = input.Read(buffer, 0, wanted);
                if (read <= 0)
                {
                    throw new EndfieldVfsException($"short VFS range read: expected {length} bytes, received {length - remaining}");
                }
                rawMd5.AppendData(buffer, 0, read);
                if (cipher != null)
                {
                    cipher.ApplyKeystream(buffer.AsSpan(0, read));
                }
                fileMd5?.AppendData(buffer, 0, read);
                remaining -= read;
                position += read;
            }
        }

        private static bool IsSafeLogicalPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Contains('\0') || Path.IsPathRooted(path))
            {
                return false;
            }
            var normalized = path.Replace('\\', '/');
            return normalized.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
        }

        private static string NormalizeVirtualPath(string path) => path?.Replace('\\', '/');

        private static string FileOverlayState(
            EndfieldVfsCatalogEntry entry,
            EndfieldVfsFileInfo file,
            IReadOnlyDictionary<string, EndfieldVfsFileInfo> fallbackFiles,
            bool shadowedFallback)
        {
            if (shadowedFallback) return "shadowed_fallback";
            if (!entry.CanonicalIsPrimary) return "fallback_only";
            if (!fallbackFiles.TryGetValue(NormalizeVirtualPath(file.FileName), out var fallback))
            {
                return "primary_only";
            }
            if (FileMetadataEquivalent(file, fallback)) return "identical";
            return entry.State == EndfieldVfsCatalogState.Replaced
                ? "replaced" : "conflicting_metadata";
        }

        private static bool FileMetadataEquivalent(EndfieldVfsFileInfo left, EndfieldVfsFileInfo right) =>
            string.Equals(NormalizeVirtualPath(left.FileName), NormalizeVirtualPath(right.FileName), StringComparison.OrdinalIgnoreCase)
            && left.FileNameHash == right.FileNameHash
            && left.FileChunkMd5 == right.FileChunkMd5
            && left.FileDataMd5 == right.FileDataMd5
            && left.Offset == right.Offset
            && left.Length == right.Length
            && left.BlockTypeValue == right.BlockTypeValue
            && left.UseEncrypt == right.UseEncrypt
            && left.IvSeed == right.IvSeed
            && left.FileTag == right.FileTag;

        private static JObject MetadataIdentity(EndfieldVfsBlockMainInfo info, string path)
        {
            if (info == null || string.IsNullOrEmpty(path)) return null;
            return new JObject
            {
                ["path"] = NormalizePath(path),
                ["sha256"] = Sha256File(path),
                ["decryptedSha256"] = Convert.ToHexString(
                    SHA256.HashData(EndfieldVfsLoader.ReadDecryptedMetadata(path))),
                ["version"] = info.Version,
                ["codeVersion"] = info.CodeVersion,
                ["blockTypeValue"] = info.BlockTypeValue,
                ["groupConfigName"] = info.GroupConfigName,
                ["groupConfigHashName"] = info.GroupConfigHashName,
                ["declaredFileCount"] = info.GroupFileInfoNum,
                ["declaredChunkBytes"] = info.GroupChunksLength,
                ["crc32Declared"] = $"{info.MetadataCrc32Declared:X8}",
                ["crc32Recomputed"] = $"{info.MetadataCrc32Recomputed:X8}",
                ["trailerHex"] = Convert.ToHexString(info.MetadataTrailer),
            };
        }

        private static IEnumerable<JObject> InventoryMetadata(string assetsRoot, string role)
        {
            if (string.IsNullOrWhiteSpace(assetsRoot)) yield break;
            var vfsRoot = Path.Combine(Path.GetFullPath(assetsRoot), EndfieldVfsLoader.VfsDirectoryName);
            if (!Directory.Exists(vfsRoot)) yield break;
            foreach (var path in Directory.EnumerateFiles(vfsRoot, "*.blc", SearchOption.AllDirectories)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                var relative = NormalizePath(Path.GetRelativePath(vfsRoot, path));
                var directory = Path.GetFileName(Path.GetDirectoryName(path));
                var expectedRelative = $"{directory}/{directory}.blc";
                yield return new JObject
                {
                    ["role"] = role,
                    ["path"] = NormalizePath(Path.GetFullPath(path)),
                    ["relativePath"] = relative,
                    ["hashDirectory"] = directory,
                    ["length"] = new FileInfo(path).Length,
                    ["sha256"] = Sha256File(path),
                    ["expectedRelativePath"] = expectedRelative,
                    ["expectedLayout"] = string.Equals(relative, expectedRelative, StringComparison.OrdinalIgnoreCase),
                };
            }
        }

        private static IEnumerable<JObject> InventoryChunks(string assetsRoot, string role)
        {
            if (string.IsNullOrWhiteSpace(assetsRoot)) yield break;
            var vfsRoot = Path.Combine(Path.GetFullPath(assetsRoot), EndfieldVfsLoader.VfsDirectoryName);
            if (!Directory.Exists(vfsRoot)) yield break;
            foreach (var path in Directory.EnumerateFiles(vfsRoot, "*.chk", SearchOption.AllDirectories)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                yield return new JObject
                {
                    ["role"] = role,
                    ["path"] = NormalizePath(Path.GetFullPath(path)),
                    ["relativePath"] = NormalizePath(Path.GetRelativePath(vfsRoot, path)),
                    ["hashDirectory"] = Path.GetFileName(Path.GetDirectoryName(path)),
                    ["fileName"] = Path.GetFileName(path),
                    ["length"] = new FileInfo(path).Length,
                };
            }
        }

        private static JArray BuildFingerprints(string primaryAssets)
        {
            var array = new JArray();
            var dataRoot = Directory.GetParent(primaryAssets)?.FullName;
            var installRoot = string.IsNullOrEmpty(dataRoot) ? null : Directory.GetParent(dataRoot)?.FullName;
            var candidates = new[]
            {
                Path.Combine(dataRoot ?? string.Empty, "app.info"),
                Path.Combine(installRoot ?? string.Empty, "GameAssembly.dll"),
                Path.Combine(installRoot ?? string.Empty, "Endfield.exe"),
                Path.Combine(dataRoot ?? string.Empty, "il2cpp_data", "Metadata", "global-metadata.dat"),
                Environment.ProcessPath,
            }.Where(path => !string.IsNullOrWhiteSpace(path));
            foreach (var path in candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path)) continue;
                array.Add(new JObject
                {
                    ["path"] = NormalizePath(path),
                    ["length"] = new FileInfo(path).Length,
                    ["sha256"] = Sha256File(path),
                });
            }
            return array;
        }

        private static string Sha256File(string path)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.SequentialScan);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(input));
        }

        private static bool IsChunkMissing(
            EndfieldVfsLoader loader,
            EndfieldVfsCatalogEntry entry,
            EndfieldVfsChunkInfo chunk)
        {
            try
            {
                loader.ResolveChunkPath(entry, chunk);
                return false;
            }
            catch (EndfieldVfsChunkNotFoundException)
            {
                return true;
            }
        }

        private static string ChunkSource(EndfieldVfsLoader loader, string path)
        {
            if (path == null) return "missing";
            var full = Path.GetFullPath(path);
            if (!string.IsNullOrEmpty(loader.StreamingAssetsPath)
                && full.StartsWith(Path.GetFullPath(loader.StreamingAssetsPath), StringComparison.OrdinalIgnoreCase)) return "primary";
            if (!string.IsNullOrEmpty(loader.FallbackAssetsPath)
                && full.StartsWith(Path.GetFullPath(loader.FallbackAssetsPath), StringComparison.OrdinalIgnoreCase)) return "fallback";
            return "unknown";
        }

        private static JToken ChunkRoot(EndfieldVfsLoader loader, string path)
        {
            var source = ChunkSource(loader, path);
            return source switch
            {
                "primary" => NormalizePath(Path.GetFullPath(loader.StreamingAssetsPath)),
                "fallback" => NormalizePath(Path.GetFullPath(loader.FallbackAssetsPath)),
                _ => JValue.CreateNull(),
            };
        }

        private static string ChunkOverlayState(
            EndfieldVfsLoader loader,
            EndfieldVfsCatalogEntry entry,
            EndfieldVfsChunkInfo chunk)
        {
            var relative = Path.Combine(EndfieldVfsLoader.VfsDirectoryName, entry.HashDirectory, chunk.FileName);
            var primary = Path.Combine(loader.StreamingAssetsPath, relative);
            var fallback = string.IsNullOrEmpty(loader.FallbackAssetsPath)
                ? null : Path.Combine(loader.FallbackAssetsPath, relative);
            var primaryExists = File.Exists(primary);
            var fallbackExists = !string.IsNullOrEmpty(fallback) && File.Exists(fallback);
            if (primaryExists && fallbackExists)
            {
                if (new FileInfo(primary).Length == 0 && new FileInfo(fallback).Length > 0)
                {
                    return "shadowed_empty";
                }
                return entry.State == EndfieldVfsCatalogState.Identical ? "identical" : "replaced";
            }
            if (primaryExists) return "primary_only";
            if (fallbackExists) return "fallback_only";
            return "missing_both";
        }

        private static string KnownName(EndfieldVfsLoader loader, string hash)
        {
            foreach (var blockType in EndfieldVfsBlockTypes.AllDumpable)
            {
                if (string.Equals(loader.BlockDirectoryName(blockType), hash, StringComparison.OrdinalIgnoreCase))
                {
                    return blockType.GetName();
                }
            }
            return "UnknownHash";
        }

        private static bool DigestMatches(UInt128 expected, byte[] actual)
        {
            if (actual == null || actual.Length != 16)
            {
                return false;
            }
            Span<byte> expectedBytes = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(expectedBytes[..8], (ulong)expected);
            BinaryPrimitives.WriteUInt64LittleEndian(expectedBytes[8..], (ulong)(expected >> 64));
            return expectedBytes.SequenceEqual(actual);
        }

        private static string BlockIdentityError(EndfieldVfsBlockMainInfo info, string hashDirectory)
        {
            var expectedDirectory = EndfieldVfsHash.VfsBlockHash(
                info.GroupConfigName, EndfieldVfsKeys.UnityHashSecret);
            if (!string.Equals(expectedDirectory, hashDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return $"group directory hash mismatch for {info.GroupConfigName}: expected {expectedDirectory}, actual {hashDirectory}";
            }
            var directoryValue = uint.Parse(expectedDirectory, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var expectedGroupHash = unchecked((int)BinaryPrimitives.ReverseEndianness(directoryValue));
            if (info.GroupConfigHashName != expectedGroupHash)
            {
                return $"GroupConfigHashName mismatch for {info.GroupConfigName}: expected {expectedGroupHash} (0x{unchecked((uint)expectedGroupHash):X8}), actual {info.GroupConfigHashName}";
            }
            if (EndfieldVfsBlockTypes.IsKnown(info.BlockTypeValue)
                && !string.Equals(EndfieldVfsBlockTypes.GetName(info.BlockTypeValue), info.GroupConfigName, StringComparison.Ordinal))
            {
                return $"known block type {info.BlockTypeValue} name {EndfieldVfsBlockTypes.GetName(info.BlockTypeValue)} does not match group name {info.GroupConfigName}";
            }
            return null;
        }

        private static string CatalogConflictError(EndfieldVfsCatalogEntry entry) => entry.State switch
        {
            EndfieldVfsCatalogState.Conflicting => "primary and fallback metadata conflict and cannot be merged",
            EndfieldVfsCatalogState.ShadowedEmpty => "empty primary metadata shadows non-empty fallback metadata",
            _ => null,
        };

        private static string ExpectedDigestHex(UInt128 value) =>
            EndfieldVfsFormatting.UInt128LittleEndianHex(value);

        private static JObject Diagnostic(
            string code,
            string block,
            string logicalPath,
            string chunk,
            string sourceRoot,
            object expected,
            object actual,
            string message,
            long? offset = null,
            long? length = null,
            string chunkPath = null)
        {
            var bounded = string.IsNullOrEmpty(message) ? code : message.Length <= 320 ? message : message[..320];
            return new JObject
            {
                ["code"] = code,
                ["block"] = block == null ? JValue.CreateNull() : block,
                ["logicalPath"] = logicalPath == null ? JValue.CreateNull() : logicalPath,
                ["chunk"] = chunk == null ? JValue.CreateNull() : chunk,
                ["sourceRoot"] = sourceRoot == null ? JValue.CreateNull() : sourceRoot,
                ["physicalChunkPath"] = chunkPath == null ? JValue.CreateNull() : NormalizePath(chunkPath),
                ["offset"] = offset.HasValue ? offset.Value : JValue.CreateNull(),
                ["length"] = length.HasValue ? length.Value : JValue.CreateNull(),
                ["expected"] = expected == null ? JValue.CreateNull() : JToken.FromObject(expected),
                ["actual"] = actual == null ? JValue.CreateNull() : JToken.FromObject(actual),
                ["message"] = bounded,
            };
        }

        private static void Fail(AuditResult result, JObject diagnostic)
        {
            result.FailureCount++;
            if (result.StructuredDiagnostics.Count < MaxDiagnostics)
            {
                result.StructuredDiagnostics.Add(diagnostic);
                result.Diagnostics.Add((string)diagnostic["message"]);
            }
        }

        private static void Emit(AuditResult result, JObject record)
        {
            result.LedgerWriter.Write(record.ToString(Formatting.None));
            result.LedgerWriter.Write('\n');
        }

        private static void CountFileStatus(AuditResult result, string boundaryStatus, string overlayState)
        {
            result.BoundaryStatusCounts.TryGetValue(boundaryStatus, out var boundaryCount);
            result.BoundaryStatusCounts[boundaryStatus] = boundaryCount + 1;
            result.OverlayStateCounts.TryGetValue(overlayState, out var overlayCount);
            result.OverlayStateCounts[overlayState] = overlayCount + 1;
        }

        private static void RecordUnverified(AuditResult result, JObject fileRecord)
        {
            if (string.Equals((string)fileRecord["boundaryStatus"], "boundary_verified", StringComparison.Ordinal))
            {
                return;
            }
            result.UnverifiedFiles.Add(new JObject
            {
                ["blockName"] = fileRecord["blockName"]?.DeepClone(),
                ["blockTypeValue"] = fileRecord["blockTypeValue"]?.DeepClone(),
                ["virtualPath"] = fileRecord["virtualPath"]?.DeepClone(),
                ["chunkFile"] = fileRecord["chunkFile"]?.DeepClone(),
                ["length"] = fileRecord["length"]?.DeepClone(),
                ["boundaryStatus"] = fileRecord["boundaryStatus"]?.DeepClone(),
                ["overlayState"] = fileRecord["overlayState"]?.DeepClone(),
                ["diagnostic"] = fileRecord["diagnostic"]?.DeepClone(),
            });
        }

        private static string StateName(EndfieldVfsCatalogState state) => state switch
        {
            EndfieldVfsCatalogState.PrimaryOnly => "primary_only",
            EndfieldVfsCatalogState.FallbackOnly => "fallback_only",
            EndfieldVfsCatalogState.Identical => "identical",
            EndfieldVfsCatalogState.Replaced => "replaced",
            EndfieldVfsCatalogState.ShadowedEmpty => "shadowed_empty",
            EndfieldVfsCatalogState.Conflicting => "conflicting_metadata",
            _ => "missing_both",
        };

        private static AuditOptions ParseOptions(string[] args)
        {
            var options = new AuditOptions();
            for (var i = 1; i < args.Length; i++)
            {
                var token = args[i];
                if (token is "-h" or "--help" or "/?")
                {
                    EndfieldVfsCli.TryRun(new[] { "vfs-audit", "--help" }, out _);
                    throw new InvalidOperationException("help requested");
                }
                var equals = token.IndexOf('=');
                var value = equals > 0 ? token[(equals + 1)..] : null;
                if (equals > 0)
                {
                    token = token[..equals];
                }
                if (value == null && i + 1 < args.Length && token is "-s" or "--streaming-assets" or "--fallback-assets" or "--summary-json" or "--ledger-jsonl-gz" or "--report-md" or "--block-hash")
                {
                    value = args[++i];
                }
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException($"{token} requires a value");
                }
                switch (token)
                {
                    case "-s":
                    case "--streaming-assets": options.PrimaryAssets = value; break;
                    case "--fallback-assets": options.FallbackAssets = value; break;
                    case "--summary-json": options.SummaryJson = value; break;
                    case "--ledger-jsonl-gz": options.LedgerJsonlGz = value; break;
                    case "--report-md": options.ReportMd = value; break;
                    case "--block-hash": options.BlockHashes.Add(value.ToUpperInvariant()); break;
                    default: throw new ArgumentException($"unexpected argument: {token}");
                }
            }
            if (string.IsNullOrWhiteSpace(options.PrimaryAssets))
            {
                throw new ArgumentException("--streaming-assets is required");
            }
            var outputPaths = new[] { options.SummaryJson, options.LedgerJsonlGz, options.ReportMd }
                .Select(Path.GetFullPath).ToList();
            if (outputPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputPaths.Count)
            {
                throw new ArgumentException("summary, ledger, and report outputs must be three distinct paths");
            }
            return options;
        }

        private static void WriteSummary(string path, JObject summary) =>
            File.WriteAllText(path, summary.ToString(Formatting.Indented).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", new UTF8Encoding(false));

        private static void WriteReport(string path, AuditResult result)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# AnimeStudio VFS boundary audit");
            builder.AppendLine();
            builder.AppendLine($"- Blocks: {result.BlockCount}");
            builder.AppendLine($"- Chunks: {result.ChunkCount}");
            builder.AppendLine($"- Ledger file rows: {result.FileCount}");
            builder.AppendLine($"- Canonical metadata declarations: {result.CurrentDeclaredFileCount:N0} files / {result.CurrentDeclaredLogicalBytes:N0} logical bytes / {result.CurrentDeclaredChunkBytes:N0} chunk bytes");
            builder.AppendLine($"- Available: {result.AvailableFileCount:N0}");
            builder.AppendLine($"- Boundary verified: {result.BoundaryVerifiedCount:N0}");
            builder.AppendLine($"- Missing: {result.MissingFileCount:N0}");
            builder.AppendLine($"- Unavailable: {result.UnavailableFileCount:N0}");
            builder.AppendLine($"- Excluded: {result.ExcludedFileCount:N0}");
            builder.AppendLine($"- Failed: {result.FailedFileCount:N0}");
            builder.AppendLine($"- Conditionally excluded missing audio blocks: {result.ExcludedBlockCount}");
            builder.AppendLine($"- Failures: {result.FailureCount}");
            builder.AppendLine($"- All available files boundary verified: {(result.AvailableFileCount == result.BoundaryVerifiedCount ? "yes" : "no")}");
            builder.AppendLine($"- Full audit passed: {(result.FailureCount == 0 ? "yes" : "no")}");
            builder.AppendLine($"- Input set SHA-256: `{result.InputSetSha256}`");
            builder.AppendLine($"- Physical .blc files: {((JArray)result.Summary["physicalMetadata"]).Count}");
            builder.AppendLine($"- Physical .chk files: {result.PhysicalChunkInventory.Count} ({result.ReferencedPhysicalChunks.Count} referenced by ledger, {result.PhysicalChunkInventory.Count - result.ReferencedPhysicalChunks.Count} unselected overlay/orphan copies)");
            builder.AppendLine();
            builder.AppendLine("## File terminal reasons");
            builder.AppendLine();
            foreach (var item in result.BoundaryStatusCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                builder.Append("- ").Append(item.Key).Append(": ").AppendLine(item.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (result.UnverifiedFiles.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("## Unverified logical-file rows");
                builder.AppendLine();
                foreach (var file in result.UnverifiedFiles)
                {
                    builder.Append("- `").Append(file["blockName"]).Append("/")
                        .Append(file["virtualPath"]).Append("`: ").Append(file["boundaryStatus"])
                        .Append("; chunk `").Append(file["chunkFile"]).Append("`; ")
                        .AppendLine((string)file["diagnostic"]?["message"] ?? "no diagnostic");
                }
            }
            builder.AppendLine();
            builder.AppendLine("## Block reconciliation");
            builder.AppendLine();
            builder.AppendLine("| Block | ID | Overlay | Files | Logical bytes | Chunk bytes |");
            builder.AppendLine("|---|---:|---|---:|---:|---:|");
            foreach (var block in result.BlockSummaries.Cast<JObject>())
            {
                builder.Append("| ").Append(block["blockName"]).Append(" | ")
                    .Append(block["blockTypeValue"]).Append(" | ").Append(block["overlayState"])
                    .Append(" | ").Append(block["declaredFileCount"]).Append(" | ")
                    .Append(block["declaredLogicalBytes"]).Append(" | ")
                    .Append(block["declaredChunkBytes"]).AppendLine(" |");
            }
            builder.AppendLine();
            builder.AppendLine("## Physical chunk interval coverage");
            builder.AppendLine();
            builder.AppendLine("Every canonical metadata chunk and explicit shadowed fallback row is listed. `gap` is declared chunk bytes not assigned to a logical file; overlap is independently required to remain zero.");
            builder.AppendLine();
            builder.AppendLine("| Block | Chunk | Source | Declared | Actual | Logical | Gap | Intervals | Overlap | Status |");
            builder.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---|");
            foreach (var chunk in result.ChunkSummaries.Cast<JObject>())
            {
                builder.Append("| ").Append(chunk["blockName"]).Append(" | `")
                    .Append(chunk["chunkFile"]).Append("` | ").Append(chunk["physicalChunkSource"])
                    .Append(" | ").Append(chunk["declaredLength"]).Append(" | ")
                    .Append(chunk["actualPhysicalLength"]).Append(" | ").Append(chunk["logicalBytes"])
                    .Append(" | ").Append(chunk["gapBytes"]).Append(" | ").Append(chunk["intervalCount"])
                    .Append(" | ").Append(chunk["overlapBytes"]).Append(" | ").Append(chunk["boundaryStatus"])
                    .AppendLine(" |");
            }
            var unselectedPhysical = result.PhysicalChunkInventory.Cast<JObject>()
                .Where(item => string.Equals((string)item["selectionStatus"], "unselected_overlay_or_orphan", StringComparison.Ordinal))
                .ToList();
            if (unselectedPhysical.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("## Unselected physical chunk copies");
                builder.AppendLine();
                foreach (var physical in unselectedPhysical)
                {
                    builder.Append("- ").Append(physical["role"]).Append(": `")
                        .Append(physical["relativePath"]).Append("` (").Append(physical["length"])
                        .AppendLine(" bytes; not selected by the effective metadata/chunk overlay)");
                }
            }
            if (result.Diagnostics.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("## Bounded diagnostics");
                foreach (var diagnostic in result.Diagnostics)
                {
                    builder.Append("- ").AppendLine(diagnostic);
                }
            }
            File.WriteAllText(path, builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
        }

        private static string CreateTemporaryPath(string output)
        {
            var full = Path.GetFullPath(output);
            var parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent)) throw new ArgumentException($"output has no parent directory: {output}");
            Directory.CreateDirectory(parent);
            return Path.Combine(parent, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        }

        private static void Replace(string temporary, string output)
        {
            var full = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.Move(temporary, full, true);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string NormalizePath(string path) => path?.Replace('\\', '/');

        private sealed class AuditOptions
        {
            public string PrimaryAssets { get; set; }
            public string FallbackAssets { get; set; }
            public string SummaryJson { get; set; } = "./vfs_audit_summary.json";
            public string LedgerJsonlGz { get; set; } = "./vfs_audit_ledger.jsonl.gz";
            public string ReportMd { get; set; } = "./vfs_audit_report.md";
            public HashSet<string> BlockHashes { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class AuditResult
        {
            public JObject Summary { get; set; }
            public TextWriter LedgerWriter { get; set; }
            public List<string> Diagnostics { get; } = new();
            public List<JObject> StructuredDiagnostics { get; } = new();
            public string InputSetSha256 { get; set; }
            public JArray BlockSummaries { get; set; }
            public JArray ChunkSummaries { get; set; }
            public JArray PhysicalChunkInventory { get; set; }
            public HashSet<string> ReferencedPhysicalChunks { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int BlockCount { get; set; }
            public int ChunkCount { get; set; }
            public long FileCount { get; set; }
            public int FailureCount { get; set; }
            public int ExcludedBlockCount { get; set; }
            public int MissingBlockCount { get; set; }
            public long ShadowedFallbackCount { get; set; }
            public long CurrentDeclaredFileCount { get; set; }
            public long CurrentDeclaredChunkCount { get; set; }
            public long CurrentDeclaredLogicalBytes { get; set; }
            public long CurrentDeclaredChunkBytes { get; set; }
            public long AvailableFileCount { get; set; }
            public long BoundaryVerifiedCount { get; set; }
            public long MissingFileCount { get; set; }
            public long UnavailableFileCount { get; set; }
            public long ExcludedFileCount { get; set; }
            public long FailedFileCount { get; set; }
            public int MissingChunkCount { get; set; }
            public Dictionary<string, long> BoundaryStatusCounts { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, long> OverlayStateCounts { get; } = new(StringComparer.Ordinal);
            public List<JObject> UnverifiedFiles { get; } = new();
        }

        private sealed class FileDigest
        {
            public FileDigest(byte[] digest, long actualBytes)
            {
                Digest = digest;
                ActualBytes = actualBytes;
            }
            public byte[] Digest { get; }
            public long ActualBytes { get; }
        }
    }
}
