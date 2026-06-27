using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
            "stream",
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
                    case "stream":
                        RunStream(ParseVfsOptions(args, ""));
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
            foreach (var selectedFile in EnumerateSelectedFiles(
                loader,
                options,
                (_, e) => Console.Error.WriteLine($"Warning: Block {e.HashDirectory} not found, skipping")))
            {
                try
                {
                    var data = loader.ExtractFileToBytes(selectedFile.BlockType, selectedFile.Chunk, selectedFile.File);
                    var payload = new JObject
                    {
                        ["blockType"] = selectedFile.BlockType.GetName(),
                        ["fileName"] = selectedFile.File.FileName,
                        ["length"] = data.Length,
                        ["dataBase64"] = Convert.ToBase64String(data),
                    };
                    Console.Out.WriteLine(payload.ToString(Formatting.None));
                    emittedCount++;
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"Error: Failed to stream {selectedFile.File.FileName}: {e.Message}");
                }
            }
            Console.Error.WriteLine($"Streamed {emittedCount} files");
        }

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

        private static int CountSelectedFiles(VfsOptions options, EndfieldVfsBlockMainInfo blockInfo) =>
            blockInfo.Chunks.Sum(chunk => SelectedFiles(options, chunk).Count());

        private static void DumpBlock(EndfieldVfsLoader loader, VfsBlockSelection block, string output, VfsOptions options)
        {
            var blockType = block.BlockType;
            var blockInfo = block.Info;

            var successCount = 0;
            var errorCount = 0;
            var totalFiles = CountSelectedFiles(options, blockInfo);

            foreach (var chunk in blockInfo.Chunks)
            {
                Parallel.ForEach(SelectedFiles(options, chunk), file =>
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
            string output)
        {
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

                    var files = new JArray();
                    var chunkFileCount = 0;
                    var chunkByteCount = 0L;
                    foreach (var file in SelectedFiles(options, chunk))
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
                        chunkFileCount++;
                        chunkByteCount += file.Length;
                    }

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
            const string blockTypeValues = "[default: all] [possible values: all, initial-audio, initial-bundle, initial-extend-data, bundle-manifest, i-fix-patch, audit-streaming, audit-dynamic-streaming, audit-iv, audit-audio, audit-video, bundle, audio, video, iv, streaming, dynamic-streaming, lua, table, json-data, extend-data, hotfix-audio, audio-chinese, audio-english, audio-japanese, audio-korean]";

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
                        "  -l, --language <LANGUAGE>",
                        "          [default: all] [possible values: all, chinese, english, japanese, korean]",
                        "  -f, --format <FORMAT>",
                        "          [default: wav] [possible values: wem, wav]",
                        "  -b, --block <BLOCK>",
                        "          [default: all] [possible values: all, voice, audio, initial-audio, audit-audio]",
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
