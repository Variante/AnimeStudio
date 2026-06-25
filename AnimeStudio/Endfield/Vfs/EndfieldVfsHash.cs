using System;
using System.Buffers.Binary;
using System.Numerics;

namespace AnimeStudio.Endfield
{
    public static class EndfieldVfsHash
    {
        private static readonly ulong[] InitAcc =
        {
            0xc2b2ae3dUL,
            0x9e3779b185ebca87UL,
            0xc2b2ae3d27d4eb4fUL,
            0x165667b19e3779f9UL,
            0x85ebca77c2b2ae63UL,
            0x85ebca77UL,
            0x27d4eb2f165667c5UL,
            0x9e3779b1UL,
        };

        public static string VfsBlockHash(string name, ReadOnlySpan<byte> secret)
        {
            var h64 = Hash64(System.Text.Encoding.ASCII.GetBytes(name), secret, 0);
            var h32 = (uint)((h64 & 0xFFFFFFFFUL) ^ (h64 >> 32));
            var swapped = BinaryPrimitives.ReverseEndianness(h32);
            return swapped.ToString("X8");
        }

        public static ulong Hash64(ReadOnlySpan<byte> data, ReadOnlySpan<byte> secret, ulong seed)
        {
            if (secret.Length < 136)
            {
                throw new ArgumentException($"secret key must be at least 136 bytes, got {secret.Length}", nameof(secret));
            }

            var length = data.Length;
            return length < 16
                ? Hash64Len0To16(data, secret, seed)
                : length < 128
                    ? Hash64Len17To128(data, secret, seed)
                    : length < 240
                        ? Hash64Len129To240(data, secret, seed)
                        : Hash64Long(data, secret);
        }

        private static uint ReadUInt32LE(ReadOnlySpan<byte> data, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

        private static ulong ReadUInt64LE(ReadOnlySpan<byte> data, int offset) =>
            BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));

        private static ulong Mult128Fold64(ulong a, ulong b)
        {
            var full = (UInt128)a * b;
            return (ulong)full ^ (ulong)(full >> 64);
        }

        private static ulong Xxh3Avalanche(ulong h)
        {
            unchecked
            {
                h ^= h >> 37;
                h *= 0x165667919E3779F9UL;
                h ^= h >> 32;
                return h;
            }
        }

        private static ulong Xxh64Avalanche(ulong h)
        {
            unchecked
            {
                h ^= h >> 33;
                h *= 0xC2B2AE3D27D4EB4FUL;
                h ^= h >> 29;
                h *= 0x165667B19E3779F9UL;
                h ^= h >> 32;
                return h;
            }
        }

        private static ulong Xxh3Rrmxmx(ulong h, int length)
        {
            unchecked
            {
                h = h ^ BitOperations.RotateRight(h, 15) ^ BitOperations.RotateRight(h, 40);
                h *= 0x9FB21C651E98DF25UL;
                h = ((h >> 35) + (ulong)length) ^ h;
                h *= 0x9FB21C651E98DF25UL;
                h ^= h >> 28;
                return h;
            }
        }

        private static ulong Hash64Len0To16(ReadOnlySpan<byte> data, ReadOnlySpan<byte> secret, ulong seed)
        {
            unchecked
            {
                var length = data.Length;
                if (length > 8)
                {
                    var inputLo = ReadUInt64LE(data, 0);
                    var inputHi = ReadUInt64LE(data, length - 8);
                    var uVar1 = ((ReadUInt64LE(secret, 0x20) ^ ReadUInt64LE(secret, 0x18)) + seed) ^ inputLo;
                    var uVar4 = ((ReadUInt64LE(secret, 0x30) ^ ReadUInt64LE(secret, 0x28)) - seed) ^ inputHi;
                    var fold = Mult128Fold64(uVar1, uVar4);
                    var acc = fold + BinaryPrimitives.ReverseEndianness(uVar1) + uVar4 + (ulong)length;
                    return Xxh3Avalanche(acc);
                }

                if (length >= 4)
                {
                    var inputLo = (ulong)ReadUInt32LE(data, 0);
                    var inputHi = (ulong)ReadUInt32LE(data, length - 4);
                    var combined = (inputLo << 32) | inputHi;
                    var seedLo = (uint)seed;
                    var seedSwapped = BinaryPrimitives.ReverseEndianness(seedLo);
                    var seedAdj = seed ^ ((ulong)seedSwapped << 32);
                    var bitflip = (ReadUInt64LE(secret, 0x08) ^ ReadUInt64LE(secret, 0x10)) - seedAdj;
                    return Xxh3Rrmxmx(combined ^ bitflip, length);
                }

                if (length >= 1)
                {
                    var c1 = (uint)data[0];
                    var c2 = (uint)data[length >> 1];
                    var c3 = (uint)data[length - 1];
                    var combined = ((((c1 | (c2 << 8)) << 8) | (uint)length) << 8) | c3;
                    var bitflip = (ulong)(ReadUInt32LE(secret, 0) ^ ReadUInt32LE(secret, 4)) + seed;
                    return Xxh64Avalanche(combined ^ bitflip);
                }

                var emptyBitflip = (ReadUInt64LE(secret, 0x38) ^ ReadUInt64LE(secret, 0x40)) ^ seed;
                return Xxh64Avalanche(emptyBitflip);
            }
        }

