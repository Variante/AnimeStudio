using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace AnimeStudio.Endfield
{
    public sealed class EndfieldVfsLoader
    {
        public const string VfsDirectoryName = "VFS";
        public const int VfsProtoVersion = 3;
        private const int BlockHeadLength = 12;

        private readonly string vfsPath;
        private readonly string fallbackVfsPath;

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
            var blockDir = Path.Combine(vfsPath, blockDirName);
            if (!Directory.Exists(blockDir))
            {
                throw new EndfieldVfsBlockNotFoundException(blockDirName);
            }

            var blockFilePath = Path.Combine(blockDir, $"{blockDirName}.blc");
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

        public byte[] ExtractFileToBytes(EndfieldVfsBlockType blockType, EndfieldVfsChunkInfo chunk, EndfieldVfsFileInfo file)
        {
            if (file.Length < 0 || file.Length > int.MaxValue)
            {
                throw new EndfieldVfsException($"invalid file length: {file.Length}");
            }

            using var output = new MemoryStream((int)file.Length);
            ExtractFile(blockType, chunk, file, output);
            return output.ToArray();
        }

        public long ExtractFile(EndfieldVfsBlockType blockType, EndfieldVfsChunkInfo chunk, EndfieldVfsFileInfo file, Stream output)
        {
            var chunkPath = ResolveChunkPath(blockType, chunk);
            using var input = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            input.Seek(file.Offset, SeekOrigin.Begin);

            EndfieldChaCha20 cipher = null;
            if (file.UseEncrypt)
            {
                Span<byte> nonce = stackalloc byte[12];
                BinaryPrimitives.WriteInt32LittleEndian(nonce[..4], VfsProtoVersion);
                BinaryPrimitives.WriteInt64LittleEndian(nonce[4..], file.IvSeed);
                cipher = new EndfieldChaCha20(EndfieldVfsKeys.ChaChaKey, nonce, 1);
            }

            return CopyRange(input, output, file.Length, cipher);
        }

        public string StreamingAssetsPath => Directory.GetParent(vfsPath)?.FullName ?? vfsPath;

        public string FallbackAssetsPath => string.IsNullOrEmpty(fallbackVfsPath)
            ? null
            : Directory.GetParent(fallbackVfsPath)?.FullName ?? fallbackVfsPath;

        private static long CopyRange(Stream input, Stream output, long length, EndfieldChaCha20 cipher)
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
                        break;
                    }

                    var span = buffer.AsSpan(0, read);
                    cipher?.ApplyKeystream(span);
                    output.Write(span);
                    remaining -= read;
                    written += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, true);
            }
            return written;
        }

        private static EndfieldVfsBlockMainInfo ParseBlockInfo(byte[] data, bool verifyCrc)
        {
            if (data.Length < 4)
            {
                throw new EndfieldVfsException("invalid block data: data too short");
            }

            if (verifyCrc)
            {
                var dataLength = data.Length - 4;
                var expected = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(dataLength, 4));
                var actual = unchecked((int)EndfieldCrc32.Compute(data.AsSpan(0, dataLength)));
                if (expected != actual)
                {
                    throw new EndfieldVfsException($"CRC mismatch: expected 0x{expected:X8}, got 0x{actual:X8}");
                }
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

            var block = new EndfieldVfsBlockMainInfo
            {
                Version = version,
                CodeVersion = codeVersion,
                GroupConfigName = ReadString(reader, reader.ReadUInt16()),
                GroupConfigHashName = reader.ReadInt64(),
                GroupFileInfoNum = reader.ReadInt32(),
                GroupChunksLength = reader.ReadInt64(),
                BlockType = EndfieldVfsBlockTypes.FromByte(reader.ReadByte()),
            };

            var chunkCount = ReadCount(reader, "chunk_count");
            for (var i = 0; i < chunkCount; i++)
            {
                var chunk = new EndfieldVfsChunkInfo
                {
                    Md5Name = ReadUInt128LittleEndian(reader),
                    ContentMd5 = ReadUInt128LittleEndian(reader),
                    Length = reader.ReadInt64(),
                    BlockType = EndfieldVfsBlockTypes.FromByte(reader.ReadByte()),
                    MainTag = EndfieldVfsFileTag.None,
                };

                if (codeVersion > 3)
                {
                    chunk.MainTag = EndfieldVfsBlockTypes.FileTagFromByte((byte)reader.ReadInt32());
                }

                var fileCount = ReadCount(reader, "file_count");
                for (var j = 0; j < fileCount; j++)
                {
                    var fileNameLength = reader.ReadUInt16();
                    var file = new EndfieldVfsFileInfo
                    {
                        FileName = ReadString(reader, fileNameLength),
                        FileNameHash = reader.ReadInt64(),
                        FileChunkMd5 = ReadUInt128LittleEndian(reader),
                        FileDataMd5 = ReadUInt128LittleEndian(reader),
                        Offset = reader.ReadInt64(),
                        Length = reader.ReadInt64(),
                        BlockType = EndfieldVfsBlockTypes.FromByte(reader.ReadByte()),
                        UseEncrypt = reader.ReadByte() != 0,
                    };

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

                block.Chunks.Add(chunk);
            }

            return block;
        }

        private static int ReadCount(BinaryReader reader, string fieldName)
        {
            var count = reader.ReadInt32();
            if (count < 0)
            {
                throw new EndfieldVfsException($"invalid block data: negative {fieldName} {count}");
            }
            return count;
        }

        private static string ReadString(BinaryReader reader, int length)
        {
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException();
            }
            return Encoding.UTF8.GetString(bytes);
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
