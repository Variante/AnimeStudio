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
            var streams = DemuxBytes(data);
            MuxToMp4(streams, outputPath);
        }

        private static DemuxedStreams DemuxBytes(byte[] data)
        {
            var offset = FindPattern(data, Crid, 0);
            if (offset < 0)
            {
                throw new EndfieldVfsException("invalid USM data: CRID marker not found");
            }

            var videoStreams = new Dictionary<uint, List<byte>>();
            var audioStreams = new Dictionary<uint, List<byte>>();

            while (offset + 8 <= data.Length)
            {
                var blockId = data.AsSpan(offset, 4).ToArray();
                if (!IsKnownBlock(blockId))
                {
                    break;
                }

                var blockSize = ReadUInt32BigEndian(data, offset + 4);
                var blockEnd = offset + 8L + blockSize;
                if (blockEnd > data.Length)
                {
                    break;
                }

                var isVideo = blockId.SequenceEqual(Sfv);
                var isAudio = blockId.SequenceEqual(Sfa);
                if ((isVideo || isAudio) && offset + 0xE <= data.Length)
                {
                    var headerSize = ReadUInt16BigEndian(data, offset + 8);
                    var footerSize = ReadUInt16BigEndian(data, offset + 0xA);
                    var streamId = isAudio ? data[offset + 0xC] : (byte)0;
                    if (blockSize > headerSize + footerSize)
                    {
                        var payloadSize = checked((int)blockSize - headerSize - footerSize);
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
    }
}
