using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace AnimeStudio.CLI
{
    /// <summary>
    /// Writes opt-in exact managed-reference evidence without changing normal
    /// JSON or object-index output. A final file exists only after a complete
    /// export and ends with a terminal summary row.
    /// </summary>
    public sealed class ManagedReferenceDiagnosticsJsonlWriter : IDisposable
    {
        private const int SchemaVersion = 1;
        private const int MaxRecords = 4096;
        private const int MaxPayloadBytes = 1024 * 1024;
        private const int MaxSerializedRefTypeTreeNodes = 16384;
        private const int MaxDiagnosticTextLength = 1024;
        private const string SidecarSchema = "endfield-animestudio-managed-reference-diagnostics-v1";

        private readonly object sync = new object();
        private readonly string finalPath;
        private readonly string temporaryPath;
        private readonly string inputBasePath;
        private readonly StreamWriter writer;
        private readonly Regex[] typeFilters;
        private readonly bool includeExactMatches;
        private readonly HashSet<string> emittedKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, OrderedDictionary> pendingDecodeFailures =
            new Dictionary<string, OrderedDictionary>(StringComparer.Ordinal);
        private readonly IncrementalHash contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long recordCount;
        private long contentBytes;
        private long errorCount;
        private long suppressedCount;
        private bool completed;
        private bool disposed;

        private ManagedReferenceDiagnosticsJsonlWriter(
            FileInfo output,
            FileInfo input,
            Regex[] filters,
            bool includeExactTypeMatches)
        {
            finalPath = Path.GetFullPath(output.FullName);
            temporaryPath = finalPath + ".tmp";
            var outputDirectory = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            inputBasePath = ResolveInputBasePath(input);
            typeFilters = filters?.Where(filter => filter != null).ToArray() ?? Array.Empty<Regex>();
            includeExactMatches = includeExactTypeMatches;
            writer = new StreamWriter(
                new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false),
                64 * 1024
            );
            Current = this;
            WriteContentRow(new OrderedDictionary
            {
                { "recordType", "header" },
                { "schemaVersion", SchemaVersion },
                { "sidecarSchema", SidecarSchema },
                { "input", Path.GetFullPath(input.FullName) },
                { "selection", new OrderedDictionary
                    {
                        { "partialManagedReferencesOnly", !includeExactMatches },
                        { "includeExactTypeMatches", includeExactMatches },
                        { "typeRegex", typeFilters.Select(filter => filter.ToString()).ToList() },
                        { "maxRecords", MaxRecords },
                        { "maxPayloadBytes", MaxPayloadBytes },
                    }
                },
            });
        }

        public static ManagedReferenceDiagnosticsJsonlWriter Current { get; private set; }

        public static ManagedReferenceDiagnosticsJsonlWriter Open(
            FileInfo output,
            FileInfo input,
            Regex[] filters,
            bool includeExactTypeMatches = false)
        {
            if (output == null)
            {
                return null;
            }
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }
            if (Current != null)
            {
                throw new InvalidOperationException("A managed-reference diagnostics writer is already active.");
            }
            if (includeExactTypeMatches && (filters == null || filters.Length == 0))
            {
                throw new ArgumentException(
                    "Exact managed-reference diagnostics require at least one type filter.",
                    nameof(filters));
            }
            return new ManagedReferenceDiagnosticsJsonlWriter(
                output,
                input,
                filters,
                includeExactTypeMatches);
        }

        public void WriteObject(AssetItem item, OrderedDictionary payload, byte[] rawData)
        {
            try
            {
                if (item?.Asset == null || payload == null || rawData == null)
                {
                    return;
                }
                if (!TryGetManagedReferenceEntries(payload, out var entries))
                {
                    return;
                }

                foreach (var entry in entries)
                {
                    WriteEntry(item, entry, rawData);
                }
            }
            finally
            {
                lock (sync)
                {
                    pendingDecodeFailures.Clear();
                }
            }
        }

        public void RecordDecodeFailure(
            long rid,
            string className,
            string namespaceName,
            string assemblyName,
            int offset,
            int length,
            OrderedDictionary diagnostic
        )
        {
            if (diagnostic == null)
            {
                return;
            }
            lock (sync)
            {
                EnsureWritable();
                var key = BuildDecodeFailureKey(
                    rid,
                    assemblyName,
                    namespaceName,
                    className,
                    offset,
                    length);
                if (!pendingDecodeFailures.ContainsKey(key))
                {
                    pendingDecodeFailures[key] = diagnostic;
                }
            }
        }

        public void Complete(bool isComplete)
        {
            lock (sync)
            {
                if (completed)
                {
                    return;
                }
                EnsureWritable();
                var contentSha256 = Convert.ToHexString(contentHash.GetHashAndReset()).ToLowerInvariant();
                contentHash.Dispose();
                WriteTerminalRow(new OrderedDictionary
                {
                    { "recordType", "summary" },
                    { "schemaVersion", SchemaVersion },
                    { "sidecarSchema", SidecarSchema },
                    { "complete", isComplete && errorCount == 0 },
                    { "counts", new OrderedDictionary
                        {
                            { "records", recordCount },
                            { "errors", errorCount },
                            { "suppressed", suppressedCount },
                        }
                    },
                    { "contentBytes", contentBytes },
                    { "contentSha256", contentSha256 },
                });
                writer.Flush();
                if (writer.BaseStream is FileStream fileStream)
                {
                    fileStream.Flush(true);
                }
                writer.Dispose();
                completed = true;
                disposed = true;
                Current = null;

                if (!isComplete || errorCount != 0)
                {
                    return;
                }
                if (File.Exists(finalPath))
                {
                    File.Replace(temporaryPath, finalPath, null);
                }
                else
                {
                    File.Move(temporaryPath, finalPath);
                }
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }
                writer.Dispose();
                contentHash.Dispose();
                disposed = true;
                if (ReferenceEquals(Current, this))
                {
                    Current = null;
                }
            }
        }

        private void WriteEntry(AssetItem item, OrderedDictionary entry, byte[] rawData)
        {
            if (!TryGetInt64(entry, "rid", out var rid)
                || entry["type"] is not OrderedDictionary type
                || entry["data"] is not OrderedDictionary data)
            {
                return;
            }

            var className = GetString(type, "class");
            var namespaceName = GetString(type, "ns");
            var assemblyName = GetString(type, "asm");
            var typeIdentity = $"{assemblyName}::{namespaceName}.{className}";
            var matchesTypeFilter = typeFilters.Length == 0
                || typeFilters.Any(filter => filter.IsMatch(typeIdentity));
            if (!matchesTypeFilter)
            {
                return;
            }
            if (!ContainsPartialMarker(data) && !includeExactMatches)
            {
                return;
            }

            lock (sync)
            {
                EnsureWritable();
                if (recordCount >= MaxRecords)
                {
                    suppressedCount++;
                    return;
                }

                var objectIdentity = BuildIdentity(item.Asset);
                var key = JsonConvert.SerializeObject(new object[] { objectIdentity, rid, typeIdentity }, Formatting.None);
                if (!emittedKeys.Add(key))
                {
                    return;
                }

                if (!TryGetInt32(entry, "dataOffset", out var offset))
                {
                    TryGetInt32(data, "offset", out offset);
                }
                if (!TryGetInt32(entry, "dataLength", out var length))
                {
                    TryGetInt32(data, "length", out length);
                }

                var rangeValid = offset >= 0 && length >= 0 && offset <= rawData.Length - length;
                if (!rangeValid)
                {
                    errorCount++;
                }
                var payloadSha256 = rangeValid
                    ? Convert.ToHexString(SHA256.HashData(rawData.AsSpan(offset, length))).ToLowerInvariant()
                    : string.Empty;
                var payloadTruncated = rangeValid && length > MaxPayloadBytes;
                var payloadData = new OrderedDictionary
                {
                    { "offset", offset },
                    { "length", length },
                    { "rangeValid", rangeValid },
                    { "sha256", payloadSha256 },
                    { "encoding", rangeValid && !payloadTruncated ? "base64" : "omitted" },
                    { "truncated", payloadTruncated },
                };
                if (rangeValid && !payloadTruncated)
                {
                    payloadData["base64"] = Convert.ToBase64String(rawData, offset, length);
                }

                var diagnostic = BuildDiagnosticSnapshot(data, offset, length);
                var decodeFailureKey = BuildDecodeFailureKey(
                    rid,
                    assemblyName,
                    namespaceName,
                    className,
                    offset,
                    length);
                if (pendingDecodeFailures.TryGetValue(decodeFailureKey, out var decodeFailure))
                {
                    diagnostic["decodeFailure"] = decodeFailure;
                }

                WriteContentRow(new OrderedDictionary
                {
                    { "recordType", "managedReferenceDiagnostic" },
                    { "schemaVersion", SchemaVersion },
                    { "object", objectIdentity },
                    { "rid", rid },
                    { "type", new OrderedDictionary
                        {
                            { "class", className },
                            { "ns", namespaceName },
                            { "asm", assemblyName },
                        }
                    },
                    { "markers", CollectMarkers(data) },
                    { "serializedRefTypeTree", BuildSerializedRefTypeTree(item.Asset.assetsFile, type) },
                    { "payload", payloadData },
                    { "diagnostic", diagnostic },
                });
                recordCount++;
            }
        }

        private OrderedDictionary BuildIdentity(AnimeStudio.Object asset) => new OrderedDictionary
        {
            { "serializedFile", asset.assetsFile?.fileName ?? "" },
            { "source", NormalizeSourcePath(asset.assetsFile?.originalPath) },
            { "sourceOffset", asset.assetsFile?.offset ?? 0 },
            { "pathId", asset.m_PathID },
        };

        private static OrderedDictionary BuildSerializedRefTypeTree(
            SerializedFile serializedFile,
            OrderedDictionary managedReferenceType
        )
        {
            var className = GetString(managedReferenceType, "class");
            var namespaceName = GetString(managedReferenceType, "ns");
            var assemblyName = NormalizeAssemblyName(GetString(managedReferenceType, "asm"));
            var matches = (serializedFile?.m_RefTypes ?? new List<SerializedType>())
                .Where(candidate => candidate != null
                    && string.Equals(candidate.m_KlassName ?? string.Empty, className, StringComparison.Ordinal)
                    && string.Equals(candidate.m_NameSpace ?? string.Empty, namespaceName, StringComparison.Ordinal)
                    && string.Equals(NormalizeAssemblyName(candidate.m_AsmName), assemblyName, StringComparison.Ordinal))
                .ToList();
            var result = new OrderedDictionary
            {
                { "status", matches.Count == 1 ? "resolved" : matches.Count == 0 ? "missing" : "ambiguous" },
                { "matchCount", matches.Count },
            };
            if (matches.Count != 1)
            {
                return result;
            }

            var match = matches[0];
            var nodes = match.m_Type?.m_Nodes ?? new List<TypeTreeNode>();
            result["class"] = match.m_KlassName ?? string.Empty;
            result["namespace"] = match.m_NameSpace ?? string.Empty;
            result["assembly"] = match.m_AsmName ?? string.Empty;
            result["scriptId"] = match.m_ScriptID == null ? string.Empty : Convert.ToHexString(match.m_ScriptID).ToLowerInvariant();
            result["oldTypeHash"] = match.m_OldTypeHash == null ? string.Empty : Convert.ToHexString(match.m_OldTypeHash).ToLowerInvariant();
            result["nodeCount"] = nodes.Count;
            if (nodes.Count > MaxSerializedRefTypeTreeNodes)
            {
                result["status"] = "node_limit_exceeded";
                return result;
            }
            result["nodes"] = nodes.Select(node => new OrderedDictionary
            {
                { "level", node.m_Level },
                { "type", node.m_Type ?? string.Empty },
                { "name", node.m_Name ?? string.Empty },
                { "byteSize", node.m_ByteSize },
                { "index", node.m_Index },
                { "typeFlags", node.m_TypeFlags },
                { "version", node.m_Version },
                { "metaFlag", node.m_MetaFlag },
                { "refTypeHash", $"0x{node.m_RefTypeHash:x16}" },
            }).ToList();
            return result;
        }

        private static string NormalizeAssemblyName(string value)
        {
            var normalized = value ?? string.Empty;
            return normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? normalized.Substring(0, normalized.Length - 4)
                : normalized;
        }

        private static OrderedDictionary BuildDiagnosticSnapshot(OrderedDictionary data, int offset, int length)
        {
            var diagnostic = new OrderedDictionary
            {
                { "entryStart", offset },
                { "entryEnd", (long)offset + length },
            };
            foreach (var key in new[]
            {
                "layout", "decodeError", "parseFailure", "decodeFailure",
                "remainingPayloadOffset", "remainingPayloadRelativeOffset", "remainingPayloadLength",
                "partialReasons", "observedPayloadStatus", "exactTypeTreeDecodeFailure",
                "exactTypeTreeDecoded",
            })
            {
                if (!data.Contains(key))
                {
                    continue;
                }
                diagnostic[key] = data[key] is string text
                    ? BoundText(text)
                    : data[key];
            }
            return diagnostic;
        }

        private static List<string> CollectMarkers(OrderedDictionary data)
        {
            var markers = new List<string>();
            foreach (var key in new[] { "$partial", "$unparsed", "$heuristic", "$inferred" })
            {
                if (data.Contains(key) && data[key] is bool value && value)
                {
                    markers.Add(key);
                }
            }
            return markers;
        }

        private static bool ContainsPartialMarker(object value, int depth = 0)
        {
            if (value == null || depth > 64)
            {
                return false;
            }
            if (value is IDictionary dictionary)
            {
                foreach (var marker in new[] { "$partial", "$unparsed", "$heuristic" })
                {
                    if (dictionary.Contains(marker) && dictionary[marker] is bool flag && flag)
                    {
                        return true;
                    }
                }
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (ContainsPartialMarker(entry.Value, depth + 1))
                    {
                        return true;
                    }
                }
                return false;
            }
            if (value is IEnumerable enumerable && value is not string)
            {
                foreach (var item in enumerable)
                {
                    if (ContainsPartialMarker(item, depth + 1))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool TryGetManagedReferenceEntries(OrderedDictionary payload, out List<OrderedDictionary> entries)
        {
            entries = new List<OrderedDictionary>();
            if (!payload.Contains("references")
                || payload["references"] is not OrderedDictionary references
                || !references.Contains("RefIds")
                || references["RefIds"] is not IEnumerable values)
            {
                return false;
            }
            entries.AddRange(values.Cast<object>().OfType<OrderedDictionary>());
            return entries.Count > 0;
        }

        private static string GetString(OrderedDictionary dictionary, string key) =>
            dictionary.Contains(key) ? Convert.ToString(dictionary[key], CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;

        private static bool TryGetInt32(OrderedDictionary dictionary, string key, out int value)
        {
            value = 0;
            if (dictionary == null || !dictionary.Contains(key))
            {
                return false;
            }
            try
            {
                value = Convert.ToInt32(dictionary[key], CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetInt64(OrderedDictionary dictionary, string key, out long value)
        {
            value = 0;
            if (dictionary == null || !dictionary.Contains(key))
            {
                return false;
            }
            try
            {
                value = Convert.ToInt64(dictionary[key], CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string BoundText(string value) =>
            string.IsNullOrEmpty(value) || value.Length <= MaxDiagnosticTextLength
                ? value ?? string.Empty
                : value.Substring(0, MaxDiagnosticTextLength);

        private static string BuildDecodeFailureKey(
            long rid,
            string assemblyName,
            string namespaceName,
            string className,
            int offset,
            int length
        ) => JsonConvert.SerializeObject(
            new object[]
            {
                rid,
                assemblyName ?? string.Empty,
                namespaceName ?? string.Empty,
                className ?? string.Empty,
                offset,
                length,
            },
            Formatting.None);

        private static string ResolveInputBasePath(FileInfo input)
        {
            var inputPath = Path.GetFullPath(input.FullName);
            if (Directory.Exists(inputPath))
            {
                return inputPath;
            }
            var directory = new DirectoryInfo(Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory);
            for (var current = directory; current != null; current = current.Parent)
            {
                if (string.Equals(current.Name, "VFS", StringComparison.OrdinalIgnoreCase))
                {
                    return current.FullName;
                }
            }
            return directory.FullName;
        }

        private string NormalizeSourcePath(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return string.Empty;
            }
            try
            {
                return Path.GetRelativePath(inputBasePath, Path.GetFullPath(source)).Replace('\\', '/');
            }
            catch
            {
                return source.Replace('\\', '/');
            }
        }

        private void WriteContentRow(object row)
        {
            var line = JsonConvert.SerializeObject(row, Formatting.None) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            writer.Write(line);
            contentHash.AppendData(bytes);
            contentBytes += bytes.Length;
        }

        private void WriteTerminalRow(object row) =>
            writer.WriteLine(JsonConvert.SerializeObject(row, Formatting.None));

        private void EnsureWritable()
        {
            if (disposed || completed)
            {
                throw new ObjectDisposedException(nameof(ManagedReferenceDiagnosticsJsonlWriter));
            }
        }
    }
}
