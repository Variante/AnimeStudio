using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AnimeStudio.Endfield
{
    public sealed class EndfieldSparkBufferParseResult
    {
        public EndfieldSparkBufferParseResult(string name, JToken data)
        {
            Name = name;
            Data = data;
        }

        public string Name { get; }
        public JToken Data { get; }
    }

    public sealed class EndfieldSparkBufferException : Exception
    {
        public EndfieldSparkBufferException(string message) : base(message)
        {
        }

        public EndfieldSparkBufferException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public static class EndfieldSparkBuffer
    {
        public static EndfieldSparkBufferParseResult ParseBytes(byte[] data)
        {
            using var stream = new MemoryStream(data, false);
            using var reader = new SparkReader(stream);
            return Parse(reader);
        }

        private static EndfieldSparkBufferParseResult Parse(SparkReader reader)
        {
            var typeDefOffset = reader.ReadInt32LittleEndian();
            var rootDefOffset = reader.ReadInt32LittleEndian();
            var dataOffset = reader.ReadInt32LittleEndian();
            reader.ValidateOffset(typeDefOffset, "type definition offset");
            reader.ValidateOffset(rootDefOffset, "root definition offset");
            reader.ValidateOffset(dataOffset, "data offset");

            reader.SeekAbsolute(typeDefOffset);
            var registry = new TypeRegistry();
            ParseTypeDefinitions(reader, registry);

            reader.SeekAbsolute(rootDefOffset);
            var rootDef = ParseRootDef(reader);

            reader.SeekAbsolute(dataOffset);
            var data = rootDef.FieldType switch
            {
                SparkType.Bean => ReadBeanValue(
                    reader,
                    registry.GetBean(rootDef.TypeHash ?? throw new EndfieldSparkBufferException("root bean missing type hash")),
                    registry,
                    false) ?? JValue.CreateNull(),
                SparkType.Map => ReadRootMapValue(reader, rootDef, registry),
                _ => throw new EndfieldSparkBufferException($"unsupported root type: {rootDef.FieldType}"),
            };

            reader.RequireReadThroughEnd();

            return new EndfieldSparkBufferParseResult(rootDef.Name, data);
        }

        private static void ParseTypeDefinitions(SparkReader reader, TypeRegistry registry)
        {
            var typeDefCount = reader.ReadCount("type definition count", 1);
            for (var i = 0; i < typeDefCount; i++)
            {
                var sparkType = reader.ReadSparkType();
                reader.Align4();

                switch (sparkType)
                {
                    case SparkType.Enum:
                    {
                        var typeHash = reader.ReadInt32LittleEndian();
                        var name = reader.ReadNullTerminatedString();
                        reader.Align4();
                        var enumCount = reader.ReadCount("enum item count", 5);
                        var items = new List<EnumItem>(enumCount);
                        for (var j = 0; j < enumCount; j++)
                        {
                            var itemName = reader.ReadNullTerminatedString();
                            reader.Align4();
                            var itemValue = reader.ReadInt32LittleEndian();
                            items.Add(new EnumItem(itemName, itemValue));
                        }

                        registry.InsertEnum(new EnumType(typeHash, name, items));
                        break;
                    }
                    case SparkType.Bean:
                    {
                        var typeHash = reader.ReadInt32LittleEndian();
                        var name = reader.ReadNullTerminatedString();
                        reader.Align4();
                        var fieldCount = reader.ReadCount("bean field count", 2);
                        var fields = new List<BeanField>(fieldCount);
                        var fieldNames = new HashSet<string>(StringComparer.Ordinal);
                        for (var j = 0; j < fieldCount; j++)
                        {
                            var fieldName = reader.ReadNullTerminatedString();
                            if (!fieldNames.Add(fieldName))
                            {
                                throw new EndfieldSparkBufferException(
                                    $"duplicate bean field name in {name}: {fieldName}");
                            }
                            var fieldType = reader.ReadSparkType();
                            SparkType? type2 = null;
                            SparkType? type3 = null;
                            int? typeHash1 = null;
                            int? typeHash2 = null;

                            switch (fieldType)
                            {
                                case SparkType.Bool:
                                case SparkType.Byte:
                                case SparkType.Int:
                                case SparkType.Long:
                                case SparkType.Float:
                                case SparkType.Double:
                                case SparkType.String:
                                    break;
                                case SparkType.Enum:
                                case SparkType.Bean:
                                    reader.Align4();
                                    typeHash1 = reader.ReadInt32LittleEndian();
                                    break;
                                case SparkType.Array:
                                    type2 = reader.ReadSparkType();
                                    if (type2.Value.IsEnumOrBean())
                                    {
                                        reader.Align4();
                                        typeHash1 = reader.ReadInt32LittleEndian();
                                    }
                                    break;
                                case SparkType.Map:
                                    type2 = reader.ReadSparkType();
                                    type3 = reader.ReadSparkType();
                                    if (type2.Value.IsEnumOrBean())
                                    {
                                        reader.Align4();
                                        typeHash1 = reader.ReadInt32LittleEndian();
                                    }
                                    if (type3.Value.IsEnumOrBean())
                                    {
                                        reader.Align4();
                                        typeHash2 = reader.ReadInt32LittleEndian();
                                    }
                                    break;
                                default:
                                    throw new EndfieldSparkBufferException($"unsupported field type in definition: {fieldType}");
                            }

                            fields.Add(new BeanField(fieldName, fieldType, type2, type3, typeHash1, typeHash2));
                        }

                        registry.InsertBean(new BeanType(typeHash, name, fields));
                        break;
                    }
                    default:
                        throw new EndfieldSparkBufferException($"invalid spark type in type definition: {sparkType}");
                }
            }
        }

        private static RootDef ParseRootDef(SparkReader reader)
        {
            var fieldType = reader.ReadSparkType();
            var name = reader.ReadNullTerminatedString();
            int? typeHash = null;
            SparkType? type2 = null;
            SparkType? type3 = null;
            int? typeHash2 = null;

            if (fieldType.IsEnumOrBean())
            {
                reader.Align4();
                typeHash = reader.ReadInt32LittleEndian();
            }

            if (fieldType == SparkType.Map)
            {
                type2 = reader.ReadSparkType();
                type3 = reader.ReadSparkType();

                if (type2.Value.IsEnumOrBean())
                {
                    reader.Align4();
                    typeHash = reader.ReadInt32LittleEndian();
                }
                if (type3.Value.IsEnumOrBean())
                {
                    reader.Align4();
                    typeHash2 = reader.ReadInt32LittleEndian();
                }
            }

            return new RootDef(fieldType, name, typeHash, type2, type3, typeHash2);
        }

        private static JToken ReadBeanValue(SparkReader reader, BeanType beanType, TypeRegistry registry, bool isPointer)
        {
            long? pointerOrigin = null;
            if (isPointer)
            {
                var beanOffset = reader.ReadInt32LittleEndian();
                if (beanOffset == -1)
                {
                    return null;
                }
                reader.ValidateOffset(beanOffset, "bean pointer");
                pointerOrigin = reader.Position;
                reader.SeekAbsolute(beanOffset);
            }

            var obj = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
            for (var i = 0; i < beanType.Fields.Count; i++)
            {
                var field = beanType.Fields[i];
                long? origin = null;

                if (field.FieldType == SparkType.Array)
                {
                    var fieldOffset = reader.ReadInt32LittleEndian();
                    if (fieldOffset == -1)
                    {
                        obj[field.Name] = JValue.CreateNull();
                        continue;
                    }
                    reader.ValidateOffset(fieldOffset, $"array field {field.Name} offset");
                    origin = reader.Position;
                    reader.SeekAbsolute(fieldOffset);
                }

                var value = field.FieldType switch
                {
                    SparkType.Array => ReadArrayValue(reader, field, registry),
                    SparkType.Int => new JValue(reader.ReadInt32LittleEndian()),
                    SparkType.Enum => new JValue(reader.ReadInt32LittleEndian()),
                    SparkType.Long => new JValue(reader.ReadAlignedInt64()),
                    SparkType.Float => FloatValue(reader.ReadSingleLittleEndian()),
                    SparkType.Double => FloatValue(reader.ReadAlignedDouble()),
                    SparkType.String => new JValue(reader.ReadStringAtOffset()),
                    SparkType.Bean => ReadBeanValue(
                        reader,
                        registry.GetBean(field.TypeHash ?? throw MissingTypeHash(field.Name)),
                        registry,
                        true) ?? JValue.CreateNull(),
                    SparkType.Bool => ReadBeanBoolValue(reader, beanType, i),
                    SparkType.Map => ReadNestedMapValue(reader, field, registry),
                    SparkType.Byte => throw new EndfieldSparkBufferException($"unsupported field type: {SparkType.Byte}"),
                    _ => throw new EndfieldSparkBufferException($"unsupported field type: {field.FieldType}"),
                };

                obj[field.Name] = value;

                if (origin.HasValue)
                {
                    reader.SeekAbsolute(origin.Value);
                }
            }

            if (pointerOrigin.HasValue)
            {
                reader.SeekAbsolute(pointerOrigin.Value);
            }

            return SortedObject(obj);
        }

        private static JToken ReadArrayValue(SparkReader reader, BeanField field, TypeRegistry registry)
        {
            var itemCount = reader.ReadCount("array item count", 1);
            var arr = new JArray();
            var itemType = field.Type2 ?? throw new EndfieldSparkBufferException("array field missing type2");

            for (var i = 0; i < itemCount; i++)
            {
                JToken item = itemType switch
                {
                    SparkType.String => new JValue(reader.ReadStringAtOffset()),
                    SparkType.Bean => ReadBeanValue(
                        reader,
                        registry.GetBean(field.TypeHash ?? throw MissingTypeHash(field.Name)),
                        registry,
                        true) ?? JValue.CreateNull(),
                    SparkType.Float => FloatValue(reader.ReadSingleLittleEndian()),
                    SparkType.Long => new JValue(reader.ReadAlignedInt64()),
                    SparkType.Int => new JValue(reader.ReadInt32LittleEndian()),
                    SparkType.Enum => new JValue(reader.ReadInt32LittleEndian()),
                    SparkType.Bool => new JValue(reader.ReadBoolean()),
                    SparkType.Double => FloatValue(reader.ReadAlignedDouble()),
                    _ => throw new EndfieldSparkBufferException($"unsupported field type: {itemType}"),
                };
                arr.Add(item);
            }

            return arr;
        }

        private static JToken ReadBeanBoolValue(SparkReader reader, BeanType beanType, int fieldIndex)
        {
            var value = new JValue(reader.ReadBoolean());
            if (fieldIndex + 1 < beanType.Fields.Count && beanType.Fields[fieldIndex + 1].FieldType != SparkType.Bool)
            {
                reader.Align4();
            }
            return value;
        }

        private static JToken ReadNestedMapValue(SparkReader reader, BeanField field, TypeRegistry registry)
        {
            var mapOffset = reader.ReadInt32LittleEndian();
            if (mapOffset == -1)
            {
                return JValue.CreateNull();
            }
            reader.ValidateOffset(mapOffset, $"map field {field.Name} offset");
            var mapOrigin = reader.Position;
            reader.SeekAbsolute(mapOffset);
            var mapValue = ReadMapValue(reader, field, registry);
            reader.SeekAbsolute(mapOrigin);
            return mapValue;
        }

        private static JToken ReadMapValue(SparkReader reader, BeanField field, TypeRegistry registry)
        {
            var keyValueCount = reader.ReadCount("map item count", 8);
            reader.SeekRelative(checked((long)keyValueCount * 8));
            var map = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
            var keyType = field.Type2 ?? throw new EndfieldSparkBufferException("map field missing type2");
            var valueType = field.Type3 ?? throw new EndfieldSparkBufferException("map field missing type3");

            for (var i = 0; i < keyValueCount; i++)
            {
                var key = ReadMapKey(reader, keyType);
                var value = ReadMapValueItem(reader, valueType, field.TypeHash2, registry);
                if (map.ContainsKey(key))
                {
                    throw new EndfieldSparkBufferException($"duplicate map key: {key}");
                }
                map[key] = value;
            }

            return SortedObject(map);
        }

        private static JToken ReadRootMapValue(SparkReader reader, RootDef rootDef, TypeRegistry registry)
        {
            var keyValueCount = reader.ReadCount("root map item count", 8);
            reader.SeekRelative(checked((long)keyValueCount * 8));
            var map = new SortedDictionary<string, JToken>(StringComparer.Ordinal);
            var keyType = rootDef.Type2 ?? throw new EndfieldSparkBufferException("root map missing type2");
            var valueType = rootDef.Type3 ?? throw new EndfieldSparkBufferException("root map missing type3");

            for (var i = 0; i < keyValueCount; i++)
            {
                var key = ReadMapKey(reader, keyType);
                var value = ReadMapValueItem(reader, valueType, rootDef.TypeHash2, registry);
                if (map.ContainsKey(key))
                {
                    throw new EndfieldSparkBufferException($"duplicate root map key: {key}");
                }
                map[key] = value;
            }

            return SortedObject(map);
        }

        private static string ReadMapKey(SparkReader reader, SparkType keyType) => keyType switch
        {
            SparkType.String => reader.ReadStringAtOffset(),
            SparkType.Int => reader.ReadInt32LittleEndian().ToString(),
            SparkType.Long => reader.ReadAlignedInt64().ToString(),
            _ => throw new EndfieldSparkBufferException($"unsupported field type: {keyType}"),
        };

        private static JToken ReadMapValueItem(SparkReader reader, SparkType valueType, int? typeHash, TypeRegistry registry)
        {
            return valueType switch
            {
                SparkType.Bean => ReadBeanValue(
                    reader,
                    registry.GetBean(typeHash ?? throw new EndfieldSparkBufferException("map bean value missing type hash")),
                    registry,
                    true) ?? JValue.CreateNull(),
                SparkType.String => new JValue(reader.ReadStringAtOffset()),
                SparkType.Int => new JValue(reader.ReadInt32LittleEndian()),
                SparkType.Float => FloatValue(reader.ReadSingleLittleEndian()),
                SparkType.Enum => new JValue(registry.GetEnum(typeHash ?? throw new EndfieldSparkBufferException("map enum value missing type hash")).GetName(reader.ReadInt32LittleEndian())),
                SparkType.Bool => ReadMapBoolValue(reader),
                _ => throw new EndfieldSparkBufferException($"unsupported field type: {valueType}"),
            };
        }

        private static JToken ReadMapBoolValue(SparkReader reader)
        {
            var value = new JValue(reader.ReadBoolean());
            reader.Align4();
            return value;
        }

        private static JToken FloatValue(float value) => float.IsFinite(value) ? new JValue((double)value) : JValue.CreateNull();

        private static JToken FloatValue(double value) => double.IsFinite(value) ? new JValue(value) : JValue.CreateNull();

        private static JObject SortedObject(SortedDictionary<string, JToken> values)
        {
            var obj = new JObject();
            foreach (var (key, value) in values)
            {
                obj.Add(key, value);
            }
            return obj;
        }

        private static EndfieldSparkBufferException MissingTypeHash(string fieldName) =>
            new($"field {fieldName} missing type hash");

        private enum SparkType : byte
        {
            Bool = 0,
            Byte = 1,
            Int = 2,
            Long = 3,
            Float = 4,
            Double = 5,
            Enum = 6,
            String = 7,
            Bean = 8,
            Array = 9,
            Map = 10,
        }

        private static bool IsEnumOrBean(this SparkType type) => type is SparkType.Enum or SparkType.Bean;

        private sealed class EnumItem
        {
            public EnumItem(string name, int value)
            {
                Name = name;
                Value = value;
            }

            public string Name { get; }
            public int Value { get; }
        }

        private sealed class EnumType
        {
            public EnumType(int typeHash, string name, IReadOnlyList<EnumItem> items)
            {
                TypeHash = typeHash;
                Name = name;
                Items = items;
            }

            public int TypeHash { get; }
            public string Name { get; }
            public IReadOnlyList<EnumItem> Items { get; }

            public string GetName(int value) =>
                Items.FirstOrDefault(item => item.Value == value)?.Name ?? value.ToString();
        }

        private sealed class BeanField
        {
            public BeanField(string name, SparkType fieldType, SparkType? type2, SparkType? type3, int? typeHash, int? typeHash2)
            {
                Name = name;
                FieldType = fieldType;
                Type2 = type2;
                Type3 = type3;
                TypeHash = typeHash;
                TypeHash2 = typeHash2;
            }

            public string Name { get; }
            public SparkType FieldType { get; }
            public SparkType? Type2 { get; }
            public SparkType? Type3 { get; }
            public int? TypeHash { get; }
            public int? TypeHash2 { get; }
        }

        private sealed class BeanType
        {
            public BeanType(int typeHash, string name, IReadOnlyList<BeanField> fields)
            {
                TypeHash = typeHash;
                Name = name;
                Fields = fields;
            }

            public int TypeHash { get; }
            public string Name { get; }
            public IReadOnlyList<BeanField> Fields { get; }
        }

        private sealed class RootDef
        {
            public RootDef(SparkType fieldType, string name, int? typeHash, SparkType? type2, SparkType? type3, int? typeHash2)
            {
                FieldType = fieldType;
                Name = name;
                TypeHash = typeHash;
                Type2 = type2;
                Type3 = type3;
                TypeHash2 = typeHash2;
            }

            public SparkType FieldType { get; }
            public string Name { get; }
            public int? TypeHash { get; }
            public SparkType? Type2 { get; }
            public SparkType? Type3 { get; }
            public int? TypeHash2 { get; }
        }

        private sealed class TypeRegistry
        {
            private readonly Dictionary<int, BeanType> beanTypes = new();
            private readonly Dictionary<int, EnumType> enumTypes = new();

            public BeanType GetBean(int hash) =>
                beanTypes.TryGetValue(hash, out var bean)
                    ? bean
                    : throw new EndfieldSparkBufferException($"unknown bean type hash: 0x{hash:X8}");

            public EnumType GetEnum(int hash) =>
                enumTypes.TryGetValue(hash, out var enumType)
                    ? enumType
                    : throw new EndfieldSparkBufferException($"unknown enum type hash: 0x{hash:X8}");

            public void InsertBean(BeanType bean) => beanTypes[bean.TypeHash] = bean;

            public void InsertEnum(EnumType enumType) => enumTypes[enumType.TypeHash] = enumType;
        }

        private sealed class SparkReader : IDisposable
        {
            private readonly BinaryReader reader;
            private static readonly UTF8Encoding StrictUtf8 = new(false, true);
            private long maxReadPosition;

            public SparkReader(Stream stream)
            {
                reader = new BinaryReader(stream, Encoding.UTF8, false);
            }

            public long Position => reader.BaseStream.Position;

            public long Length => reader.BaseStream.Length;

            public void Dispose() => reader.Dispose();

            public int ReadInt32LittleEndian()
            {
                EnsureAvailable(sizeof(int), "Int32");
                var value = reader.ReadInt32();
                MarkRead(sizeof(int));
                return value;
            }

            public long ReadInt64LittleEndian()
            {
                EnsureAvailable(sizeof(long), "Int64");
                var value = reader.ReadInt64();
                MarkRead(sizeof(long));
                return value;
            }

            public float ReadSingleLittleEndian()
            {
                EnsureAvailable(sizeof(float), "Single");
                var value = reader.ReadSingle();
                MarkRead(sizeof(float));
                return value;
            }

            public double ReadDoubleLittleEndian()
            {
                EnsureAvailable(sizeof(double), "Double");
                var value = reader.ReadDouble();
                MarkRead(sizeof(double));
                return value;
            }

            public bool ReadBoolean() => ReadByteChecked("boolean") != 0;

            public SparkType ReadSparkType()
            {
                var value = ReadByteChecked("spark type");
                return value switch
                {
                    0 => SparkType.Bool,
                    1 => SparkType.Byte,
                    2 => SparkType.Int,
                    3 => SparkType.Long,
                    4 => SparkType.Float,
                    5 => SparkType.Double,
                    6 => SparkType.Enum,
                    7 => SparkType.String,
                    8 => SparkType.Bean,
                    9 => SparkType.Array,
                    10 => SparkType.Map,
                    _ => throw new EndfieldSparkBufferException($"invalid spark type: {value}"),
                };
            }

            public string ReadNullTerminatedString()
            {
                var bytes = new List<byte>();
                while (true)
                {
                    var b = ReadByteChecked("null-terminated string");
                    if (b == 0)
                    {
                        break;
                    }
                    bytes.Add(b);
                }

                try
                {
                    return StrictUtf8.GetString(bytes.ToArray());
                }
                catch (DecoderFallbackException exception)
                {
                    throw new EndfieldSparkBufferException(
                        $"invalid UTF-8 in null-terminated string at offset {Position - bytes.Count - 1}",
                        exception);
                }
            }

            public string ReadStringAtOffset()
            {
                var offset = ReadInt32LittleEndian();
                if (offset == -1)
                {
                    return string.Empty;
                }

                var oldPosition = Position;
                ValidateOffset(offset, "string offset");
                SeekAbsolute(offset);
                var value = ReadNullTerminatedString();
                SeekAbsolute(oldPosition);
                return value;
            }

            public long ReadAlignedInt64()
            {
                Align8();
                return ReadInt64LittleEndian();
            }

            public double ReadAlignedDouble()
            {
                Align8();
                return ReadDoubleLittleEndian();
            }

            public int ReadCount(string fieldName)
            {
                return ReadCount(fieldName, 0);
            }

            public int ReadCount(string fieldName, long minimumBytesPerItem)
            {
                var count = ReadInt32LittleEndian();
                if (count < 0)
                {
                    throw new EndfieldSparkBufferException($"invalid {fieldName}: {count}");
                }
                if (minimumBytesPerItem > 0)
                {
                    var remaining = Length - Position;
                    if (count > remaining / minimumBytesPerItem)
                    {
                        throw new EndfieldSparkBufferException(
                            $"invalid {fieldName}: {count} exceeds remaining-byte bound {remaining / minimumBytesPerItem}");
                    }
                }
                return count;
            }

            public void Align4() => Align(4);

            public void Align8() => Align(8);

            public void SeekAbsolute(long offset)
            {
                if (offset < 0 || offset > Length)
                {
                    throw new EndfieldSparkBufferException($"invalid offset: {offset} (stream length {Length})");
                }
                reader.BaseStream.Seek(offset, SeekOrigin.Begin);
            }

            public void SeekRelative(long offset)
            {
                long target;
                try
                {
                    target = checked(Position + offset);
                }
                catch (OverflowException exception)
                {
                    throw new EndfieldSparkBufferException($"relative offset overflow: {offset}", exception);
                }
                SeekAbsolute(target);
            }

            public void ValidateOffset(long offset, string fieldName)
            {
                if (offset < 0 || offset >= Length)
                {
                    throw new EndfieldSparkBufferException(
                        $"invalid {fieldName}: {offset} (stream length {Length})");
                }
            }

            public void RequireReadThroughEnd()
            {
                if (maxReadPosition != Length)
                {
                    throw new EndfieldSparkBufferException(
                        $"unconsumed SparkBuffer trailing bytes: first offset {maxReadPosition}, "
                        + $"count {Length - maxReadPosition}, stream length {Length}");
                }
            }

            private byte ReadByteChecked(string fieldName)
            {
                EnsureAvailable(1, fieldName);
                var value = reader.ReadByte();
                MarkRead(1);
                return value;
            }

            private void EnsureAvailable(long count, string fieldName)
            {
                if (count < 0 || Position > Length - count)
                {
                    throw new EndfieldSparkBufferException(
                        $"truncated {fieldName} at offset {Position}: expected {count} bytes, stream length {Length}");
                }
            }

            private void MarkRead(long count)
            {
                maxReadPosition = Math.Max(maxReadPosition, checked(Position));
            }

            private void Align(int alignment)
            {
                if (alignment <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(alignment));
                }
                var remainder = Position % alignment;
                var aligned = remainder == 0
                    ? Position
                    : checked(Position + (alignment - remainder));
                // Some current-build payloads omit terminal alignment padding.
                // Treat an alignment request made exactly at EOF as a virtual
                // boundary; any subsequent read still fails closed at EOF.
                if (aligned > Length && Position == Length)
                {
                    aligned = Length;
                }
                SeekAbsolute(aligned);
            }
        }
    }
}
