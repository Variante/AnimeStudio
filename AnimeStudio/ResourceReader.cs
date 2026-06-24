using System.IO;

using System;

namespace AnimeStudio
{
    public class ResourceReader
    {
        private bool needSearch;
        private string path;
        private SerializedFile assetsFile;
        private long offset;
        private long size;
        private BinaryReader reader;

        public int Size
        {
            get
            {
                if (size < 0)
                {
                    throw new InvalidDataException($"Resource size is negative ({size}).");
                }
                if (size > int.MaxValue)
                {
                    throw new InvalidDataException($"Resource size {size} is too large for byte-array export.");
                }
                return (int)size;
            }
        }

        public ResourceReader(string path, SerializedFile assetsFile, long offset, long size)
        {
            needSearch = true;
            this.path = path;
            this.assetsFile = assetsFile;
            this.offset = offset;
            this.size = size;
        }

        public ResourceReader(BinaryReader reader, long offset, long size)
        {
            this.reader = reader;
            this.offset = offset;
            this.size = size;
        }

        private BinaryReader GetReader()
        {
            if (needSearch)
            {
                var resourceFileName = Path.GetFileName(path);
                if (assetsFile.assetsManager.resourceFileReaders.TryGetValue(resourceFileName, out reader))
                {
                    needSearch = false;
                    return reader;
                }
                var assetsFileDirectory = Path.GetDirectoryName(assetsFile.fullName);
                var resourceFilePath = Path.Combine(assetsFileDirectory, resourceFileName);
                if (!File.Exists(resourceFilePath))
                {
                    var findFiles = Directory.GetFiles(assetsFileDirectory, resourceFileName, SearchOption.AllDirectories);
                    if (findFiles.Length > 0)
                    {
                        resourceFilePath = findFiles[0];
                    }
                }
                if (File.Exists(resourceFilePath))
                {
                    needSearch = false;
                    reader = new BinaryReader(File.OpenRead(resourceFilePath));
                    assetsFile.assetsManager.resourceFileReaders.TryAdd(resourceFileName, reader);
                    return reader;
                }
                throw new FileNotFoundException($"Can't find the resource file {resourceFileName}");
            }
            else
            {
                return reader;
            }
        }

        private void ValidateRange(BinaryReader binaryReader, bool requireIntSize)
        {
            if (offset < 0)
            {
                throw new InvalidDataException($"Resource offset is negative ({offset}).");
            }
            if (size < 0)
            {
                throw new InvalidDataException($"Resource size is negative ({size}).");
            }
            if (requireIntSize && size > int.MaxValue)
            {
                throw new InvalidDataException($"Resource size {size} is too large for byte-array export.");
            }

            var length = binaryReader.BaseStream.Length;
            if (offset > length || size > length - offset)
            {
                throw new EndOfStreamException(
                    $"Resource range offset={offset}, size={size} exceeds stream length {length}."
                );
            }
        }

        public byte[] GetData()
        {
            var binaryReader = GetReader();
            ValidateRange(binaryReader, requireIntSize: true);
            binaryReader.BaseStream.Position = offset;
            return binaryReader.ReadBytes((int)size);
        }

        public void GetData(byte[] buff)
        {
            var binaryReader = GetReader();
            ValidateRange(binaryReader, requireIntSize: true);
            if (buff.Length < size)
            {
                throw new ArgumentException($"Buffer length {buff.Length} is smaller than resource size {size}.", nameof(buff));
            }
            binaryReader.BaseStream.Position = offset;
            binaryReader.Read(buff, 0, (int)size);
        }

        public void WriteData(string path)
        {
            var binaryReader = GetReader();
            ValidateRange(binaryReader, requireIntSize: false);
            binaryReader.BaseStream.Position = offset;
            using (var writer = File.Create(path))
            {
                binaryReader.BaseStream.CopyTo(writer, size);
            }
        }
    }
}
