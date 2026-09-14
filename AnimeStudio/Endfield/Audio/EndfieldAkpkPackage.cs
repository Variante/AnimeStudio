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
                if (objectType == 2)
                {
                    RecordType2BodyFrame(
                        payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4)),
                        structure.Version,
                        bankId,
                        ordinal,
                        objectId,
                        structure);
                }
                if (objectType == 7)
                {
                    RecordType7BodyFrame(
                        payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4)),
                        structure.Version,
                        bankId,
                        ordinal,
                        objectId,
                        structure);
                }
                if (objectType == 4)
                {
                    RecordType4U32VectorFrame(
                        payload.AsSpan(checked(cursor + 9), checked((int)objectSize - 4)),
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
            structure.Type2PrefixBytes = checked(structure.Type2PrefixBytes + (uint)prefixLength);
            var opaqueLength = checked(bodyLength - prefixLength);
            structure.Type2OpaqueTailBytes = checked(structure.Type2OpaqueTailBytes + (uint)opaqueLength);
            structure.Type2MinOpaqueTailBytes = structure.Type2PrefixCount == 1
                ? (uint)opaqueLength
                : Math.Min(structure.Type2MinOpaqueTailBytes, (uint)opaqueLength);
            structure.Type2MaxOpaqueTailBytes = Math.Max(structure.Type2MaxOpaqueTailBytes, (uint)opaqueLength);
        }

        private static void RecordType2BodyFrame(
            ReadOnlySpan<byte> body,
            uint? bankVersion,
            ulong bankId,
            uint ordinal,
            uint objectId,
            EndfieldBnkStructure structure)
        {
            var result = FrameType2Body(body, bankVersion);
            structure.Type2BodyFrameCount = checked(structure.Type2BodyFrameCount + 1);
            structure.Type2BodyBytes = checked(structure.Type2BodyBytes + (uint)body.Length);
            if (result.Status == "exact")
            {
                structure.Type2BodyExactCount = checked(structure.Type2BodyExactCount + 1);
                structure.Type2BodyExactCursorBytes = checked(
                    structure.Type2BodyExactCursorBytes + (uint)result.CursorOffset);
                structure.Type2BodyMinExactBytes = structure.Type2BodyExactCount == 1
                    ? (uint)result.CursorOffset
                    : Math.Min(structure.Type2BodyMinExactBytes, (uint)result.CursorOffset);
                structure.Type2BodyMaxExactBytes = Math.Max(
                    structure.Type2BodyMaxExactBytes, (uint)result.CursorOffset);
                foreach (var pair in result.GroupCounts)
                {
                    structure.Type2BodyGroupCounts.TryGetValue(pair.Key, out var groupTotal);
                    structure.Type2BodyGroupCounts[pair.Key] = checked(groupTotal + pair.Value);
                }
                foreach (var pair in result.SelectorCounts)
                {
                    structure.Type2BodySelectorCounts.TryGetValue(pair.Key, out var selectorTotal);
                    structure.Type2BodySelectorCounts[pair.Key] = checked(selectorTotal + pair.Value);
                }
                return;
            }

            structure.Type2BodyNonExactBytes = checked(
                structure.Type2BodyNonExactBytes + (uint)body.Length);
            if (result.Status == "unsupported")
            {
                structure.Type2BodyUnsupportedCount = checked(structure.Type2BodyUnsupportedCount + 1);
                structure.Type2BodyUnsupportedCategories.TryGetValue(result.Category, out var unsupported);
                structure.Type2BodyUnsupportedCategories[result.Category] = checked(unsupported + 1);
            }
            else
            {
                structure.Type2BodyFailedCount = checked(structure.Type2BodyFailedCount + 1);
                structure.Type2BodyFailureCounts.TryGetValue(result.Category, out var failed);
                structure.Type2BodyFailureCounts[result.Category] = checked(failed + 1);
            }

            if (structure.Type2BodyFailureExamples.Count < 8)
            {
                structure.Type2BodyFailureExamples.Add(new EndfieldHircBodyFrameExample
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

        private static void RecordType7BodyFrame(
            ReadOnlySpan<byte> body,
            uint? bankVersion,
            ulong bankId,
            uint ordinal,
            uint objectId,
            EndfieldBnkStructure structure)
        {
            var result = FrameType7Body(body, bankVersion);
            structure.Type7BodyFrameCount = checked(structure.Type7BodyFrameCount + 1);
            structure.Type7BodyBytes = checked(structure.Type7BodyBytes + (uint)body.Length);
            if (result.Status == "exact")
            {
                structure.Type7BodyExactCount = checked(structure.Type7BodyExactCount + 1);
                structure.Type7BodyExactCursorBytes = checked(
                    structure.Type7BodyExactCursorBytes + (uint)result.CursorOffset);
                structure.Type7BodyMinExactBytes = structure.Type7BodyExactCount == 1
                    ? (uint)result.CursorOffset
                    : Math.Min(structure.Type7BodyMinExactBytes, (uint)result.CursorOffset);
                structure.Type7BodyMaxExactBytes = Math.Max(
                    structure.Type7BodyMaxExactBytes, (uint)result.CursorOffset);
                foreach (var pair in result.GroupCounts)
                {
                    structure.Type7BodyGroupCounts.TryGetValue(pair.Key, out var groupTotal);
                    structure.Type7BodyGroupCounts[pair.Key] = checked(groupTotal + pair.Value);
                }
                foreach (var pair in result.SelectorCounts)
                {
                    structure.Type7BodySelectorCounts.TryGetValue(pair.Key, out var selectorTotal);
                    structure.Type7BodySelectorCounts[pair.Key] = checked(selectorTotal + pair.Value);
                }
                return;
            }

            structure.Type7BodyNonExactBytes = checked(
                structure.Type7BodyNonExactBytes + (uint)body.Length);
            if (result.Status == "unsupported")
            {
                structure.Type7BodyUnsupportedCount = checked(structure.Type7BodyUnsupportedCount + 1);
                structure.Type7BodyUnsupportedCategories.TryGetValue(result.Category, out var unsupported);
                structure.Type7BodyUnsupportedCategories[result.Category] = checked(unsupported + 1);
            }
            else
            {
                structure.Type7BodyFailedCount = checked(structure.Type7BodyFailedCount + 1);
                structure.Type7BodyFailureCounts.TryGetValue(result.Category, out var failed);
                structure.Type7BodyFailureCounts[result.Category] = checked(failed + 1);
            }

            if (structure.Type7BodyFailureExamples.Count < 8)
            {
                structure.Type7BodyFailureExamples.Add(new EndfieldHircBodyFrameExample
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
            if (!FrameHircGroupH(body, ref cursor, groups, out failure))
            {
                return false;
            }
            return FrameHircGroupI(body, ref cursor, groups, selectors, out failure);
        }

        private static EndfieldHircBodyFrameResult HircFrameExact(
            int cursor,
            int length,
            Dictionary<string, uint> groups,
            Dictionary<string, uint> selectors) => new()
            {
                Status = "exact",
                Category = "",
                CursorOffset = cursor,
                ExpectedBytes = length,
                ActualBytes = cursor,
                GroupCounts = groups,
                SelectorCounts = selectors,
            };

        // Numeric HIRC type 0x07 opens with the shared node groups and ends with one
        // counted vector of fixed-width anonymous references.
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
            cursor = checked(cursor + (int)childCount * 4);
            HircBump(groups, "childEntries", childCount);

            if (cursor != body.Length)
            {
                return HircFrameOutcome("failed", "trailing_bytes", cursor, body.Length, cursor);
            }
            return HircFrameExact(cursor, body.Length, groups, selectors);
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

        private static bool FrameHircGroupH(
            ReadOnlySpan<byte> body,
            ref int cursor,
            Dictionary<string, uint> groups,
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
                if (!HircTake(body, ref cursor, stateCount * 12, out failure, "groupHStates"))
                {
                    return false;
                }
                HircBump(groups, "groupHStates", stateCount);
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
            EndfieldBnkStructure structure)
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
        public Dictionary<string, uint> Type4U32VectorFailureCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> Type4U32VectorUnsupportedCategories { get; } = new(StringComparer.Ordinal);
        public List<EndfieldHircType4VectorFrameExample> Type4U32VectorFailureExamples { get; } = new();
        public uint Type2BodyFrameCount { get; set; }
        public uint Type2BodyExactCount { get; set; }
        public uint Type2BodyUnsupportedCount { get; set; }
        public uint Type2BodyFailedCount { get; set; }
        public uint Type2BodyBytes { get; set; }
        public uint Type2BodyExactCursorBytes { get; set; }
        public uint Type2BodyMinExactBytes { get; set; }
        public uint Type2BodyMaxExactBytes { get; set; }
        public uint Type2BodyNonExactBytes { get; set; }
        public Dictionary<string, uint> Type2BodyGroupCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> Type2BodySelectorCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> Type2BodyFailureCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> Type2BodyUnsupportedCategories { get; } = new(StringComparer.Ordinal);
        public List<EndfieldHircBodyFrameExample> Type2BodyFailureExamples { get; } = new();
        public uint Type7BodyFrameCount { get; set; }
        public uint Type7BodyExactCount { get; set; }
        public uint Type7BodyUnsupportedCount { get; set; }
        public uint Type7BodyFailedCount { get; set; }
        public uint Type7BodyBytes { get; set; }
        public uint Type7BodyExactCursorBytes { get; set; }
        public uint Type7BodyNonExactBytes { get; set; }
        public uint Type7BodyMinExactBytes { get; set; }
        public uint Type7BodyMaxExactBytes { get; set; }
        public Dictionary<string, uint> Type7BodyGroupCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> Type7BodySelectorCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> Type7BodyFailureCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, uint> Type7BodyUnsupportedCategories { get; } = new(StringComparer.Ordinal);
        public List<EndfieldHircBodyFrameExample> Type7BodyFailureExamples { get; } = new();
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
