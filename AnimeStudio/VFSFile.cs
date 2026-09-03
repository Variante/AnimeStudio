using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnimeStudio
{
    public class VFSFile
    {
        private List<BundleFile.StorageBlock> m_BlocksInfo;
        private List<BundleFile.Node> m_DirectoryInfo;

        public BundleFile.Header m_Header;
        public List<StreamFile> fileList;
        public long Offset;
        /// <summary>
        /// The decoded storage-block table.  A successful VFSFile construction
        /// means this table and the directory have passed the structural gates;
        /// consumers must not interpret the node payloads as Unity objects here.
        /// </summary>
        public IReadOnlyList<BundleFile.StorageBlock> BlocksInfo => m_BlocksInfo;
        /// <summary>The decoded directory table for this logical VFS container.</summary>
        public IReadOnlyList<BundleFile.Node> DirectoryInfo => m_DirectoryInfo;
        private const long MaxInMemoryBlockStreamSize = 64L * 1024 * 1024;

        private static int CheckedSize(uint value, string fieldName)
        {
            if (value > int.MaxValue)
            {
                throw new InvalidDataException($"{fieldName} size {value} is too large for an in-memory buffer.");
            }
            return (int)value;
        }

        private static string BoundedExceptionMessage(Exception exception)
        {
            const int maxLength = 256;
            var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
            return message.Length <= maxLength ? message : message[..maxLength] + "...";
        }

        private static void ReadExactly(FileReader reader, Span<byte> destination, string path, int blockIndex)
        {
            var totalRead = 0;
            while (totalRead < destination.Length)
            {
                var read = reader.Read(destination[totalRead..]);
                if (read <= 0)
                {
                    break;
                }
                totalRead += read;
            }

            if (totalRead != destination.Length)
            {
                throw new EndOfStreamException(
                    $"VFS bundle '{path}' block {blockIndex} compressed payload truncated: " +
                    $"expected={destination.Length}, actual={totalRead} bytes.");
            }
        }

        public VFSFile(FileReader reader, string path, GameType game)
        {
            Offset = reader.Position;
            reader.Endian = EndianType.BigEndian;


            if (!VFSUtils.IsValidHeader(reader, game))
            {
                throw new Exception("Not a VFS file / VFS version mismatch");
            }

            // read header
            reader.ReadBytes(8);
            m_Header = VFSUtils.ReadHeader(reader, game);
            Logger.Verbose($"Header : {m_Header.ToString()}");
            var availableContainerBytes = reader.Length - Offset;
            if ((m_Header.flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0
                && (m_Header.size <= 0
                    || m_Header.size > availableContainerBytes
                    || m_Header.compressedBlocksInfoSize > m_Header.size))
            {
                throw new InvalidDataException(
                    $"VFS end-positioned block-info range is invalid for '{path}': " +
                    $"decodedSizeWord={m_Header.size}, infoBytes={m_Header.compressedBlocksInfoSize}, " +
                    $"available={availableContainerBytes} bytes.");
            }

            // go to blocks info
            uint blockInfosOffset;

            if ((m_Header.flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0)
                blockInfosOffset = (uint)(m_Header.size) - m_Header.compressedBlocksInfoSize;
            else
            {
                if (m_Header.encFlags >= 7)
                    blockInfosOffset = 48;
                else
                    blockInfosOffset = 40;
            }

            reader.Position = Offset + blockInfosOffset;
            ReadBlocksInfoAndDirectory(reader, game);

            // go to data
            uint dataOffset;

            if (m_Header.encFlags >= 7)
                dataOffset = 48;
            else
                dataOffset = 40;
            if (((m_Header.flags) & ArchiveFlags.BlocksInfoAtTheEnd) == 0)
            {
                var temp = m_Header.compressedBlocksInfoSize;
                if (((m_Header.flags) & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
                    temp = (temp + 15) & 0xFFFFFFF0;
                dataOffset += temp;
            }

            reader.Position = Offset + dataOffset;

            //
            using var blocksStream = CreateBlocksStream(path);
            ReadBlocks(reader, blocksStream, game, path);
            ValidateDecodedBlockLength(blocksStream, path);
            ValidateCompressedBlockConsumption(reader, path);
            ReadFiles(blocksStream, path);
        }

        private void ReadBlocksInfoAndDirectory(FileReader reader, GameType game)
        {
            byte[] blocksInfoBytes = reader.ReadBytes(CheckedSize(m_Header.compressedBlocksInfoSize, nameof(m_Header.compressedBlocksInfoSize)));

            MemoryStream blocksInfoUncompressedStream = new MemoryStream();
            if (((int)m_Header.flags & 0x3F) != 0)
            {
                // compressed + encrypted
                VFSUtils.DecryptBlock(blocksInfoBytes, game);

                var uncompressedSize = m_Header.uncompressedBlocksInfoSize;
                var blocksInfoBytesSpan = blocksInfoBytes.AsSpan(0, blocksInfoBytes.Length);
                var uncompressedSizeInt = CheckedSize(uncompressedSize, nameof(m_Header.uncompressedBlocksInfoSize));
                var uncompressedBytes = ArrayPool<byte>.Shared.Rent(uncompressedSizeInt);

                try
                {
                    var uncompressedBytesSpan = uncompressedBytes.AsSpan(0, uncompressedSizeInt);
                    // normal LZ4
                    var numWrite = LZ4.Instance.Decompress(blocksInfoBytesSpan, uncompressedBytesSpan);

                    if (numWrite != uncompressedSize)
                    {
                        throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                    }
                    blocksInfoUncompressedStream = new MemoryStream(uncompressedBytesSpan.ToArray());
                } catch (Exception e)
                {
                    throw new IOException($"Lz4 decompression error {e.Message}");
                } finally
                {
                    ArrayPool<byte>.Shared.Return(uncompressedBytes, true);
                }
            } else
            {
                blocksInfoUncompressedStream = new MemoryStream(blocksInfoBytes);
            }

            // read
            using (var blocksInfoReader = new EndianBinaryReader(blocksInfoUncompressedStream))
            {
                reader.Endian = EndianType.BigEndian;
                m_BlocksInfo = VFSUtils.ReadBlocksInfos(blocksInfoReader, game);
                m_DirectoryInfo = VFSUtils.ReadDirectoryInfos(blocksInfoReader, game);
                if (blocksInfoReader.Remaining != 0)
                {
                    throw new InvalidDataException(
                        $"VFS block-info trailing bytes: expected=0, actual={blocksInfoReader.Remaining}.");
                }
            }
        }

        private Stream CreateBlocksStream(string path)
        {
            Stream blocksStream;
            var uncompressedSizeSum = m_BlocksInfo.Sum(x => (long)x.uncompressedSize);
            Logger.Verbose($"Total size of decompressed blocks: 0x{uncompressedSizeSum:X8}");
            if (uncompressedSizeSum > MaxInMemoryBlockStreamSize)
                blocksStream = CreateTemporaryBlockStream();
            else
                blocksStream = new MemoryStream((int)uncompressedSizeSum);
            return blocksStream;
        }

        private static FileStream CreateTemporaryBlockStream()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"AnimeStudio_{Guid.NewGuid():N}.tmp");
            return new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                1024 * 1024,
                FileOptions.DeleteOnClose | FileOptions.SequentialScan
            );
        }

        private static Stream CreateNodeStream(long size)
        {
            if (size > MaxInMemoryBlockStreamSize)
            {
                return CreateTemporaryBlockStream();
            }
            return new MemoryStream((int)size);
        }

        private void ReadBlocks(FileReader reader, Stream blocksStream, GameType game, string path)
        {
            for (var blockIndex = 0; blockIndex < m_BlocksInfo.Count; blockIndex++)
            {
                var blockInfo = m_BlocksInfo[blockIndex];
                var compressionType = (int)blockInfo.flags; // no mask
                Logger.Verbose($"Block compression type {compressionType}");

                switch (compressionType)
                {
                    case 0:
                        var size = CheckedSize(blockInfo.uncompressedSize, nameof(blockInfo.uncompressedSize));
                        var buffer = reader.ReadBytes(size);
                        blocksStream.Write(buffer);
                        break;
                    case 5:
                        var compressedSize = CheckedSize(blockInfo.compressedSize, nameof(blockInfo.compressedSize));
                        var uncompressedSize = CheckedSize(blockInfo.uncompressedSize, nameof(blockInfo.uncompressedSize));

                        var compressedBytes = ArrayPool<byte>.Shared.Rent(compressedSize);
                        var uncompressedBytes = ArrayPool<byte>.Shared.Rent(uncompressedSize);

                        var compressedBytesSpan = compressedBytes.AsSpan(0, compressedSize);
                        var uncompressedBytesSpan = uncompressedBytes.AsSpan(0, uncompressedSize);

                        var numWrite = -1;
                        try
                        {
                            try
                            {
                                ReadExactly(reader, compressedBytesSpan, path, blockIndex);

                                VFSUtils.DecryptBlock(compressedBytesSpan, game);

                                // LZ4Inv this time. Do not publish the pooled span until
                                // both decoding and the exact output-size gate pass.
                                numWrite = LZ4Inv.Instance.Decompress(compressedBytesSpan, uncompressedBytesSpan);
                                if (numWrite != uncompressedSize)
                                {
                                    throw new InvalidDataException(
                                        $"Lz4 output length mismatch: expected={uncompressedSize}, actual={numWrite} bytes.");
                                }
                            }
                            catch (Exception e)
                            {
                                var actual = numWrite >= 0 ? numWrite.ToString() : "unknown";
                                throw new IOException(
                                    $"VFS bundle '{path}' block {blockIndex} type-5 decode failed: " +
                                    $"expected={uncompressedSize}, actual={actual} bytes; detail={BoundedExceptionMessage(e)}",
                                    e);
                            }

                            // Publish only after a complete, exact decode.
                            blocksStream.Write(uncompressedBytesSpan);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(compressedBytes, true);
                            ArrayPool<byte>.Shared.Return(uncompressedBytes, true);
                        }

                        break;
                    default:
                        throw new Exception($"Unsupported block compression type {compressionType}");
                }
            }
        }

        private void ReadFiles(Stream blocksStream, string path)
        {
            Logger.Verbose($"Writing files from blocks stream...");

            var paths = new HashSet<string>(StringComparer.Ordinal);
            var ranges = new List<(long Offset, long End, string Path)>();
            for (int i = 0; i < m_DirectoryInfo.Count; i++)
            {
                var node = m_DirectoryInfo[i];
                ValidateNodePath(node.path, i, path);
                if (!paths.Add(node.path))
                {
                    throw new InvalidDataException(
                        $"VFS duplicate directory node path '{node.path}' at node {i}.");
                }
                if (node.offset < 0 || node.size < 0 || node.offset > blocksStream.Length || node.size > blocksStream.Length - node.offset)
                {
                    throw new EndOfStreamException(
                        $"VFS node {node.path} range offset={node.offset}, size={node.size} exceeds block stream length {blocksStream.Length}."
                    );
                }
                var end = checked(node.offset + node.size);
                ranges.Add((node.offset, end, node.path));
            }

            ranges.Sort((left, right) =>
            {
                var compare = left.Offset.CompareTo(right.Offset);
                return compare != 0 ? compare : left.End.CompareTo(right.End);
            });
            for (var i = 1; i < ranges.Count; i++)
            {
                var previous = ranges[i - 1];
                var current = ranges[i];
                if (current.Offset < previous.End)
                {
                    throw new InvalidDataException(
                        $"VFS overlapping directory ranges: '{previous.Path}' " +
                        $"[{previous.Offset},{previous.End}) and '{current.Path}' " +
                        $"[{current.Offset},{current.End}).");
                }
            }

            fileList = new List<StreamFile>();
            try
            {
                foreach (var node in m_DirectoryInfo)
                {
                    var file = new StreamFile
                    {
                        path = node.path,
                        fileName = Path.GetFileName(node.path),
                        stream = CreateNodeStream(node.size),
                    };
                    fileList.Add(file);
                    blocksStream.Position = node.offset;
                    CopyRange(blocksStream, file.stream, node.size, path, node.path);
                    file.stream.Position = 0;
                }
            }
            catch
            {
                foreach (var file in fileList)
                {
                    file.stream?.Dispose();
                }
                fileList.Clear();
                throw;
            }
        }

        private static void ValidateNodePath(string nodePath, int nodeIndex, string containerPath)
        {
            if (string.IsNullOrEmpty(nodePath))
            {
                throw new InvalidDataException(
                    $"VFS directory node {nodeIndex} in '{containerPath}' has an empty path.");
            }
            if (nodePath.IndexOf('\0') >= 0 || Path.IsPathRooted(nodePath))
            {
                throw new InvalidDataException(
                    $"VFS directory node {nodeIndex} path '{nodePath}' is rooted or contains NUL.");
            }
            var segments = nodePath.Replace('\\', '/').Split('/');
            if (segments.Any(segment => segment == ".."))
            {
                throw new InvalidDataException(
                    $"VFS directory node {nodeIndex} path '{nodePath}' contains path traversal.");
            }
        }

        private static void CopyRange(Stream input, Stream output, long length, string containerPath, string nodePath)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            var remaining = length;
            var copied = 0L;
            try
            {
                while (remaining > 0)
                {
                    var requested = (int)Math.Min(buffer.Length, remaining);
                    var read = input.Read(buffer, 0, requested);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException(
                            $"VFS node '{nodePath}' in '{containerPath}' short read: " +
                            $"expected={length}, actual={copied}.");
                    }
                    output.Write(buffer, 0, read);
                    copied += read;
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, true);
            }
            if (copied != length)
            {
                throw new EndOfStreamException(
                    $"VFS node '{nodePath}' in '{containerPath}' short read: " +
                    $"expected={length}, actual={copied}.");
            }
        }

        private void ValidateCompressedBlockConsumption(FileReader reader, string path)
        {
            var expected = 0L;
            foreach (var block in m_BlocksInfo)
            {
                var encodedSize = (int)block.flags == 0
                    ? block.uncompressedSize
                    : block.compressedSize;
                expected = checked(expected + encodedSize);
            }
            var actual = reader.Position - (Offset + (m_Header.encFlags >= 7 ? 48 : 40));
            if ((m_Header.flags & ArchiveFlags.BlocksInfoAtTheEnd) == 0)
            {
                var infoLength = checked((long)m_Header.compressedBlocksInfoSize);
                if ((m_Header.flags & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
                {
                    infoLength = (infoLength + 15) & ~15L;
                }
                actual -= infoLength;
            }
            if (actual != expected)
            {
                throw new InvalidDataException(
                    $"VFS compressed block consumption mismatch for '{path}': " +
                    $"expected={expected}, actual={actual}.");
            }
        }

        private void ValidateDecodedBlockLength(Stream blocksStream, string path)
        {
            var expectedLength = 0L;
            foreach (var block in m_BlocksInfo)
            {
                expectedLength = checked(expectedLength + block.uncompressedSize);
            }

            if (blocksStream.Length != expectedLength)
            {
                throw new InvalidDataException(
                    $"VFS bundle '{path}' decoded block length mismatch: " +
                    $"expected={expectedLength}, actual={blocksStream.Length} bytes."
                );
            }
        }
    }
}
