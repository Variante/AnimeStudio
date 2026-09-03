using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Org.Brotli.Dec;

namespace AnimeStudio.Endfield
{
    /// <summary>
    /// Decodes ExtendData/Main/CompressData.bin, the indexed Brotli/UTF-16LE
    /// BehaviourTree container used by the Endfield client.
    /// </summary>
    public static class EndfieldCompressData
    {
        private const int HeaderLength = sizeof(uint);
        private const int OffsetLength = sizeof(uint);
        private const int RecordHeaderLength = sizeof(uint) * 2;
        private const int MaxRecordLength = 256 * 1024 * 1024;

        public static EndfieldCompressDataDocument Decode(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (data.Length < HeaderLength)
            {
                throw new EndfieldCompressDataException("file is shorter than the record-count header");
            }

            var countValue = ReadUInt32(data, 0, "record count");
            if (countValue > int.MaxValue)
            {
                throw new EndfieldCompressDataException($"record count {countValue} exceeds the supported limit");
            }

            var count = (int)countValue;
            var tableLength = HeaderLength + (long)count * OffsetLength;
            if (tableLength > data.Length)
            {
                throw new EndfieldCompressDataException(
                    $"offset table ends at {tableLength}, beyond file length {data.Length}");
            }

            if (count == 0 && data.Length != tableLength)
            {
                throw new EndfieldCompressDataException(
                    $"empty container has {data.Length - tableLength} trailing bytes");
            }

            var offsets = new int[count];
            var previousOffset = -1L;
            for (var index = 0; index < count; index++)
            {
                var offset = ReadUInt32(data, HeaderLength + index * OffsetLength, $"record {index} offset");
                if (offset > int.MaxValue || offset > data.Length)
                {
                    throw new EndfieldCompressDataException(
                        $"record {index} offset {offset} is outside the file");
                }

                if (index == 0)
                {
                    if (offset != tableLength)
                    {
                        throw new EndfieldCompressDataException(
                            $"first record offset {offset} does not equal offset-table end {tableLength}");
                    }
                }
                else if (offset <= previousOffset)
                {
                    throw new EndfieldCompressDataException(
                        $"record offsets are not strictly increasing at record {index}: {previousOffset}, {offset}");
                }

                offsets[index] = (int)offset;
                previousOffset = offset;
            }

            var records = new List<EndfieldCompressDataRecord>(count);
            for (var index = 0; index < count; index++)
            {
                var offset = offsets[index];
                var end = index + 1 < count ? offsets[index + 1] : data.Length;
                var recordLength = end - offset;
                if (recordLength < RecordHeaderLength)
                {
                    throw new EndfieldCompressDataException(
                        $"record {index} at offset {offset} is shorter than its header");
                }

                var compressedLength = ReadUInt32(data, offset, $"record {index} compressed length");
                var uncompressedLength = ReadUInt32(data, offset + sizeof(uint), $"record {index} uncompressed length");
                if (compressedLength > MaxRecordLength || uncompressedLength > MaxRecordLength)
                {
                    throw new EndfieldCompressDataException(
                        $"record {index} exceeds the {MaxRecordLength}-byte safety limit");
                }

                var expectedRecordLength = RecordHeaderLength + (long)compressedLength;
                if (expectedRecordLength != recordLength)
                {
                    throw new EndfieldCompressDataException(
                        $"record {index} length mismatch: header declares {expectedRecordLength} bytes, span is {recordLength}");
                }

                var jsonText = DecodeRecord(data, index, offset + RecordHeaderLength, (int)compressedLength, (int)uncompressedLength);
                JObject json;
                try
                {
                    var token = ParseJsonStrict(jsonText, index);
                    json = token as JObject
                        ?? throw new EndfieldCompressDataException($"record {index} JSON root is {token.Type}, expected object");
                }
                catch (EndfieldCompressDataException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new EndfieldCompressDataException($"record {index} contains invalid JSON", exception);
                }

                var rootTypeToken = json["$type"] ?? json["type"];
                var rootType = rootTypeToken?.Type == JTokenType.String
                    ? rootTypeToken.Value<string>()
                    : null;
                records.Add(new EndfieldCompressDataRecord(
                    index,
                    offset,
                    (int)compressedLength,
                    (int)uncompressedLength,
                    jsonText,
                    json,
                    rootType));
            }

            return new EndfieldCompressDataDocument(data.Length, records);
        }

