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
        // Identities the caller wants walked. Empty by default, so the walk costs
        // nothing unless a consumer supplies them.
        public static HashSet<uint> NamedIdentityHashes { get; set; } = new();

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
                    ParseHirc(payload, bodyStart, checked((int)sectionSize), bankId, structure, NamedIdentityHashes);
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
            EndfieldBnkStructure structure,
            HashSet<uint> namedIdentityHashes)
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

            // First pass: the bank's object identities, so the anonymous reference
            // vectors framed below can be resolved against them. This walks headers
            // only and repeats the same bounds the framing pass enforces.
            var bankObjectTypes = new Dictionary<uint, byte>();
            var duplicateObjectIds = new HashSet<uint>();
            var scan = cursor;
            for (var ordinal = 0U; ordinal < objectCount; ordinal++)
            {
                if (bodyEnd - scan < 9)
                {
                    throw new InvalidDataException(
                        $"AKPK HIRC object header truncated: id={bankId}, ordinal={ordinal}, offset={scan}, expected=9, actual={bodyEnd - scan}");
                }
                var scanType = payload[scan];
                var scanSize = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(checked(scan + 1), 4));
                var scanId = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(checked(scan + 5), 4));
                if (scanSize < 4)
                {
                    throw new InvalidDataException(
                        $"AKPK HIRC object size too small: id={bankId}, ordinal={ordinal}, object={scanId}, type={scanType}, size={scanSize}, expectedMin=4");
                }
                var scanEnd = checked((long)scan + 5L + scanSize);
                if (scanEnd > bodyEnd)
                {
                    throw new InvalidDataException(
                        $"AKPK HIRC object range out of HIRC: id={bankId}, ordinal={ordinal}, object={scanId}, type={scanType}, offset={scan}, size={scanSize}, hircEnd={bodyEnd}");
                }
                // Cross-type duplicate ids are retained numerically elsewhere; a duplicate
                // here would make resolution ambiguous, so record it rather than collapse it.
                if (!bankObjectTypes.TryAdd(scanId, scanType))
                {
                    structure.ReferenceCensus.DuplicateObjectIds =
                        checked(structure.ReferenceCensus.DuplicateObjectIds + 1);
                    duplicateObjectIds.Add(scanId);
                }
                scan = checked((int)scanEnd);
            }

            var referrerCounts = new Dictionary<uint, uint>();
            var referrerOf = new Dictionary<uint, uint>();
            var bankEdges = new Dictionary<uint, List<uint>>();
            var bankSourceIds = new Dictionary<uint, uint>();
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
                if (objectType == 3)
                {
                    var actionBodyStart = checked(cursor + 9);
                    var actionBodyLength = checked((int)objectSize - 4);
                    RecordType3ActionFrame(
                        payload.AsSpan(actionBodyStart, actionBodyLength),
                        structure.Version,
                        bankId,
                        ordinal,
                        objectId,
                        structure);
                }
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
                if (objectType is 2 or 5 or 6 or 7 or 14 or 22)
                {
                    var bodySpan = payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4));
                    // Both switches are exhaustive on purpose: widening the guard above
                    // without adding arms here must fail loudly rather than silently
                    // frame a new type with another type's framer and census.
                    var census = objectType switch
                    {
                        2 => structure.Type2Body,
                        5 => structure.Type5Body,
                        6 => structure.Type6Body,
                        7 => structure.Type7Body,
                        14 => structure.Type14Body,
                        22 => structure.Type22Body,
                        _ => throw new InvalidDataException(
                            $"AKPK HIRC body census is not defined for type {objectType}"),
                    };
                    var frame = objectType switch
                    {
                        2 => FrameType2Body(bodySpan, structure.Version),
                        5 => FrameType5Body(bodySpan, structure.Version),
                        6 => FrameType6Body(bodySpan, structure.Version),
                        7 => FrameType7Body(bodySpan, structure.Version),
                        14 => FrameType14Body(bodySpan, structure.Version),
                        22 => FrameType22Body(bodySpan, structure.Version),
                        _ => throw new InvalidDataException(
                            $"AKPK HIRC body framer is not defined for type {objectType}"),
                    };
                    RecordHircBodyFrame(census, frame, bodySpan, bankId, ordinal, objectId);
                    if (frame.Status == "exact" && frame.References is { Count: > 0 })
                    {
                        bankEdges[objectId] = new List<uint>(frame.References);
                    }
                    if (objectType == 2 && bodySpan.Length >= 14)
                    {
                        bankSourceIds[objectId] =
                            BinaryPrimitives.ReadUInt32LittleEndian(bodySpan.Slice(5, 4));
                    }
                    ResolveHircReferences(
                        structure.ReferenceCensus,
                        frame,
                        objectType,
                        objectId,
                        bankObjectTypes,
                        duplicateObjectIds,
                        referrerCounts,
                        referrerOf);
                }
                if (objectType == 9)
                {
                    var census09 = structure.Type09;
                    var body09 = payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4));
                    census09.Bodies = checked(census09.Bodies + 1);
                    census09.BodyBytes = checked(census09.BodyBytes + (uint)body09.Length);
                    RecordType09(census09, body09, structure.Version);
                }
                if (objectType == 17)
                {
                    var census17 = structure.Type17;
                    var body17 = payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4));
                    census17.Bodies = checked(census17.Bodies + 1);
                    census17.BodyBytes = checked(census17.BodyBytes + (uint)body17.Length);
                    RecordType17(census17, body17);
                }
                // Numeric type 0x08's leading word: null, or one same-bank object.
                if (objectType == 8)
                {
                    var head08 = structure.Type08Head;
                    head08.Bodies = checked(head08.Bodies + 1);
                    var head08Body = payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4));
                    if (head08Body.Length < 4)
                    {
                        head08.TooShort = checked(head08.TooShort + 1);
                    }
                    else
                    {
                        var word = BinaryPrimitives.ReadUInt32LittleEndian(head08Body.Slice(0, 4));
                        if (word == 0)
                        {
                            head08.Null = checked(head08.Null + 1);
                        }
                        else if (bankObjectTypes.ContainsKey(word))
                        {
                            head08.Resolved = checked(head08.Resolved + 1);
                            bankEdges[objectId] = new List<uint> { word };
                        }
                        else
                        {
                            head08.Unresolved = checked(head08.Unresolved + 1);
                        }
                    }
                }
                // Numeric type 0x0B opens with one byte, a 32-bit record count, and that
                // many fourteen-byte records: plug-in id, stream-type byte, source id,
                // then five further bytes. Only the counted run is read here; what
                // follows it is not established and is deliberately not touched.
                if (objectType == 11)
                {
                    var sources = structure.Type11Sources;
                    sources.Bodies = checked(sources.Bodies + 1);
                    var sourceBody = payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4));
                    if (sourceBody.Length < 5)
                    {
                        sources.TooShort = checked(sources.TooShort + 1);
                    }
                    else
                    {
                        var recordCount = BinaryPrimitives.ReadUInt32LittleEndian(sourceBody.Slice(1, 4));
                        HircBump(sources.RecordCountCounts, $"records_{recordCount}", 1);
                        if (recordCount > Type11MaximumRecords
                            || 5 + recordCount * Type11SourceRecordBytes > (uint)sourceBody.Length)
                        {
                            sources.RecordsOutOfRange = checked(sources.RecordsOutOfRange + 1);
                        }
                        else
                        {
                            if (recordCount > 0)
                            {
                                sources.BodiesWithRecords = checked(sources.BodiesWithRecords + 1);
                            }
                            for (var record = 0U; record < recordCount; record++)
                            {
                                var at = checked(5 + (int)record * Type11SourceRecordBytes);
                                var plugin = BinaryPrimitives.ReadUInt32LittleEndian(
                                    sourceBody.Slice(at, 4));
                                sources.Records = checked(sources.Records + 1);
                                HircBump(sources.PluginIdCounts, $"plugin_{plugin:X8}", 1);
                                HircBump(sources.StreamTypeCounts, $"streamType_{sourceBody[at + 4]:X2}", 1);
                            }
                        }
                    }
                }
                // Numeric types 0x0A and 0x0D carry one 32-bit word near the head that
                // names an object in the same bank. Byte 2 selects where it sits: zero
                // puts it at offset 9, nonzero at offset 5. The offset is computed from
                // that byte, never searched for, and an unobserved discriminant is
                // counted as unknown rather than guessed either way.
                if (objectType is 10 or 12 or 13)
                {
                    var head = structure.MusicHeadReferences;
                    head.Bodies = checked(head.Bodies + 1);
                    HircBump(head.BodiesByType, $"type{objectType:X2}", 1);
                    var musicBody = payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4));
                    if (musicBody.Length < 3)
                    {
                        head.TooShort = checked(head.TooShort + 1);
                    }
                    else if (musicBody[0] != 0)
                    {
                        HircBump(head.HeadShapeCounts, $"head_{musicBody[0]:X2}", 1);
                        head.UnknownHeadShape = checked(head.UnknownHeadShape + 1);
                    }
                    else
                    {
                        HircBump(head.HeadShapeCounts, $"head_{musicBody[0]:X2}", 1);
                        var discriminant = musicBody[2];
                        HircBump(head.DiscriminantCounts, $"byte2_{discriminant:X2}", 1);
                        if (discriminant > 2)
                        {
                            head.UnknownDiscriminant = checked(head.UnknownDiscriminant + 1);
                        }
                        else
                        {
                            var offset = discriminant == 0 ? 9 : 5;
                            if (musicBody.Length < offset + 4)
                            {
                                head.TooShort = checked(head.TooShort + 1);
                            }
                            else
                            {
                                HircBump(head.OffsetCounts, $"offset_{offset}", 1);
                                var target = BinaryPrimitives.ReadUInt32LittleEndian(
                                    musicBody.Slice(offset, 4));
                                if (target == 0)
                                {
                                    head.Zero = checked(head.Zero + 1);
                                }
                                else if (bankObjectTypes.ContainsKey(target))
                                {
                                    head.Resolved = checked(head.Resolved + 1);
                                    bankEdges[objectId] = new List<uint> { target };
                                }
                                else
                                {
                                    head.Unresolved = checked(head.Unresolved + 1);
                                }
                            }
                        }
                    }
                }
                // Type 0x04 is framed by its own candidate-vector reader, so its edges
                // have to be collected here rather than from the shared body result.
                if (objectType == 4 && objectSize >= 5)
                {
                    var vectorStart = checked(cursor + 9);
                    var vectorLength = checked((int)objectSize - 4);
                    var entryCount = payload[vectorStart];
                    if (checked(1 + entryCount * 4) <= vectorLength)
                    {
                        var targets = new List<uint>(entryCount);
                        for (var entry = 0; entry < entryCount; entry++)
                        {
                            targets.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                                payload.AsSpan(checked(vectorStart + 1 + entry * 4), 4)));
                        }
                        bankEdges[objectId] = targets;
                    }
                }
                if (objectType == 3 && objectSize >= 10)
                {
                    var target = BinaryPrimitives.ReadUInt32LittleEndian(
                        payload.AsSpan(checked(cursor + 11), 4));
                    bankEdges[objectId] = new List<uint> { target };
                }
                if (objectType == 4)
                {
                    RecordType4U32VectorFrame(
                        payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4)),
                        bankId,
                        ordinal,
                        objectId,
                        structure,
                        bankObjectTypes,
                        duplicateObjectIds,
                        referrerCounts,
                        referrerOf);
                }
                cursor = checked((int)objectEnd);
            }

            foreach (var pair in bankObjectTypes)
            {
                var typeKey = $"type{pair.Value:X2}";
                structure.ReferenceCensus.ObjectCountsByType.TryGetValue(typeKey, out var typeCount);
                structure.ReferenceCensus.ObjectCountsByType[typeKey] = checked(typeCount + 1);
            }
            structure.ReferenceCensus.DistinctDuplicateObjectIds = checked(
                structure.ReferenceCensus.DistinctDuplicateObjectIds + (uint)duplicateObjectIds.Count);
            MeasureHircReferenceShape(structure.ReferenceCensus, referrerOf);
            WalkHircNamedReach(
                structure.NamedReachCensus,
                namedIdentityHashes,
                bankObjectTypes,
                bankEdges,
                bankSourceIds);

            if (cursor != bodyEnd)
            {
                throw new InvalidDataException(
                    $"AKPK HIRC cursor mismatch: id={bankId}, cursor={cursor}, end={bodyEnd}, trailing={bodyEnd - cursor}");
            }
        }

        private static void RecordType3ActionFrame(
            ReadOnlySpan<byte> body,
            uint? bankVersion,
            ulong bankId,
            uint ordinal,
            uint objectId,
            EndfieldBnkStructure structure)
        {
            var result = FrameType3ActionBody(body, bankVersion);
            structure.Type3ActionFrameCount = checked(structure.Type3ActionFrameCount + 1);
            structure.Type3ActionBodyBytes = checked(structure.Type3ActionBodyBytes + (uint)body.Length);
            if (result.OperationCode is ushort operationCode)
            {
                structure.Type3ActionOperationCounts.TryGetValue(operationCode, out var operationCount);
                structure.Type3ActionOperationCounts[operationCode] = checked(operationCount + 1);
            }

            if (result.Status == "exact")
            {
                structure.Type3ActionExactCount = checked(structure.Type3ActionExactCount + 1);
                structure.Type3ActionExactCursorBytes = checked(
                    structure.Type3ActionExactCursorBytes + (uint)result.CursorOffset);
                return;
            }
            if (result.Status == "unsupported")
            {
                structure.Type3ActionUnsupportedCount = checked(
                    structure.Type3ActionUnsupportedCount + 1);
            }
            else
            {
                structure.Type3ActionFailedCount = checked(structure.Type3ActionFailedCount + 1);
            }

            var failureCategory = string.IsNullOrEmpty(result.FailureCategory)
                ? result.Status
                : result.FailureCategory;
            structure.Type3ActionFailureCounts.TryGetValue(failureCategory, out var failureCount);
            structure.Type3ActionFailureCounts[failureCategory] = checked(failureCount + 1);
            if (structure.Type3ActionFailureExamples.Count < 8)
            {
                structure.Type3ActionFailureExamples.Add(new EndfieldHircActionFrameFailure
                {
                    BankId = bankId,
                    Ordinal = ordinal,
                    ObjectId = objectId,
                    OperationCode = result.OperationCode,
                    Status = result.Status,
                    FailureCategory = failureCategory,
                    CursorOffset = result.CursorOffset,
                    ExpectedBytes = result.ExpectedBytes,
                    ActualBytes = result.ActualBytes,
                });
            }
        }

        private static EndfieldHircActionFrameResult FrameType3ActionBody(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return new EndfieldHircActionFrameResult
                {
                    Status = "unsupported",
                    FailureCategory = "unsupported_bank_version",
                };
            }
            if (body.Length < 2)
            {
                return ActionFrameFailure("truncated_action_type", null, 0, 2, body.Length);
            }

            var actionType = BinaryPrimitives.ReadUInt16LittleEndian(body);
            var operationCode = (ushort)(actionType & 0xFF00);
            if (body.Length < 7)
            {
                return ActionFrameFailure("truncated_action_header", operationCode, 0, 7, body.Length);
            }

            var cursor = 7;
            if (!TryReadByteCount(body, ref cursor, "truncated_scalar_property_count", operationCode, out var scalarCount, out var failure)
                || !TrySkip(body, ref cursor, scalarCount, "truncated_scalar_property_ids", operationCode, out failure)
                || !TrySkip(body, ref cursor, scalarCount * 4, "truncated_scalar_property_values", operationCode, out failure))
            {
                return failure!;
            }
            if (!TryReadByteCount(body, ref cursor, "truncated_range_property_count", operationCode, out var rangeCount, out failure)
                || !TrySkip(body, ref cursor, rangeCount, "truncated_range_property_ids", operationCode, out failure)
                || !TrySkip(body, ref cursor, rangeCount * 8, "truncated_range_property_values", operationCode, out failure))
            {
                return failure!;
            }

            if (operationCode == 0x0400)
            {
                if (!TrySkip(body, ref cursor, 9, "truncated_play_tail", operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode == 0x2100)
            {
                // The v150 PlayEvent body ends after the common header and property bundles.
            }
            else if (operationCode == 0x1200 || operationCode == 0x1900)
            {
                if (!TrySkip(body, ref cursor, 8, "truncated_pair_tail", operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode == 0x1300 || operationCode == 0x1400)
            {
                if (!TrySkip(body, ref cursor, 15, "truncated_game_parameter_tail", operationCode, out failure)
                    || !TrySkipExceptionRows(body, ref cursor, operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode == 0x6100)
            {
                if (!TrySkip(body, ref cursor, 8, "truncated_rtpc_tail", operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode is 0x0100 or 0x0200 or 0x0300)
            {
                if (!TrySkip(body, ref cursor, 2, "truncated_action_flags", operationCode, out failure)
                    || !TrySkipExceptionRows(body, ref cursor, operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode is 0x0600 or 0x0700)
            {
                if (!TrySkip(body, ref cursor, 1, "truncated_fade_flags", operationCode, out failure)
                    || !TrySkipExceptionRows(body, ref cursor, operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode is 0x0800 or 0x0900 or 0x0A00 or 0x0B00 or 0x0C00
                or 0x0D00 or 0x0E00 or 0x0F00 or 0x2000 or 0x3000)
            {
                if (!TrySkip(body, ref cursor, 14, "truncated_value_tail", operationCode, out failure)
                    || !TrySkipExceptionRows(body, ref cursor, operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode is 0x3100 or 0x3200)
            {
                if (!TrySkip(body, ref cursor, 7, "truncated_effect_slot_tail", operationCode, out failure)
                    || !TrySkipExceptionRows(body, ref cursor, operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode is 0x3300 or 0x3400 or 0x3500 or 0x3600 or 0x3700)
            {
                if (!TrySkip(body, ref cursor, 2, "truncated_effect_flags", operationCode, out failure)
                    || !TrySkipExceptionRows(body, ref cursor, operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode == 0x1E00)
            {
                if (!TrySkip(body, ref cursor, 14, "truncated_seek_tail", operationCode, out failure)
                    || !TrySkipExceptionRows(body, ref cursor, operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode == 0x2200)
            {
                if (!TrySkip(body, ref cursor, 1, "truncated_fade_flags", operationCode, out failure)
                    || !TrySkipExceptionRows(body, ref cursor, operationCode, out failure))
                {
                    return failure!;
                }
            }
            else if (operationCode is 0x1000 or 0x1100 or 0x1A00 or 0x1B00 or 0x1F00)
            {
                // These v150 operation variants have no serialized tail after the common bundles.
            }
            else
            {
                return new EndfieldHircActionFrameResult
                {
                    Status = "unsupported",
                    OperationCode = operationCode,
                    FailureCategory = "unsupported_operation",
                    CursorOffset = cursor,
                };
            }

            if (cursor != body.Length)
            {
                return ActionFrameFailure(
                    "unexpected_trailing_bytes",
                    operationCode,
                    cursor,
                    0,
                    body.Length - cursor);
            }
            return new EndfieldHircActionFrameResult
            {
                Status = "exact",
                OperationCode = operationCode,
                CursorOffset = cursor,
            };
        }

        private static EndfieldHircActionFrameResult ActionFrameFailure(
            string category,
            ushort? operationCode,
            int offset,
            long expectedBytes,
            long actualBytes) => new()
        {
            Status = "failed",
            OperationCode = operationCode,
            FailureCategory = category,
            CursorOffset = offset,
            ExpectedBytes = expectedBytes,
            ActualBytes = actualBytes,
        };

        private static bool TryReadByteCount(
            ReadOnlySpan<byte> body,
            ref int cursor,
            string category,
            ushort operationCode,
            out int count,
            out EndfieldHircActionFrameResult failure)
        {
            if (cursor >= body.Length)
            {
                count = 0;
                failure = ActionFrameFailure(category, operationCode, cursor, 1, 0);
                return false;
            }
            count = body[cursor++];
            failure = null!;
            return true;
        }

        private static bool TrySkip(
            ReadOnlySpan<byte> body,
            ref int cursor,
            int count,
            string category,
            ushort operationCode,
            out EndfieldHircActionFrameResult failure)
        {
            var available = body.Length - cursor;
            if (count < 0 || count > available)
            {
                failure = ActionFrameFailure(category, operationCode, cursor, count, available);
                return false;
            }
            cursor += count;
            failure = null!;
            return true;
        }

        private static bool TrySkipExceptionRows(
            ReadOnlySpan<byte> body,
            ref int cursor,
            ushort operationCode,
            out EndfieldHircActionFrameResult failure)
        {
            ulong count = 0;
            var terminated = false;
            for (var index = 0; index < 5; index++)
            {
                if (cursor >= body.Length)
                {
                    failure = ActionFrameFailure("truncated_exception_count", operationCode, cursor, 1, 0);
                    return false;
                }
                var value = body[cursor++];
                if (index == 4 && (value & 0x80) != 0)
                {
                    failure = ActionFrameFailure("unterminated_exception_count", operationCode, cursor - 1, 1, 1);
                    return false;
                }
                if (index == 4 && (value & 0x70) != 0)
                {
                    failure = ActionFrameFailure("exception_count_overflow", operationCode, cursor - 1, 1, 1);
                    return false;
                }
                count |= (ulong)(value & 0x7F) << (index * 7);
                if ((value & 0x80) == 0)
                {
                    terminated = true;
                    break;
                }
            }
            if (!terminated)
            {
                failure = ActionFrameFailure("unterminated_exception_count", operationCode, cursor, 0, body.Length - cursor);
                return false;
            }

            var available = body.Length - cursor;
            var expected = count * 5UL;
            if (expected > (ulong)available)
            {
                failure = ActionFrameFailure(
                    "exception_rows_out_of_range",
                    operationCode,
                    cursor,
                    checked((long)expected),
                    available);
                return false;
            }
            cursor += checked((int)expected);
            failure = null!;
            return true;
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
            // The whole plug-in id, not just its type field. Numeric type 0x0B's source
            // records are checked against this set, and that check only has force if
            // both sides are the same sparse 32-bit value.
            HircBump(structure.Type2PluginIdCounts, $"plugin_{pluginId:X8}", 1);
            structure.Type2PrefixBytes = checked(structure.Type2PrefixBytes + (uint)prefixLength);
            var opaqueLength = checked(bodyLength - prefixLength);
            structure.Type2OpaqueTailBytes = checked(structure.Type2OpaqueTailBytes + (uint)opaqueLength);
            structure.Type2MinOpaqueTailBytes = structure.Type2PrefixCount == 1
                ? (uint)opaqueLength
                : Math.Min(structure.Type2MinOpaqueTailBytes, (uint)opaqueLength);
            structure.Type2MaxOpaqueTailBytes = Math.Max(structure.Type2MaxOpaqueTailBytes, (uint)opaqueLength);
        }

        // Breadth-first closure from every object whose identity the caller supplied.
        // Edges that leave the bank are counted, never followed: this corpus cannot
        // say what they name.
        private static void WalkHircNamedReach(
            EndfieldHircNamedReachCensus census,
            HashSet<uint> namedIdentityHashes,
            Dictionary<uint, byte> bankObjectTypes,
            Dictionary<uint, List<uint>> bankEdges,
            Dictionary<uint, uint> bankSourceIds)
        {
            if (namedIdentityHashes == null || namedIdentityHashes.Count == 0)
            {
                return;
            }
            foreach (var pair in bankObjectTypes)
            {
                if (!namedIdentityHashes.Contains(pair.Key))
                {
                    continue;
                }
                census.MatchedObjects = checked(census.MatchedObjects + 1);
                var typeKey = $"type{pair.Value:X2}";
                census.MatchesByObjectType.TryGetValue(typeKey, out var typeCount);
                census.MatchesByObjectType[typeKey] = checked(typeCount + 1);
                if (pair.Value != 4)
                {
                    continue;
                }
                census.MatchedNamedType = checked(census.MatchedNamedType + 1);

                var seen = new HashSet<uint> { pair.Key };
                var queue = new Queue<uint>();
                queue.Enqueue(pair.Key);
                var sources = new HashSet<uint>();
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (!bankEdges.TryGetValue(current, out var references))
                    {
                        continue;
                    }
                    foreach (var reference in references)
                    {
                        if (!bankObjectTypes.ContainsKey(reference))
                        {
                            census.WalkEdgesLeavingTheBank =
                                checked(census.WalkEdgesLeavingTheBank + 1);
                            continue;
                        }
                        if (!seen.Add(reference))
                        {
                            continue;
                        }
                        if (bankSourceIds.TryGetValue(reference, out var sourceId))
                        {
                            sources.Add(sourceId);
                        }
                        queue.Enqueue(reference);
                    }
                }
                if (sources.Count > 0)
                {
                    census.ReachingASource = checked(census.ReachingASource + 1);
                }
                else
                {
                    census.ReachingNoSource = checked(census.ReachingNoSource + 1);
                }
                census.ReachedSourceIds = checked(census.ReachedSourceIds + (uint)sources.Count);
                var identity = pair.Key.ToString("X8");
                census.ReachedSourceIdsByIdentity.TryGetValue(identity, out var existing);
                census.ReachedSourceIdsByIdentity[identity] =
                    Math.Max(existing, (uint)sources.Count);
            }
        }

        // Depth and acyclicity of the reference relation inside one bank. In-degree at
        // most one does not by itself exclude a cycle, so walk it.
        private static void MeasureHircReferenceShape(
            EndfieldHircReferenceCensus census,
            Dictionary<uint, uint> referrerOf)
        {
            var depth = new Dictionary<uint, uint>();
            foreach (var start in referrerOf.Keys)
            {
                if (depth.ContainsKey(start))
                {
                    continue;
                }
                var path = new List<uint>();
                var onPath = new HashSet<uint>();
                var node = start;
                uint baseDepth = 0;
                while (true)
                {
                    if (depth.TryGetValue(node, out var known))
                    {
                        baseDepth = known;
                        break;
                    }
                    // A node with no referrer is a root and sits at depth zero, so depth
                    // counts references traversed rather than nodes visited.
                    if (!referrerOf.TryGetValue(node, out var parent))
                    {
                        break;
                    }
                    if (!onPath.Add(node))
                    {
                        // Every node still on the walk belongs to a cycle or feeds one.
                        census.ReferenceCycleOrFeedingNodes =
                            checked(census.ReferenceCycleOrFeedingNodes + (uint)path.Count);
                        foreach (var member in path)
                        {
                            depth[member] = 0;
                        }
                        path.Clear();
                        break;
                    }
                    path.Add(node);
                    node = parent;
                }
                for (var i = path.Count - 1; i >= 0; i--)
                {
                    baseDepth = checked(baseDepth + 1);
                    depth[path[i]] = baseDepth;
                    if (baseDepth > census.MaximumReferenceDepth)
                    {
                        census.MaximumReferenceDepth = baseDepth;
                    }
                }
            }
        }

        // Join each anonymous reference to the bank's object identities. Resolution is
        // an identity fact; it says nothing about direction, containment or meaning.
        private static void ResolveHircReferences(
            EndfieldHircReferenceCensus census,
            EndfieldHircBodyFrameResult frame,
            byte sourceType,
            uint sourceId,
            Dictionary<uint, byte> bankObjectTypes,
            HashSet<uint> duplicateObjectIds,
            Dictionary<uint, uint> referrerCounts,
            Dictionary<uint, uint> referrerOf)
        {
            if (frame.Status != "exact" || frame.References == null)
            {
                return;
            }
            foreach (var word in frame.CandidateWords ?? new List<uint>())
            {
                census.CandidateWords = checked(census.CandidateWords + 1);
                if (bankObjectTypes.ContainsKey(word))
                {
                    census.CandidateWordsMatchingAnObject =
                        checked(census.CandidateWordsMatchingAnObject + 1);
                }
            }
            foreach (var reference in frame.References)
            {
                census.References = checked(census.References + 1);
                if (reference == sourceId)
                {
                    census.SelfReferences = checked(census.SelfReferences + 1);
                }
                referrerCounts.TryGetValue(reference, out var referrers);
                referrerCounts[reference] = checked(referrers + 1);
                if (referrers == 0)
                {
                    referrerOf[reference] = sourceId;
                }
                if (referrers == 1)
                {
                    census.TargetsWithMultipleReferrers =
                        checked(census.TargetsWithMultipleReferrers + 1);
                }
                // A reference into a duplicated id cannot name one object, so count it
                // rather than let first-wins lookup hide the ambiguity.
                if (duplicateObjectIds.Contains(reference))
                {
                    census.ReferencesToDuplicateIds =
                        checked(census.ReferencesToDuplicateIds + 1);
                }
                if (bankObjectTypes.TryGetValue(reference, out var targetType))
                {
                    census.ResolvedSameBank = checked(census.ResolvedSameBank + 1);
                    var edge = $"type{sourceType:X2}_to_type{targetType:X2}";
                    census.EdgeCounts.TryGetValue(edge, out var edgeCount);
                    census.EdgeCounts[edge] = checked(edgeCount + 1);
                }
                else
                {
                    census.UnresolvedInBank = checked(census.UnresolvedInBank + 1);
                }
            }
        }

        private static void RecordHircBodyFrame(
            EndfieldHircBodyCensus census,
            EndfieldHircBodyFrameResult result,
            ReadOnlySpan<byte> body,
            ulong bankId,
            uint ordinal,
            uint objectId)
        {
            census.FrameCount = checked(census.FrameCount + 1);
            census.BodyBytes = checked(census.BodyBytes + (uint)body.Length);
            if (result.Status == "exact")
            {
                census.ExactCount = checked(census.ExactCount + 1);
                census.ExactCursorBytes = checked(census.ExactCursorBytes + (uint)result.CursorOffset);
                census.MinExactBytes = census.ExactCount == 1
                    ? (uint)result.CursorOffset
                    : Math.Min(census.MinExactBytes, (uint)result.CursorOffset);
                census.MaxExactBytes = Math.Max(census.MaxExactBytes, (uint)result.CursorOffset);
                foreach (var pair in result.GroupCounts)
                {
                    census.GroupCounts.TryGetValue(pair.Key, out var groupTotal);
                    census.GroupCounts[pair.Key] = checked(groupTotal + pair.Value);
                }
                foreach (var pair in result.SelectorCounts)
                {
                    census.SelectorCounts.TryGetValue(pair.Key, out var selectorTotal);
                    census.SelectorCounts[pair.Key] = checked(selectorTotal + pair.Value);
                }
                return;
            }

            census.NonExactBytes = checked(census.NonExactBytes + (uint)body.Length);
            if (result.Status == "unsupported")
            {
                census.UnsupportedCount = checked(census.UnsupportedCount + 1);
                census.UnsupportedCategories.TryGetValue(result.Category, out var unsupported);
                census.UnsupportedCategories[result.Category] = checked(unsupported + 1);
            }
            else
            {
                census.FailedCount = checked(census.FailedCount + 1);
                census.FailureCounts.TryGetValue(result.Category, out var failed);
                census.FailureCounts[result.Category] = checked(failed + 1);
            }

            if (census.FailureExamples.Count < 8)
            {
                census.FailureExamples.Add(new EndfieldHircBodyFrameExample
                {
                    BankId = bankId,
                    Ordinal = ordinal,
                    ObjectId = objectId,
                    Status = result.Status,
                    Category = result.Category,
                    CursorOffset = result.CursorOffset,
                    ExpectedBytes = result.ExpectedBytes,
                    ActualBytes = result.ActualBytes,
                });
            }
        }

        // Anonymous group framing for numeric HIRC type 0x02 bodies. Group letters are
        // deliberate: the corpus proves byte extents, not field ownership or meaning.
        internal static EndfieldHircBodyFrameResult FrameType2Body(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }

            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;

            // Bounded 14-byte source prefix; plugin kind 2 adds a checked length-prefixed range.
            if (!HircTake(body, ref cursor, 4, out var failure, "prefixPluginId"))
            {
                return failure;
            }
            var pluginId = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor - 4, 4));
            if (!HircTake(body, ref cursor, 10, out failure, "prefixSourceInfo"))
            {
                return failure;
            }
            if ((pluginId & 0x0F) == 2 && pluginId != 0)
            {
                if (!HircReadUInt32(body, ref cursor, out var parameterLength, out failure, "prefixParamLength"))
                {
                    return failure;
                }
                if (parameterLength > (uint)(body.Length - cursor))
                {
                    return HircFrameOutcome(
                        "failed", "range_prefixParams", cursor, parameterLength, body.Length - cursor);
                }
                cursor = checked(cursor + (int)parameterLength);
                HircBump(groups, "prefixParamBytes", parameterLength);
            }

            if (!FrameHircNodeGroups(body, ref cursor, groups, selectors, out failure))
            {
                return failure;
            }

            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors);
        }

        // The nine anonymous groups are shared by every HIRC type whose body opens with
        // this node frame. Extents are proven by whole-corpus exact closure; group
        // letters carry no field ownership or meaning.
        private static bool FrameHircNodeGroups(
            ReadOnlySpan<byte> body,
            ref int cursor,
            Dictionary<string, uint> groups,
            Dictionary<string, uint> selectors,
            out EndfieldHircBodyFrameResult failure)
        {
            // Group A: one flag byte, one count byte, an optional shared mask byte and
            // count fixed-width slots.
            if (!HircReadByte(body, ref cursor, out var groupAFlag, out failure, "groupAFlag"))
            {
                return false;
            }
            HircBump(selectors, $"groupAFlag_{groupAFlag:X2}", 1);
            if (!HircReadByte(body, ref cursor, out var groupACount, out failure, "groupACount"))
            {
                return false;
            }
            if (groupACount > 0)
            {
                if (!HircTake(body, ref cursor, 1, out failure, "groupAMask"))
                {
                    return false;
                }
                if (!HircTake(body, ref cursor, groupACount * 6, out failure, "groupAEntries"))
                {
                    return false;
                }
            }
            HircBump(groups, "groupAEntries", groupACount);

            // Group B: one flag byte and one count byte. No body in any framed HIRC type
            // carries a nonempty vector, so its element width is unresolved and must fail
            // closed rather than assume a width from an empty sample.
            if (!HircReadByte(body, ref cursor, out var groupBFlag, out failure, "groupBFlag"))
            {
                return false;
            }
            HircBump(selectors, $"groupBFlag_{groupBFlag:X2}", 1);
            if (!HircReadByte(body, ref cursor, out var groupBCount, out failure, "groupBCount"))
            {
                return false;
            }
            if (groupBCount > 0)
            {
                failure = HircFrameOutcome(
                    "unsupported", "unsupported_groupB_nonempty", cursor - 1, 0, groupBCount);
                return false;
            }

            if (!HircTake(body, ref cursor, 9, out failure, "anonymousScalars"))
            {
                return false;
            }

            // Group C: count, count one-byte keys, count four-byte values.
            if (!HircReadByte(body, ref cursor, out var groupCCount, out failure, "groupCCount"))
            {
                return false;
            }
            if (!HircTake(body, ref cursor, groupCCount * 5, out failure, "groupCEntries"))
            {
                return false;
            }
            HircBump(groups, "groupCEntries", groupCCount);

            // Group D: count, count one-byte keys, count eight-byte values.
            if (!HircReadByte(body, ref cursor, out var groupDCount, out failure, "groupDCount"))
            {
                return false;
            }
            if (!HircTake(body, ref cursor, groupDCount * 9, out failure, "groupDEntries"))
            {
                return false;
            }
            HircBump(groups, "groupDEntries", groupDCount);

            if (!FrameHircGroupE(body, ref cursor, groups, selectors, out failure))
            {
                return false;
            }
            if (!FrameHircGroupF(body, ref cursor, selectors, out failure))
            {
                return false;
            }
            if (!HircTake(body, ref cursor, 6, out failure, "groupG"))
            {
                return false;
            }
            if (!FrameHircGroupH(body, ref cursor, groups, selectors, out failure))
            {
                return false;
            }
            return FrameHircGroupI(body, ref cursor, groups, selectors, out failure);
        }

        private static EndfieldHircBodyFrameResult HircFrameExact(
            int cursor,
            int length,
            Dictionary<string, uint> groups,
            Dictionary<string, uint> selectors,
            List<uint> references = null,
            List<uint> candidateWords = null) => new()
            {
                Status = "exact",
                Category = "",
                CursorOffset = cursor,
                ExpectedBytes = length,
                ActualBytes = cursor,
                GroupCounts = groups,
                SelectorCounts = selectors,
                References = references ?? new List<uint>(),
                CandidateWords = candidateWords ?? new List<uint>(),
            };

        // Numeric HIRC type 0x06 opens with the shared node groups, then a fixed
        // ten-byte opaque header, one counted vector of four-byte anonymous references,
        // one counted list of groups each holding its own counted reference vector, and
        // one counted vector of fourteen-byte records that open with a reference.
        internal static EndfieldHircBodyFrameResult FrameType6Body(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }

            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var references = new List<uint>();
            // Group items and record leading words match a bank object most of the time but
            // not always, so they are not claimed as references; they are counted instead.
            var candidateWords = new List<uint>();
            var cursor = 0;
            if (!FrameHircNodeGroups(body, ref cursor, groups, selectors, out var failure))
            {
                return failure;
            }
            // The header carries two id-shaped words that no bank object matches, so they
            // are consumed opaquely rather than offered to the reference join.
            if (!HircTake(body, ref cursor, 10, out failure, "suffixHeader"))
            {
                return failure;
            }
            if (!HircReadUInt32(body, ref cursor, out var childCount, out failure, "childCount"))
            {
                return failure;
            }
            if (childCount > (uint)((body.Length - cursor) / 4))
            {
                return HircFrameOutcome(
                    "failed", "range_childEntries", cursor - 4, (body.Length - cursor) / 4, childCount);
            }
            for (var i = 0U; i < childCount; i++)
            {
                references.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)));
                cursor = checked(cursor + 4);
            }
            HircBump(groups, "childEntries", childCount);

            if (!HircReadUInt32(body, ref cursor, out var groupCount, out failure, "groupCount"))
            {
                return failure;
            }
            if (groupCount > (uint)((body.Length - cursor) / 8))
            {
                return HircFrameOutcome(
                    "failed", "range_groupEntries", cursor - 4, (body.Length - cursor) / 8, groupCount);
            }
            HircBump(groups, "groupEntries", groupCount);
            HircBump(groups, "groupItemEntries", 0);
            for (var i = 0U; i < groupCount; i++)
            {
                // The group's own key is not a bank object identity either.
                if (!HircTake(body, ref cursor, 4, out failure, "groupKey"))
                {
                    return failure;
                }
                if (!HircReadUInt32(body, ref cursor, out var itemCount, out failure, "groupItemCount"))
                {
                    return failure;
                }
                if (itemCount > (uint)((body.Length - cursor) / 4))
                {
                    return HircFrameOutcome(
                        "failed", "range_groupItemEntries", cursor - 4, (body.Length - cursor) / 4, itemCount);
                }
                for (var item = 0U; item < itemCount; item++)
                {
                    candidateWords.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)));
                    cursor = checked(cursor + 4);
                }
                HircBump(groups, "groupItemEntries", itemCount);
            }

            if (!HircReadUInt32(body, ref cursor, out var recordCount, out failure, "recordCount"))
            {
                return failure;
            }
            if (recordCount > (uint)((body.Length - cursor) / 14))
            {
                return HircFrameOutcome(
                    "failed", "range_recordEntries", cursor - 4, (body.Length - cursor) / 14, recordCount);
            }
            for (var i = 0U; i < recordCount; i++)
            {
                candidateWords.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)));
                cursor = checked(cursor + 14);
            }
            HircBump(groups, "recordEntries", recordCount);

            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors, references, candidateWords);
        }

        // Numeric HIRC type 0x05 opens with the shared node groups, then a fixed opaque
        // block, one counted vector of four-byte anonymous references and one counted
        // vector of eight-byte anonymous records. The two counts are independent.
        internal static EndfieldHircBodyFrameResult FrameType5Body(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }

            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (!FrameHircNodeGroups(body, ref cursor, groups, selectors, out var failure))
            {
                return failure;
            }
            if (!HircTake(body, ref cursor, 24, out failure, "suffixHeader"))
            {
                return failure;
            }
            if (!HircReadUInt32(body, ref cursor, out var referenceCount, out failure, "referenceCount"))
            {
                return failure;
            }
            if (referenceCount > (uint)((body.Length - cursor) / 4))
            {
                return HircFrameOutcome(
                    "failed", "range_referenceEntries", cursor - 4, (body.Length - cursor) / 4, referenceCount);
            }
            var references = new List<uint>(checked((int)referenceCount));
            for (var i = 0U; i < referenceCount; i++)
            {
                references.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)));
                cursor = checked(cursor + 4);
            }
            HircBump(groups, "referenceEntries", referenceCount);
            if (!HircReadUInt16(body, ref cursor, out var recordCount, out failure, "recordCount"))
            {
                return failure;
            }
            if (recordCount > (body.Length - cursor) / 8)
            {
                return HircFrameOutcome(
                    "failed", "range_recordEntries", cursor - 2, (body.Length - cursor) / 8, recordCount);
            }
            cursor = checked(cursor + recordCount * 8);
            HircBump(groups, "recordEntries", recordCount);
            // Publish how often the two counts disagree so "the vectors are counted
            // independently" is a corpus measurement rather than an assertion.
            HircBump(groups, "referenceRecordCountMismatch", referenceCount == recordCount ? 0u : 1u);

            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors, references);
        }

        // Numeric HIRC type 0x07 opens with the shared node groups and ends with one
        // counted vector of fixed-width anonymous references.
        internal const int Type14ElementBytes = 12;
        internal const int Type11SourceRecordBytes = 14;
        // A bound so a corrupt count cannot make the reader walk the whole body.
        internal const uint Type11MaximumRecords = 64;

        internal static EndfieldHircBodyFrameResult FrameType7Body(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }

            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (!FrameHircNodeGroups(body, ref cursor, groups, selectors, out var failure))
            {
                return failure;
            }
            if (!HircReadUInt32(body, ref cursor, out var childCount, out failure, "childCount"))
            {
                return failure;
            }
            if (childCount > (uint)((body.Length - cursor) / 4))
            {
                return HircFrameOutcome(
                    "failed", "range_childEntries", cursor - 4, (body.Length - cursor) / 4, childCount);
            }
            var references = new List<uint>(checked((int)childCount));
            for (var i = 0U; i < childCount; i++)
            {
                references.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4)));
                cursor = checked(cursor + 4);
            }
            HircBump(groups, "childEntries", childCount);

            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors, references);
        }

        // Numeric type 0x0E does not use the shared node frame. Its body is a fixed
        // 21-byte head, an optional 20-byte block selected by the second byte, then a
        // counted list whose entries each carry a selector, a counted run of 12-byte
        // elements, and finally a two-byte terminator.
        //
        // The optional block is the only branch, and it is decided by a byte, not by a
        // search: byte 1 is 0 or 1 across the whole corpus and predicts the prefix
        // length in every body. Any other value fails closed rather than guessing.
        internal const int Type14FixedHeadBytes = 21;
        internal const int Type14OptionalBlockBytes = 20;

        internal static EndfieldHircBodyFrameResult FrameType14Body(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }

            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (body.Length < Type14FixedHeadBytes)
            {
                return HircFrameOutcome(
                    "failed", "short_fixedHead", 0, Type14FixedHeadBytes, body.Length);
            }
            HircBump(selectors, $"headByte_{body[0]:X2}", 1);

            var flag = body[1];
            if (flag > 1)
            {
                // The prefix length depends on this byte, so an unobserved value means the
                // body cannot be framed at all. Widening it would be a guess.
                return HircFrameOutcome("failed", "unknown_optionalBlockFlag", 1, 1, flag);
            }
            HircBump(selectors, $"optionalBlockFlag_{flag:X2}", 1);
            cursor = Type14FixedHeadBytes;
            if (flag == 1)
            {
                if (!HircTake(body, ref cursor, Type14OptionalBlockBytes, out var blockFailure, "optionalBlock"))
                {
                    return blockFailure;
                }
                HircBump(groups, "optionalBlock", 1);
            }

            if (!HircReadByte(body, ref cursor, out var entryCount, out var failure, "listEntryCount"))
            {
                return failure;
            }
            HircBump(groups, "listEntries", entryCount);
            for (var entry = 0; entry < entryCount; entry++)
            {
                if (!HircReadByte(body, ref cursor, out var entrySelector, out failure, "listEntrySelector"))
                {
                    return failure;
                }
                HircBump(selectors, $"listEntrySelector_{entrySelector:X2}", 1);
                if (!HircReadUInt16(body, ref cursor, out var elementCount, out failure, "listElementCount"))
                {
                    return failure;
                }
                if (elementCount > (body.Length - cursor) / Type14ElementBytes)
                {
                    return HircFrameOutcome(
                        "failed",
                        "range_listElements",
                        cursor - 2,
                        (body.Length - cursor) / Type14ElementBytes,
                        elementCount);
                }
                cursor = checked(cursor + elementCount * Type14ElementBytes);
                HircBump(groups, "listElements", elementCount);
            }

            // Two bytes close every body. They read as zero everywhere, so a nonzero value
            // would mean content this frame does not describe: reject instead of ignoring.
            if (!HircReadUInt16(body, ref cursor, out var terminator, out failure, "listTerminator"))
            {
                return failure;
            }
            if (terminator != 0)
            {
                return HircFrameOutcome("failed", "nonzero_listTerminator", cursor - 2, 0, terminator);
            }
            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors, null);
        }

        // Numeric type 0x16 does not use the whole node frame, but it ends with the
        // node frame's group I structure verbatim. That is why this framer is short:
        // the only new part is the counted key/value block in front of it, and group I
        // is reused rather than re-guessed.
        internal static EndfieldHircBodyFrameResult FrameType22Body(
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            if (bankVersion != 150)
            {
                return HircFrameOutcome("unsupported", "unsupported_bank_version", 0, 0, body.Length);
            }

            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (!HircReadByte(body, ref cursor, out var propertyCount, out var failure, "propertyCount"))
            {
                return failure;
            }
            // Keys and values are two parallel runs, not interleaved pairs: a key is one
            // byte and a value is four, and the keys all precede the values.
            if (propertyCount > (body.Length - cursor) / 5)
            {
                return HircFrameOutcome(
                    "failed", "range_properties", cursor - 1, (body.Length - cursor) / 5, propertyCount);
            }
            for (var i = 0; i < propertyCount; i++)
            {
                HircBump(selectors, $"propertyKey_{body[cursor + i]:X2}", 1);
            }
            cursor = checked(cursor + propertyCount);
            cursor = checked(cursor + propertyCount * 4);
            HircBump(groups, "propertyEntries", propertyCount);

            if (!HircTake(body, ref cursor, 1, out failure, "anonymousByte"))
            {
                return failure;
            }
            if (!FrameHircGroupI(body, ref cursor, groups, selectors, out failure))
            {
                return failure;
            }
            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors, null);
        }

        private const int Type17RunElementBytes = 6;

        private static void RecordType09(
            EndfieldHircType09Census census,
            ReadOnlySpan<byte> body,
            uint? bankVersion)
        {
            void Fail(string category)
            {
                census.Failed = checked(census.Failed + 1);
                HircBump(census.FailureCounts, category, 1);
            }

            if (bankVersion != 150)
            {
                Fail("unsupported_bank_version");
                return;
            }
            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            var cursor = 0;
            if (!FrameHircNodeGroups(body, ref cursor, groups, selectors, out var failure))
            {
                Fail(failure.Category ?? "nodeFrame");
                return;
            }
            if (!HircReadUInt32(body, ref cursor, out var entryCount, out failure, "type09RunCount"))
            {
                Fail(failure.Category ?? "truncated_type09RunCount");
                return;
            }
            if (entryCount > (uint)((body.Length - cursor) / 4))
            {
                Fail("range_type09Run");
                return;
            }
            cursor = checked(cursor + (int)entryCount * 4);
            if (!HircReadUInt32(body, ref cursor, out var secondCount, out failure, "type09SecondCount"))
            {
                Fail(failure.Category ?? "truncated_type09SecondCount");
                return;
            }
            if (secondCount != 0)
            {
                // A nonzero second count introduces records this reader cannot frame.
                // Consuming them by guess would produce an exact-looking body that is
                // not evidence of anything.
                census.UnestablishedSecondRun = checked(census.UnestablishedSecondRun + 1);
                return;
            }
            if (!HircReadByte(body, ref cursor, out var tailFlag, out failure, "type09TailFlag"))
            {
                Fail(failure.Category ?? "truncated_type09TailFlag");
                return;
            }
            HircBump(census.TailFlagCounts, $"tail_{tailFlag:X2}", 1);
            if (cursor != body.Length)
            {
                Fail("trailing_bytes");
                return;
            }
            census.Exact = checked(census.Exact + 1);
            census.ExactBytes = checked(census.ExactBytes + (uint)body.Length);
            census.RunEntries = checked(census.RunEntries + entryCount);
        }

        private static void RecordType17(EndfieldHircType17Census census, ReadOnlySpan<byte> body)
        {
            void Fail(string category)
            {
                census.Failed = checked(census.Failed + 1);
                HircBump(census.FailureCounts, category, 1);
            }

            if (body.Length < 8)
            {
                Fail("short_header");
                return;
            }
            var size = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4));
            if (size > (uint)(body.Length - 8))
            {
                Fail("range_section");
                return;
            }
            var cursor = checked(8 + (int)size);
            if (cursor + 1 > body.Length)
            {
                Fail("truncated_anonymousByte");
                return;
            }
            cursor = checked(cursor + 1);
            var groups = new Dictionary<string, uint>(StringComparer.Ordinal);
            var selectors = new Dictionary<string, uint>(StringComparer.Ordinal);
            if (!FrameHircGroupI(body, ref cursor, groups, selectors, out var failure))
            {
                Fail(failure.Category ?? "groupI");
                return;
            }
            if (cursor + 2 > body.Length)
            {
                Fail("truncated_optionalBlockFlag");
                return;
            }
            var flag = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
            cursor = checked(cursor + 2);
            if (flag != 0)
            {
                // Two block widths fit every flagged body and nothing distinguishes
                // them, so this body is not framed rather than framed by guess.
                census.TiedOptionalBlock = checked(census.TiedOptionalBlock + 1);
                return;
            }
            if (cursor + 2 > body.Length)
            {
                Fail("truncated_runCount");
                return;
            }
            var runCount = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
            cursor = checked(cursor + 2);
            if (runCount > (body.Length - cursor) / Type17RunElementBytes)
            {
                Fail("range_run");
                return;
            }
            cursor = checked(cursor + runCount * Type17RunElementBytes);
            if (cursor != body.Length)
            {
                Fail("trailing_bytes");
                return;
            }
            census.Exact = checked(census.Exact + 1);
            census.ExactBytes = checked(census.ExactBytes + (uint)body.Length);
            census.RunElements = checked(census.RunElements + runCount);
            groups.TryGetValue("groupIEntries", out var entries);
            census.GroupIEntries = checked(census.GroupIEntries + entries);
        }

        private static bool FrameHircGroupE(
            ReadOnlySpan<byte> body,
            ref int cursor,
            Dictionary<string, uint> groups,
            Dictionary<string, uint> selectors,
            out EndfieldHircBodyFrameResult failure)
        {
            if (!HircReadByte(body, ref cursor, out var bits, out failure, "groupESelector"))
            {
                return false;
            }
            HircBump(selectors, $"groupESelector_{bits:X2}", 1);
            var low = bits & 0x03;
            if (low == 0x02)
            {
                // Selector bit 1 alone is never observed, so this body cannot choose a
                // branch: bit-1-only and both-bits-set remain indistinguishable predicates.
                failure = HircFrameOutcome(
                    "unsupported", "unsupported_groupE_selector", cursor - 1, 0x03, low);
                return false;
            }
            if ((low & 0x02) == 0)
            {
                // Selector 0x01 bodies carry no extension, which rules out a bit-0 predicate.
                return true;
            }
            if (!HircTake(body, ref cursor, 1, out failure, "groupEFlags"))
            {
                return false;
            }
            var branch = (bits >> 5) & 0x03;
            HircBump(selectors, $"groupEBranch_{branch}", 1);
            if (branch == 0)
            {
                return true;
            }
            if (branch == 3)
            {
                failure = HircFrameOutcome(
                    "unsupported", "unsupported_groupE_branch", cursor - 2, 2, branch);
                return false;
            }
            if (!HircTake(body, ref cursor, 5, out failure, "groupEBranchHeader"))
            {
                return false;
            }
            if (!HircReadUInt32(body, ref cursor, out var vertexCount, out failure, "groupEVertexCount"))
            {
                return false;
            }
            if (vertexCount > (uint)((body.Length - cursor) / 16))
            {
                failure = HircFrameOutcome(
                    "failed", "range_groupEVertices", cursor - 4, (body.Length - cursor) / 16, vertexCount);
                return false;
            }
            cursor = checked(cursor + (int)vertexCount * 16);
            if (!HircReadUInt32(body, ref cursor, out var itemCount, out failure, "groupEItemCount"))
            {
                return false;
            }
            if (itemCount > (uint)((body.Length - cursor) / 20))
            {
                failure = HircFrameOutcome(
                    "failed", "range_groupEItems", cursor - 4, (body.Length - cursor) / 20, itemCount);
                return false;
            }
            cursor = checked(cursor + (int)itemCount * 20);
            HircBump(groups, "groupEVertices", vertexCount);
            HircBump(groups, "groupEItems", itemCount);
            return true;
        }

        private static bool FrameHircGroupF(
            ReadOnlySpan<byte> body,
            ref int cursor,
            Dictionary<string, uint> selectors,
            out EndfieldHircBodyFrameResult failure)
        {
            if (!HircReadByte(body, ref cursor, out var bits, out failure, "groupFSelector"))
            {
                return false;
            }
            HircBump(selectors, $"groupFSelector_{bits:X2}", 1);
            if ((bits & 0x08) != 0 && !HircTake(body, ref cursor, 16, out failure, "groupFBlock"))
            {
                return false;
            }
            return HircTake(body, ref cursor, 4, out failure, "groupFScalar");
        }

        // Widths above this are bucketed so a malformed bank cannot flood the published
        // selector inventory with one key per observed element count.
        private const int HircStateWidthHistogramLimit = 8;

        private static bool FrameHircGroupH(
            ReadOnlySpan<byte> body,
            ref int cursor,
            Dictionary<string, uint> groups,
            Dictionary<string, uint> selectors,
            out EndfieldHircBodyFrameResult failure)
        {
            if (!HircReadByte(body, ref cursor, out var propCount, out failure, "groupHPropCount"))
            {
                return false;
            }
            if (!HircTake(body, ref cursor, propCount * 3, out failure, "groupHProps"))
            {
                return false;
            }
            HircBump(groups, "groupHProps", propCount);
            if (!HircReadByte(body, ref cursor, out var groupCount, out failure, "groupHGroupCount"))
            {
                return false;
            }
            HircBump(groups, "groupHGroups", groupCount);
            // Seed both state counters so a body that frames no group still publishes the
            // rows; an absent row and a zero row must not look different to a consumer.
            HircBump(groups, "groupHStates", 0);
            HircBump(groups, "groupHStateElements", 0);
            for (var i = 0; i < groupCount; i++)
            {
                if (!HircTake(body, ref cursor, 5, out failure, "groupHGroupHeader"))
                {
                    return false;
                }
                if (!HircReadByte(body, ref cursor, out var stateCount, out failure, "groupHStateCount"))
                {
                    return false;
                }
                HircBump(groups, "groupHStates", stateCount);
                // Each state is a four-byte key and its own counted vector of six-byte
                // elements, not a fixed twelve bytes. Every state in types 0x02, 0x05 and
                // 0x07 carries exactly one element, so a fixed width survived all three
                // corpora; type 0x09 bodies carry two and disprove it.
                for (var state = 0; state < stateCount; state++)
                {
                    if (!HircTake(body, ref cursor, 4, out failure, "groupHStateKey"))
                    {
                        return false;
                    }
                    if (!HircReadUInt16(body, ref cursor, out var elementCount, out failure, "groupHStateElementCount"))
                    {
                        return false;
                    }
                    if (elementCount > (body.Length - cursor) / 6)
                    {
                        failure = HircFrameOutcome(
                            "failed",
                            "range_groupHStateElements",
                            cursor - 2,
                            (body.Length - cursor) / 6,
                            elementCount);
                        return false;
                    }
                    cursor = checked(cursor + elementCount * 6);
                    HircBump(groups, "groupHStateElements", elementCount);
                    // Bucket wide states rather than minting one selector key per width:
                    // a malformed bank could otherwise put 65,536 rows in the report.
                    HircBump(
                        selectors,
                        elementCount <= HircStateWidthHistogramLimit
                            ? $"groupHStateWidth_{6 + elementCount * 6}"
                            : $"groupHStateWidth_over_{6 + HircStateWidthHistogramLimit * 6}",
                        1);
                }
            }
            return true;
        }

        private static bool FrameHircGroupI(
            ReadOnlySpan<byte> body,
            ref int cursor,
            Dictionary<string, uint> groups,
            Dictionary<string, uint> selectors,
            out EndfieldHircBodyFrameResult failure)
        {
            if (!HircReadUInt16(body, ref cursor, out var entryCount, out failure, "groupICount"))
            {
                return false;
            }
            if (entryCount > (body.Length - cursor) / 14)
            {
                failure = HircFrameOutcome(
                    "failed", "range_groupIEntries", cursor - 2, (body.Length - cursor) / 14, entryCount);
                return false;
            }
            HircBump(groups, "groupIEntries", entryCount);
            for (var i = 0; i < entryCount; i++)
            {
                if (!HircTake(body, ref cursor, 6, out failure, "groupIEntryHead"))
                {
                    return false;
                }
                // One anonymous variable-size key. Type 0x02 bodies only ever spend one
                // byte here, so a fixed width survived that corpus; type 0x07 bodies
                // carry continued values and disprove it.
                var keyStart = cursor;
                if (!HircReadVariableSize(body, ref cursor, out _, out failure, "groupIKey"))
                {
                    return false;
                }
                // Publish the observed width histogram: the five-byte cap and the 32-bit
                // range are inherited policy, so a reader must be able to audit how wide
                // this corpus actually goes.
                HircBump(groups, "groupIKeyBytes", (uint)(cursor - keyStart));
                HircBump(selectors, $"groupIKeyWidth_{cursor - keyStart}", 1);
                if (!HircTake(body, ref cursor, 5, out failure, "groupIEntryTail"))
                {
                    return false;
                }
                if (!HircReadUInt16(body, ref cursor, out var pointCount, out failure, "groupIPointCount"))
                {
                    return false;
                }
                if (pointCount > (body.Length - cursor) / 12)
                {
                    failure = HircFrameOutcome(
                        "failed", "range_groupIPoints", cursor - 2, (body.Length - cursor) / 12, pointCount);
                    return false;
                }
                cursor = checked(cursor + pointCount * 12);
                HircBump(groups, "groupIPoints", pointCount);
            }
            return true;
        }

        private static void HircBump(Dictionary<string, uint> counters, string key, uint value)
        {
            if (value == 0 && !counters.ContainsKey(key))
            {
                counters[key] = 0;
                return;
            }
            counters.TryGetValue(key, out var current);
            counters[key] = checked(current + value);
        }

        private static EndfieldHircBodyFrameResult HircFrameOutcome(
            string status,
            string category,
            int cursor,
            long expected,
            long actual) => new()
            {
                Status = status,
                Category = category,
                CursorOffset = cursor,
                ExpectedBytes = expected,
                ActualBytes = actual,
                GroupCounts = new Dictionary<string, uint>(StringComparer.Ordinal),
                SelectorCounts = new Dictionary<string, uint>(StringComparer.Ordinal),
            };

        private static bool HircTake(
            ReadOnlySpan<byte> body,
            ref int cursor,
            int length,
            out EndfieldHircBodyFrameResult failure,
            string what)
        {
            if (length < 0 || body.Length - cursor < length)
            {
                failure = HircFrameOutcome("failed", $"truncated_{what}", cursor, length, body.Length - cursor);
                return false;
            }
            cursor = checked(cursor + length);
            failure = null;
            return true;
        }

        // Seven-bit continuation groups, least significant first, at most five bytes.
        // This matches the encoding the type 0x03 Action reader already validates.
        private static bool HircReadVariableSize(
            ReadOnlySpan<byte> body,
            ref int cursor,
            out ulong value,
            out EndfieldHircBodyFrameResult failure,
            string what)
        {
            value = 0;
            // Four bytes may continue; the fifth must terminate, so there is no
            // unreachable fall-through and every guard below is separately reachable.
            for (var index = 0; index < 4; index++)
            {
                if (body.Length - cursor < 1)
                {
                    failure = HircFrameOutcome("failed", $"truncated_{what}", cursor, 1, 0);
                    return false;
                }
                var current = body[cursor];
                cursor = checked(cursor + 1);
                value |= (ulong)(current & 0x7F) << (index * 7);
                if ((current & 0x80) == 0)
                {
                    failure = null;
                    return true;
                }
            }
            if (body.Length - cursor < 1)
            {
                failure = HircFrameOutcome("failed", $"truncated_{what}", cursor, 1, 0);
                return false;
            }
            var last = body[cursor];
            cursor = checked(cursor + 1);
            if ((last & 0x80) != 0)
            {
                failure = HircFrameOutcome("failed", $"unterminated_{what}", cursor - 1, 1, 1);
                return false;
            }
            // Byte five contributes bits 28..34, so bits 4..6 would exceed 32 bits.
            if ((last & 0x70) != 0)
            {
                failure = HircFrameOutcome("failed", $"overflow_{what}", cursor - 1, 1, 1);
                return false;
            }
            value |= (ulong)(last & 0x7F) << 28;
            failure = null;
            return true;
        }

        private static bool HircReadByte(
            ReadOnlySpan<byte> body,
            ref int cursor,
            out byte value,
            out EndfieldHircBodyFrameResult failure,
            string what)
        {
            if (body.Length - cursor < 1)
            {
                value = 0;
                failure = HircFrameOutcome("failed", $"truncated_{what}", cursor, 1, body.Length - cursor);
                return false;
            }
            value = body[cursor];
            cursor = checked(cursor + 1);
            failure = null;
            return true;
        }

        private static bool HircReadUInt16(
            ReadOnlySpan<byte> body,
            ref int cursor,
            out ushort value,
            out EndfieldHircBodyFrameResult failure,
            string what)
        {
            if (body.Length - cursor < 2)
            {
                value = 0;
                failure = HircFrameOutcome("failed", $"truncated_{what}", cursor, 2, body.Length - cursor);
                return false;
            }
            value = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2));
            cursor = checked(cursor + 2);
            failure = null;
            return true;
        }

        private static bool HircReadUInt32(
            ReadOnlySpan<byte> body,
            ref int cursor,
            out uint value,
            out EndfieldHircBodyFrameResult failure,
            string what)
        {
            if (body.Length - cursor < 4)
            {
                value = 0;
                failure = HircFrameOutcome("failed", $"truncated_{what}", cursor, 4, body.Length - cursor);
                return false;
            }
            value = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
            cursor = checked(cursor + 4);
            failure = null;
            return true;
        }

        private static void RecordType4U32VectorFrame(
            ReadOnlySpan<byte> body,
            ulong bankId,
            uint ordinal,
            uint objectId,
            EndfieldBnkStructure structure,
            Dictionary<uint, byte> bankObjectTypes,
            HashSet<uint> duplicateObjectIds,
            Dictionary<uint, uint> referrerCounts,
            Dictionary<uint, uint> referrerOf)
        {
            structure.Type4U32VectorFrameCount = checked(structure.Type4U32VectorFrameCount + 1);
            structure.Type4U32VectorBodyBytes = checked(
                structure.Type4U32VectorBodyBytes + (uint)body.Length);

            if (body.Length < 1)
            {
                RecordType4U32VectorFailure(
                    "truncated_count",
                    1,
                    body.Length,
                    bankId,
                    ordinal,
                    objectId,
                    structure);
                structure.Type4U32VectorFailedBodyBytes = checked(
                    structure.Type4U32VectorFailedBodyBytes + (uint)body.Length);
                return;
            }

            var entryCount = body[0];
            var expectedBytes = checked(1 + entryCount * sizeof(uint));
            if (expectedBytes > body.Length)
            {
                RecordType4U32VectorFailure(
                    "truncated_entries",
                    expectedBytes,
                    body.Length,
                    bankId,
                    ordinal,
                    objectId,
                    structure);
                structure.Type4U32VectorFailedBodyBytes = checked(
                    structure.Type4U32VectorFailedBodyBytes + (uint)body.Length);
                return;
            }

            structure.Type4U32VectorPrefixBytes = checked(
                structure.Type4U32VectorPrefixBytes + (uint)expectedBytes);
            structure.Type4U32VectorEntryCount = checked(
                structure.Type4U32VectorEntryCount + entryCount);
            if (expectedBytes == body.Length)
            {
                structure.Type4U32VectorExactCount = checked(
                    structure.Type4U32VectorExactCount + 1);
                structure.Type4U32VectorExactCursorBytes = checked(
                    structure.Type4U32VectorExactCursorBytes + (uint)body.Length);
                // Only an exact body's entries reach the reference census, so publish
                // that subset rather than the total the gate would otherwise tie against.
                structure.Type4U32VectorExactEntryCount = checked(
                    structure.Type4U32VectorExactEntryCount + entryCount);
                var references = new List<uint>(entryCount);
                for (var entry = 0; entry < entryCount; entry++)
                {
                    references.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                        body.Slice(checked(1 + entry * 4), 4)));
                }
                ResolveHircReferences(
                    structure.ReferenceCensus,
                    HircFrameExact(body.Length, body.Length, new Dictionary<string, uint>(StringComparer.Ordinal), new Dictionary<string, uint>(StringComparer.Ordinal), references),
                    4,
                    objectId,
                    bankObjectTypes,
                    duplicateObjectIds,
                    referrerCounts,
                    referrerOf);
                return;
            }

            structure.Type4U32VectorUnsupportedCount = checked(
                structure.Type4U32VectorUnsupportedCount + 1);
            const string unsupportedCategory = "opaque_tail_after_candidate_vector";
            structure.Type4U32VectorUnsupportedCategories.TryGetValue(unsupportedCategory, out var unsupportedCount);
            structure.Type4U32VectorUnsupportedCategories[unsupportedCategory] = checked(unsupportedCount + 1);
            structure.Type4U32VectorUnsupportedPrefixBytes = checked(
                structure.Type4U32VectorUnsupportedPrefixBytes + (uint)expectedBytes);
            structure.Type4U32VectorOpaqueTailBytes = checked(
                structure.Type4U32VectorOpaqueTailBytes + (uint)(body.Length - expectedBytes));
            if (structure.Type4U32VectorFailureExamples.Count < 8)
            {
                structure.Type4U32VectorFailureExamples.Add(new EndfieldHircType4VectorFrameExample
                {
                    BankId = bankId,
                    Ordinal = ordinal,
                    ObjectId = objectId,
                    Status = "unsupported",
                    Category = "opaque_tail_after_candidate_vector",
                    ExpectedBytes = expectedBytes,
                    ActualBytes = body.Length,
                    OpaqueTailBytes = body.Length - expectedBytes,
                });
            }
        }

        private static void RecordType4U32VectorFailure(
            string category,
            int expectedBytes,
            int actualBytes,
            ulong bankId,
            uint ordinal,
            uint objectId,
            EndfieldBnkStructure structure)
        {
            structure.Type4U32VectorFailedCount = checked(
                structure.Type4U32VectorFailedCount + 1);
            structure.Type4U32VectorFailureCounts.TryGetValue(category, out var failureCount);
            structure.Type4U32VectorFailureCounts[category] = checked(failureCount + 1);
            if (structure.Type4U32VectorFailureExamples.Count < 8)
            {
                structure.Type4U32VectorFailureExamples.Add(new EndfieldHircType4VectorFrameExample
                {
                    BankId = bankId,
                    Ordinal = ordinal,
                    ObjectId = objectId,
                    Status = "failed",
                    Category = category,
                    ExpectedBytes = expectedBytes,
                    ActualBytes = actualBytes,
                    OpaqueTailBytes = 0,
                });
            }
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
        public Dictionary<string, uint> Type2PluginIdCounts { get; } = new(StringComparer.Ordinal);
        public uint Type2PrefixBytes { get; set; }
        public uint Type2OpaqueTailBytes { get; set; }
        public uint Type2MinOpaqueTailBytes { get; set; }
        public uint Type2MaxOpaqueTailBytes { get; set; }
        public uint Type3ActionFrameCount { get; set; }
        public uint Type3ActionExactCount { get; set; }
        public uint Type3ActionUnsupportedCount { get; set; }
        public uint Type3ActionFailedCount { get; set; }
        public uint Type3ActionBodyBytes { get; set; }
        public uint Type3ActionExactCursorBytes { get; set; }
        public Dictionary<ushort, uint> Type3ActionOperationCounts { get; } = new();
        public Dictionary<string, uint> Type3ActionFailureCounts { get; } = new(StringComparer.Ordinal);
        public List<EndfieldHircActionFrameFailure> Type3ActionFailureExamples { get; } = new();
        public uint Type4U32VectorFrameCount { get; set; }
        public uint Type4U32VectorExactCount { get; set; }
        public uint Type4U32VectorUnsupportedCount { get; set; }
        public uint Type4U32VectorFailedCount { get; set; }
        public uint Type4U32VectorBodyBytes { get; set; }
        public uint Type4U32VectorPrefixBytes { get; set; }
        public uint Type4U32VectorUnsupportedPrefixBytes { get; set; }
        public uint Type4U32VectorExactCursorBytes { get; set; }
        public uint Type4U32VectorOpaqueTailBytes { get; set; }
        public uint Type4U32VectorFailedBodyBytes { get; set; }
        public uint Type4U32VectorEntryCount { get; set; }
        public uint Type4U32VectorExactEntryCount { get; set; }
        public Dictionary<string, uint> Type4U32VectorFailureCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> Type4U32VectorUnsupportedCategories { get; } = new(StringComparer.Ordinal);
        public List<EndfieldHircType4VectorFrameExample> Type4U32VectorFailureExamples { get; } = new();
        public EndfieldHircReferenceCensus ReferenceCensus { get; } = new();
        public EndfieldHircNamedReachCensus NamedReachCensus { get; } = new();
        public EndfieldHircBodyCensus Type14Body { get; } = new();
        public EndfieldHircBodyCensus Type22Body { get; } = new();
        public EndfieldHircMusicHeadReferenceCensus MusicHeadReferences { get; } = new();
        public EndfieldHircType11SourceCensus Type11Sources { get; } = new();
        public EndfieldHircType08HeadCensus Type08Head { get; } = new();
        public EndfieldHircType17Census Type17 { get; } = new();
        public EndfieldHircType09Census Type09 { get; } = new();
        public EndfieldHircBodyCensus Type2Body { get; } = new();
        public EndfieldHircBodyCensus Type5Body { get; } = new();
        public EndfieldHircBodyCensus Type6Body { get; } = new();
        public EndfieldHircBodyCensus Type7Body { get; } = new();
    }

    // One numeric HIRC type's whole-body census. Every type that opens with the shared
    // node frame accumulates through this, so their counters cannot drift apart.
    public sealed class EndfieldHircBodyCensus
    {
        public uint FrameCount { get; set; }
        public uint ExactCount { get; set; }
        public uint UnsupportedCount { get; set; }
        public uint FailedCount { get; set; }
        public uint BodyBytes { get; set; }
        public uint ExactCursorBytes { get; set; }
        public uint NonExactBytes { get; set; }
        public uint MinExactBytes { get; set; }
        public uint MaxExactBytes { get; set; }
        public Dictionary<string, uint> GroupCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> SelectorCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> FailureCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> UnsupportedCategories { get; } = new(StringComparer.Ordinal);
        public List<EndfieldHircBodyFrameExample> FailureExamples { get; } = new();
    }

    // Where each anonymous reference lands. This is an identity join, not a claim
    // about what the relation means.
    public sealed class EndfieldHircReferenceCensus
    {
        public uint References { get; set; }
        public uint ResolvedSameBank { get; set; }
        public uint UnresolvedInBank { get; set; }
        public uint SelfReferences { get; set; }
        public uint TargetsWithMultipleReferrers { get; set; }
        public uint DuplicateObjectIds { get; set; }
        public uint ReferencesToDuplicateIds { get; set; }
        public uint CandidateWords { get; set; }
        public uint CandidateWordsMatchingAnObject { get; set; }
        // Nodes on a cycle plus any tail feeding it: the walk stops at the repeat and
        // cannot separate the two, so the name must not promise cycle membership.
        public uint ReferenceCycleOrFeedingNodes { get; set; }
        public uint DistinctDuplicateObjectIds { get; set; }
        public Dictionary<string, uint> ObjectCountsByType { get; } = new(StringComparer.Ordinal);
        public uint MaximumReferenceDepth { get; set; }
        public Dictionary<string, uint> EdgeCounts { get; } = new(StringComparer.Ordinal);
    }

    // Where a shipped identifier reaches. Counters only: the walk direction is the
    // physical one, which object's body holds the value, and nothing is named here
    // except the entry point the caller supplied a hash for.
    // Numeric types 0x0A and 0x0D are not framed: their bodies still contain a
    // variable-length region nobody has isolated. One thing in them *is* determined,
    // and only that is recorded here -- a single 32-bit word near the head whose
    // offset is selected by body byte 2. This is identity resolution, not framing,
    // and it says nothing about what the relation means.
    // Numeric type 0x0B is not framed either, but its head is a counted run of
    // fourteen-byte records whose first word is a plug-in id drawn from the same
    // sparse set numeric type 0x02 uses. Plug-in ids are not small integers, so a
    // wrong record stride would scatter them out of that set almost immediately --
    // which is what makes the stride evidence rather than an assumption.
    // Numeric type 0x08 is not framed. Its leading 32-bit word, however, is either
    // null or the identity of an object in the same bank -- never a non-null value
    // that names nothing. Null is a real outcome here, not a failure, so it is
    // counted separately instead of being folded into either side.
    // Numeric type 0x11: an eight-byte header whose second word sizes an opaque
    // section, one byte, the node frame's group I structure, a flag, and a counted
    // run of six-byte elements.
    //
    // The flag gates an optional block whose width this corpus cannot determine.
    // Two widths, 21 and 27, consume every flagged body exactly -- they are the same
    // bytes read two ways, with 27 swallowing the run's single element and reading a
    // zero count. The flag is never greater than 1 anywhere, so no body can separate
    // them, and a flagged body is therefore fenced as unsupported rather than framed
    // on a coin flip.
    // Numeric type 0x09 does use the shared node frame -- it opens every one of its
    // bodies. After it comes a counted run of four-byte entries, then a second count.
    // When that second count is zero the body ends with one more byte and is consumed
    // exactly. When it is not zero it introduces records whose shape is not
    // established, so those bodies are fenced rather than framed.
    public sealed class EndfieldHircType09Census
    {
        public uint Bodies { get; set; }
        public uint Exact { get; set; }
        public uint UnestablishedSecondRun { get; set; }
        public uint Failed { get; set; }
        public uint ExactBytes { get; set; }
        public uint BodyBytes { get; set; }
        public uint RunEntries { get; set; }
        public Dictionary<string, uint> FailureCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> TailFlagCounts { get; } = new(StringComparer.Ordinal);
    }

    public sealed class EndfieldHircType17Census
    {
        public uint Bodies { get; set; }
        public uint Exact { get; set; }
        public uint TiedOptionalBlock { get; set; }
        public uint Failed { get; set; }
        public uint ExactBytes { get; set; }
        public uint BodyBytes { get; set; }
        public uint RunElements { get; set; }
        public uint GroupIEntries { get; set; }
        public Dictionary<string, uint> FailureCounts { get; } = new(StringComparer.Ordinal);
    }

    public sealed class EndfieldHircType08HeadCensus
    {
        public uint Bodies { get; set; }
        public uint Resolved { get; set; }
        public uint Null { get; set; }
        public uint Unresolved { get; set; }
        public uint TooShort { get; set; }
    }

    public sealed class EndfieldHircType11SourceCensus
    {
        public uint Bodies { get; set; }
        public uint BodiesWithRecords { get; set; }
        public uint Records { get; set; }
        public uint RecordsOutOfRange { get; set; }
        public uint TooShort { get; set; }
        public Dictionary<string, uint> PluginIdCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> StreamTypeCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> RecordCountCounts { get; } = new(StringComparer.Ordinal);
    }

    public sealed class EndfieldHircMusicHeadReferenceCensus
    {
        public uint Bodies { get; set; }
        public uint Resolved { get; set; }
        public uint Unresolved { get; set; }
        public uint Zero { get; set; }
        public uint UnknownDiscriminant { get; set; }
        // Bodies whose first byte is not 0. Only numeric type 0x0C has any, and their
        // head is a different shape, so they are excluded from the claim and counted
        // here rather than quietly shrinking the denominator.
        public uint UnknownHeadShape { get; set; }
        public uint TooShort { get; set; }
        public Dictionary<string, uint> BodiesByType { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> OffsetCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> DiscriminantCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> HeadShapeCounts { get; } = new(StringComparer.Ordinal);
    }

    public sealed class EndfieldHircNamedReachCensus
    {
        public uint MatchedObjects { get; set; }
        public uint MatchedNamedType { get; set; }
        public uint ReachingASource { get; set; }
        public uint ReachingNoSource { get; set; }
        public uint ReachedSourceIds { get; set; }
        public uint WalkEdgesLeavingTheBank { get; set; }
        public Dictionary<string, uint> MatchesByObjectType { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> ReachedSourceIdsByIdentity { get; } = new(StringComparer.Ordinal);
    }

    public sealed class EndfieldHircBodyFrameExample
    {
        public ulong BankId { get; init; }
        public uint Ordinal { get; init; }
        public uint ObjectId { get; init; }
        public string Status { get; init; } = "";
        public string Category { get; init; } = "";
        public int CursorOffset { get; init; }
        public long ExpectedBytes { get; init; }
        public long ActualBytes { get; init; }
    }

    public sealed class EndfieldHircBodyFrameResult
    {
        public string Status { get; init; } = "";
        public string Category { get; init; } = "";
        public int CursorOffset { get; init; }
        public long ExpectedBytes { get; init; }
        public long ActualBytes { get; init; }
        public Dictionary<string, uint> GroupCounts { get; init; }
        public Dictionary<string, uint> SelectorCounts { get; init; }
        public List<uint> References { get; init; }
        // Words that look like identities but are not claimed as references. They are
        // counted against the bank so the counter-evidence is published, not hidden.
        public List<uint> CandidateWords { get; init; }
    }

    public sealed class EndfieldHircActionFrameResult
    {
        public string Status { get; init; } = "";
        public ushort? OperationCode { get; init; }
        public string FailureCategory { get; init; } = "";
        public int CursorOffset { get; init; }
        public long? ExpectedBytes { get; init; }
        public long? ActualBytes { get; init; }
    }

    public sealed class EndfieldHircActionFrameFailure
    {
        public ulong BankId { get; init; }
        public uint Ordinal { get; init; }
        public uint ObjectId { get; init; }
        public ushort? OperationCode { get; init; }
        public string Status { get; init; } = "";
        public string FailureCategory { get; init; } = "";
        public int CursorOffset { get; init; }
        public long? ExpectedBytes { get; init; }
        public long? ActualBytes { get; init; }
    }

    public sealed class EndfieldHircType4VectorFrameExample
    {
        public ulong BankId { get; init; }
        public uint Ordinal { get; init; }
        public uint ObjectId { get; init; }
        public string Status { get; init; } = "";
        public string Category { get; init; } = "";
        public int ExpectedBytes { get; init; }
        public int ActualBytes { get; init; }
        public int OpaqueTailBytes { get; init; }
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
