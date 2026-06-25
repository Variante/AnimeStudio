using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AnimeStudio.Endfield
{
    public sealed class EndfieldAkpkPackage
    {
        private readonly byte[] data;

        private EndfieldAkpkPackage(byte[] data)
        {
            this.data = data;
        }

        public List<EndfieldWemEntry> Entries { get; } = new();
        public Dictionary<uint, string> Languages { get; } = new();

        public static EndfieldAkpkPackage Parse(byte[] input)
        {
            if (input.Length < 16)
            {
                throw new InvalidDataException("invalid AKPK magic");
            }

            var data = (byte[])input.Clone();
            if (HasMagic(data, ":)xD"))
            {
                var headerSize = BitConverter.ToUInt32(data, 4);
                if (headerSize < 4 || headerSize > data.Length)
                {
                    throw new InvalidDataException("invalid AKPK header size");
                }

                EndfieldAudioCrypto.DecryptVfs(data, 12, checked((int)headerSize - 4), headerSize, 0);
                data[0] = (byte)'A';
                data[1] = (byte)'K';
                data[2] = (byte)'P';
                data[3] = (byte)'K';
                BitConverter.GetBytes(1U).CopyTo(data, 8);
            }

            if (!HasMagic(data, "AKPK"))
            {
                throw new InvalidDataException("invalid AKPK magic");
            }

            var package = new EndfieldAkpkPackage(data);
            using var stream = new MemoryStream(data, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, false);

            stream.Position = 4;
            var headerSizeValue = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            var languagesSectorSize = reader.ReadUInt32();
            var banksSectorSize = reader.ReadUInt32();
            var soundsSectorSize = reader.ReadUInt32();
            var externalsSectorSize = 0U;
            if (languagesSectorSize + banksSectorSize + soundsSectorSize + 0x10 < headerSizeValue)
            {
                externalsSectorSize = reader.ReadUInt32();
            }

            package.ParseLanguages(reader, languagesSectorSize);
            package.ParseSector(reader, banksSectorSize, isSounds: false, isExternals: false);
            package.ParseSector(reader, soundsSectorSize, isSounds: true, isExternals: false);
            package.ParseSector(reader, externalsSectorSize, isSounds: true, isExternals: true);
            return package;
        }

        public byte[] GetWemData(EndfieldWemEntry entry)
        {
            if (entry.Offset > int.MaxValue || entry.Size > int.MaxValue || entry.Offset + entry.Size > (ulong)data.Length)
            {
                throw new EndfieldVfsException("invalid WEM entry range");
            }

            var result = new byte[entry.Size];
            Array.Copy(data, (long)entry.Offset, result, 0, (long)entry.Size);
            if (result.Length >= 4 && !HasMagic(result, "RIFF") && !HasMagic(result, "RIFX"))
            {
                EndfieldAudioCrypto.DecryptWem(result, (uint)entry.Id);
            }
            return result;
        }

        private static bool HasMagic(byte[] buffer, string magic)
        {
            if (buffer.Length < magic.Length)
            {
                return false;
            }

            for (var i = 0; i < magic.Length; i++)
            {
                if (buffer[i] != (byte)magic[i])
                {
                    return false;
                }
            }
            return true;
        }

        private void ParseLanguages(BinaryReader reader, uint sectorSize)
        {
            var stringOffset = (uint)reader.BaseStream.Position;
            var langCount = reader.ReadUInt32();
            for (var i = 0; i < langCount; i++)
            {
                var langOffset = reader.ReadUInt32();
                var langId = reader.ReadUInt32();
                var current = reader.BaseStream.Position;
                reader.BaseStream.Position = stringOffset + langOffset;

                var testBytes = reader.ReadBytes(2);
                reader.BaseStream.Position = stringOffset + langOffset;
                string langName;
                if (testBytes.Length == 2 && (testBytes[0] == 0 || testBytes[1] == 0))
                {
                    var bytes = reader.ReadBytes(32);
                    var chars = new List<ushort>();
                    for (var j = 0; j + 1 < bytes.Length; j += 2)
                    {
                        var value = BitConverter.ToUInt16(bytes, j);
                        if (value == 0)
                        {
                            break;
                        }
                        chars.Add(value);
                    }
                    langName = Encoding.Unicode.GetString(ToBytes(chars));
                }
                else
                {
                    var bytes = reader.ReadBytes(16);
                    langName = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
                }

                Languages[langId] = langName;
                reader.BaseStream.Position = current;
            }

            reader.BaseStream.Position = stringOffset + sectorSize;
        }

        private void ParseSector(BinaryReader reader, uint sectorSize, bool isSounds, bool isExternals)
        {
            if (sectorSize == 0)
            {
                return;
            }

            var fileCount = reader.ReadUInt32();
            if (fileCount == 0)
            {
                return;
            }

            var entrySize = (sectorSize - 4) / fileCount;
            var altMode = entrySize == 0x18;

            for (var i = 0; i < fileCount; i++)
            {
                var fileIdLow = (ulong)reader.ReadUInt32();
                ulong? fileIdHigh = null;
                if (altMode && isExternals)
                {
                    fileIdHigh = reader.ReadUInt32();
                }

                var blockSize = reader.ReadUInt32();
                ulong size;
                if (altMode && isExternals)
                {
                    size = reader.ReadUInt32();
                }
                else if (altMode)
                {
                    size = reader.ReadUInt64();
                }
                else
                {
                    size = reader.ReadUInt32();
                }

                var offset = (ulong)reader.ReadUInt32();
                var langId = reader.ReadUInt32();
                if (blockSize != 0)
                {
                    offset *= blockSize;
                }

                Languages.TryGetValue(langId, out var language);
                var finalId = fileIdHigh.HasValue ? (fileIdHigh.Value << 32) | fileIdLow : fileIdLow;
                if (!isSounds)
                {
                    foreach (var (wemId, wemOffset, wemSize) in ParseBnk(offset, size))
                    {
                        Entries.Add(new EndfieldWemEntry
                        {
                            Id = wemId,
                            Offset = offset + wemOffset,
                            Size = wemSize,
                            Language = language,
                        });
                    }
                }
                else
                {
                    Entries.Add(new EndfieldWemEntry
                    {
                        Id = finalId,
                        Offset = offset,
                        Size = size,
                        Language = language,
                    });
                }
            }
        }

        private IEnumerable<(ulong id, ulong offset, ulong size)> ParseBnk(ulong offset, ulong size)
        {
            if (offset > int.MaxValue || size > int.MaxValue || offset + size > (ulong)data.Length || size < 8)
            {
                yield break;
            }

            var start = (int)offset;
            var end = checked(start + (int)size);
            if (!HasMagicAt(data, start, "BKHD"))
            {
                yield break;
            }

            var bkhdSize = BitConverter.ToUInt32(data, start + 4);
            var pos = start + 8 + checked((int)bkhdSize);
            if (pos + 8 > end || !HasMagicAt(data, pos, "DIDX"))
            {
                yield break;
            }

            var didxSize = BitConverter.ToUInt32(data, pos + 4);
            var nWems = didxSize / 12;
            pos += 8;

            var wems = new List<(uint id, uint offset, uint size)>((int)nWems);
            for (var i = 0; i < nWems; i++)
            {
                if (pos + 12 > end)
                {
                    yield break;
                }

                wems.Add((BitConverter.ToUInt32(data, pos), BitConverter.ToUInt32(data, pos + 4), BitConverter.ToUInt32(data, pos + 8)));
                pos += 12;
            }

            if (pos + 8 > end || !HasMagicAt(data, pos, "DATA"))
            {
                yield break;
            }

            var dataOffset = (uint)(pos - start + 8);
            foreach (var (id, wemOffset, wemSize) in wems)
            {
                yield return (id, dataOffset + wemOffset, wemSize);
            }
        }

        private static bool HasMagicAt(byte[] buffer, int offset, string magic)
        {
            if (offset < 0 || offset + magic.Length > buffer.Length)
            {
                return false;
            }

            for (var i = 0; i < magic.Length; i++)
            {
                if (buffer[offset + i] != (byte)magic[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static byte[] ToBytes(List<ushort> values)
        {
            var bytes = new byte[values.Count * 2];
            for (var i = 0; i < values.Count; i++)
            {
                BitConverter.GetBytes(values[i]).CopyTo(bytes, i * 2);
            }
            return bytes;
        }
    }

    public sealed class EndfieldWemEntry
    {
        public ulong Id { get; set; }
        public ulong Offset { get; set; }
        public ulong Size { get; set; }
        public string Language { get; set; }
    }
}
