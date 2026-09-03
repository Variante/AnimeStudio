using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Collections.Generic;

namespace AnimeStudio.Endfield
{
    public sealed class EndfieldVfsLoader
    {
        public const string VfsDirectoryName = "VFS";
        public const int VfsProtoVersion = 3;
        private const int BlockHeadLength = 12;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private readonly string vfsPath;
        private readonly string fallbackVfsPath;
        private readonly ConcurrentDictionary<string, Lazy<ChunkDigest>> chunkDigests = new(StringComparer.OrdinalIgnoreCase);

        public EndfieldVfsLoader(string streamingAssetsPath, string fallbackAssetsPath = null)
        {
            vfsPath = Path.Combine(streamingAssetsPath, VfsDirectoryName);
            if (!string.IsNullOrEmpty(fallbackAssetsPath))
            {
                var candidate = Path.Combine(fallbackAssetsPath, VfsDirectoryName);
                if (Directory.Exists(candidate))
                {
                    fallbackVfsPath = candidate;
                }
            }
        }

        public string BlockDirectoryName(EndfieldVfsBlockType blockType) =>
            EndfieldVfsHash.VfsBlockHash(blockType.GetName(), EndfieldVfsKeys.UnityHashSecret);

        public EndfieldVfsBlockMainInfo LoadBlockInfo(EndfieldVfsBlockType blockType)
        {
            var blockDirName = BlockDirectoryName(blockType);
            var blockFileName = $"{blockDirName}.blc";
            var primaryBlockDirectory = Path.Combine(vfsPath, blockDirName);
            var blockFilePath = Path.Combine(primaryBlockDirectory, blockFileName);
            var fallbackBlockDirectory = string.IsNullOrEmpty(fallbackVfsPath)
                ? null
                : Path.Combine(fallbackVfsPath, blockDirName);
            if (!File.Exists(blockFilePath) && !string.IsNullOrEmpty(fallbackVfsPath))
            {
                var fallbackBlockFilePath = Path.Combine(fallbackBlockDirectory, blockFileName);
                if (File.Exists(fallbackBlockFilePath))
                {
                    blockFilePath = fallbackBlockFilePath;
                }
            }

            if (!File.Exists(blockFilePath))
            {
                if (Directory.Exists(primaryBlockDirectory)
                    || (!string.IsNullOrEmpty(fallbackBlockDirectory) && Directory.Exists(fallbackBlockDirectory)))
                {
                    throw new EndfieldVfsException($"block metadata file not found: {blockFileName}");
                }
                throw new EndfieldVfsBlockNotFoundException(blockDirName);
            }

            return LoadBlockInfoFromMetadataPath(blockFilePath);
        }

        internal EndfieldVfsBlockMainInfo LoadBlockInfoFromMetadataPath(string blockFilePath)
        {
            var blockData = File.ReadAllBytes(blockFilePath);
            if (blockData.Length < BlockHeadLength)
            {
                throw new EndfieldVfsException("invalid block data: block file too short");
            }

            var nonce = blockData.AsSpan(0, BlockHeadLength).ToArray();
            var decrypted = blockData.AsSpan(BlockHeadLength).ToArray();
            var cipher = new EndfieldChaCha20(EndfieldVfsKeys.ChaChaKey, nonce, 1);
            cipher.ApplyKeystream(decrypted);

            return ParseBlockInfo(decrypted, true);
        }

        public IReadOnlyList<EndfieldVfsCatalogEntry> DiscoverCatalog()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddDirectoryNames(vfsPath, names);
            AddDirectoryNames(fallbackVfsPath, names);

