using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace AnimeStudio.Endfield
{
    /// <summary>
    /// The metadata and a bounded, reopenable stream for one logical VFS file.
    /// The stream must contain exactly <see cref="Length"/> logical bytes.
    /// </summary>
    public sealed class EndfieldVfsCorpusFile
    {
        public byte BlockTypeValue { get; set; }
        public string BlockTypeName { get; set; } = string.Empty;
        public string VirtualPath { get; set; } = string.Empty;
        public string ChunkFileName { get; set; } = string.Empty;
        public string ChunkMd5 { get; set; } = string.Empty;
        public string ChunkContentMd5 { get; set; } = string.Empty;
        public string ChunkSource { get; set; } = string.Empty;
        public long ChunkLength { get; set; }
        public long Offset { get; set; }
        public long Length { get; set; }
        public bool UseEncrypt { get; set; }
        /// <summary>Set only after the caller has validated the selected metadata.</summary>
        public bool MetadataVerified { get; set; }
        /// <summary>Optional terminal status used for excluded or unavailable metadata rows.</summary>
        public string StatusOverride { get; set; }
        public string DiagnosticOverride { get; set; }
        public Func<Stream> OpenStream { get; set; }

        internal string StableKey => string.Join("\u001f", new[]
        {
            BlockTypeValue.ToString(CultureInfo.InvariantCulture),
            VirtualPath ?? string.Empty,
            ChunkSource ?? string.Empty,
            ChunkFileName ?? string.Empty,
            Offset.ToString(CultureInfo.InvariantCulture),
            Length.ToString(CultureInfo.InvariantCulture),
        });

        /// <summary>Effective logical identity used for duplicate-ledger checks.</summary>
        internal string LogicalKey => string.Join("\u001f", new[]
        {
            BlockTypeValue.ToString(CultureInfo.InvariantCulture),
            (VirtualPath ?? string.Empty).Replace('\\', '/'),
        });

    }

    /// <summary>Deterministic bounded observations for one logical VFS file.</summary>
    public sealed class EndfieldVfsCorpusObservation
    {
        public int SchemaVersion { get; set; } = 1;
        public string RecordType { get; set; } = "vfs_corpus_observation";
        public string Status { get; set; } = string.Empty;
        public string Diagnostic { get; set; }
        public byte BlockTypeRawId { get; set; }
        public string BlockTypeName { get; set; } = string.Empty;
        public string VirtualPath { get; set; } = string.Empty;
        public string[] VirtualPathTokens { get; set; } = Array.Empty<string>();
        public string Suffix { get; set; } = string.Empty;
        public string PathFamily { get; set; } = string.Empty;
        public string SizeBand { get; set; } = string.Empty;
        public string StructureClusterKey { get; set; } = string.Empty;
        public long DeclaredSize { get; set; }
        public long BytesRead { get; set; }
        public string ChunkFileName { get; set; } = string.Empty;
        public string ChunkMd5 { get; set; } = string.Empty;
        public string ChunkContentMd5 { get; set; } = string.Empty;
        public string ChunkSource { get; set; } = string.Empty;
        public long ChunkLength { get; set; }
        public long FileOffset { get; set; }
        public bool Encrypted { get; set; }
        public bool MetadataVerified { get; set; }
        public int BoundedByteLimit { get; set; }
        public string FirstBytesHex { get; set; } = string.Empty;
        public string LastBytesHex { get; set; } = string.Empty;
        public string Magic { get; set; } = string.Empty;
        public string MagicHex { get; set; } = string.Empty;
        public AlignmentObservation Alignment { get; set; } = new();
        public EntropyObservation Entropy { get; set; } = new();
        public TextObservation TextCandidate { get; set; } = new();
        public string[] CompressionSignatures { get; set; } = Array.Empty<string>();
        public string Sha256 { get; set; } = string.Empty;
    }

    public sealed class AlignmentObservation
    {
        public int OffsetMod2 { get; set; }
        public int OffsetMod4 { get; set; }
        public int OffsetMod8 { get; set; }
        public int SizeMod2 { get; set; }
        public int SizeMod4 { get; set; }
        public int SizeMod8 { get; set; }
        public int CommonAlignment { get; set; }
    }

    public sealed class EntropyObservation
    {
        public string Band { get; set; } = "empty";
        public double BitsPerByte { get; set; }
        public long SampledBytes { get; set; }
    }

    public sealed class TextObservation
    {
        public bool IsCandidate { get; set; }
        public string Encoding { get; set; } = string.Empty;
        public double PrintableRatio { get; set; }
        public string Sample { get; set; } = string.Empty;
    }

    public sealed class EndfieldVfsCorpusSummary
    {
        public int SchemaVersion { get; set; } = 1;
        public string RecordType { get; set; } = "vfs_corpus_summary";
        public string PrimaryAssets { get; set; } = string.Empty;
        public string FallbackAssets { get; set; } = string.Empty;
        public string LedgerSha256 { get; set; } = string.Empty;
        public bool Complete { get; set; }
        public int FileCount { get; set; }
        public long DeclaredBytes { get; set; }
        public long BytesRead { get; set; }
        public int FailureCount { get; set; }
        public int MetadataUnverifiedCount { get; set; }
        public int UnavailableCount { get; set; }
        public int ExcludedCount { get; set; }
        public long UnavailableBytes { get; set; }
        public long ExcludedBytes { get; set; }
        public int UnavailableBlockCount { get; set; }
        public int UnavailableChunkCount { get; set; }
        public int ExcludedBlockCount { get; set; }
        public int ExcludedChunkCount { get; set; }
        public int ReconciledClusterFileCount { get; set; }
        public long ReconciledClusterBytes { get; set; }
        public bool ClusterCountsReconciled { get; set; }
        public bool ClusterBytesReconciled { get; set; }
        public int DistinctContentSha256Count { get; set; }
        public int ExactDuplicateGroupCount { get; set; }
        public int ExactDuplicateFileCount { get; set; }
        public long ExactDuplicateBytes { get; set; }
        public List<ExactContentDuplicateGroup> ExactContentSha256DuplicateGroups { get; set; } = new();
        public List<StructureClusterSummary> StructureClusters { get; set; } = new();
        public int DuplicateInputCount { get; set; }
        public Dictionary<string, int> StatusCounts { get; set; } = new(StringComparer.Ordinal);
        public string FirstFailureKey { get; set; }
        public string FirstFailureDiagnostic { get; set; }
        public string Ordering { get; set; } = "blockTypeRawId,virtualPath,chunkSource,chunkFileName,fileOffset,fileSize";

        public void RecomputeCompleteness()
        {
            Complete = DuplicateInputCount == 0 && FailureCount == 0
                && MetadataUnverifiedCount == 0 && UnavailableCount == 0
                && UnavailableBlockCount == 0 && UnavailableChunkCount == 0
                && ClusterCountsReconciled && ClusterBytesReconciled;
        }
    }

    public sealed class ExactContentDuplicateGroup
    {
        public string Sha256 { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public long DeclaredBytes { get; set; }
    }

    public sealed class StructureClusterSummary
    {
        public string Key { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public long DeclaredBytes { get; set; }
    }

    /// <summary>
    /// Streams each logical file once and emits bounded structural observations.
    /// No output field contains an unbounded payload or decoded content.
    /// </summary>
    public static class EndfieldVfsCorpusClassifier
    {
        public const int DefaultBoundedByteLimit = 64;
        private const int ReadBufferSize = 64 * 1024;
        private const int TextProbeLimit = 512;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Encoding StrictUtf16 = new UnicodeEncoding(false, true, true);
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
        };

        /// <summary>
        /// Creates a streaming classifier input from a selected catalog row. The
        /// returned stream opens the physical chunk on demand and decrypts only the
        /// requested range; no logical file-sized buffer is allocated.
        /// </summary>
        public static EndfieldVfsCorpusFile FromLoader(
            EndfieldVfsLoader loader,
            EndfieldVfsCatalogEntry catalog,
            EndfieldVfsChunkInfo chunk,
            EndfieldVfsFileInfo file,
            bool metadataVerified,
            string chunkSource = null)
        {
            if (loader == null) throw new ArgumentNullException(nameof(loader));
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            if (file == null) throw new ArgumentNullException(nameof(file));
            return new EndfieldVfsCorpusFile
            {
                BlockTypeValue = file.BlockTypeValue,
                BlockTypeName = EndfieldVfsBlockTypes.GetName(file.BlockTypeValue),
                VirtualPath = file.FileName,
                ChunkFileName = chunk.FileName,
                ChunkMd5 = EndfieldVfsFormatting.UInt128LittleEndianHex(chunk.Md5Name),
                ChunkContentMd5 = EndfieldVfsFormatting.UInt128LittleEndianHex(chunk.ContentMd5),
                ChunkSource = chunkSource ?? (catalog.CanonicalIsPrimary ? "primary" : "fallback"),
                ChunkLength = chunk.Length,
                Offset = file.Offset,
                Length = file.Length,
                UseEncrypt = file.UseEncrypt,
                MetadataVerified = metadataVerified,
                OpenStream = () => new LogicalFileStream(
                    loader.ResolveChunkPath(catalog, chunk), chunk.Length, file.Offset, file.Length,
                    file.UseEncrypt, file.IvSeed),
            };
        }

        public static EndfieldVfsCorpusObservation Observe(
            EndfieldVfsCorpusFile input,
            int boundedByteLimit = DefaultBoundedByteLimit)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (boundedByteLimit < 1 || boundedByteLimit > 4096)
                throw new ArgumentOutOfRangeException(nameof(boundedByteLimit), "must be between 1 and 4096");

            var observation = CreateMetadataObservation(input, boundedByteLimit);
            if (input.Length < 0)
                return Fail(observation, "invalid_declared_length");
            if (input.Offset < 0 || input.ChunkLength < 0 || input.Offset > input.ChunkLength
                || input.Length > input.ChunkLength - input.Offset)
                return Fail(observation, "invalid_declared_range");
            if (!string.IsNullOrEmpty(input.StatusOverride))
            {
                observation.Status = input.StatusOverride;
                observation.Diagnostic = BoundDiagnostic(input.DiagnosticOverride);
                observation.StructureClusterKey = BuildStructureClusterKey(observation);
                return observation;
            }
            if (input.OpenStream == null)
                return Fail(observation, "stream_provider_missing");

            try
            {
                using var stream = input.OpenStream();
                if (stream == null) return Fail(observation, "stream_provider_returned_null");
                ReadBounded(stream, input.Length, boundedByteLimit, observation);
                // This pass proves bounded structural observation over an
                // authenticated metadata selection.  It does not recompute the
                // VFS FileDataMd5 (that is the responsibility of vfs-audit), so
                // do not label the row as a broader integrity certification.
                observation.Status = input.MetadataVerified ? "profiled" : "metadata_unverified";
                return observation;
            }
            catch (Exception exception) when (exception is EndfieldVfsException
                || exception is EndOfStreamException || exception is IOException
                || exception is CryptographicException)
            {
                return Fail(observation, BoundDiagnostic(exception.Message));
            }
        }

        /// <summary>
        /// Writes sorted JSONL.GZ plus a deterministic summary. Sorting metadata is
        /// bounded by the number of catalog rows; logical payloads are never retained.
        /// </summary>
        public static EndfieldVfsCorpusSummary WriteJsonlGzip(
            IEnumerable<EndfieldVfsCorpusFile> inputs,
            string ledgerPath,
            string summaryPath,
            int boundedByteLimit = DefaultBoundedByteLimit)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            if (string.IsNullOrWhiteSpace(ledgerPath)) throw new ArgumentException("path is required", nameof(ledgerPath));
            if (string.IsNullOrWhiteSpace(summaryPath)) throw new ArgumentException("path is required", nameof(summaryPath));

            var ordered = inputs.OrderBy(item => item?.StableKey ?? string.Empty, StringComparer.Ordinal).ToList();
            var summary = new EndfieldVfsCorpusSummary { FileCount = ordered.Count };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var contentGroups = new Dictionary<string, ExactContentDuplicateGroup>(StringComparer.Ordinal);
            var clusters = new Dictionary<string, StructureClusterSummary>(StringComparer.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ledgerPath))!);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(summaryPath))!);
            using (var file = new FileStream(ledgerPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
            {
                foreach (var input in ordered)
                {
                    var key = input?.StableKey ?? string.Empty;
                    if (input != null && !seen.Add(input.LogicalKey)) summary.DuplicateInputCount++;
                    var row = input == null
                        ? new EndfieldVfsCorpusObservation { Status = "input_missing", Diagnostic = "null input row" }
                        : Observe(input, boundedByteLimit);
                    writer.WriteLine(JsonConvert.SerializeObject(row, Formatting.None, JsonSettings));
                    summary.DeclaredBytes = checked(summary.DeclaredBytes + Math.Max(0, row.DeclaredSize));
                    summary.BytesRead = checked(summary.BytesRead + Math.Max(0, row.BytesRead));
                    summary.StatusCounts.TryGetValue(row.Status, out var count);
                    summary.StatusCounts[row.Status] = count + 1;
                    if (row.Status == "metadata_unverified") summary.MetadataUnverifiedCount++;
                    if (row.Status == "unavailable")
                    {
                        summary.UnavailableCount++;
                        summary.UnavailableBytes = checked(summary.UnavailableBytes + Math.Max(0, row.DeclaredSize));
                    }
                    if (row.Status == "excluded")
                    {
                        summary.ExcludedCount++;
                        summary.ExcludedBytes = checked(summary.ExcludedBytes + Math.Max(0, row.DeclaredSize));
                    }
                    if (!string.IsNullOrEmpty(row.Sha256))
                    {
                        if (!contentGroups.TryGetValue(row.Sha256, out var group))
                        {
                            group = new ExactContentDuplicateGroup { Sha256 = row.Sha256 };
                            contentGroups.Add(row.Sha256, group);
                        }
                        group.FileCount++;
                        group.DeclaredBytes = checked(group.DeclaredBytes + Math.Max(0, row.DeclaredSize));
                    }
                    var clusterKey = row.StructureClusterKey ?? string.Empty;
                    if (!clusters.TryGetValue(clusterKey, out var cluster))
                    {
                        cluster = new StructureClusterSummary { Key = clusterKey };
                        clusters.Add(clusterKey, cluster);
                    }
                    cluster.FileCount++;
                    cluster.DeclaredBytes = checked(cluster.DeclaredBytes + Math.Max(0, row.DeclaredSize));
                    if (summary.FirstFailureKey == null && row.Status != "profiled")
                    {
                        summary.FirstFailureKey = key;
                        summary.FirstFailureDiagnostic = row.Diagnostic ?? row.Status;
                    }
                }
            }
            summary.FailureCount = summary.StatusCounts
                .Where(pair => pair.Key != "profiled" && pair.Key != "metadata_unverified"
                    && pair.Key != "unavailable" && pair.Key != "excluded")
                .Sum(pair => pair.Value);
            summary.DistinctContentSha256Count = contentGroups.Count;
            summary.ExactContentSha256DuplicateGroups = contentGroups.Values
                .Where(group => group.FileCount > 1)
                .OrderBy(group => group.Sha256, StringComparer.Ordinal).ToList();
            summary.ExactDuplicateGroupCount = summary.ExactContentSha256DuplicateGroups.Count;
            summary.ExactDuplicateFileCount = summary.ExactContentSha256DuplicateGroups.Sum(group => group.FileCount);
            summary.ExactDuplicateBytes = summary.ExactContentSha256DuplicateGroups.Sum(group => group.DeclaredBytes);
            summary.StructureClusters = clusters.Values.OrderBy(cluster => cluster.Key, StringComparer.Ordinal).ToList();
            summary.ReconciledClusterFileCount = summary.StructureClusters.Sum(cluster => cluster.FileCount);
            summary.ReconciledClusterBytes = summary.StructureClusters.Sum(cluster => cluster.DeclaredBytes);
            summary.ClusterCountsReconciled = summary.ReconciledClusterFileCount == summary.FileCount;
            summary.ClusterBytesReconciled = summary.ReconciledClusterBytes == summary.DeclaredBytes;
            summary.RecomputeCompleteness();
            WriteSummary(summaryPath, summary);
            return summary;
        }

        public static void WriteSummary(string summaryPath, EndfieldVfsCorpusSummary summary)
        {
            if (string.IsNullOrWhiteSpace(summaryPath)) throw new ArgumentException("path is required", nameof(summaryPath));
            if (summary == null) throw new ArgumentNullException(nameof(summary));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(summaryPath))!);
            File.WriteAllText(summaryPath, JsonConvert.SerializeObject(summary, Formatting.Indented, JsonSettings), new UTF8Encoding(false));
        }

        private static EndfieldVfsCorpusObservation CreateMetadataObservation(EndfieldVfsCorpusFile input, int boundedByteLimit) => new()
        {
            BlockTypeRawId = input.BlockTypeValue,
            BlockTypeName = string.IsNullOrEmpty(input.BlockTypeName)
                ? EndfieldVfsBlockTypes.GetName(input.BlockTypeValue) : input.BlockTypeName,
            VirtualPath = NormalizePath(input.VirtualPath),
            VirtualPathTokens = PathTokens(input.VirtualPath),
            Suffix = GetSuffix(input.VirtualPath),
            PathFamily = GetPathFamily(input.VirtualPath),
            SizeBand = GetSizeBand(input.Length),
            DeclaredSize = input.Length,
            ChunkFileName = input.ChunkFileName ?? string.Empty,
            ChunkMd5 = input.ChunkMd5 ?? string.Empty,
            ChunkContentMd5 = input.ChunkContentMd5 ?? string.Empty,
            ChunkSource = input.ChunkSource ?? string.Empty,
            ChunkLength = input.ChunkLength,
            FileOffset = input.Offset,
            Encrypted = input.UseEncrypt,
            MetadataVerified = input.MetadataVerified,
            BoundedByteLimit = boundedByteLimit,
            Alignment = BuildAlignment(input.Offset, input.Length),
            StructureClusterKey = string.Empty,
        };

        private static void ReadBounded(Stream stream, long expectedLength, int limit, EndfieldVfsCorpusObservation observation)
        {
            var prefix = new byte[Math.Min(limit, (int)Math.Min(expectedLength, limit))];
            var tail = new byte[limit];
            var tailCount = 0;
            var tailNext = 0;
            var textProbe = new MemoryStream(Math.Min(TextProbeLimit, prefix.Length == 0 ? TextProbeLimit : TextProbeLimit));
            var histogram = new long[256];
            using var sha256 = SHA256.Create();
            var buffer = new byte[ReadBufferSize];
            var remaining = expectedLength;
            var bytesRead = 0L;
            while (remaining > 0)
            {
                var wanted = (int)Math.Min(buffer.Length, remaining);
                var read = stream.Read(buffer, 0, wanted);
                if (read <= 0) throw new EndOfStreamException($"short logical file: expected {expectedLength}, received {bytesRead}");
                sha256.TransformBlock(buffer, 0, read, buffer, 0);
                for (var i = 0; i < read; i++)
                {
                    var value = buffer[i];
                    histogram[value]++;
                    if (bytesRead + i < prefix.Length) prefix[bytesRead + i] = value;
                    if (textProbe.Length < TextProbeLimit) textProbe.WriteByte(value);
                    tail[tailNext] = value;
                    tailNext = (tailNext + 1) % tail.Length;
                    if (tailCount < tail.Length) tailCount++;
                }
                bytesRead += read;
                observation.BytesRead = bytesRead;
                remaining -= read;
            }
            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            if (stream.ReadByte() != -1) throw new EndfieldVfsException("logical stream has bytes beyond declared length");

            observation.BytesRead = bytesRead;
            observation.FirstBytesHex = Convert.ToHexString(prefix);
            observation.LastBytesHex = Convert.ToHexString(TailBytes(tail, tailCount, tailNext));
            observation.MagicHex = Convert.ToHexString(prefix.AsSpan(0, Math.Min(prefix.Length, 8)));
            observation.Magic = IdentifyMagic(prefix);
            observation.CompressionSignatures = IdentifyCompression(prefix);
            observation.Entropy = BuildEntropy(histogram, bytesRead);
            observation.TextCandidate = BuildTextCandidate(textProbe.ToArray());
            observation.Sha256 = Convert.ToHexString(sha256.Hash!);
            observation.StructureClusterKey = BuildStructureClusterKey(observation);
        }

        private static string[] IdentifyCompression(byte[] prefix)
        {
            if (prefix.Length < 2) return Array.Empty<string>();
            var signatures = new List<string>();
            if (StartsWith(prefix, 0x1F, 0x8B)) signatures.Add("gzip");
            if (StartsWith(prefix, 0x78, 0x01) || StartsWith(prefix, 0x78, 0x5E) || StartsWith(prefix, 0x78, 0x9C) || StartsWith(prefix, 0x78, 0xDA)) signatures.Add("zlib");
            if (StartsWith(prefix, 0x28, 0xB5, 0x2F, 0xFD)) signatures.Add("zstd");
            if (StartsWith(prefix, 0x04, 0x22, 0x4D, 0x18)) signatures.Add("lz4_frame");
            if (StartsWith(prefix, 0x42, 0x5A, 0x68)) signatures.Add("bzip2");
            if (StartsWith(prefix, 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00)) signatures.Add("xz");
            if (StartsWith(prefix, 0x50, 0x4B, 0x03, 0x04)) signatures.Add("zip");
            return signatures.ToArray();
        }

        private static string IdentifyMagic(byte[] prefix)
        {
            if (StartsWith(prefix, 0x1F, 0x8B)) return "gzip";
            if (StartsWith(prefix, 0x28, 0xB5, 0x2F, 0xFD)) return "zstd";
            if (StartsWith(prefix, 0x04, 0x22, 0x4D, 0x18)) return "lz4_frame";
            if (StartsWith(prefix, 0x89, 0x50, 0x4E, 0x47)) return "png";
            if (StartsWith(prefix, 0xFF, 0xD8, 0xFF)) return "jpeg";
            if (StartsWith(prefix, 0x52, 0x49, 0x46, 0x46)) return "riff";
            if (StartsWith(prefix, 0x55, 0x53, 0x4D, 0x00)) return "crid_usm";
            if (StartsWith(prefix, 0x41, 0x4B, 0x50, 0x4B)) return "akpk";
            if (StartsWith(prefix, 0x55, 0x6E, 0x69, 0x74, 0x79, 0x46, 0x53)) return "unityfs";
            if (StartsWith(prefix, 0x50, 0x4B, 0x03, 0x04)) return "zip";
            if (prefix.Length > 0 && (prefix[0] == (byte)'{' || prefix[0] == (byte)'[')) return "json_candidate";
            return prefix.Length == 0 ? "empty" : "unknown";
        }

        private static TextObservation BuildTextCandidate(byte[] bytes)
        {
            if (bytes.Length == 0) return new TextObservation();
            var encoding = string.Empty;
            string text = null;
            try
            {
                if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                {
                    encoding = "utf-16le";
                    text = StrictUtf16.GetString(bytes, 2, bytes.Length - 2);
                }
                else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    encoding = "utf-8-bom";
                    text = StrictUtf8.GetString(bytes, 3, bytes.Length - 3);
                }
                else
                {
                    encoding = "utf-8";
                    text = StrictUtf8.GetString(bytes);
                }
            }
            catch (DecoderFallbackException)
            {
                return new TextObservation();
            }
            if (text.Length == 0) return new TextObservation { Encoding = encoding };
            var printable = text.Count(ch => ch == '\t' || ch == '\r' || ch == '\n' || !char.IsControl(ch));
            var ratio = Math.Round((double)printable / text.Length, 6, MidpointRounding.ToEven);
            var candidate = ratio >= 0.85 && !text.Contains('\0');
            return new TextObservation
            {
                IsCandidate = candidate,
                Encoding = encoding,
                PrintableRatio = ratio,
                Sample = candidate ? text.Substring(0, Math.Min(text.Length, 256)) : string.Empty,
            };
        }

        private static EntropyObservation BuildEntropy(long[] histogram, long count)
        {
            if (count == 0) return new EntropyObservation();
            var entropy = 0d;
            foreach (var value in histogram)
            {
                if (value == 0) continue;
                var probability = (double)value / count;
                entropy -= probability * Math.Log(probability, 2);
            }
            entropy = Math.Round(entropy, 6, MidpointRounding.ToEven);
            return new EntropyObservation
            {
                BitsPerByte = entropy,
                SampledBytes = count,
                Band = entropy < 4d ? "low" : entropy < 7d ? "medium" : "high",
            };
        }

        private static AlignmentObservation BuildAlignment(long offset, long size)
        {
            var common = 1;
            foreach (var alignment in new[] { 8, 4, 2 })
            {
                if (offset % alignment == 0 && size % alignment == 0) { common = alignment; break; }
            }
            return new AlignmentObservation
            {
                OffsetMod2 = Mod(offset, 2), OffsetMod4 = Mod(offset, 4), OffsetMod8 = Mod(offset, 8),
                SizeMod2 = Mod(size, 2), SizeMod4 = Mod(size, 4), SizeMod8 = Mod(size, 8), CommonAlignment = common,
            };
        }

        private static string[] PathTokens(string path) => (path ?? string.Empty)
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static string NormalizePath(string path) => string.Join("/", PathTokens(path));

        private static string GetSuffix(string path)
        {
            var file = PathTokens(path).LastOrDefault() ?? string.Empty;
            var dot = file.LastIndexOf('.');
            return dot >= 0 ? file.Substring(dot).ToLowerInvariant() : string.Empty;
        }

        private static string GetPathFamily(string path)
        {
            var tokens = PathTokens(path);
            if (tokens.Length <= 1) return "root";
            // Keep enough directory depth to separate high-volume families such
            // as Data/Json/LipSync without leaking the filename into the family.
            var directoryCount = tokens.Length - 1;
            var depth = Math.Min(3, directoryCount);
            return string.Join("/", tokens.Take(depth)).ToLowerInvariant();
        }

        private static string GetSizeBand(long size) => size < 0 ? "invalid"
            : size == 0 ? "empty"
            : size <= 256 ? "tiny"
            : size <= 4 * 1024 ? "small"
            : size <= 1024 * 1024 ? "medium"
            : size <= 16 * 1024 * 1024 ? "large" : "huge";

        private static string BuildStructureClusterKey(EndfieldVfsCorpusObservation observation) => string.Join("|", new[]
        {
            observation.BlockTypeRawId.ToString(CultureInfo.InvariantCulture),
            observation.PathFamily ?? string.Empty,
            observation.Suffix ?? string.Empty,
            observation.SizeBand ?? string.Empty,
            string.IsNullOrEmpty(observation.Magic) ? "unknown" : observation.Magic,
            string.Join(",", observation.CompressionSignatures ?? Array.Empty<string>()),
            observation.Alignment?.CommonAlignment.ToString(CultureInfo.InvariantCulture) ?? "0",
            observation.Entropy?.Band ?? "empty",
        });

        private static byte[] TailBytes(byte[] tail, int count, int next)
        {
            var result = new byte[count];
            for (var i = 0; i < count; i++) result[i] = tail[(next - count + i + tail.Length) % tail.Length];
            return result;
        }

        private static bool StartsWith(byte[] data, params byte[] signature) => data.Length >= signature.Length && data.AsSpan(0, signature.Length).SequenceEqual(signature);
        private static int Mod(long value, int modulus) => (int)(value % modulus);
        private static EndfieldVfsCorpusObservation Fail(EndfieldVfsCorpusObservation observation, string diagnostic)
        {
            observation.Status = diagnostic.StartsWith("short logical", StringComparison.Ordinal) ? "short_read" : "failed";
            observation.Diagnostic = BoundDiagnostic(diagnostic);
            return observation;
        }

        private static string BoundDiagnostic(string diagnostic)
        {
            if (string.IsNullOrEmpty(diagnostic)) return "unknown classifier failure";
            return diagnostic.Length <= 512 ? diagnostic : diagnostic.Substring(0, 512);
        }

        private sealed class LogicalFileStream : Stream
        {
            private readonly FileStream input;
            private readonly EndfieldChaCha20 cipher;
            private long remaining;

            public LogicalFileStream(string path, long chunkLength, long offset, long length, bool encrypted, long ivSeed)
            {
                if (chunkLength < 0 || offset < 0 || length < 0 || offset > chunkLength || length > chunkLength - offset)
                    throw new EndfieldVfsException($"invalid logical file range: offset {offset}, length {length}, chunk length {chunkLength}");
                input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ReadBufferSize, FileOptions.SequentialScan);
                if (input.Length != chunkLength)
                {
                    input.Dispose();
                    throw new EndfieldVfsException($"chunk length mismatch: metadata {chunkLength}, actual {input.Length}");
                }
                input.Seek(offset, SeekOrigin.Begin);
                remaining = length;
                if (encrypted)
                {
                    Span<byte> nonce = stackalloc byte[12];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(nonce[..4], EndfieldVfsLoader.VfsProtoVersion);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(nonce[4..], ivSeed);
                    cipher = new EndfieldChaCha20(EndfieldVfsKeys.ChaChaKey, nonce, 1);
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (remaining == 0) return 0;
                var read = input.Read(buffer, offset, (int)Math.Min(count, remaining));
                if (read > 0)
                {
                    cipher?.ApplyKeystream(buffer.AsSpan(offset, read));
                    remaining -= read;
                }
                return read;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => remaining;
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing)
            {
                if (disposing) input.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