        private static string DecodeRecord(byte[] data, int index, int compressedOffset, int compressedLength, int uncompressedLength)
        {
            try
            {
                using var compressed = new MemoryStream(data, compressedOffset, compressedLength, writable: false);
                using var brotli = new BrotliInputStream(compressed);
                using var decoded = new MemoryStream(uncompressedLength);
                brotli.CopyTo(decoded);
                if (compressed.Position != compressed.Length)
                {
                    throw new EndfieldCompressDataException(
                        $"record {index} Brotli stream left {compressed.Length - compressed.Position} trailing bytes");
                }

                if (decoded.Length != uncompressedLength)
                {
                    throw new EndfieldCompressDataException(
                        $"record {index} decoded length {decoded.Length} does not match declared length {uncompressedLength}");
                }

                var bytes = decoded.ToArray();
                if ((bytes.Length & 1) != 0)
                {
                    throw new EndfieldCompressDataException($"record {index} UTF-16LE payload has odd length {bytes.Length}");
                }

                return new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true)
                    .GetString(bytes);
            }
            catch (EndfieldCompressDataException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new EndfieldCompressDataException($"record {index} Brotli/UTF-16LE decoding failed", exception);
            }
        }

        private static JToken ParseJsonStrict(string jsonText, int index)
        {
            using var reader = new JsonTextReader(new StringReader(jsonText))
            {
                DateParseHandling = DateParseHandling.None,
                SupportMultipleContent = false,
            };
            var token = JToken.Load(reader, new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Load,
            });
            if (ContainsJsonComment(token))
            {
                throw new EndfieldCompressDataException($"record {index} JSON contains comments");
            }
            if (reader.Read())
            {
                throw new EndfieldCompressDataException(
                    $"record {index} JSON contains trailing content ({reader.TokenType})");
            }
            return token;
        }

        private static bool ContainsJsonComment(JToken token)
        {
            if (token.Type == JTokenType.Comment)
            {
                return true;
            }

            foreach (var child in token.Children())
            {
                if (ContainsJsonComment(child))
                {
                    return true;
                }
            }
            return false;
        }

        private static uint ReadUInt32(byte[] data, long offset, string fieldName)
        {
            if (offset < 0 || offset > data.Length - sizeof(uint))
            {
                throw new EndfieldCompressDataException($"{fieldName} is outside the file");
            }

            return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)offset, sizeof(uint)));
        }
    }

    public sealed class EndfieldCompressDataDocument
    {
        internal EndfieldCompressDataDocument(int sourceLength, IReadOnlyList<EndfieldCompressDataRecord> records)
        {
            SourceLength = sourceLength;
            Records = records;
        }

        public int SourceLength { get; }
        public IReadOnlyList<EndfieldCompressDataRecord> Records { get; }
    }

    public sealed class EndfieldCompressDataRecord
    {
        internal EndfieldCompressDataRecord(
            int index,
            int sourceOffset,
            int compressedLength,
            int uncompressedLength,
            string jsonText,
            JObject json,
            string rootType)
        {
            Index = index;
            SourceOffset = sourceOffset;
            CompressedLength = compressedLength;
            UncompressedLength = uncompressedLength;
            JsonText = jsonText;
            Json = json;
            RootType = rootType;
        }

        public int Index { get; }
        public int SourceOffset { get; }
        public int CompressedLength { get; }
        public int UncompressedLength { get; }
        public string JsonText { get; }
        public JObject Json { get; }
        public string RootType { get; }
    }

    public sealed class EndfieldCompressDataException : Exception
    {
        public EndfieldCompressDataException(string message) : base(message)
        {
        }

        public EndfieldCompressDataException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