            var entries = new List<EndfieldVfsCatalogEntry>();
            foreach (var hashDirectory in names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                var primaryDirectory = Path.Combine(vfsPath, hashDirectory);
                var fallbackDirectory = string.IsNullOrEmpty(fallbackVfsPath)
                    ? null
                    : Path.Combine(fallbackVfsPath, hashDirectory);
                var entry = new EndfieldVfsCatalogEntry
                {
                    HashDirectory = hashDirectory,
                    PrimaryDirectoryPresent = Directory.Exists(primaryDirectory),
                    FallbackDirectoryPresent = !string.IsNullOrEmpty(fallbackDirectory) && Directory.Exists(fallbackDirectory),
                };
                entry.PrimaryMetadataPath = FindMetadataPath(primaryDirectory, hashDirectory, out var primaryMetadataIssue);
                entry.FallbackMetadataPath = FindMetadataPath(fallbackDirectory, hashDirectory, out var fallbackMetadataIssue);
                if (!string.IsNullOrEmpty(primaryMetadataIssue))
                {
                    entry.PrimaryError = primaryMetadataIssue;
                }
                if (!string.IsNullOrEmpty(fallbackMetadataIssue))
                {
                    entry.FallbackError = fallbackMetadataIssue;
                }
                if (entry.PrimaryMetadataPath != null)
                {
                    try
                    {
                        entry.PrimaryInfo = LoadBlockInfoFromMetadataPath(entry.PrimaryMetadataPath);
                    }
                    catch (Exception exception)
                    {
                        entry.PrimaryError = BoundCatalogError(exception.Message);
                    }
                }
                if (entry.FallbackMetadataPath != null)
                {
                    try
                    {
                        entry.FallbackInfo = LoadBlockInfoFromMetadataPath(entry.FallbackMetadataPath);
                    }
                    catch (Exception exception)
                    {
                        entry.FallbackError = BoundCatalogError(exception.Message);
                    }
                }
                ClassifyCatalogEntry(entry);
                entries.Add(entry);
            }
            return entries;
        }

