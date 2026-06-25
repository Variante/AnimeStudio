using System;
using System.Buffers.Binary;

namespace AnimeStudio.Endfield
{
    public static class EndfieldVfsKeys
    {
        public static readonly byte[] ChaChaKey = Convert.FromBase64String("6VsxesT4KFadI6hr8nHctT6Eb6dckk1nHbqOOPTKUuE=");

        public static readonly byte[] UnityHashSecret =
        {
            0xb8, 0xfe, 0x6c, 0x39, 0x23, 0xa4, 0x4b, 0xbe, 0x7c, 0x01, 0x81, 0x2c, 0xf7, 0x21, 0xad, 0x1c,
            0xde, 0xd4, 0x6d, 0xe9, 0x83, 0x90, 0x97, 0xdb, 0x72, 0x40, 0xa4, 0xa4, 0xb7, 0xb3, 0x67, 0x1f,
            0xcb, 0x79, 0xe6, 0x4e, 0xcc, 0xc0, 0xe5, 0x78, 0x82, 0x5a, 0xd0, 0x7d, 0xcc, 0xff, 0x72, 0x21,
            0xb8, 0x08, 0x46, 0x74, 0xf7, 0x43, 0x24, 0x8e, 0xe0, 0x35, 0x90, 0xe6, 0x81, 0x3a, 0x26, 0x4c,
            0x3c, 0x28, 0x52, 0xbb, 0x91, 0xc3, 0x00, 0xcb, 0x88, 0xd0, 0x65, 0x8b, 0x1b, 0x53, 0x2e, 0xa3,
            0x71, 0x64, 0x48, 0x97, 0xa2, 0x0d, 0xf9, 0x4e, 0x38, 0x19, 0xef, 0x46, 0xa9, 0xde, 0xac, 0xd8,
            0xa8, 0xfa, 0x76, 0x3f, 0xe3, 0x9c, 0x34, 0x3f, 0xf9, 0xdc, 0xbb, 0xc7, 0xc7, 0x0b, 0x4f, 0x1d,
            0x8a, 0x51, 0xe0, 0x4b, 0xcd, 0xb4, 0x59, 0x31, 0xc8, 0x9f, 0x7e, 0xc9, 0xd9, 0x78, 0x73, 0x64,
            0xea, 0xc5, 0xac, 0x83, 0x34, 0xd3, 0xeb, 0xc3, 0xc5, 0x81, 0xa0, 0xff, 0xfa, 0x13, 0x63, 0xeb,
            0x17, 0x0d, 0xdd, 0x51, 0xb7, 0xf0, 0xda, 0x49, 0xd3, 0x16, 0x55, 0x26, 0x29, 0xd4, 0x68, 0x9e,
            0x2b, 0x16, 0xbe, 0x58, 0x7d, 0x47, 0xa1, 0xfc, 0x8f, 0xf8, 0xb8, 0xd1, 0x7a, 0xd0, 0x31, 0xce,
            0x45, 0xcb, 0x3a, 0x8f, 0x95, 0x16, 0x04, 0x28, 0xaf, 0xd7, 0xfb, 0xca, 0xbb, 0x4b, 0x40, 0x7e,
        };

        public static readonly byte[] XxteaKey = System.Text.Encoding.ASCII.GetBytes("d41d8cd98f00b204");
    }

    internal sealed class EndfieldChaCha20
    {
        private readonly uint[] state = new uint[16];

        public EndfieldChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter)
        {
            if (key.Length != 32)
            {
                throw new ArgumentException("ChaCha20 key must be 32 bytes.", nameof(key));
            }
            if (nonce.Length != 12)
            {
                throw new ArgumentException("ChaCha20 nonce must be 12 bytes.", nameof(nonce));
            }

            state[0] = 0x61707865;
            state[1] = 0x3320646e;
            state[2] = 0x79622d32;
            state[3] = 0x6b206574;
            for (var i = 0; i < 8; i++)
            {
                state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4, 4));
            }
            state[12] = counter;
            state[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[..4]);
            state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4, 4));
            state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8, 4));
        }

        public void ApplyKeystream(Span<byte> data)
        {
            Span<byte> block = stackalloc byte[64];
            var offset = 0;
            while (offset < data.Length)
            {
                GenerateBlock(block);
                var count = Math.Min(block.Length, data.Length - offset);
                for (var i = 0; i < count; i++)
                {
                    data[offset + i] ^= block[i];
                }
                offset += count;
                unchecked
                {
                    state[12]++;
                }
            }
        }

        private void GenerateBlock(Span<byte> output)
        {
            Span<uint> working = stackalloc uint[16];
            state.AsSpan().CopyTo(working);

            for (var i = 0; i < 10; i++)
            {
                QuarterRound(working, 0, 4, 8, 12);
                QuarterRound(working, 1, 5, 9, 13);
                QuarterRound(working, 2, 6, 10, 14);
                QuarterRound(working, 3, 7, 11, 15);
                QuarterRound(working, 0, 5, 10, 15);
                QuarterRound(working, 1, 6, 11, 12);
                QuarterRound(working, 2, 7, 8, 13);
                QuarterRound(working, 3, 4, 9, 14);
            }

            for (var i = 0; i < 16; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(i * 4, 4), unchecked(working[i] + state[i]));
            }
        }

        private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
        {
            unchecked
            {
                x[a] += x[b]; x[d] = RotateLeft(x[d] ^ x[a], 16);
                x[c] += x[d]; x[b] = RotateLeft(x[b] ^ x[c], 12);
                x[a] += x[b]; x[d] = RotateLeft(x[d] ^ x[a], 8);
                x[c] += x[d]; x[b] = RotateLeft(x[b] ^ x[c], 7);
            }
        }

        private static uint RotateLeft(uint value, int count) => (value << count) | (value >> (32 - count));
    }

    internal static class EndfieldCrc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFU;
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            return ~crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (var i = 0U; i < table.Length; i++)
            {
                var crc = i;
                for (var j = 0; j < 8; j++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320U ^ (crc >> 1) : crc >> 1;
                }
                table[i] = crc;
            }
            return table;
        }
    }
}
