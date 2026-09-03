using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio.Endfield
{
    public static class EndfieldUsmConverter
    {
        private static readonly byte[] Crid = Encoding.ASCII.GetBytes("CRID");
        private static readonly byte[] Sfv = { 0x40, 0x53, 0x46, 0x56 };
        private static readonly byte[] Sfa = { 0x40, 0x53, 0x46, 0x41 };
        private static readonly byte[] Alp = { 0x40, 0x41, 0x4C, 0x50 };
        private static readonly byte[] Sbt = { 0x40, 0x53, 0x42, 0x54 };
        private static readonly byte[] Cue = { 0x40, 0x43, 0x55, 0x45 };
        private static readonly byte[] Utf = { 0x40, 0x55, 0x54, 0x46 };
        private static readonly byte[] HeaderEnd = Encoding.ASCII.GetBytes("#HEADER END     ===============\0");
        private static readonly byte[] MetadataEnd = Encoding.ASCII.GetBytes("#METADATA END   ===============\0");
        private static readonly byte[] ContentsEnd = Encoding.ASCII.GetBytes("#CONTENTS END   ===============\0");

        public static void ConvertBytesToMp4(byte[] data, string outputPath)
        {
            // Validate the CRI outer framing before handing the source to an
            // optional helper.  A helper may produce an apparently playable
            // file from a truncated prefix, which would hide a bad VFS
            // boundary or an unsupported/multi-stream USM.
            var inspection = Inspect(data);
            if (TryConvertWithUsmHelper(data, outputPath))
            {
                return;
            }

            var streams = DemuxBytes(data, inspection);
            MuxToMp4(streams, outputPath);
        }

        internal static UsmInspection Inspect(byte[] data)
        {
            if (data == null)
            {
                throw new EndfieldVfsException("invalid USM data: input is null");
            }

            var offset = 0L;
            var blockCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var videoStreamIds = new HashSet<byte>();
            var audioStreamIds = new HashSet<byte>();
            var blockOrdinal = 0;
            while (offset < data.Length)
            {
                if (data.Length - offset < 8)
                {
                    throw new EndfieldVfsException(
                        $"invalid USM framing: short block header at offset {offset}; expected=8 actual={data.Length - offset}");
                }

                var blockId = Encoding.ASCII.GetString(data, checked((int)offset), 4);
                if (blockOrdinal == 0 && !string.Equals(blockId, "CRID", StringComparison.Ordinal))
                {
                    throw new EndfieldVfsException(
                        $"invalid USM framing: first block must be CRID; actual={blockId} offset={offset}");
                }
                if (!IsKnownBlock(data, checked((int)offset)))
                {
                    throw new EndfieldVfsException(
                        $"invalid USM framing: unknown block id '{blockId}' at offset {offset}; block={blockOrdinal}");
                }

                var blockSize = ReadUInt32BigEndian(data, checked((int)offset + 4));
                var blockEnd = checked(offset + 8L + blockSize);
                if (blockEnd > data.Length)
                {
                    throw new EndfieldVfsException(
                        $"invalid USM framing: block overruns input; block={blockOrdinal} id={blockId} offset={offset} declared={blockSize} remaining={data.Length - offset - 8}");
                }
                if (blockSize < 24)
                {
                    throw new EndfieldVfsException(
                        $"invalid USM framing: block header is truncated; block={blockOrdinal} id={blockId} offset={offset} declared={blockSize} expectedAtLeast=24");
                }

                var bodyOffset = checked((int)offset + 8);
                var headerSize = ReadUInt16BigEndian(data, bodyOffset);
                var footerSize = ReadUInt16BigEndian(data, bodyOffset + 2);
                if (headerSize != 24 || headerSize > blockSize)
                {
                    throw new EndfieldVfsException(
                        $"invalid USM framing: invalid header size; block={blockOrdinal} id={blockId} offset={offset} header={headerSize} expected=24 blockSize={blockSize}");
                }
                if ((ulong)headerSize + footerSize > blockSize)
                {
                    throw new EndfieldVfsException(
                        $"invalid USM framing: header/footer exceed block; block={blockOrdinal} id={blockId} offset={offset} header={headerSize} footer={footerSize} blockSize={blockSize}");
                }

                blockCounts[blockId] = blockCounts.GetValueOrDefault(blockId) + 1;
                var streamId = data[bodyOffset + 4];
                if (string.Equals(blockId, "@SFV", StringComparison.Ordinal))
                {
                    videoStreamIds.Add(streamId);
                }
                else if (string.Equals(blockId, "@SFA", StringComparison.Ordinal))
                {
                    audioStreamIds.Add(streamId);
                }

                offset = blockEnd;
                blockOrdinal++;
            }

            if (!blockCounts.TryGetValue("CRID", out var cridCount))
            {
                throw new EndfieldVfsException("invalid USM data: CRID marker not found at offset 0");
            }
            if (cridCount != 1)
            {
                throw new EndfieldVfsException($"invalid USM data: expected exactly one CRID block; actual={cridCount}");
            }
            if (!blockCounts.ContainsKey("@SFV"))
            {
                throw new EndfieldVfsException("invalid USM data: no video stream found");
            }
            if (videoStreamIds.Count > 1)
            {
                throw new EndfieldVfsException(
                    $"unsupported USM data: multiple video streams found; streamIds={string.Join(',', videoStreamIds.OrderBy(id => id))}");
            }
            if (audioStreamIds.Count > 1)
            {
                throw new EndfieldVfsException(
                    $"unsupported USM data: multiple audio streams found; streamIds={string.Join(',', audioStreamIds.OrderBy(id => id))}");
            }

            return new UsmInspection
            {
                ByteLength = data.Length,
                BlockCount = blockOrdinal,
                BlockCounts = blockCounts,
                VideoStreamIds = videoStreamIds.OrderBy(id => id).ToArray(),
                AudioStreamIds = audioStreamIds.OrderBy(id => id).ToArray(),
            };
        }

        private static bool TryConvertWithUsmHelper(byte[] data, string outputPath)
        {
            var helper = ResolveUsmConvert();
            if (helper == null)
            {
                return false;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), $"AnimeStudioUsmHelper_{Guid.NewGuid():N}");
            var inputPath = Path.Combine(tempDir, "input.usm");
            var helperOutputDir = Path.Combine(tempDir, "out");
            var helperOutputPath = Path.Combine(helperOutputDir, "input.mp4");
            try
            {
                Directory.CreateDirectory(helperOutputDir);
                File.WriteAllBytes(inputPath, data);

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = helper,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                    },
                };
                process.StartInfo.ArgumentList.Add("-o");
                process.StartInfo.ArgumentList.Add(helperOutputDir);
                process.StartInfo.ArgumentList.Add(inputPath);
                process.Start();
                process.WaitForExit();

                if (process.ExitCode != 0 || !File.Exists(helperOutputPath))
                {
                    return false;
                }

                var parent = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                File.Copy(helperOutputPath, outputPath, overwrite: true);
                return true;
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }
        }

        private static string ResolveUsmConvert()
        {
            var configured = Environment.GetEnvironmentVariable("ANIMESTUDIO_USM_CONVERT");
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            {
                return configured;
            }

            var local = Path.Combine(AppContext.BaseDirectory, "usm-convert.exe");
            if (File.Exists(local))
            {
                return local;
            }

            var repoLocal = Path.Combine(
                Environment.CurrentDirectory,
                "tools",
                "fluffy-dumper-src",
                "target",
                "release",
                "usm-convert.exe");
            return File.Exists(repoLocal) ? repoLocal : null;
        }
        private static DemuxedStreams DemuxBytes(byte[] data, UsmInspection inspection)
        {
            // Inspect has already proved exact framing; keep this parser's
            // cursor checks defensive because it is also the payload demuxer.
            var offset = 0;

            var videoStreams = new Dictionary<uint, List<byte>>();
            var audioStreams = new Dictionary<uint, List<byte>>();

            while (offset + 8 <= data.Length)
            {
                var blockId = data.AsSpan(offset, 4).ToArray();
                if (!IsKnownBlock(blockId))
                    throw new EndfieldVfsException($"invalid USM data: unknown block at offset {offset}");

                var blockSize = ReadUInt32BigEndian(data, offset + 4);
                var blockEnd = offset + 8L + blockSize;
                if (blockEnd > data.Length)
                    throw new EndfieldVfsException($"invalid USM data: block at offset {offset} exceeds input");

                var isVideo = blockId.SequenceEqual(Sfv);
                var isAudio = blockId.SequenceEqual(Sfa);
                if ((isVideo || isAudio) && offset + 0xE <= data.Length)
                {
                    var headerSize = ReadUInt16BigEndian(data, offset + 8);
                    var footerSize = ReadUInt16BigEndian(data, offset + 0xA);
                    var streamId = isAudio ? data[offset + 0xC] : (byte)0;
                    if ((ulong)headerSize + footerSize > blockSize || headerSize != 24)
                    {
                        throw new EndfieldVfsException($"invalid USM data: invalid block header at offset {offset}");
                    }
                    if (blockSize > headerSize + footerSize)
                    {
                        var payloadSize = checked((int)(blockSize - headerSize - footerSize));
                        var payloadStart = offset + 8 + headerSize;
                        var payloadEnd = payloadStart + payloadSize;
                        if (payloadEnd <= data.Length)
                        {
                            var target = isVideo ? videoStreams : audioStreams;
                            var key = isAudio ? (uint)streamId | ReadUInt32LittleEndian(blockId, 0) : ReadUInt32LittleEndian(blockId, 0);
                            if (!target.TryGetValue(key, out var bytes))
                            {
                                bytes = new List<byte>();
                                target[key] = bytes;
                            }
                            bytes.AddRange(data.AsSpan(payloadStart, payloadSize).ToArray());
                        }
                    }
                }

                offset = checked((int)blockEnd);
            }

            if (offset != data.Length)
            {
                throw new EndfieldVfsException($"invalid USM data: parser consumed {offset} of {data.Length} bytes");
            }

            var video = videoStreams.Values.FirstOrDefault();
            if (video == null)
            {
                throw new EndfieldVfsException("no video stream found");
            }

            var audio = audioStreams.Values.FirstOrDefault();
            var audioBytes = audio == null ? null : StripMarkers(audio.ToArray());
            return new DemuxedStreams
            {
                Video = StripMarkers(video.ToArray()),
                Audio = audioBytes,
                AudioExtension = audioBytes == null ? string.Empty : DetectAudioExtension(audioBytes),
            };
        }

        private static void MuxToMp4(DemuxedStreams streams, string outputPath)
        {
            var ffmpeg = ResolveFfmpeg();
            var tempDir = Path.Combine(Path.GetTempPath(), $"AnimeStudioUsm_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var videoPath = Path.Combine(tempDir, "video.m2v");
                File.WriteAllBytes(videoPath, streams.Video);

                string audioPath = null;
                if (streams.Audio != null)
                {
                    audioPath = Path.Combine(tempDir, $"audio{streams.AudioExtension}");
                    File.WriteAllBytes(audioPath, streams.Audio);
                }

                var parent = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                if (audioPath != null && RunFfmpeg(ffmpeg, outputPath, videoPath, audioPath) == 0)
                {
                    return;
                }

                var exitCode = RunFfmpeg(ffmpeg, outputPath, videoPath, null);
                if (exitCode != 0)
                {
                    throw new EndfieldVfsException($"ffmpeg remux failed with exit code {exitCode}");
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }
        }

        private static int RunFfmpeg(string ffmpeg, string outputPath, string videoPath, string audioPath)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                },
            };
            process.StartInfo.ArgumentList.Add("-y");
            process.StartInfo.ArgumentList.Add("-loglevel");
            process.StartInfo.ArgumentList.Add("error");
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(videoPath);
            if (audioPath != null)
            {
                process.StartInfo.ArgumentList.Add("-i");
                process.StartInfo.ArgumentList.Add(audioPath);
            }
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add("copy");
            process.StartInfo.ArgumentList.Add("-video_track_timescale");
            process.StartInfo.ArgumentList.Add("90000");
            process.StartInfo.ArgumentList.Add(outputPath);
            process.Start();
            process.WaitForExit();
            return process.ExitCode;
        }

        private static string ResolveFfmpeg()
        {
            var configured = Environment.GetEnvironmentVariable("ANIMESTUDIO_FFMPEG");
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            {
                return configured;
            }

            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local))
            {
                return local;
            }

            return "ffmpeg";
        }

        private static bool IsKnownBlock(byte[] id) =>
            id.SequenceEqual(Crid) ||
            id.SequenceEqual(Sfv) ||
            id.SequenceEqual(Sfa) ||
            id.SequenceEqual(Alp) ||
            id.SequenceEqual(Sbt) ||
            id.SequenceEqual(Cue) ||
            id.SequenceEqual(Utf);

        private static bool IsKnownBlock(byte[] data, int offset)
        {
            return data.AsSpan(offset, 4).SequenceEqual(Crid) ||
                data.AsSpan(offset, 4).SequenceEqual(Sfv) ||
                data.AsSpan(offset, 4).SequenceEqual(Sfa) ||
                data.AsSpan(offset, 4).SequenceEqual(Alp) ||
                data.AsSpan(offset, 4).SequenceEqual(Sbt) ||
                data.AsSpan(offset, 4).SequenceEqual(Cue) ||
                data.AsSpan(offset, 4).SequenceEqual(Utf);
        }

        private static byte[] StripMarkers(byte[] data)
        {
            var headerEnd = FindPattern(data, HeaderEnd, 0);
            var metadataEnd = FindPattern(data, MetadataEnd, 0);
            var headerSize = 0;
            if (headerEnd >= 0 && metadataEnd >= 0)
            {
                headerSize = metadataEnd > headerEnd
                    ? metadataEnd + MetadataEnd.Length
                    : headerEnd + HeaderEnd.Length;
            }
            else if (headerEnd >= 0)
            {
                headerSize = headerEnd + HeaderEnd.Length;
            }
            else if (metadataEnd >= 0)
            {
                headerSize = metadataEnd + MetadataEnd.Length;
            }

            var start = headerSize <= data.Length ? headerSize : 0;
            var footer = FindPattern(data, ContentsEnd, start);
            var end = footer >= 0 ? footer : data.Length;
            var length = Math.Max(0, end - start);
            var result = new byte[length];
            Array.Copy(data, start, result, 0, length);
            return result;
        }

        private static string DetectAudioExtension(byte[] data)
        {
            if (data.Length < 4)
            {
                return ".bin";
            }

            if (data.AsSpan(0, 4).SequenceEqual(Encoding.ASCII.GetBytes("AIXF")))
            {
                return ".aix";
            }
            if (data[0] == 0x80)
            {
                return ".adx";
            }
            if (data[0] == (byte)'H' && data[1] == (byte)'C' && data[2] == (byte)'A' && data[3] == 0)
            {
                return ".hca";
            }
            return ".bin";
        }

        private static int FindPattern(byte[] data, byte[] pattern, int start)
        {
            for (var i = start; i <= data.Length - pattern.Length; i++)
            {
                var found = true;
                for (var j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found)
                {
                    return i;
                }
            }
            return -1;
        }

        private static ushort ReadUInt16BigEndian(byte[] data, int offset) =>
            (ushort)((data[offset] << 8) | data[offset + 1]);

        private static uint ReadUInt32BigEndian(byte[] data, int offset) =>
            ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

        private static uint ReadUInt32LittleEndian(byte[] data, int offset) =>
            (uint)data[offset] | ((uint)data[offset + 1] << 8) | ((uint)data[offset + 2] << 16) | ((uint)data[offset + 3] << 24);

        private sealed class DemuxedStreams
        {
            public byte[] Video { get; set; }
            public byte[] Audio { get; set; }
            public string AudioExtension { get; set; }
        }

        internal sealed class UsmInspection
        {
            public int ByteLength { get; init; }
            public int BlockCount { get; init; }
            public IReadOnlyDictionary<string, int> BlockCounts { get; init; }
            public IReadOnlyList<byte> VideoStreamIds { get; init; }
            public IReadOnlyList<byte> AudioStreamIds { get; init; }
        }
    }
}
