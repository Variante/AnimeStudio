using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnimeStudio.Endfield;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    public static class EndfieldAudioCli
    {
        public static int RunAudit(string[] args)
        {
            var options = ParseAuditOptions(args);
            var loader = new EndfieldVfsLoader(options.StreamingAssets, options.FallbackAssets);
            var rows = new List<Dictionary<string, object?>>();
            var failures = 0;
            foreach (var blockType in options.BlockTypes)
            {
                List<(EndfieldVfsChunkInfo chunk, EndfieldVfsFileInfo file)> pckFiles;
                EndfieldVfsBlockMainInfo blockInfo;
                try
                {
                    blockInfo = loader.LoadBlockInfo(blockType);
                    pckFiles = ExtractPckFiles(blockInfo);
                }
                catch (EndfieldVfsException e)
                {
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["block"] = blockType.GetName(),
                        ["status"] = "missing_block",
                        ["diagnostic"] = BoundDiagnostic(e.Message),
                    });
                    failures++;
                    continue;
                }

                if (IsAudioBlock(blockType)
                    && blockInfo.Chunks.Count > 0
                    && blockInfo.Chunks.All(chunk => IsChunkMissing(loader, blockType, chunk)))
                {
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["block"] = blockType.GetName(),
                        ["status"] = "excluded_missing_audio",
                        ["source"] = "missing_both",
                        ["declaredChunks"] = blockInfo.Chunks.Count,
                        ["declaredFiles"] = blockInfo.GroupFileInfoNum,
                        ["expected"] = "at least one declared audio chunk in primary or fallback root",
                        ["actual"] = 0,
                        ["diagnostic"] = "every declared audio chunk is absent from primary and fallback roots; the block is conditionally ignored",
                    });
                    continue;
                }

                if (pckFiles.Count == 0)
                {
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["block"] = blockType.GetName(),
                        ["status"] = "empty_block",
                        ["source"] = options.StreamingAssets,
                        ["declaredChunks"] = blockInfo.Chunks.Count,
                        ["declaredFiles"] = blockInfo.GroupFileInfoNum,
                        ["expected"] = "at least one .pck logical file",
                        ["actual"] = 0,
                        ["diagnostic"] = "audio block metadata contains no .pck logical files",
                    });
                    failures++;
                    continue;
                }

                foreach (var (chunk, file) in pckFiles)
                {
                    var row = new Dictionary<string, object?>
                    {
                        ["block"] = blockType.GetName(),
                        ["path"] = file.FileName,
                        ["chunk"] = chunk.FileName,
                        ["source"] = "unresolved",
                        ["declaredBytes"] = file.Length,
                        ["status"] = "failed",
                    };
                    try
                    {
                        row["source"] = loader.ResolveChunkPath(blockType, chunk);
                        var package = EndfieldAkpkPackage.Parse(
                            loader.ExtractFileToBytes(blockType, chunk, file, verifyMd5: true));
                        var mediaRiff = 0;
                        var mediaPlugin = 0;
                        var mediaInvalid = 0;
                        var invalidExamples = new List<Dictionary<string, object?>>();
                        if (!options.HircOnly)
                        {
                            foreach (var entry in package.Entries)
                            {
                                var media = package.GetWemData(entry);
                                if (HasMagic(media, "PLUG"))
                                {
                                    mediaPlugin++;
                                }
                                else if (HasMagic(media, "RIFF") || HasMagic(media, "RIFX"))
                                {
                                    mediaRiff++;
                                }
                                else
                                {
                                    mediaInvalid++;
                                    if (invalidExamples.Count < 8)
                                    {
                                        invalidExamples.Add(new Dictionary<string, object?>
                                        {
                                            ["id"] = entry.Id.ToString("x"),
                                            ["offset"] = entry.Offset,
                                            ["declaredBytes"] = entry.Size,
                                            ["magic"] = MagicPreview(media),
                                        });
                                    }
                                }
                            }
                        }
                        row["package"] = new Dictionary<string, object?>
                        {
                            ["headerSize"] = package.HeaderSize,
                            ["version"] = package.Version,
                            ["encryptedHeader"] = package.EncryptedHeader,
                            ["languageSectorBytes"] = package.LanguageSectorSize,
                            ["banksSectorBytes"] = package.BanksSectorSize,
                            ["soundsSectorBytes"] = package.SoundsSectorSize,
                            ["externalsSectorBytes"] = package.ExternalsSectorSize,
                            ["languages"] = package.Languages.Count,
                            ["banks"] = package.BankCount,
                            ["sounds"] = package.SoundCount,
                            ["externals"] = package.ExternalCount,
                            ["mediaEntries"] = package.Entries.Count,
                            ["mediaRiff"] = mediaRiff,
                            ["mediaPlugin"] = mediaPlugin,
                            ["mediaInvalid"] = mediaInvalid,
                            ["mediaVerification"] = options.HircOnly ? "skipped" : "verified",
                            ["invalidExamples"] = invalidExamples,
                            ["languageNames"] = package.Languages.Values.Distinct(StringComparer.Ordinal).OrderBy(x => x).ToArray(),
                            ["bnkPayloads"] = package.BnkStructures.Count,
                            ["bnkSections"] = package.BnkStructures.Sum(x => x.Sections.Count),
                            ["hircObjects"] = package.BnkStructures.Sum(x => checked((long)x.HircObjectCount)),
                            ["hircType02Prefix"] = new Dictionary<string, object?>
                            {
                                ["count"] = package.BnkStructures.Sum(x => (long)x.Type2PrefixCount),
                                ["prefixBytes"] = package.BnkStructures.Sum(x => (long)x.Type2PrefixBytes),
                                ["opaqueTailBytes"] = package.BnkStructures.Sum(x => (long)x.Type2OpaqueTailBytes),
                                ["minOpaqueTailBytes"] = package.BnkStructures.Where(x => x.Type2PrefixCount > 0).Select(x => x.Type2MinOpaqueTailBytes).DefaultIfEmpty(0u).Min(),
                                ["maxOpaqueTailBytes"] = package.BnkStructures.Select(x => x.Type2MaxOpaqueTailBytes).DefaultIfEmpty(0u).Max(),
                                ["pluginTypeCounts"] = package.BnkStructures
                                    .SelectMany(x => x.Type2PluginTypeCounts)
                                    .GroupBy(x => x.Key)
                                    .OrderBy(x => x.Key)
                                    .ToDictionary(x => $"0x{x.Key:X}", x => x.Sum(y => (long)y.Value)),
                            },
                            ["hircObjectTypeCounts"] = package.BnkStructures
                                .SelectMany(x => x.HircObjectTypeCounts)
                                .GroupBy(x => x.Key)
                                .OrderBy(x => x.Key)
                                .ToDictionary(x => $"0x{x.Key:X2}", x => x.Sum(y => (long)y.Value)),
                            ["hircObjectTypeStats"] = package.BnkStructures
                                .SelectMany(x => x.HircObjectTypeStats)
                                .GroupBy(x => x.Key)
                                .OrderBy(x => x.Key)
                                .ToDictionary(
                                    x => $"0x{x.Key:X2}",
                                    x => new Dictionary<string, object?>
                                    {
                                        ["count"] = x.Sum(y => (long)y.Value.Count),
                                        ["declaredLengthBytes"] = x.Sum(y => (long)y.Value.DeclaredLengthBytes),
                                        ["minDeclaredLength"] = x.Min(y => y.Value.MinDeclaredLength),
                                        ["maxDeclaredLength"] = x.Max(y => y.Value.MaxDeclaredLength),
                                    }),
                            ["bnkSectionTagCounts"] = package.BnkStructures
                                .SelectMany(x => x.Sections)
                                .GroupBy(x => x.Tag, StringComparer.Ordinal)
                                .OrderBy(x => x.Key, StringComparer.Ordinal)
                                .ToDictionary(x => x.Key, x => x.LongCount(), StringComparer.Ordinal),
                            ["bnkStructures"] = package.BnkStructures.Select(x => new Dictionary<string, object?>
                            {
                                ["bankId"] = x.BankId,
                                ["byteLength"] = x.ByteLength,
                                ["version"] = x.Version,
                                ["sections"] = x.Sections.Select(section => new Dictionary<string, object?>
                                {
                                    ["tag"] = section.Tag,
                                    ["offset"] = section.Offset,
                                    ["declaredSize"] = section.DeclaredSize,
                                }).ToArray(),
                                ["hircObjectCount"] = x.HircObjectCount,
                                ["hircType02Prefix"] = new Dictionary<string, object?>
                                {
                                    ["count"] = x.Type2PrefixCount,
                                    ["prefixBytes"] = x.Type2PrefixBytes,
                                    ["opaqueTailBytes"] = x.Type2OpaqueTailBytes,
                                    ["minOpaqueTailBytes"] = x.Type2MinOpaqueTailBytes,
                                    ["maxOpaqueTailBytes"] = x.Type2MaxOpaqueTailBytes,
                                    ["pluginTypeCounts"] = x.Type2PluginTypeCounts
                                        .OrderBy(pair => pair.Key)
                                        .ToDictionary(pair => $"0x{pair.Key:X}", pair => pair.Value),
                                },
                                ["hircObjectTypeStats"] = x.HircObjectTypeStats
                                    .OrderBy(pair => pair.Key)
                                    .ToDictionary(
                                        pair => $"0x{pair.Key:X2}",
                                        pair => new Dictionary<string, object?>
                                        {
                                            ["count"] = pair.Value.Count,
                                            ["declaredLengthBytes"] = pair.Value.DeclaredLengthBytes,
                                            ["minDeclaredLength"] = pair.Value.MinDeclaredLength,
                                            ["maxDeclaredLength"] = pair.Value.MaxDeclaredLength,
                                        }),
                            }).ToArray(),
                        };
                        if (mediaInvalid != 0)
                        {
                            var firstInvalidId = invalidExamples.Count == 0 ? "unknown" : invalidExamples[0].GetValueOrDefault("id")?.ToString();
                            throw new InvalidDataException($"AKPK media entries without RIFF/RIFX/PLUG: count={mediaInvalid}; first={firstInvalidId}");
                        }
                        row["status"] = "verified";
                    }
                    catch (Exception e)
                    {
                        row["diagnostic"] = BoundDiagnostic(e.Message);
                        failures++;
                    }
                    rows.Add(row);
                }
            }

            var report = new Dictionary<string, object?>
            {
                ["schemaVersion"] = "akpk-structure-audit-v1",
                ["streamingAssets"] = options.StreamingAssets,
                ["fallbackAssets"] = options.FallbackAssets,
                ["conditionalExclusions"] = "any audio block is ignored only when every declared chunk is absent from both roots",
                ["blocks"] = options.BlockTypes.Select(x => x.GetName()).ToArray(),
                ["rows"] = rows,
                ["summary"] = new Dictionary<string, object?>
                {
                    ["packages"] = rows.Count(x => x.TryGetValue("path", out _)),
                    ["verified"] = rows.Count(x => Equals(x.GetValueOrDefault("status"), "verified")),
                    ["failures"] = failures,
                    ["missingBlocks"] = rows.Count(x => Equals(x.GetValueOrDefault("status"), "missing_block")),
                    ["excluded"] = rows.Count(x => Equals(x.GetValueOrDefault("status"), "excluded_missing_audio")),
                },
            };
            var outputParent = Path.GetDirectoryName(Path.GetFullPath(options.Output));
            if (!string.IsNullOrEmpty(outputParent))
            {
                Directory.CreateDirectory(outputParent);
            }
            File.WriteAllText(options.Output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            var packageCount = rows.Count(x => x.TryGetValue("path", out _));
            var verifiedCount = rows.Count(x => Equals(x.GetValueOrDefault("status"), "verified"));
            Console.WriteLine($"AKPK audit: {packageCount} packages, {verifiedCount} verified, {failures} failures");
            return failures == 0 ? 0 : 1;
        }

        public static void Run(string[] args)
        {
            var options = ParseOptions(args);
            var loader = new EndfieldVfsLoader(options.StreamingAssets, options.FallbackAssets);

            Console.WriteLine("Loading AudioDialog.json...");
            var audioDialog = LoadAudioDialog(loader);
            var converter = options.Format != AudioOutputFormat.Wem
                ? EndfieldVgmstreamConverter.CreateDefault()
                : null;

            var totalSuccess = 0;
            var totalErrors = 0;
            var totalUnmapped = 0;
            var totalPluginMedia = 0;
            var totalDuplicatePathUnavailablePackages = 0;
            var totalPackageErrors = 0;

            foreach (var language in options.Languages)
            {
                var audioMap = EndfieldAudioMap.FromAudioDialog(audioDialog, language);
                var processedPckNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Console.WriteLine($"  Found {audioMap.Count} {language.Name()} audio entries");

                foreach (var blockType in options.BlockTypes(language))
                {
                    Console.WriteLine($"Extracting {language.Name()} audio files from {blockType.GetName()}...");
                    List<(EndfieldVfsChunkInfo chunk, EndfieldVfsFileInfo file)> pckFiles;
                    try
                    {
                        pckFiles = ExtractPckFiles(loader, blockType);
                    }
                    catch (EndfieldVfsException)
                    {
                        Console.WriteLine($"  Skip: No PCK files found in {blockType.GetName()}");
                        continue;
                    }

                    if (pckFiles.Count == 0)
                    {
                        Console.WriteLine("  Skip: No PCK files found");
                        continue;
                    }

                    Console.WriteLine($"  Found {pckFiles.Count} PCK files");
                    foreach (var (chunk, file) in pckFiles)
                    {
                        var pckName = file.FileName;
                        Console.WriteLine($"  Processing {pckName}");
                        EndfieldAkpkPackage package;
                        try
                        {
                            // Extract one PCK at a time instead of retaining every
                            // package in the block during the full conversion pass.
                            var pckData = loader.ExtractFileToBytes(blockType, chunk, file);
                            package = EndfieldAkpkPackage.Parse(pckData);
                        }
                        catch (EndfieldVfsChunkNotFoundException e) when (processedPckNames.Contains(pckName))
                        {
                            Console.WriteLine(
                                $"    Skip: {e.Message}; the same logical PCK path was already processed from an earlier block"
                            );
                            totalDuplicatePathUnavailablePackages++;
                            continue;
                        }
                        catch (Exception e)
                        {
                            Console.Error.WriteLine($"    Error: Failed to parse {pckName}: {e.Message}");
                            totalPackageErrors++;
                            continue;
                        }
                        processedPckNames.Add(pckName);

                        var successCount = 0;
                        var errorCount = 0;
                        var unmappedCount = 0;
                        var pluginMediaCount = 0;
                        var entries = package.Entries.ToArray();

                        Parallel.ForEach(entries, new ParallelOptions
                        {
                            MaxDegreeOfParallelism = options.Jobs,
                        }, entry =>
                        {
                            try
                            {
                                var wemData = package.GetWemData(entry);
                                if (HasMagic(wemData, "PLUG"))
                                {
                                    Interlocked.Increment(ref pluginMediaCount);
                                    return;
                                }
                                if (wemData.Length < 4 || (!HasMagic(wemData, "RIFF") && !HasMagic(wemData, "RIFX")))
                                {
                                    Console.Error.WriteLine(
                                        $"    Error: Unsupported media entry {entry.Id:x} in {pckName}: " +
                                        $"expected RIFF/RIFX, got {MagicPreview(wemData)} ({wemData.Length} bytes)"
                                    );
                                    Interlocked.Increment(ref errorCount);
                                    return;
                                }

                                var hash = entry.Id.ToString("x");
                                var outputRoot = options.OutputForBlock(blockType);
                                string outputPath;
                                var mappedPath = audioMap.GetPath(hash);
                                if (!string.IsNullOrEmpty(mappedPath))
                                {
                                    outputPath = options.Format == AudioOutputFormat.Wem
                                        ? Path.Combine(outputRoot, mappedPath)
                                        : Path.Combine(
                                            outputRoot,
                                            mappedPath.Replace(
                                                ".wem",
                                                $".{options.Format.Extension()}",
                                                StringComparison.Ordinal
                                            )
                                        );
                                }
                                else
                                {
                                    Interlocked.Increment(ref unmappedCount);
                                    // The language is already encoded in the output root
                                    // (Audio/<LANG> or Audio/shared); unmapped media are
                                    // grouped by their source bank instead of a redundant
                                    // language subfolder. A Wwise event-category subfolder
                                    // is added later by the Python indexer where resolvable.
                                    outputPath = Path.Combine(
                                        outputRoot,
                                        "unmapped",
                                        UnmappedBankFolder(pckName),
                                        $"{entry.Id}.{options.Format.Extension()}"
                                    );
                                }

                                WriteAudioFile(wemData, outputPath, options.Format, converter);
                                Interlocked.Increment(ref successCount);
                            }
                            catch (Exception e)
                            {
                                Console.Error.WriteLine($"    Error: Failed to extract/write media {entry.Id:x}: {e.Message}");
                                Interlocked.Increment(ref errorCount);
                            }
                        });

                        totalSuccess += successCount;
                        totalErrors += errorCount;
                        totalUnmapped += unmappedCount;
                        totalPluginMedia += pluginMediaCount;
                        Console.WriteLine(
                            $"    Done: Extracted {successCount}/{entries.Length} audio entries" +
                            (pluginMediaCount > 0
                                ? $" ({pluginMediaCount} Wwise FX plugin-media entries skipped)"
                                : string.Empty)
                        );
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine(
                $"Complete: Extracted {totalSuccess} files ({totalUnmapped} unmapped, " +
                $"{totalPluginMedia} Wwise FX plugin-media entries skipped, " +
                $"{totalDuplicatePathUnavailablePackages} duplicate-path packages unavailable, " +
                $"{totalPackageErrors + totalErrors} errors)"
            );
        }

        public static void PrintHelp()
        {
            Console.WriteLine("Usage: AnimeStudio.CLI audio -s <StreamingAssets> [-o <output>] [--shared-output <output>] [-l <language>] [-f <flac|wav|wem>] [-b <block>] [-j <jobs>] [--fallback-assets <StreamingAssets>]");
        }

        private static JToken LoadAudioDialog(EndfieldVfsLoader loader)
        {
            var merged = new JObject();
            var layerLoaders = new List<EndfieldVfsLoader> { loader };
            if (!string.IsNullOrEmpty(loader.FallbackAssetsPath)
                && !string.Equals(loader.StreamingAssetsPath, loader.FallbackAssetsPath, StringComparison.OrdinalIgnoreCase))
            {
                // Keep the primary loader's fallback resolution: some primary
                // Table metadata references a chunk that is present only in
                // the fallback VFS.  The second loader reads the fallback
                // table itself so its Persistent rows are also merged.
                layerLoaders.Add(new EndfieldVfsLoader(loader.FallbackAssetsPath));
            }

            foreach (var layerLoader in layerLoaders)
            {
                try
                {
                    var layer = LoadAudioDialogLayer(layerLoader);
                    foreach (var property in layer.Properties())
                    {
                        // Persistent is the overlay layer, so rows in it win
                        // when the same authored id exists in both roots.
                        merged[property.Name] = property.Value.DeepClone();
                    }
                }
                catch (EndfieldVfsBlockNotFoundException)
                {
                    // A language/install root may legitimately omit the Table
                    // block; keep loading the other root if it has one.
                }
            }

            if (merged.Count > 0)
            {
                return merged;
            }

            throw new EndfieldVfsException("AudioDialog.bytes not found in Table block");
        }

        private static string BoundDiagnostic(string message) =>
            string.IsNullOrEmpty(message) ? "unknown AKPK failure" : message.Length <= 240 ? message : message[..240];

        private sealed class AudioAuditOptions
        {
            public string StreamingAssets { get; set; }
            public string FallbackAssets { get; set; }
            public string Output { get; set; } = "./akpk_audit.json";
            public List<EndfieldVfsBlockType> BlockTypes { get; } = new();
            public bool HircOnly { get; set; }
        }

        private static AudioAuditOptions ParseAuditOptions(string[] args)
        {
            var options = new AudioAuditOptions();
            for (var i = 1; i < args.Length; i++)
            {
                var token = args[i];
                var value = token.Contains('=') ? token[(token.IndexOf('=') + 1)..] : null;
                if (value is not null)
                {
                    token = token[..token.IndexOf('=')];
                }
                string Next()
                {
                    if (value is not null) return value;
                    if (++i >= args.Length) throw new ArgumentException($"{token} requires a value");
                    return args[i];
                }
                switch (token)
                {
                    case "-s":
                    case "--streaming-assets": options.StreamingAssets = Next(); break;
                    case "--fallback-assets": options.FallbackAssets = Next(); break;
                    case "--hirc-only": options.HircOnly = true; break;
                    case "-o":
                    case "--output": options.Output = Next(); break;
                    case "-b":
                    case "--block":
                    {
                        var block = ParseAuditBlock(Next());
                        options.BlockTypes.Add(block);
                        break;
                    }
                    default: throw new ArgumentException($"unexpected argument: {token}");
                }
            }
            if (string.IsNullOrEmpty(options.StreamingAssets)) throw new ArgumentException("--streaming-assets is required");
            if (options.BlockTypes.Count == 0)
            {
                options.BlockTypes.AddRange(new[]
                {
                    EndfieldVfsBlockType.Audio,
                    EndfieldVfsBlockType.InitialAudio,
                    EndfieldVfsBlockType.AuditAudio,
                    EndfieldVfsBlockType.HotfixAudio,
                    EndfieldVfsBlockType.AudioChinese,
                    EndfieldVfsBlockType.AudioEnglish,
                    EndfieldVfsBlockType.AudioJapanese,
                    EndfieldVfsBlockType.AudioKorean,
                });
            }
            return options;
        }

        private static EndfieldVfsBlockType ParseAuditBlock(string value) => value.ToLowerInvariant() switch
        {
            "audio" => EndfieldVfsBlockType.Audio,
            "initial-audio" or "initialaudio" => EndfieldVfsBlockType.InitialAudio,
            "audit-audio" or "auditaudio" => EndfieldVfsBlockType.AuditAudio,
            "hotfix-audio" or "hotfixaudio" => EndfieldVfsBlockType.HotfixAudio,
            "audio-chinese" or "audiochinese" or "chinese" => EndfieldVfsBlockType.AudioChinese,
            "audio-english" or "audioenglish" or "english" => EndfieldVfsBlockType.AudioEnglish,
            "audio-japanese" or "audiojapanese" or "japanese" => EndfieldVfsBlockType.AudioJapanese,
            "audio-korean" or "audiokorean" or "korean" => EndfieldVfsBlockType.AudioKorean,
            _ => throw new ArgumentException($"unsupported or excluded audio block: {value}"),
        };

        private static bool IsAudioBlock(EndfieldVfsBlockType blockType) => blockType is
            EndfieldVfsBlockType.InitialAudio or
            EndfieldVfsBlockType.AuditAudio or
            EndfieldVfsBlockType.Audio or
            EndfieldVfsBlockType.HotfixAudio or
            EndfieldVfsBlockType.AudioChinese or
            EndfieldVfsBlockType.AudioEnglish or
            EndfieldVfsBlockType.AudioJapanese or
            EndfieldVfsBlockType.AudioKorean;

        private static JObject LoadAudioDialogLayer(EndfieldVfsLoader loader)
        {
            var blockInfo = loader.LoadBlockInfo(EndfieldVfsBlockType.Table);
            foreach (var chunk in blockInfo.Chunks)
            {
                foreach (var file in chunk.Files)
                {
                    byte[] data;
                    try
                    {
                        data = loader.ExtractFileToBytes(EndfieldVfsBlockType.Table, chunk, file);
                    }
                    catch (EndfieldVfsChunkNotFoundException)
                    {
                        // Persistent Table metadata can retain audit chunks
                        // supplied by the primary VFS.  A fallback-only loader
                        // cannot read those shared chunks; continue to the
                        // chunks that are actually present in Persistent.
                        continue;
                    }
                    try
                    {
                        var parsed = EndfieldSparkBuffer.ParseBytes(data);
                        if (parsed.Name == "AudioDialog" && parsed.Data is JObject obj)
                        {
                            return obj;
                        }
                    }
                    catch
                    {
                        // fluffy-dumper ignores non-SparkBuffer table rows while looking for AudioDialog.
                    }
                }
            }

            throw new EndfieldVfsException("AudioDialog.bytes not found in Table block");
        }

        private static bool IsChunkMissing(
            EndfieldVfsLoader loader,
            EndfieldVfsBlockType blockType,
            EndfieldVfsChunkInfo chunk)
        {
            try
            {
                loader.ResolveChunkPath(blockType, chunk);
                return false;
            }
            catch (EndfieldVfsChunkNotFoundException)
            {
                return true;
            }
        }

        private static List<(EndfieldVfsChunkInfo chunk, EndfieldVfsFileInfo file)> ExtractPckFiles(EndfieldVfsBlockMainInfo blockInfo)
        {
            var files = new List<(EndfieldVfsChunkInfo chunk, EndfieldVfsFileInfo file)>();
            foreach (var chunk in blockInfo.Chunks)
            {
                foreach (var file in chunk.Files)
                {
                    if (file.FileName.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add((chunk, file));
                    }
                }
            }
            return files;
        }

        private static List<(EndfieldVfsChunkInfo chunk, EndfieldVfsFileInfo file)> ExtractPckFiles(
            EndfieldVfsLoader loader,
            EndfieldVfsBlockType blockType) =>
            ExtractPckFiles(loader.LoadBlockInfo(blockType));

        private static void WriteAudioFile(byte[] wemData, string outputPath, AudioOutputFormat format, EndfieldVgmstreamConverter converter)
        {
            var parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (format == AudioOutputFormat.Wem)
            {
                File.WriteAllBytes(outputPath, wemData);
            }
            else if (format == AudioOutputFormat.Flac)
            {
                converter.ConvertBytesToFlac(wemData, outputPath);
            }
            else
            {
                converter.ConvertBytes(wemData, outputPath);
            }
        }

        // Maps a Wwise PCK file name to its source-bank folder, matching the Python
        // indexer (build_audio.unmapped_bank_for_pck_name).
        private static string UnmappedBankFolder(string pckName)
        {
            var name = Path.GetFileName(pckName ?? string.Empty).ToLowerInvariant();
            if (name.Contains("external_source", StringComparison.Ordinal))
            {
                return "external";
            }
            if (name.StartsWith("init", StringComparison.Ordinal))
            {
                return "initial";
            }
            if (name.StartsWith("audit", StringComparison.Ordinal))
            {
                return "audit";
            }
            if (name.StartsWith("hotfix", StringComparison.Ordinal))
            {
                return "hotfix";
            }
            return "main";
        }

        private static bool HasMagic(byte[] data, string magic)
        {
            if (data.Length < magic.Length)
            {
                return false;
            }

            for (var i = 0; i < magic.Length; i++)
            {
                if (data[i] != (byte)magic[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static string MagicPreview(byte[] data) =>
            data.Length == 0
                ? "<empty>"
                : Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 8)));

        private static AudioOptions ParseOptions(string[] args)
        {
            var options = new AudioOptions
            {
                Output = "./output",
                LanguageMode = "all",
                Format = AudioOutputFormat.Flac,
                BlockMode = AudioBlockMode.All,
                Jobs = Math.Min(8, Math.Max(1, Environment.ProcessorCount)),
            };

            for (var i = 1; i < args.Length; i++)
            {
                var token = args[i];
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
                    case "--shared-output":
                        options.SharedOutput = value ?? NextValue(args, ref i, token);
                        break;
                    case "-l":
                    case "--language":
                        options.LanguageMode = value ?? NextValue(args, ref i, token);
                        break;
                    case "-f":
                    case "--format":
                        var rawFormat = value ?? NextValue(args, ref i, token);
                        options.Format = rawFormat.ToLowerInvariant() switch
                        {
                            "flac" => AudioOutputFormat.Flac,
                            "wem" => AudioOutputFormat.Wem,
                            "wav" => AudioOutputFormat.Wav,
                            _ => throw new ArgumentException($"unknown format: {rawFormat}"),
                        };
                        break;
                    case "-b":
                    case "--block":
                        options.BlockMode = ParseBlockMode(value ?? NextValue(args, ref i, token));
                        break;
                    case "-j":
                    case "--jobs":
                        options.Jobs = int.Parse(value ?? NextValue(args, ref i, token));
                        break;
                    default:
                        throw new ArgumentException($"unexpected argument: {token}");
                }
            }

            if (string.IsNullOrEmpty(options.StreamingAssets))
            {
                throw new ArgumentException("--streaming-assets is required");
            }
            if (options.Jobs <= 0)
            {
                throw new ArgumentException("--jobs must be greater than zero");
            }

            return options;
        }

        private static string NextValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{option} requires a value");
            }
            index++;
            return args[index];
        }

        private static AudioBlockMode ParseBlockMode(string value) => value.ToLowerInvariant() switch
        {
            "all" => AudioBlockMode.All,
            "voice" => AudioBlockMode.Voice,
            "audio" => AudioBlockMode.Audio,
            "initial-audio" => AudioBlockMode.InitialAudio,
            "initialaudio" => AudioBlockMode.InitialAudio,
            "audit-audio" => AudioBlockMode.AuditAudio,
            "auditaudio" => AudioBlockMode.AuditAudio,
            "hotfix-audio" => AudioBlockMode.HotfixAudio,
            "hotfixaudio" => AudioBlockMode.HotfixAudio,
            _ => throw new ArgumentException($"unknown audio block: {value}"),
        };

        private sealed class AudioOptions
        {
            public string StreamingAssets { get; set; }
            public string FallbackAssets { get; set; }
            public string Output { get; set; }
            public string SharedOutput { get; set; }
            public string LanguageMode { get; set; }
            public AudioOutputFormat Format { get; set; }
            public AudioBlockMode BlockMode { get; set; }
            public int Jobs { get; set; }

            public IReadOnlyList<EndfieldAudioLanguage> Languages
            {
                get
                {
                    if (string.Equals(LanguageMode, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        return EndfieldAudioLanguages.All;
                    }

                    if (EndfieldAudioLanguages.TryParse(LanguageMode, out var language))
                    {
                        return new[] { language };
                    }

                    throw new ArgumentException($"unknown language: {LanguageMode}");
                }
            }

            public IReadOnlyList<EndfieldVfsBlockType> BlockTypes(EndfieldAudioLanguage language) => BlockMode switch
            {
                AudioBlockMode.All => new[]
                {
                    EndfieldVfsBlockType.Audio,
                    EndfieldVfsBlockType.InitialAudio,
                    EndfieldVfsBlockType.AuditAudio,
                    VoiceBlock(language),
                },
                AudioBlockMode.Voice => new[] { VoiceBlock(language) },
                AudioBlockMode.Audio => new[] { EndfieldVfsBlockType.Audio },
                AudioBlockMode.InitialAudio => new[] { EndfieldVfsBlockType.InitialAudio },
                AudioBlockMode.AuditAudio => new[] { EndfieldVfsBlockType.AuditAudio },
                AudioBlockMode.HotfixAudio => new[] { EndfieldVfsBlockType.HotfixAudio },
                _ => Array.Empty<EndfieldVfsBlockType>(),
            };

            public string OutputForBlock(EndfieldVfsBlockType blockType) =>
                !string.IsNullOrEmpty(SharedOutput) && IsSharedBlock(blockType)
                    ? SharedOutput
                    : Output;

            private static bool IsSharedBlock(EndfieldVfsBlockType blockType) => blockType is
                EndfieldVfsBlockType.Audio or
                EndfieldVfsBlockType.InitialAudio or
                EndfieldVfsBlockType.AuditAudio or
                EndfieldVfsBlockType.HotfixAudio;

            private static EndfieldVfsBlockType VoiceBlock(EndfieldAudioLanguage language) => language switch
            {
                EndfieldAudioLanguage.Chinese => EndfieldVfsBlockType.AudioChinese,
                EndfieldAudioLanguage.English => EndfieldVfsBlockType.AudioEnglish,
                EndfieldAudioLanguage.Japanese => EndfieldVfsBlockType.AudioJapanese,
                EndfieldAudioLanguage.Korean => EndfieldVfsBlockType.AudioKorean,
                _ => EndfieldVfsBlockType.Audio,
            };
        }

        public enum AudioOutputFormat
        {
            Flac,
            Wem,
            Wav,
        }

        private enum AudioBlockMode
        {
            All,
            Voice,
            Audio,
            InitialAudio,
            AuditAudio,
            HotfixAudio,
        }
    }

    internal static class AudioOutputFormatExtensions
    {
        public static string Extension(this EndfieldAudioCli.AudioOutputFormat format) =>
            format switch
            {
                EndfieldAudioCli.AudioOutputFormat.Flac => "flac",
                EndfieldAudioCli.AudioOutputFormat.Wav => "wav",
                _ => "wem",
            };
    }
}
