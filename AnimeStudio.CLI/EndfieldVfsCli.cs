using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnimeStudio.Endfield;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.CLI
{
    public static class EndfieldVfsCli
    {
        private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
        {
            "dump",
            "audio",
            "vfs-index",
            "vfsindex",
            "list",
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
                    case "audio":
                        EndfieldAudioCli.Run(args);
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
            foreach (var blockType in options.SelectedBlockTypes())
            {
                DumpBlock(loader, blockType, options.Output);
            }
        }

        private static void DumpBlock(EndfieldVfsLoader loader, EndfieldVfsBlockType blockType, string output)
        {
            Console.WriteLine($"Dumping {blockType.GetName()} files...");
            EndfieldVfsBlockMainInfo blockInfo;
            try
            {
                blockInfo = loader.LoadBlockInfo(blockType);
            }
            catch (EndfieldVfsBlockNotFoundException e)
            {
                Console.WriteLine($"  Warning: Block {e.HashDirectory} not found, skipping");
                return;
            }

            var successCount = 0;
            var errorCount = 0;
            var totalFiles = blockInfo.Chunks.Sum(chunk => chunk.Files.Count);

            foreach (var chunk in blockInfo.Chunks)
            {
                Parallel.ForEach(chunk.Files, file =>
                {
                    try
                    {
                        ProcessDumpFile(loader, blockType, chunk, file, output);
                        Interlocked.Increment(ref successCount);
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine($"  Error: Failed to extract {file.FileName}: {e.Message}");
                        Interlocked.Increment(ref errorCount);
                    }
                });
            }

            Console.WriteLine($"  Done: Extracted {successCount}/{totalFiles} files");
            if (errorCount > 0)
            {
                Console.WriteLine($"  Warning: {errorCount} files failed");
            }
        }

        private static void ProcessDumpFile(
            EndfieldVfsLoader loader,
            EndfieldVfsBlockType blockType,
            EndfieldVfsChunkInfo chunk,
            EndfieldVfsFileInfo file,
            string output)
        {
            if (string.IsNullOrEmpty(file.FileName) || file.FileName.EndsWith("/") || file.FileName.EndsWith("\\"))
            {
                return;
            }

            string outputPath;
            if (blockType == EndfieldVfsBlockType.Table)
            {
                var data = loader.ExtractFileToBytes(blockType, chunk, file);
                outputPath = EndfieldDumpProcessors.ProcessTableFile(data, output);
            }
            else if (blockType == EndfieldVfsBlockType.Lua)
            {
                var data = loader.ExtractFileToBytes(blockType, chunk, file);
                outputPath = EndfieldDumpProcessors.ProcessLuaFile(data, file.FileName, output);
            }
            else if (blockType == EndfieldVfsBlockType.Video || blockType == EndfieldVfsBlockType.AuditVideo)
            {
                var data = loader.ExtractFileToBytes(blockType, chunk, file);
                outputPath = EndfieldDumpProcessors.ProcessVideoFile(data, file.FileName, output);
            }
            else
            {
                outputPath = Path.Combine(output, file.FileName);
                var parent = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
                loader.ExtractFile(blockType, chunk, file, stream);
            }
            if (!File.Exists(outputPath))
            {
                throw new IOException($"Failed to create output file: {outputPath}");
            }
        }

        private static void RunVfsIndex(VfsOptions options)
        {
            var loader = new EndfieldVfsLoader(options.StreamingAssets, options.FallbackAssets);
            var blocks = new JArray();
            var flatFiles = new JArray();
            var missingBlocks = new JArray();
            var totalChunks = 0;
            var totalFiles = 0;
            var totalBytes = 0L;
            var missingChunks = 0;

            foreach (var blockType in options.SelectedBlockTypes())
            {
                Console.WriteLine($"Indexing {blockType.GetName()} metadata...");
                var blockDirName = loader.BlockDirectoryName(blockType);
                EndfieldVfsBlockMainInfo blockInfo;
                try
                {
                    blockInfo = loader.LoadBlockInfo(blockType);
                }
                catch (EndfieldVfsBlockNotFoundException e)
                {
                    Console.WriteLine($"  Warning: Block {e.HashDirectory} not found, skipping");
                    missingBlocks.Add(new JObject
                    {
                        ["name"] = blockType.GetName(),
                        ["hashDirectory"] = blockDirName,
                    });
                    continue;
                }

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

                    var files = new JArray();
                    foreach (var file in chunk.Files)
                    {
                        files.Add(new JObject
                        {
                            ["blockType"] = file.BlockType.GetName(),
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
                            ["fileBlockType"] = file.BlockType.GetName(),
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
                    }

                    var chunkFileCount = files.Count;
                    var chunkByteCount = chunk.Files.Sum(file => file.Length);
                    blockFileCount += chunkFileCount;
                    blockByteCount += chunkByteCount;

                    chunkValues.Add(new JObject
                    {
                        ["absolutePath"] = chunkAbsolutePath == null ? JValue.CreateNull() : chunkAbsolutePath,
                        ["blockType"] = chunk.BlockType.GetName(),
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
                    ["blockType"] = blockInfo.BlockType.GetName(),
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
            Console.WriteLine($"  Done: indexed {totalFiles} files across {totalChunks} chunks -> {options.Output}");
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
                BlockFilterName = "All",
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
                            options.BlockType = null;
                            options.BlockFilterName = "All";
                        }
                        else if (EndfieldVfsBlockTypes.TryParseCliValue(rawBlock, out var blockType))
                        {
                            options.BlockType = blockType;
                            options.BlockFilterName = blockType.GetName();
                        }
                        else
                        {
                            throw new ArgumentException($"invalid block type: {rawBlock}");
                        }
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
            switch (command.ToLowerInvariant())
            {
                case "dump":
                    Console.WriteLine("Usage: AnimeStudio.CLI dump -s <StreamingAssets> [-o <output>] [-b <block-type>] [--fallback-assets <StreamingAssets>]");
                    break;
                case "vfs-index":
                case "vfsindex":
                    Console.WriteLine("Usage: AnimeStudio.CLI vfs-index -s <StreamingAssets> [-o <output.json>] [-b <block-type>] [--fallback-assets <StreamingAssets>]");
                    break;
                case "audio":
                    Console.WriteLine("Usage: AnimeStudio.CLI audio -s <StreamingAssets> [-o <output>] [-l <language>] [-f <wem|wav>] [-b <block>] [--fallback-assets <StreamingAssets>]");
                    break;
            }
        }

        private sealed class VfsOptions
        {
            public string StreamingAssets { get; set; }
            public string FallbackAssets { get; set; }
            public string Output { get; set; }
            public EndfieldVfsBlockType? BlockType { get; set; }
            public string BlockFilterName { get; set; }

            public IEnumerable<EndfieldVfsBlockType> SelectedBlockTypes() =>
                BlockType.HasValue ? new[] { BlockType.Value } : EndfieldVfsBlockTypes.AllDumpable;
        }

        private sealed class HelpRequestedException : Exception
        {
        }
    }
}
