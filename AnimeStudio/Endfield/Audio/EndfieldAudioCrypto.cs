using System;
using System.Buffers.Binary;

namespace AnimeStudio.Endfield
{
    public static class EndfieldAudioCrypto
    {
        public static void DecryptWem(Span<byte> data, uint wemId) =>
            DecryptVfs(data, 0, data.Length, wemId, 0);

        public static void DecryptVfs(Span<byte> data, int start, int length, uint seed, uint dataOffset)
        {
            if (start < 0 || length < 0 || start > data.Length || length > data.Length - start)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            unchecked
            {
                var keyIndex = seed + (dataOffset >> 2);
                var pos = start;
                var remaining = length;
                var alignment = (int)(dataOffset & 3);

                if (alignment != 0)
                {
                    var key = DeriveKey(keyIndex);
                    var toAlign = Math.Min(4 - alignment, remaining);
                    for (var i = 0; i < toAlign; i++)
                    {
                        if (pos >= start + length)
                        {
                            break;
                        }

                        var bytePos = alignment + i;
                        data[pos] ^= (byte)((key >> (bytePos * 8)) & 0xFF);
                        pos++;
                    }

                    remaining -= toAlign;
                    keyIndex++;
                }

                var blockCount = remaining / 4;
                for (var i = 0; i < blockCount; i++)
                {
                    var key = DeriveKey(keyIndex);
                    var decrypted = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4)) ^ key;
                    BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(pos, 4), decrypted);
                    pos += 4;
                    keyIndex++;
                }

                var trailing = remaining & 3;
                if (trailing > 0)
                {
                    var key = DeriveKey(keyIndex);
                    for (var i = 0; i < trailing; i++)
                    {
                        data[pos] ^= (byte)((key >> (i * 8)) & 0xFF);
                        pos++;
                    }
                }
            }
        }

        private static uint DeriveKey(uint seed)
        {
            unchecked
            {
                var key = ((seed & 0xFF) ^ 0x9C5A0B29U) * 81861667U;
                key = (key ^ ((seed >> 8) & 0xFF)) * 81861667U;
                key = (key ^ ((seed >> 16) & 0xFF)) * 81861667U;
                key = (key ^ ((seed >> 24) & 0xFF)) * 81861667U;
                return key;
            }
        }
    }
}