        private static ulong Mix16B(ReadOnlySpan<byte> data, int dataOffset, ReadOnlySpan<byte> secret, int secretOffset, ulong seed)
        {
            unchecked
            {
                var inputLo = ReadUInt64LE(data, dataOffset);
                var inputHi = ReadUInt64LE(data, dataOffset + 8);
                return Mult128Fold64(
                    inputLo ^ (ReadUInt64LE(secret, secretOffset) + seed),
                    inputHi ^ (ReadUInt64LE(secret, secretOffset + 8) - seed)
                );
            }
        }

        private static ulong Hash64Len17To128(ReadOnlySpan<byte> data, ReadOnlySpan<byte> secret, ulong seed)
        {
            unchecked
            {
                var length = data.Length;
                var acc = (ulong)length * 0x9E3779B185EBCA87UL;

                if (length > 32)
                {
                    if (length > 64)
                    {
                        if (length > 96)
                        {
                            acc += Mix16B(data, 48, secret, 0x60, seed);
                            acc += Mix16B(data, length - 64, secret, 0x70, seed);
                        }
                        acc += Mix16B(data, 32, secret, 0x40, seed);
                        acc += Mix16B(data, length - 48, secret, 0x50, seed);
                    }
                    acc += Mix16B(data, 16, secret, 0x20, seed);
                    acc += Mix16B(data, length - 32, secret, 0x30, seed);
                }

                acc += Mix16B(data, 0, secret, 0x00, seed);
                acc += Mix16B(data, length - 16, secret, 0x10, seed);
                return Xxh3Avalanche(acc);
            }
        }

        private static ulong Hash64Len129To240(ReadOnlySpan<byte> data, ReadOnlySpan<byte> secret, ulong seed)
        {
            unchecked
            {
                var length = data.Length;
                var acc = (ulong)length * 0x9E3779B185EBCA87UL;

                for (var i = 0; i < 8; i++)
                {
                    acc += Mix16B(data, i * 16, secret, i * 16, seed);
                }

                acc = Xxh3Avalanche(acc);
                var numBlocks = (length + 15) / 16;
                for (var i = 8; i < numBlocks; i++)
                {
                    var dataOffset = 128 + (i - 8) * 16;
                    var secretOffset = 3 + (i - 8) * 16;
                    if (dataOffset + 16 <= length)
                    {
                        acc += Mix16B(data, dataOffset, secret, secretOffset, seed);
                    }
                }

                acc += Mix16B(data, length - 16, secret, 0x77, seed);
                return Xxh3Avalanche(acc);
            }
        }

        private static void AccumulateStripe(Span<ulong> acc, ReadOnlySpan<byte> data, int dataOffset, ReadOnlySpan<byte> secret, int secretOffset)
        {
            unchecked
            {
                for (var i = 0; i < 8; i++)
                {
                    var inputVal = ReadUInt64LE(data, dataOffset + i * 8);
                    var secretVal = ReadUInt64LE(secret, secretOffset + i * 8);
                    var keyed = inputVal ^ secretVal;
                    acc[i ^ 1] += inputVal;
                    acc[i] += (ulong)(uint)(keyed >> 32) * (uint)keyed;
                }
            }
        }

        private static void ScrambleAcc(Span<ulong> acc, ReadOnlySpan<byte> secret, int secretOffset)
        {
            unchecked
            {
                for (var i = 0; i < acc.Length; i++)
                {
                    var secretVal = ReadUInt64LE(secret, secretOffset + i * 8);
                    acc[i] = (acc[i] ^ (acc[i] >> 47) ^ secretVal) * 0x9e3779b1UL;
                }
            }
        }

        private static ulong MixAcc(ReadOnlySpan<ulong> acc, ReadOnlySpan<byte> secret, int secretOffset)
        {
            unchecked
            {
                var result = 0UL;
                for (var i = 0; i < 8; i += 2)
                {
                    var a = acc[i] ^ ReadUInt64LE(secret, secretOffset + i * 8);
                    var b = acc[i + 1] ^ ReadUInt64LE(secret, secretOffset + (i + 1) * 8);
                    result += Mult128Fold64(a, b);
                }
                return result;
            }
        }

        private static ulong MergeAccs(ReadOnlySpan<ulong> acc, ReadOnlySpan<byte> secret, int secretOffset, ulong start)
        {
            unchecked
            {
                var result = start + MixAcc(acc, secret, secretOffset);
                result = (result ^ (result >> 37)) * 0x165667919e3779f9UL;
                return result ^ (result >> 32);
            }
        }

        private static ulong Hash64Long(ReadOnlySpan<byte> data, ReadOnlySpan<byte> secret)
        {
            unchecked
            {
                var length = data.Length;
                Span<ulong> acc = stackalloc ulong[8];
                for (var i = 0; i < InitAcc.Length; i++)
                {
                    acc[i] = InitAcc[i];
                }

                var nbBlocks = (length - 1) / 1024;
                for (var block = 0; block < nbBlocks; block++)
                {
                    var blockOffset = block * 1024;
                    for (var stripe = 0; stripe < 16; stripe++)
                    {
                        AccumulateStripe(acc, data, blockOffset + stripe * 64, secret, stripe * 8);
                    }
                    ScrambleAcc(acc, secret, 0x80);
                }

                var remaining = length - nbBlocks * 1024;
                var nbStripes = (remaining - 1) / 64;
                var lastBlockOffset = nbBlocks * 1024;
                for (var stripe = 0; stripe < nbStripes; stripe++)
                {
                    AccumulateStripe(acc, data, lastBlockOffset + stripe * 64, secret, stripe * 8);
                }

                AccumulateStripe(acc, data, length - 64, secret, 121);
                var start = (ulong)length * 0x9e3779b185ebca87UL;
                return MergeAccs(acc, secret, 11, start);
            }
        }
    }
}
