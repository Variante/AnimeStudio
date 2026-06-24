using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AnimeStudio
{
    public class EndianBinaryReader : BinaryReader
    {
        private readonly byte[] buffer;

        public EndianType Endian;

        public EndianBinaryReader(Stream stream, EndianType endian = EndianType.BigEndian, bool leaveOpen = false) : base(stream, Encoding.UTF8, leaveOpen)
        {
            Endian = endian;
            buffer = new byte[8];
        }

        public long Position
        {
            get => BaseStream.Position;
            set => BaseStream.Position = value;
        }

        public long Length => BaseStream.Length;
        public virtual long Remaining => Length - Position;

        public void EnsureReadable(long count, string fieldName = "read")
        {
            if (count < 0)
            {
                throw new InvalidDataException($"{fieldName} has negative byte count {count} at offset 0x{Position:X}.");
            }

            var remaining = Remaining;
            if (count > remaining)
            {
                throw new EndOfStreamException(
                    $"{fieldName} requests {count} bytes at offset 0x{Position:X}, but only {remaining} bytes remain."
                );
            }
        }

        public int EnsureCount(int count, long minBytesPerItem = 1, string fieldName = "count")
        {
            if (count < 0)
            {
                throw new InvalidDataException($"{fieldName} is negative ({count}) at offset 0x{Position:X}.");
            }

            if (minBytesPerItem > 0)
            {
                var remaining = Remaining;
                var maxItems = remaining / minBytesPerItem;
                if (count > maxItems)
                {
                    throw new EndOfStreamException(
                        $"{fieldName} is {count} at offset 0x{Position:X}, " +
                        $"but at least {count * minBytesPerItem} bytes are required and only {remaining} bytes remain."
                    );
                }
            }

            return count;
        }

        public int ReadInt32Count(long minBytesPerItem = 1, string fieldName = "count")
        {
            return EnsureCount(ReadInt32(), minBytesPerItem, fieldName);
        }

        public override short ReadInt16()
        {
            if (Endian == EndianType.BigEndian)
            {
                Read(buffer, 0, 2);
                return BinaryPrimitives.ReadInt16BigEndian(buffer);
            }
            return base.ReadInt16();
        }

        public override int ReadInt32()
        {
            if (Endian == EndianType.BigEndian)
            {
                Read(buffer, 0, 4);
                return BinaryPrimitives.ReadInt32BigEndian(buffer);
            }
            return base.ReadInt32();
        }

        public override long ReadInt64()
        {
            if (Endian == EndianType.BigEndian)
            {
                Read(buffer, 0, 8);
                return BinaryPrimitives.ReadInt64BigEndian(buffer);
            }
            return base.ReadInt64();
        }

        public override ushort ReadUInt16()
        {
            if (Endian == EndianType.BigEndian)
            {
                Read(buffer, 0, 2);
                return BinaryPrimitives.ReadUInt16BigEndian(buffer);
            }
            return base.ReadUInt16();
        }

        public override uint ReadUInt32()
        {
            if (Endian == EndianType.BigEndian)
            {
                Read(buffer, 0, 4);
                return BinaryPrimitives.ReadUInt32BigEndian(buffer);
            }
            return base.ReadUInt32();
        }

        public override ulong ReadUInt64()
        {
            if (Endian == EndianType.BigEndian)
            {
                Read(buffer, 0, 8);
                return BinaryPrimitives.ReadUInt64BigEndian(buffer);
            }
            return base.ReadUInt64();
        }

        public override float ReadSingle()
        {
            if (Endian == EndianType.BigEndian)
            {
                Read(buffer, 0, 4);
                Array.Reverse(buffer, 0, 4);
                return BitConverter.ToSingle(buffer, 0);
            }
            return base.ReadSingle();
        }

        public override double ReadDouble()
        {
            if (Endian == EndianType.BigEndian)
            {
                Read(buffer, 0, 8);
                Array.Reverse(buffer);
                return BitConverter.ToDouble(buffer, 0);
            }
            return base.ReadDouble();
        }
        public override byte[] ReadBytes(int count)
        {
            EnsureReadable(count, nameof(ReadBytes));
            if (count == 0)
            {
                return Array.Empty<byte>();
            }

            var result = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                int n = Read(result, offset, count - offset);
                if (n == 0)
                {
                    throw new EndOfStreamException(
                        $"ReadBytes reached the end of the stream after {offset} of {count} bytes."
                    );
                }

                offset += n;
            }
            return result;
        }

        public void AlignStream()
        {
            AlignStream(4);
        }

        public void AlignStream(int alignment)
        {
            var pos = Position;
            var mod = pos % alignment;
            if (mod != 0)
            {
                Position += alignment - mod;
            }
        }

        public string ReadAlignedString()
        {
            var result = "";
            var length = ReadInt32();
            if (length < 0)
            {
                throw new InvalidDataException($"String length is negative ({length}) at offset 0x{Position - 4:X}.");
            }
            if (length > 0)
            {
                EnsureReadable(length, nameof(ReadAlignedString));
                var stringData = ReadBytes(length);
                result = Encoding.UTF8.GetString(stringData);
            }
            AlignStream();
            return result;
        }

        public string ReadStringToNull(int maxLength = 32767)
        {
            var bytes = new List<byte>();
            int count = 0;
            while (Remaining > 0 && count < maxLength)
            {
                var b = ReadByte();
                if (b == 0)
                {
                    break;
                }
                bytes.Add(b);
                count++;
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public Quaternion ReadQuaternion()
        {
            return new Quaternion(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
        }

        public Vector2 ReadVector2()
        {
            return new Vector2(ReadSingle(), ReadSingle());
        }

        public Vector4 ReadVector4()
        {
            return new Vector4(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
        }

        public Color ReadColor4()
        {
            return new Color(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
        }

        public Matrix4x4 ReadMatrix()
        {
            return new Matrix4x4(ReadSingleArray(16));
        }

        public Float ReadFloat()
        {
            return new Float(ReadSingle());
        }

        public int ReadMhyInt()
        {
            var buffer = ReadBytes(6);
            return buffer[2] | (buffer[4] << 8) | (buffer[0] << 0x10) | (buffer[5] << 0x18);
        }

        public uint ReadMhyUInt()
        {
            var buffer = ReadBytes(7);
            return (uint)(buffer[1] | (buffer[6] << 8) | (buffer[3] << 0x10) | (buffer[2] << 0x18));
        }

        public string ReadMhyString()
        {
            var pos = BaseStream.Position;
            var str = ReadStringToNull();
            BaseStream.Position += 0x105 - (BaseStream.Position - pos);
            return str;
        }

        internal T[] ReadArray<T>(Func<T> del, int length)
        {
            EnsureCount(length, 1, "array length");
            if (length == 0)
            {
                return Array.Empty<T>();
            }

            if (length < 0x1000)
            {
                var array = new T[length];
                for (int i = 0; i < length; i++)
                {
                    array[i] = del();
                }
                return array;
            }
            else
            {
                var list = new List<T>();
                for (int i = 0; i < length; i++)
                {
                    list.Add(del());
                }
                return list.ToArray();
            }
        }

        public bool[] ReadBooleanArray(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            EnsureCount(length, 1, nameof(ReadBooleanArray));
            return ReadArray(ReadBoolean, length);
        }

        public byte[] ReadUInt8Array(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            return ReadBytes(length);
        }

        public short[] ReadInt16Array(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            EnsureCount(length, 2, nameof(ReadInt16Array));
            return ReadArray(ReadInt16, length);
        }

        public ushort[] ReadUInt16Array(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            EnsureCount(length, 2, nameof(ReadUInt16Array));
            return ReadArray(ReadUInt16, length);
        }

        public int[] ReadInt32Array(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            EnsureCount(length, 4, nameof(ReadInt32Array));
            return ReadArray(ReadInt32, length);
        }

        public uint[] ReadUInt32Array(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            EnsureCount(length, 4, nameof(ReadUInt32Array));
            return ReadArray(ReadUInt32, length);
        }

        public ulong[] ReadUInt64Array(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            EnsureCount(length, 8, nameof(ReadUInt64Array));
            return ReadArray(ReadUInt64, length);
        }

        public uint[][] ReadUInt32ArrayArray(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            EnsureCount(length, 4, nameof(ReadUInt32ArrayArray));
            return ReadArray(() => ReadUInt32Array(), length);
        }

        public float[] ReadSingleArray(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            EnsureCount(length, 4, nameof(ReadSingleArray));
            return ReadArray(ReadSingle, length);
        }

        public string[] ReadStringArray(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            EnsureCount(length, 4, nameof(ReadStringArray));
            return ReadArray(ReadAlignedString, length);
        }

        public Vector2[] ReadVector2Array(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            EnsureCount(length, 8, nameof(ReadVector2Array));
            return ReadArray(ReadVector2, length);
        }

        public Vector4[] ReadVector4Array(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            EnsureCount(length, 16, nameof(ReadVector4Array));
            return ReadArray(ReadVector4, length);
        }

        public Matrix4x4[] ReadMatrixArray(int length = -1)
        {
            if (length == -1)
            {
                length = ReadInt32();
            }
            EnsureCount(length, 64, nameof(ReadMatrixArray));
            return ReadArray(ReadMatrix, length);
        }
    }
}