        public string ResolveChunkPath(EndfieldVfsCatalogEntry entry, EndfieldVfsChunkInfo chunk)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }
            var roots = entry.CanonicalIsPrimary
                ? new[] { vfsPath, fallbackVfsPath }
                : new[] { fallbackVfsPath };
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }
                var path = Path.Combine(root, entry.HashDirectory, chunk.FileName);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            throw new EndfieldVfsChunkNotFoundException(chunk.FileName);
        }

        private static void AddDirectoryNames(string root, HashSet<string> names)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return;
            }
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                names.Add(Path.GetFileName(directory));
            }
        }

        private static string FindMetadataPath(string directory, string hashDirectory, out string issue)
        {
            issue = null;
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return null;
            }
            var files = Directory.EnumerateFiles(directory, "*.blc", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
            {
                issue = "metadata .blc file is missing";
                return null;
            }
            var exact = files.FirstOrDefault(path => string.Equals(
                Path.GetFileNameWithoutExtension(path), hashDirectory, StringComparison.OrdinalIgnoreCase));
            if (files.Count > 1)
            {
                issue = $"multiple metadata .blc files found ({files.Count})";
            }
            return exact ?? files[0];
        }

        private static void ClassifyCatalogEntry(EndfieldVfsCatalogEntry entry)
        {
            var primaryPresent = entry.PrimaryInfo != null;
            var fallbackPresent = entry.FallbackInfo != null;
            if (!primaryPresent && !fallbackPresent)
            {
                entry.State = EndfieldVfsCatalogState.MissingMetadata;
                entry.CanonicalIsPrimary = entry.PrimaryDirectoryPresent;
                return;
            }
            if (primaryPresent && !fallbackPresent)
            {
                entry.State = entry.FallbackMetadataPath != null && !string.IsNullOrEmpty(entry.FallbackError)
                    ? EndfieldVfsCatalogState.MissingMetadata
                    : EndfieldVfsCatalogState.PrimaryOnly;
                entry.CanonicalInfo = entry.PrimaryInfo;
                entry.CanonicalIsPrimary = true;
                return;
            }
            if (!primaryPresent)
            {
                entry.State = entry.PrimaryMetadataPath != null && !string.IsNullOrEmpty(entry.PrimaryError)
                    ? EndfieldVfsCatalogState.MissingMetadata
                    : EndfieldVfsCatalogState.FallbackOnly;
                entry.CanonicalInfo = entry.FallbackInfo;
                entry.CanonicalIsPrimary = false;
                return;
            }

            entry.CanonicalInfo = entry.PrimaryInfo;
            entry.CanonicalIsPrimary = true;
            var primaryBytes = ReadDecryptedMetadata(entry.PrimaryMetadataPath);
            var fallbackBytes = ReadDecryptedMetadata(entry.FallbackMetadataPath);
            if (primaryBytes.AsSpan().SequenceEqual(fallbackBytes))
            {
                entry.State = EndfieldVfsCatalogState.Identical;
            }
            else if (IsEmpty(entry.PrimaryInfo) && !IsEmpty(entry.FallbackInfo))
            {
                entry.State = EndfieldVfsCatalogState.ShadowedEmpty;
            }
            else if (!IsEmpty(entry.PrimaryInfo) && IsEmpty(entry.FallbackInfo))
            {
                entry.State = EndfieldVfsCatalogState.Replaced;
            }
            else if (IsOrderedReplacement(entry.PrimaryInfo, entry.FallbackInfo))
            {
                entry.State = EndfieldVfsCatalogState.Replaced;
            }
            else
            {
                entry.State = EndfieldVfsCatalogState.Conflicting;
            }
        }

        internal static byte[] ReadDecryptedMetadata(string path)
        {
            var blockData = File.ReadAllBytes(path);
            if (blockData.Length < BlockHeadLength)
            {
                throw new EndfieldVfsException("invalid block data: block file too short");
            }
            var nonce = blockData.AsSpan(0, BlockHeadLength);
            var decrypted = blockData.AsSpan(BlockHeadLength).ToArray();
            var cipher = new EndfieldChaCha20(EndfieldVfsKeys.ChaChaKey, nonce, 1);
            cipher.ApplyKeystream(decrypted);
            return decrypted;
        }

        private static bool IsOrderedReplacement(EndfieldVfsBlockMainInfo primary, EndfieldVfsBlockMainInfo fallback) =>
            primary.Version > fallback.Version
            && primary.BlockTypeValue == fallback.BlockTypeValue
            && string.Equals(primary.GroupConfigName, fallback.GroupConfigName, StringComparison.Ordinal)
            && primary.GroupConfigHashName == fallback.GroupConfigHashName;

        private static bool IsEmpty(EndfieldVfsBlockMainInfo info) =>
            info.GroupFileInfoNum == 0 && info.GroupChunksLength == 0 && info.Chunks.Count == 0;

        private static string BoundCatalogError(string message) =>
            string.IsNullOrEmpty(message) ? "metadata parse failed" : message.Length <= 240 ? message : message[..240];

        public string ResolveChunkPath(EndfieldVfsBlockType blockType, EndfieldVfsChunkInfo chunk)
        {
            var blockDirName = BlockDirectoryName(blockType);
            var chunkName = chunk.FileName;
            var primaryPath = Path.Combine(vfsPath, blockDirName, chunkName);
            if (File.Exists(primaryPath))
            {
                return primaryPath;
            }

            if (!string.IsNullOrEmpty(fallbackVfsPath))
            {
                var fallbackPath = Path.Combine(fallbackVfsPath, blockDirName, chunkName);
                if (File.Exists(fallbackPath))
                {
                    return fallbackPath;
                }
            }

            throw new EndfieldVfsChunkNotFoundException(chunkName);
        }

        public byte[] ExtractFileToBytes(
            EndfieldVfsBlockType blockType,
            EndfieldVfsChunkInfo chunk,
            EndfieldVfsFileInfo file,
            bool verifyMd5 = false)
        {
            if (file.Length < 0 || file.Length > int.MaxValue)
            {
                throw new EndfieldVfsException($"invalid file length: {file.Length}");
            }

            using var output = new MemoryStream((int)file.Length);
            ExtractFile(blockType, chunk, file, output, verifyMd5);
            return output.ToArray();
        }

        public long ExtractFile(
            EndfieldVfsBlockType blockType,
            EndfieldVfsChunkInfo chunk,
            EndfieldVfsFileInfo file,
            Stream output,
            bool verifyMd5 = false)
        {
            ValidateFileRange(chunk, file);
            if (verifyMd5)
            {
                VerifyChunkContentMd5(blockType, chunk);
            }
            var chunkPath = ResolveChunkPath(blockType, chunk);
            using var input = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            if (input.Length != chunk.Length)
            {
                throw new EndfieldVfsException(
                    $"chunk length mismatch for {chunk.FileName}: metadata {chunk.Length}, actual {input.Length}");
            }
            input.Seek(file.Offset, SeekOrigin.Begin);

            EndfieldChaCha20 cipher = null;
            if (file.UseEncrypt)
            {
                Span<byte> nonce = stackalloc byte[12];
                BinaryPrimitives.WriteInt32LittleEndian(nonce[..4], VfsProtoVersion);
                BinaryPrimitives.WriteInt64LittleEndian(nonce[4..], file.IvSeed);
                cipher = new EndfieldChaCha20(EndfieldVfsKeys.ChaChaKey, nonce, 1);
            }

            using var dataMd5 = verifyMd5 ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
            var written = CopyRange(input, output, file.Length, cipher, dataMd5);
            if (verifyMd5)
            {
                VerifyDigest(
                    $"file DataMd5 for {file.FileName}",
                    file.FileDataMd5,
                    dataMd5.GetHashAndReset());
            }
            return written;
        }

        public void VerifyChunkContentMd5(EndfieldVfsBlockType blockType, EndfieldVfsChunkInfo chunk)
        {
            if (chunk == null)
            {
                throw new ArgumentNullException(nameof(chunk));
            }

            var chunkPath = ResolveChunkPath(blockType, chunk);
            var digest = chunkDigests.GetOrAdd(
                chunkPath,
                path => new Lazy<ChunkDigest>(
                    () => ComputeChunkDigest(path),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            if (digest.Length != chunk.Length)
            {
                throw new EndfieldVfsException(
                    $"chunk length mismatch for {chunk.FileName}: metadata {chunk.Length}, actual {digest.Length}");
            }

            VerifyDigest($"chunk ContentMd5 for {chunk.FileName}", chunk.ContentMd5, digest.Md5);
        }

        public string StreamingAssetsPath => Directory.GetParent(vfsPath)?.FullName ?? vfsPath;

        public string FallbackAssetsPath => string.IsNullOrEmpty(fallbackVfsPath)
            ? null
            : Directory.GetParent(fallbackVfsPath)?.FullName ?? fallbackVfsPath;

        internal static long CopyRange(
            Stream input,
            Stream output,
            long length,
            EndfieldChaCha20 cipher,
            IncrementalHash hash = null)
        {
            if (length < 0)
            {
                throw new EndfieldVfsException($"invalid file length: {length}");
            }

            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            var remaining = length;
            var written = 0L;
            try
            {
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = input.Read(buffer, 0, toRead);
                    if (read == 0)
                    {
                        throw new EndfieldVfsException(
                            $"short VFS range read: expected {length} bytes, received {written}");
                    }

                    var span = buffer.AsSpan(0, read);
                    cipher?.ApplyKeystream(span);
                    hash?.AppendData(span);
                    output.Write(span);
                    remaining -= read;
                    written += read;
                }
                if (written != length)
                {
                    throw new EndfieldVfsException(
                        $"short VFS range read: expected {length} bytes, received {written}");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, true);
            }
            return written;
        }

        internal static EndfieldVfsBlockMainInfo ParseBlockInfo(byte[] data, bool verifyCrc)
        {
            try
            {
                return ParseBlockInfoCore(data, verifyCrc);
            }
            catch (EndfieldVfsException)
            {
                throw;
            }
            catch (EndOfStreamException exception)
            {
                throw new EndfieldVfsException("invalid block data: truncated metadata", exception);
            }
        }

        private static EndfieldVfsBlockMainInfo ParseBlockInfoCore(byte[] data, bool verifyCrc)
        {
            if (data.Length < 4)
            {
                throw new EndfieldVfsException("invalid block data: data too short");
            }

            var dataLength = data.Length - 4;
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(dataLength, 4));
            var actualCrc = EndfieldCrc32.Compute(data.AsSpan(0, dataLength));
            if (verifyCrc && expectedCrc != actualCrc)
            {
                throw new EndfieldVfsException($"CRC mismatch: expected 0x{expectedCrc:X8}, got 0x{actualCrc:X8}");
            }

            using var stream = new MemoryStream(data, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, false);

            var rawVersion = reader.ReadInt32();
            int codeVersion;
            int version;
            if (rawVersion < 11)
            {
                version = reader.ReadInt32();
                codeVersion = rawVersion;
            }
            else
            {
                codeVersion = 3;
                version = rawVersion;
            }
            if (codeVersion < 1 || codeVersion > 4)
            {
                throw new EndfieldVfsException($"invalid block data: unsupported code version {codeVersion}");
            }

            var groupConfigName = ReadString(reader, reader.ReadUInt16());
            var groupConfigHashName = reader.ReadInt64();
            var groupFileInfoNum = reader.ReadInt32();
            var groupChunksLength = reader.ReadInt64();
            var blockTypeValue = reader.ReadByte();
            if (groupFileInfoNum < 0)
            {
                throw new EndfieldVfsException($"invalid block data: negative group_file_info_num {groupFileInfoNum}");
            }
            if (groupChunksLength < 0)
            {
                throw new EndfieldVfsException($"invalid block data: negative group_chunks_length {groupChunksLength}");
            }

            var block = new EndfieldVfsBlockMainInfo
            {
                Version = version,
                CodeVersion = codeVersion,
                GroupConfigName = groupConfigName,
                GroupConfigHashName = groupConfigHashName,
                GroupFileInfoNum = groupFileInfoNum,
                GroupChunksLength = groupChunksLength,
                BlockType = EndfieldVfsBlockTypes.FromByte(blockTypeValue),
                BlockTypeValue = blockTypeValue,
                MetadataCrc32Declared = expectedCrc,
                MetadataCrc32Recomputed = actualCrc,
            };

            var parsedFileCount = 0L;
            var summedChunkLength = 0L;
            var virtualFileNames = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            var chunkCount = ReadCount(reader, "chunk_count", 45 + (codeVersion > 3 ? 4 : 0));
            for (var i = 0; i < chunkCount; i++)
            {
                var chunkMd5Name = ReadUInt128LittleEndian(reader);
                var chunkContentMd5 = ReadUInt128LittleEndian(reader);
                var chunkLength = reader.ReadInt64();
                var chunkTypeValue = reader.ReadByte();
                var chunk = new EndfieldVfsChunkInfo
                {
                    Md5Name = chunkMd5Name,
                    ContentMd5 = chunkContentMd5,
                    Length = chunkLength,
                    BlockType = EndfieldVfsBlockTypes.FromByte(chunkTypeValue),
                    BlockTypeValue = chunkTypeValue,
                    MainTag = EndfieldVfsFileTag.None,
                };
                if (chunk.Length < 0)
                {
                    throw new EndfieldVfsException($"invalid block data: negative chunk length {chunk.Length}");
                }
                if (chunkTypeValue != blockTypeValue)
                {
                    throw new EndfieldVfsException(
                        $"invalid block data: chunk {i} type {chunkTypeValue} does not match block type {blockTypeValue}");
                }
                if (chunk.Length > long.MaxValue - summedChunkLength)
                {
                    throw new EndfieldVfsException(
                        $"invalid block data: declared chunk length sum overflow at chunk {i}");
                }
                summedChunkLength += chunk.Length;

                if (codeVersion > 3)
                {
                    chunk.MainTag = EndfieldVfsBlockTypes.FileTagFromByte((byte)reader.ReadInt32());
                }

                var fileCount = ReadCount(reader, "file_count", 60);
                for (var j = 0; j < fileCount; j++)
                {
                    var fileNameLength = reader.ReadUInt16();
                    var fileName = ReadString(reader, fileNameLength);
                    var fileNameHash = reader.ReadInt64();
                    var fileChunkMd5 = ReadUInt128LittleEndian(reader);
                    var fileDataMd5 = ReadUInt128LittleEndian(reader);
                    var fileOffset = reader.ReadInt64();
                    var fileLength = reader.ReadInt64();
                    var fileTypeValue = reader.ReadByte();
                    var file = new EndfieldVfsFileInfo
                    {
                        FileName = fileName,
                        FileNameHash = fileNameHash,
                        FileChunkMd5 = fileChunkMd5,
                        FileDataMd5 = fileDataMd5,
                        Offset = fileOffset,
                        Length = fileLength,
                        BlockType = EndfieldVfsBlockTypes.FromByte(fileTypeValue),
                        BlockTypeValue = fileTypeValue,
                        UseEncrypt = reader.ReadByte() != 0,
                    };
                    if (file.Offset < 0 || file.Length < 0)
                    {
                        throw new EndfieldVfsException(
                            $"invalid block data: negative file range for {file.FileName} (offset {file.Offset}, length {file.Length})");
                    }
                    if (fileTypeValue != blockTypeValue)
                    {
                        throw new EndfieldVfsException(
                            $"invalid block data: file {file.FileName} type {fileTypeValue} does not match block type {blockTypeValue}");
                    }
                    if (!virtualFileNames.Add(fileName))
                    {
                        throw new EndfieldVfsException(
                            $"invalid block data: duplicate virtual filename in block: {fileName}");
                    }
                    parsedFileCount++;

                    if (file.UseEncrypt)
                    {
                        file.IvSeed = reader.ReadInt64();
                    }

                    if (codeVersion > 3)
                    {
                        file.FileTag = EndfieldVfsBlockTypes.FileTagFromByte((byte)reader.ReadInt32());
                    }

                    chunk.Files.Add(file);
                }

                ValidateChunkRanges(chunk);

                block.Chunks.Add(chunk);
            }

            if (parsedFileCount != groupFileInfoNum)
            {
                throw new EndfieldVfsException(
                    $"invalid block data: group_file_info_num {groupFileInfoNum} does not match parsed file count {parsedFileCount}");
            }
            if (summedChunkLength != groupChunksLength)
            {
                throw new EndfieldVfsException(
                    $"invalid block data: group_chunks_length {groupChunksLength} does not match declared chunk length sum {summedChunkLength}");
            }
            if (verifyCrc && reader.BaseStream.Position != data.Length - 4)
            {
                var remaining = data.AsSpan((int)reader.BaseStream.Position, data.Length - 4 - (int)reader.BaseStream.Position);
                if (remaining.Length != 10)
                {
                    throw new EndfieldVfsException(
                        $"invalid block data: unconsumed metadata bytes before CRC ({remaining.Length}): {Convert.ToHexString(remaining)}");
                }
                block.MetadataTrailer = remaining.ToArray();
                reader.BaseStream.Seek(10, SeekOrigin.Current);
            }

            return block;
        }

        private static int ReadCount(BinaryReader reader, string fieldName, int minimumBytesPerItem = 0)
        {
            var count = reader.ReadInt32();
            if (count < 0)
            {
                throw new EndfieldVfsException($"invalid block data: negative {fieldName} {count}");
            }
            if (minimumBytesPerItem > 0)
            {
                var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
                if (count > remaining / minimumBytesPerItem)
                {
                    throw new EndfieldVfsException(
                        $"invalid block data: {fieldName} count {count} exceeds remaining-byte bound {remaining / minimumBytesPerItem}");
                }
            }
            return count;
        }

        private static void ValidateChunkRanges(EndfieldVfsChunkInfo chunk)
        {
            var ranges = chunk.Files
                .OrderBy(file => file.Offset)
                .ThenBy(file => file.Length)
                .ToList();
            var previousEnd = 0L;
            var hasPrevious = false;
            foreach (var file in ranges)
            {
                if (file.Offset > chunk.Length || file.Length > chunk.Length - file.Offset)
                {
                    throw new EndfieldVfsException(
                        $"invalid file range for {file.FileName}: offset {file.Offset}, length {file.Length}, chunk length {chunk.Length}");
                }
                if (hasPrevious && file.Offset < previousEnd)
                {
                    throw new EndfieldVfsException(
                        $"overlapping file ranges in {chunk.FileName} at offset {file.Offset}");
                }
                previousEnd = file.Offset + file.Length;
                hasPrevious = true;
            }
        }

        private static void ValidateFileRange(EndfieldVfsChunkInfo chunk, EndfieldVfsFileInfo file)
        {
            if (chunk == null || file == null)
            {
                throw new ArgumentNullException(chunk == null ? nameof(chunk) : nameof(file));
            }
            if (chunk.Length < 0 || file.Offset < 0 || file.Length < 0
                || file.Offset > chunk.Length || file.Length > chunk.Length - file.Offset)
            {
                throw new EndfieldVfsException(
                    $"invalid file range for {file.FileName}: offset {file.Offset}, length {file.Length}, chunk length {chunk.Length}");
            }
        }

        private static ChunkDigest ComputeChunkDigest(string path)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            using var md5 = MD5.Create();
            return new ChunkDigest(input.Length, md5.ComputeHash(input));
        }

        private static void VerifyDigest(string fieldName, UInt128 expected, byte[] actual)
        {
            Span<byte> expectedBytes = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(expectedBytes[..8], (ulong)expected);
            BinaryPrimitives.WriteUInt64LittleEndian(expectedBytes[8..], (ulong)(expected >> 64));
            if (!expectedBytes.SequenceEqual(actual))
            {
                throw new EndfieldVfsException(
                    $"{fieldName} mismatch: expected {Convert.ToHexString(expectedBytes)}, actual {Convert.ToHexString(actual)}");
            }
        }

        private readonly struct ChunkDigest
        {
            public ChunkDigest(long length, byte[] md5)
            {
                Length = length;
                Md5 = md5;
            }

            public long Length { get; }
            public byte[] Md5 { get; }
        }

        private static string ReadString(BinaryReader reader, int length)
        {
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException();
            }
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new EndfieldVfsException(
                    $"invalid UTF-8 metadata string of {length} bytes", exception);
            }
        }

        private static UInt128 ReadUInt128LittleEndian(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(16);
            if (bytes.Length != 16)
            {
                throw new EndOfStreamException();
            }

            var low = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0, 8));
            var high = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8, 8));
            return ((UInt128)high << 64) | low;
        }
    }
}
