using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeStudio.Endfield
{
    internal static class EndfieldXxtea
    {
        private const uint Delta = 0x9E3779B9;

        public static byte[] Decrypt(byte[] data, byte[] key)
        {
            if (key.Length != 16)
            {
                throw new InvalidDataException($"key must be exactly 16 bytes, got {key.Length}");
            }

            if (data.Length == 0)
            {
                return Array.Empty<byte>();
            }

            var v = BytesToUInt32LittleEndian(data);
            var k = new[]
            {
                BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(0, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(4, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(8, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(12, 4)),
            };

            var n = v.Length;
            if (n < 2)
            {
                return data.ToArray();
            }

            var rounds = 6 + 52 / n;
            var sum = unchecked((uint)rounds * Delta);
            var y = v[0];

            while (sum != 0)
            {
                var e = (sum >> 2) & 3;

                for (var p = n - 1; p >= 1; p--)
                {
                    var z = v[p - 1];
                    v[p] = unchecked(v[p] - Mx(sum, y, z, p, e, k));
                    y = v[p];
                }

                var last = v[n - 1];
                v[0] = unchecked(v[0] - Mx(sum, y, last, 0, e, k));
                y = v[0];
                sum = unchecked(sum - Delta);
            }

            var result = UInt32ToBytesLittleEndian(v);
            var originalLength = v[n - 1];
            var maxLength = n * 4;
            var minLength = Math.Max(maxLength - 7, 0);
            if (originalLength >= minLength && originalLength <= maxLength)
            {
                Array.Resize(ref result, (int)originalLength);
            }

            return result;
        }

        private static uint Mx(uint sum, uint y, uint z, int p, uint e, IReadOnlyList<uint> k)
        {
            unchecked
            {
                return ((z >> 5 ^ y << 2) + (y >> 3 ^ z << 4))
                    ^ ((sum ^ y) + (k[(p & 3) ^ (int)e] ^ z));
            }
        }

        private static uint[] BytesToUInt32LittleEndian(byte[] data)
        {
            var paddedLength = ((data.Length + 3) / 4) * 4;
            var padded = new byte[paddedLength];
            Buffer.BlockCopy(data, 0, padded, 0, data.Length);

            var values = new uint[paddedLength / 4];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(i * 4, 4));
            }
            return values;
        }

        private static byte[] UInt32ToBytesLittleEndian(IReadOnlyList<uint> data)
        {
            var bytes = new byte[data.Count * 4];
            for (var i = 0; i < data.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), data[i]);
            }
            return bytes;
        }
    }
}
