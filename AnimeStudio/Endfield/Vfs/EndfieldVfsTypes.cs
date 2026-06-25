using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace AnimeStudio.Endfield
{
    public enum EndfieldVfsBlockType : byte
    {
        None = 0,
        InitialAudio = 1,
        InitialBundle = 2,
        InitialExtendData = 3,
        BundleManifest = 4,
        IFixPatch = 5,
        AuditStreaming = 6,
        AuditDynamicStreaming = 7,
        AuditIV = 8,
        AuditAudio = 9,
        AuditVideo = 10,
        Bundle = 11,
        Audio = 12,
        Video = 13,
        IV = 14,
        Streaming = 15,
        DynamicStreaming = 16,
        Lua = 17,
        Table = 18,
        JsonData = 19,
        ExtendData = 20,
        HotfixAudio = 21,
        AudioChinese = 101,
        AudioEnglish = 102,
        AudioJapanese = 103,
        AudioKorean = 104,
        Raw = 255,
    }

    public enum EndfieldVfsFileTag : byte
    {
        None = 0,
        Audit = 1,
    }

    public static class EndfieldVfsBlockTypes
    {
        private static readonly EndfieldVfsBlockType[] Dumpable =
        {
            EndfieldVfsBlockType.InitialAudio,
            EndfieldVfsBlockType.InitialBundle,
            EndfieldVfsBlockType.InitialExtendData,
            EndfieldVfsBlockType.BundleManifest,
            EndfieldVfsBlockType.IFixPatch,
            EndfieldVfsBlockType.AuditStreaming,
            EndfieldVfsBlockType.AuditDynamicStreaming,
            EndfieldVfsBlockType.AuditIV,
            EndfieldVfsBlockType.AuditAudio,
            EndfieldVfsBlockType.AuditVideo,
            EndfieldVfsBlockType.Bundle,
            EndfieldVfsBlockType.Audio,
            EndfieldVfsBlockType.Video,
            EndfieldVfsBlockType.IV,
            EndfieldVfsBlockType.Streaming,
            EndfieldVfsBlockType.DynamicStreaming,
            EndfieldVfsBlockType.Lua,
            EndfieldVfsBlockType.Table,
            EndfieldVfsBlockType.JsonData,
            EndfieldVfsBlockType.ExtendData,
            EndfieldVfsBlockType.HotfixAudio,
            EndfieldVfsBlockType.AudioChinese,
            EndfieldVfsBlockType.AudioEnglish,
            EndfieldVfsBlockType.AudioJapanese,
            EndfieldVfsBlockType.AudioKorean,
        };

        public static IReadOnlyList<EndfieldVfsBlockType> AllDumpable => Dumpable;

        public static EndfieldVfsBlockType FromByte(byte value) => value switch
        {
            0 => EndfieldVfsBlockType.None,
            1 => EndfieldVfsBlockType.InitialAudio,
            2 => EndfieldVfsBlockType.InitialBundle,
            3 => EndfieldVfsBlockType.InitialExtendData,
            4 => EndfieldVfsBlockType.BundleManifest,
            5 => EndfieldVfsBlockType.IFixPatch,
            6 => EndfieldVfsBlockType.AuditStreaming,
            7 => EndfieldVfsBlockType.AuditDynamicStreaming,
            8 => EndfieldVfsBlockType.AuditIV,
            9 => EndfieldVfsBlockType.AuditAudio,
            10 => EndfieldVfsBlockType.AuditVideo,
            11 => EndfieldVfsBlockType.Bundle,
            12 => EndfieldVfsBlockType.Audio,
            13 => EndfieldVfsBlockType.Video,
            14 => EndfieldVfsBlockType.IV,
            15 => EndfieldVfsBlockType.Streaming,
            16 => EndfieldVfsBlockType.DynamicStreaming,
            17 => EndfieldVfsBlockType.Lua,
            18 => EndfieldVfsBlockType.Table,
            19 => EndfieldVfsBlockType.JsonData,
            20 => EndfieldVfsBlockType.ExtendData,
            21 => EndfieldVfsBlockType.HotfixAudio,
            101 => EndfieldVfsBlockType.AudioChinese,
            102 => EndfieldVfsBlockType.AudioEnglish,
            103 => EndfieldVfsBlockType.AudioJapanese,
            104 => EndfieldVfsBlockType.AudioKorean,
            _ => EndfieldVfsBlockType.Raw,
        };

        public static EndfieldVfsFileTag FileTagFromByte(byte value) => value switch
        {
            1 => EndfieldVfsFileTag.Audit,
            _ => EndfieldVfsFileTag.None,
        };

        public static string GetName(this EndfieldVfsBlockType blockType) => blockType switch
        {
            EndfieldVfsBlockType.None => "None",
            EndfieldVfsBlockType.InitialAudio => "InitAudio",
            EndfieldVfsBlockType.InitialBundle => "InitBundle",
            EndfieldVfsBlockType.InitialExtendData => "InitialExtendData",
            EndfieldVfsBlockType.BundleManifest => "BundleManifest",
            EndfieldVfsBlockType.IFixPatch => "IFixPatchOut",
            EndfieldVfsBlockType.AuditStreaming => "AuditStreaming",
            EndfieldVfsBlockType.AuditDynamicStreaming => "AuditDynamicStreaming",
            EndfieldVfsBlockType.AuditIV => "AuditIV",
            EndfieldVfsBlockType.AuditAudio => "AuditAudio",
            EndfieldVfsBlockType.AuditVideo => "AuditVideo",
            EndfieldVfsBlockType.Bundle => "Bundle",
            EndfieldVfsBlockType.Audio => "Audio",
            EndfieldVfsBlockType.Video => "Video",
            EndfieldVfsBlockType.IV => "IV",
            EndfieldVfsBlockType.Streaming => "Streaming",
            EndfieldVfsBlockType.DynamicStreaming => "DynamicStreaming",
            EndfieldVfsBlockType.Lua => "Lua",
            EndfieldVfsBlockType.Table => "Table",
            EndfieldVfsBlockType.JsonData => "JsonData",
            EndfieldVfsBlockType.ExtendData => "ExtendData",
            EndfieldVfsBlockType.HotfixAudio => "HotfixAudio",
            EndfieldVfsBlockType.AudioChinese => "AudioChinese",
            EndfieldVfsBlockType.AudioEnglish => "AudioEnglish",
            EndfieldVfsBlockType.AudioJapanese => "AudioJapanese",
            EndfieldVfsBlockType.AudioKorean => "AudioKorean",
            EndfieldVfsBlockType.Raw => "Raw",
            _ => "Raw",
        };

        public static bool TryParseCliValue(string value, out EndfieldVfsBlockType blockType)
        {
            var normalized = NormalizeCliValue(value);
            foreach (var item in Dumpable)
            {
                if (NormalizeCliValue(item.ToString()) == normalized || NormalizeCliValue(item.GetName()) == normalized)
                {
                    blockType = item;
                    return true;
                }
            }

            blockType = EndfieldVfsBlockType.Raw;
            return false;
        }

        private static string NormalizeCliValue(string value) =>
            value.Replace("-", "", StringComparison.Ordinal)
                .Replace("_", "", StringComparison.Ordinal)
                .ToLowerInvariant();
    }

    public sealed class EndfieldVfsFileInfo
    {
        public string FileName { get; set; } = string.Empty;
        public long FileNameHash { get; set; }
        public UInt128 FileChunkMd5 { get; set; }
        public UInt128 FileDataMd5 { get; set; }
        public long Offset { get; set; }
        public long Length { get; set; }
        public EndfieldVfsBlockType BlockType { get; set; }
        public bool UseEncrypt { get; set; }
        public long IvSeed { get; set; }
        public EndfieldVfsFileTag FileTag { get; set; }
    }

    public sealed class EndfieldVfsChunkInfo
    {
        public UInt128 Md5Name { get; set; }
        public UInt128 ContentMd5 { get; set; }
        public long Length { get; set; }
        public EndfieldVfsBlockType BlockType { get; set; }
        public EndfieldVfsFileTag MainTag { get; set; }
        public List<EndfieldVfsFileInfo> Files { get; } = new();

        public string FileName => $"{EndfieldVfsFormatting.UInt128LittleEndianHex(Md5Name)}.chk";
    }

    public sealed class EndfieldVfsBlockMainInfo
    {
        public int Version { get; set; }
        public string GroupConfigName { get; set; } = string.Empty;
        public long GroupConfigHashName { get; set; }
        public int GroupFileInfoNum { get; set; }
        public long GroupChunksLength { get; set; }
        public EndfieldVfsBlockType BlockType { get; set; }
        public List<EndfieldVfsChunkInfo> Chunks { get; } = new();
        public int CodeVersion { get; set; }
    }

    public class EndfieldVfsException : Exception
    {
        public EndfieldVfsException(string message) : base(message)
        {
        }

        public EndfieldVfsException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public sealed class EndfieldVfsBlockNotFoundException : EndfieldVfsException
    {
        public EndfieldVfsBlockNotFoundException(string hashDirectory)
            : base($"block directory not found: {hashDirectory}")
        {
            HashDirectory = hashDirectory;
        }

        public string HashDirectory { get; }
    }

    public sealed class EndfieldVfsChunkNotFoundException : EndfieldVfsException
    {
        public EndfieldVfsChunkNotFoundException(string chunkFileName)
            : base($"chunk file not found: {chunkFileName}")
        {
            ChunkFileName = chunkFileName;
        }

        public string ChunkFileName { get; }
    }

    public static class EndfieldVfsFormatting
    {
        public static string UInt128Hex(UInt128 value) => value.ToString("X32");

        public static string UInt128LittleEndianHex(UInt128 value)
        {
            Span<byte> bytes = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[..8], (ulong)value);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], (ulong)(value >> 64));

            var builder = new StringBuilder(32);
            foreach (var b in bytes)
            {
                builder.Append(b.ToString("X2"));
            }
            return builder.ToString();
        }
    }
}
