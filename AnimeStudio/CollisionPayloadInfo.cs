using System;
using System.Security.Cryptography;

namespace AnimeStudio
{
    // Bounded diagnostics only: avoid retaining every cooked buffer during bulk
    // export. Raw object exports preserve the original bytes for format recovery.
    public sealed class CollisionPayloadInfo
    {
        public int ByteCount { get; private set; }
        public string HeaderHex { get; private set; }
        public string Sha256 { get; private set; }

        public static CollisionPayloadInfo Describe(byte[] bytes)
        {
            bytes ??= Array.Empty<byte>();
            return new CollisionPayloadInfo
            {
                ByteCount = bytes.Length,
                HeaderHex = Convert.ToHexString(bytes.AsSpan(0, Math.Min(16, bytes.Length))),
                Sha256 = bytes.Length == 0 ? null : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            };
        }
    }
}
