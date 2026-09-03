using System;
using System.Buffers.Binary;
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
        public uint HeaderSize { get; private set; }
        public uint Version { get; private set; }
        public uint LanguageSectorSize { get; private set; }
        public uint BanksSectorSize { get; private set; }
        public uint SoundsSectorSize { get; private set; }
        public uint ExternalsSectorSize { get; private set; }
        public bool EncryptedHeader { get; private set; }
        public int BankCount { get; private set; }
        public int SoundCount { get; private set; }
        public int ExternalCount { get; private set; }
        public List<EndfieldBnkStructure> BnkStructures { get; } = new();

        public static EndfieldAkpkPackage Parse(byte[] input)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }
            if (input.Length < 16)
            {
                throw new InvalidDataException("invalid AKPK magic");
            }

            var data = (byte[])input.Clone();
            var packageEncryptedHeader = false;
            if (HasMagic(data, ":)xD"))
            {
                packageEncryptedHeader = true;
                var headerSize = BitConverter.ToUInt32(data, 4);
                if (headerSize < 16 || headerSize > data.Length)
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
            package.EncryptedHeader = packageEncryptedHeader;
            using var stream = new MemoryStream(data, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, false);

            stream.Position = 4;
            var headerSizeValue = ReadUInt32(reader, "header size");
            var version = ReadUInt32(reader, "version");
            if (version != 1)
            {
                throw new InvalidDataException($"unsupported AKPK version: {version}");
            }
            var languagesSectorSize = ReadUInt32(reader, "languages sector size");
            var banksSectorSize = ReadUInt32(reader, "banks sector size");
            var soundsSectorSize = ReadUInt32(reader, "sounds sector size");
            var externalsSectorSize = 0U;
            var hasExternals = (ulong)languagesSectorSize + banksSectorSize + soundsSectorSize + 0x10UL < headerSizeValue;
            if (hasExternals)
            {
                externalsSectorSize = ReadUInt32(reader, "externals sector size");
            }

            package.HeaderSize = headerSizeValue;
            package.Version = version;
            package.LanguageSectorSize = languagesSectorSize;
            package.BanksSectorSize = banksSectorSize;
            package.SoundsSectorSize = soundsSectorSize;
            package.ExternalsSectorSize = externalsSectorSize;

            var languageStart = checked((int)stream.Position);
            package.ParseLanguages(reader, languageStart, languagesSectorSize);
            var banksStart = checked(languageStart + checked((int)languagesSectorSize));
            package.BankCount = package.ParseSector(reader, banksStart, banksSectorSize, isSounds: false, isExternals: false);
            var soundsStart = checked(banksStart + checked((int)banksSectorSize));
            package.SoundCount = package.ParseSector(reader, soundsStart, soundsSectorSize, isSounds: true, isExternals: false);
            var externalsStart = checked(soundsStart + checked((int)soundsSectorSize));
            package.ExternalCount = package.ParseSector(reader, externalsStart, externalsSectorSize, isSounds: true, isExternals: true);
            return package;
        }

        private static uint ReadUInt32(BinaryReader reader, string field)
        {
            if (reader.BaseStream.Length - reader.BaseStream.Position < 4)
            {
                throw new InvalidDataException($"truncated AKPK {field}");
            }
            return reader.ReadUInt32();
        }

        public byte[] GetWemData(EndfieldWemEntry entry)
        {
            if (entry.Offset > int.MaxValue || entry.Size > int.MaxValue || entry.Offset + entry.Size > (ulong)data.Length)
            {
                throw new EndfieldVfsException("invalid WEM entry range");
            }

            var result = new byte[entry.Size];
            Array.Copy(data, (long)entry.Offset, result, 0, (long)entry.Size);
            if (entry.ContainerSeed.HasValue)
            {
                if (entry.ContainerDataOffset > uint.MaxValue)
                {
                    throw new EndfieldVfsException($"invalid AKPK bank media offset: {entry.ContainerDataOffset}");
                }
                EndfieldAudioCrypto.DecryptVfs(
                    result,
                    0,
                    result.Length,
                    entry.ContainerSeed.Value,
                    (uint)entry.ContainerDataOffset);
            }
            // Embedded Wwise plug-in media uses a PLUG envelope rather than a
            // RIFF/RIFX WEM. It is already framed after bank-ID decryption;
            // applying the media-ID stream cipher would corrupt the envelope.
            if (result.Length >= 4
                && !HasMagic(result, "RIFF")
                && !HasMagic(result, "RIFX")
                && !HasMagic(result, "PLUG"))
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

        private void ParseLanguages(BinaryReader reader, int sectorStart, uint sectorSize)
        {
            var sectorEnd = checked((long)sectorStart + sectorSize);
            if (sectorSize < 4 || sectorEnd > data.Length)
            {
                throw new InvalidDataException($"AKPK languages sector out of range: start={sectorStart}, size={sectorSize}, data={data.Length}");
            }

            reader.BaseStream.Position = sectorStart;
            var langCount = ReadUInt32(reader, "language count");
            if (langCount > (sectorSize - 4) / 8)
            {
                throw new InvalidDataException($"AKPK language count exceeds sector: count={langCount}, sector={sectorSize}");
            }

            var stringOffset = (uint)sectorStart;
            for (var i = 0; i < langCount; i++)
            {
                var langOffset = ReadUInt32(reader, "language offset");
                var langId = ReadUInt32(reader, "language id");
                if (langOffset >= sectorSize)
                {
                    throw new InvalidDataException($"AKPK language offset out of range: offset={langOffset}, sector={sectorSize}");
                }
                var current = reader.BaseStream.Position;
                reader.BaseStream.Position = checked(stringOffset + langOffset);

                var testBytes = ReadBytesWithin(reader, 2, sectorEnd, "language string probe");
                reader.BaseStream.Position = checked(stringOffset + langOffset);
                string langName;
                if (testBytes.Length == 2 && (testBytes[0] == 0 || testBytes[1] == 0))
                {
                    var available = checked((int)Math.Min(32, sectorEnd - reader.BaseStream.Position));
                    var bytes = ReadBytesWithin(reader, available, sectorEnd, "UTF-16 language string");
                    var chars = new List<ushort>();
                    var terminated = false;
                    for (var j = 0; j + 1 < bytes.Length; j += 2)
                    {
                        var value = BitConverter.ToUInt16(bytes, j);
                        if (value == 0)
                        {
                            terminated = true;
                            break;
                        }
                        chars.Add(value);
                    }
                    if (!terminated)
                    {
                        throw new InvalidDataException($"unterminated UTF-16 AKPK language string at offset={langOffset}");
                    }
                    langName = Encoding.Unicode.GetString(ToBytes(chars));
                }
                else
                {
                    var available = checked((int)Math.Min(16, sectorEnd - reader.BaseStream.Position));
                    var bytes = ReadBytesWithin(reader, available, sectorEnd, "UTF-8 language string");
                    var terminator = Array.IndexOf(bytes, (byte)0);
                    if (terminator < 0)
                    {
                        throw new InvalidDataException($"unterminated UTF-8 AKPK language string at offset={langOffset}");
                    }
                    langName = Encoding.UTF8.GetString(bytes, 0, terminator);
                }

                Languages[langId] = langName;
                reader.BaseStream.Position = current;
            }

            reader.BaseStream.Position = sectorEnd;
        }

        private int ParseSector(BinaryReader reader, int sectorStart, uint sectorSize, bool isSounds, bool isExternals)
        {
            if (sectorSize == 0)
            {
                return 0;
            }

            var sectorEnd = checked((long)sectorStart + sectorSize);
            if (sectorSize < 4 || sectorEnd > data.Length)
            {
                throw new InvalidDataException($"AKPK sector out of range: start={sectorStart}, size={sectorSize}, data={data.Length}");
            }
            reader.BaseStream.Position = sectorStart;
            var fileCount = ReadUInt32(reader, "sector file count");
            if (fileCount == 0)
            {
                if (sectorSize != 4)
                {
                    throw new InvalidDataException($"AKPK empty sector has unexpected size: {sectorSize}");
                }
                return 0;
            }

            if ((sectorSize - 4) % fileCount != 0)
            {
                throw new InvalidDataException($"AKPK sector size is not divisible by file count: size={sectorSize}, count={fileCount}");
            }
            var entrySize = (sectorSize - 4) / fileCount;
            var altMode = entrySize == 0x18;
            if (entrySize != 20 && entrySize != 24)
            {
                throw new InvalidDataException($"unsupported AKPK sector entry size: {entrySize}");
            }

            for (var i = 0; i < fileCount; i++)
            {
                var fileIdLow = (ulong)ReadUInt32(reader, "file id");
                ulong? fileIdHigh = null;
                if (altMode && isExternals)
                {
                    fileIdHigh = ReadUInt32(reader, "external file id high");
                }

                var blockSize = ReadUInt32(reader, "block size");
                ulong size;
                if (altMode && isExternals)
                {
                    size = ReadUInt32(reader, "external size");
                }
                else if (altMode)
                {
                    if (reader.BaseStream.Length - reader.BaseStream.Position < 8)
                    {
                        throw new InvalidDataException("truncated AKPK 64-bit size");
                    }
                    size = reader.ReadUInt64();
                }
                else
                {
                    size = ReadUInt32(reader, "size");
                }

                var offset = (ulong)ReadUInt32(reader, "offset");
                var langId = ReadUInt32(reader, "language id");
                if (blockSize != 0)
                {
                    offset = checked(offset * blockSize);
                }

                if (size > (ulong)data.Length || offset > (ulong)data.Length - size)
                {
                    throw new InvalidDataException($"AKPK entry range out of bounds: offset={offset}, size={size}, data={data.Length}");
                }

                Languages.TryGetValue(langId, out var language);
                var finalId = fileIdHigh.HasValue ? (fileIdHigh.Value << 32) | fileIdLow : fileIdLow;
                if (!isSounds)
                {
                    foreach (var (wemId, wemOffset, wemSize) in ParseBnk(fileIdLow, offset, size))
                    {
                        Entries.Add(new EndfieldWemEntry
                        {
                            Id = wemId,
                            Offset = offset + wemOffset,
                            Size = wemSize,
                            Language = language,
                            ContainerSeed = checked((uint)fileIdLow),
                            ContainerDataOffset = wemOffset,
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
            if (reader.BaseStream.Position != sectorEnd)
            {
                throw new InvalidDataException($"AKPK sector cursor mismatch: cursor={reader.BaseStream.Position}, end={sectorEnd}");
            }
            return checked((int)fileCount);
        }

        private IEnumerable<(ulong id, ulong offset, ulong size)> ParseBnk(ulong bankId, ulong offset, ulong size)
        {
            if (offset > int.MaxValue || size > int.MaxValue || size < 8)
            {
                throw new InvalidDataException($"invalid AKPK bank range: id={bankId}, offset={offset}, size={size}");
            }

            var start = (int)offset;
            var end = checked(start + (int)size);
            var payload = new byte[(int)size];
            Array.Copy(data, start, payload, 0, payload.Length);
            EndfieldAudioCrypto.DecryptVfs(payload, 0, payload.Length, checked((uint)bankId), 0);
            if (!HasMagicAt(payload, 0, "BKHD"))
            {
                throw new InvalidDataException($"AKPK bank payload missing BKHD: id={bankId}, offset={offset}, size={size}");
            }

            var pos = 0;
            var didx = new List<(uint id, uint offset, uint size)>();
            var dataBodyOffset = -1;
            var dataBodySize = 0U;
            var first = true;
            var structure = new EndfieldBnkStructure
            {
                BankId = bankId,
                ByteLength = checked((int)size),
            };
            while (pos < payload.Length)
            {
                if (payload.Length - pos < 8)
                {
                    throw new InvalidDataException($"truncated AKPK BNK section: id={bankId}, offset={pos}");
                }
                var tag = Encoding.ASCII.GetString(payload, pos, 4);
                var sectionSize = BitConverter.ToUInt32(payload, pos + 4);
                var bodyStart = checked(pos + 8);
                var bodyEnd = checked(bodyStart + checked((int)sectionSize));
                if (bodyEnd > payload.Length || !IsAsciiSectionTag(tag))
                {
                    throw new InvalidDataException($"invalid AKPK BNK section: id={bankId}, tag={tag}, offset={pos}, size={sectionSize}");
                }
                if (first && tag != "BKHD")
                {
                    throw new InvalidDataException($"AKPK BNK must start with BKHD: id={bankId}");
                }
                first = false;
                structure.Sections.Add(new EndfieldBnkSection
                {
                    Tag = tag,
                    Offset = pos,
                    DeclaredSize = sectionSize,
                });
                if (tag == "BKHD")
                {
                    if (sectionSize < 4)
                    {
                        throw new InvalidDataException(
                            $"AKPK BKHD version field truncated: id={bankId}, offset={bodyStart}, expected=4, actual={sectionSize}");
                    }
                    structure.Version = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(bodyStart, 4));
                }
                if (tag == "DIDX")
                {
                    if (sectionSize % 12 != 0)
                    {
                        throw new InvalidDataException($"AKPK DIDX size is not divisible by 12: id={bankId}, size={sectionSize}");
                    }
                    for (var p = bodyStart; p < bodyEnd; p += 12)
                    {
                        didx.Add((BitConverter.ToUInt32(payload, p), BitConverter.ToUInt32(payload, p + 4), BitConverter.ToUInt32(payload, p + 8)));
                    }
                }
                else if (tag == "DATA")
                {
                    dataBodyOffset = bodyStart;
                    dataBodySize = sectionSize;
                }
                else if (tag == "HIRC")
                {
                    ParseHirc(payload, bodyStart, checked((int)sectionSize), bankId, structure);
                }
                pos = bodyEnd;
            }

            BnkStructures.Add(structure);

            if (didx.Count > 0 && dataBodyOffset < 0)
            {
                throw new InvalidDataException($"AKPK DIDX has no DATA section: id={bankId}");
            }
            foreach (var (id, wemOffset, wemSize) in didx)
            {
                if (dataBodyOffset < 0 || wemOffset > dataBodySize || wemSize > dataBodySize - wemOffset)
                {
                    throw new InvalidDataException($"AKPK DIDX media range out of DATA: bank={bankId}, media={id}, offset={wemOffset}, size={wemSize}, data={dataBodySize}");
                }
                yield return (id, checked((ulong)dataBodyOffset + wemOffset), wemSize);
            }
        }

        private static void ParseHirc(
            byte[] payload,
            int bodyStart,
            int bodyLength,
            ulong bankId,
            EndfieldBnkStructure structure)
        {
            if (bodyLength < 4)
            {
                throw new InvalidDataException($"AKPK HIRC object-count field truncated: id={bankId}, offset={bodyStart}");
            }

            var bodyEnd = checked(bodyStart + bodyLength);
            var objectCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(bodyStart, 4));
            var maximumObjectCount = checked((bodyLength - 4) / 9);
            if (objectCount > maximumObjectCount)
            {
                throw new InvalidDataException(
                    $"AKPK HIRC object count exceeds HIRC: id={bankId}, count={objectCount}, maximum={maximumObjectCount}, size={bodyLength}");
            }
            var cursor = checked(bodyStart + 4);
            structure.HircObjectCount = checked(structure.HircObjectCount + objectCount);
            for (var ordinal = 0U; ordinal < objectCount; ordinal++)
            {
                if (bodyEnd - cursor < 9)
                {
                    throw new InvalidDataException(
                        $"AKPK HIRC object header truncated: id={bankId}, ordinal={ordinal}, offset={cursor}, expected=9, actual={bodyEnd - cursor}");
                }

                var objectType = payload[cursor];
                var objectSize = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(checked(cursor + 1), 4));
                var objectId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(checked(cursor + 5), 4));
                if (objectSize < 4)
                {
                    throw new InvalidDataException(
                        $"AKPK HIRC object size too small: id={bankId}, ordinal={ordinal}, object={objectId}, type={objectType}, size={objectSize}, expectedMin=4");
                }
                var objectEnd = checked((long)cursor + 5L + objectSize);
                if (objectEnd > bodyEnd)
                {
                    throw new InvalidDataException(
                        $"AKPK HIRC object range out of HIRC: id={bankId}, ordinal={ordinal}, object={objectId}, type={objectType}, offset={cursor}, size={objectSize}, hircEnd={bodyEnd}");
                }
                structure.HircObjectTypeCounts.TryGetValue(objectType, out var typeCount);
                structure.HircObjectTypeCounts[objectType] = checked(typeCount + 1);
                if (!structure.HircObjectTypeStats.TryGetValue(objectType, out var stats))
                {
                    stats = new EndfieldBnkObjectTypeStats
                    {
                        MinDeclaredLength = objectSize,
                        MaxDeclaredLength = objectSize,
                    };
                    structure.HircObjectTypeStats[objectType] = stats;
                }
                stats.DeclaredLengthBytes = checked(stats.DeclaredLengthBytes + objectSize);
                stats.MinDeclaredLength = Math.Min(stats.MinDeclaredLength, objectSize);
                stats.MaxDeclaredLength = Math.Max(stats.MaxDeclaredLength, objectSize);
                stats.Count = checked(stats.Count + 1);
                if (objectType == 2)
                {
                    ParseType2SourcePrefix(
                        payload,
                        checked(cursor + 9),
                        checked((int)objectSize - 4),
                        bankId,
                        ordinal,
                        objectId,
                        structure);
                }
                cursor = checked((int)objectEnd);
            }

            if (cursor != bodyEnd)
            {
                throw new InvalidDataException(
                    $"AKPK HIRC cursor mismatch: id={bankId}, cursor={cursor}, end={bodyEnd}, trailing={bodyEnd - cursor}");
            }
        }

        private static void ParseType2SourcePrefix(
            byte[] payload,
            int bodyStart,
            int bodyLength,
            ulong bankId,
            uint ordinal,
            uint objectId,
            EndfieldBnkStructure structure)
        {
            if (bodyLength < 14)
            {
                throw new InvalidDataException(
                    $"AKPK HIRC type 0x02 source prefix truncated: id={bankId}, ordinal={ordinal}, object={objectId}, expected=14, actual={bodyLength}");
            }

            var pluginId = BitConverter.ToUInt32(payload, bodyStart);
            var pluginType = pluginId & 0x0F;
            var prefixLength = 14;
            if (pluginType == 2)
            {
                if (bodyLength < 18)
                {
                    throw new InvalidDataException(
                        $"AKPK HIRC type 0x02 source plugin length truncated: id={bankId}, ordinal={ordinal}, object={objectId}, expected=18, actual={bodyLength}");
                }
                var parameterLength = BitConverter.ToUInt32(payload, checked(bodyStart + 14));
                if (parameterLength > (uint)(bodyLength - 18))
                {
                    throw new InvalidDataException(
                        $"AKPK HIRC type 0x02 source plugin range out of object: id={bankId}, ordinal={ordinal}, object={objectId}, parameterLength={parameterLength}, available={bodyLength - 18}");
                }
                prefixLength = checked(18 + (int)parameterLength);
            }

            structure.Type2PrefixCount = checked(structure.Type2PrefixCount + 1);
            structure.Type2PluginTypeCounts.TryGetValue(pluginType, out var pluginCount);
            structure.Type2PluginTypeCounts[pluginType] = checked(pluginCount + 1);
            structure.Type2PrefixBytes = checked(structure.Type2PrefixBytes + (uint)prefixLength);
            var opaqueLength = checked(bodyLength - prefixLength);
            structure.Type2OpaqueTailBytes = checked(structure.Type2OpaqueTailBytes + (uint)opaqueLength);
            structure.Type2MinOpaqueTailBytes = structure.Type2PrefixCount == 1
                ? (uint)opaqueLength
                : Math.Min(structure.Type2MinOpaqueTailBytes, (uint)opaqueLength);
            structure.Type2MaxOpaqueTailBytes = Math.Max(structure.Type2MaxOpaqueTailBytes, (uint)opaqueLength);
        }

        private static byte[] ReadBytesWithin(BinaryReader reader, int count, long end, string field)
        {
            if (count < 0 || reader.BaseStream.Position > end || end - reader.BaseStream.Position < count)
            {
                throw new InvalidDataException($"truncated AKPK {field}");
            }
            var bytes = reader.ReadBytes(count);
            if (bytes.Length != count)
            {
                throw new InvalidDataException($"short AKPK {field}: expected={count}, actual={bytes.Length}");
            }
            return bytes;
        }

        private static bool IsAsciiSectionTag(string tag)
        {
            if (tag.Length != 4)
            {
                return false;
            }
            for (var i = 0; i < tag.Length; i++)
            {
                if (tag[i] < 'A' || tag[i] > 'Z')
                {
                    return false;
                }
            }
            return true;
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
        public uint? ContainerSeed { get; set; }
        public ulong ContainerDataOffset { get; set; }
    }

    public sealed class EndfieldBnkStructure
    {
        public ulong BankId { get; set; }
        public int ByteLength { get; set; }
        public uint? Version { get; set; }
        public List<EndfieldBnkSection> Sections { get; } = new();
        public uint HircObjectCount { get; set; }
        public Dictionary<byte, uint> HircObjectTypeCounts { get; } = new();
        public Dictionary<byte, EndfieldBnkObjectTypeStats> HircObjectTypeStats { get; } = new();
        public uint Type2PrefixCount { get; set; }
        public Dictionary<uint, uint> Type2PluginTypeCounts { get; } = new();
        public uint Type2PrefixBytes { get; set; }
        public uint Type2OpaqueTailBytes { get; set; }
        public uint Type2MinOpaqueTailBytes { get; set; }
        public uint Type2MaxOpaqueTailBytes { get; set; }
    }

    public sealed class EndfieldBnkObjectTypeStats
    {
        public uint Count { get; set; }
        public ulong DeclaredLengthBytes { get; set; }
        public uint MinDeclaredLength { get; set; }
        public uint MaxDeclaredLength { get; set; }
    }

    public sealed class EndfieldBnkSection
    {
        public string Tag { get; set; }
        public int Offset { get; set; }
        public uint DeclaredSize { get; set; }
    }
}
