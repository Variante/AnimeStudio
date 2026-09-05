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
    /// Writes a compact, opt-in original-data index while exporter payloads and
    /// their SerializedFile dependency context are still resident. The index is
    /// deliberately evidentiary: it preserves exact Unity identities and PPtrs,
    /// but never manufactures joins from names or PathID coincidence.
    /// </summary>
    public sealed class ObjectIndexJsonlWriter : IDisposable
    {
        private const int SchemaVersion = 1;
        private const int MaxScalarCount = 256;
        private const int MaxFieldCount = 4096;
        private const int MaxIdentifierLength = 160;
        private const int MaxFieldValueLength = 4096;
        private const int MaxRecordedErrors = 100;

        private static readonly Regex IdentifierPattern = new Regex(
            @"^[A-Za-z0-9_#./:@+\-]+$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant
        );

        private static readonly string[] IntegerIdentityTerms =
        {
            "quest", "mission", "script", "slot", "state", "stage", "level",
            "scene", "target", "logic", "entity", "world", "event", "task",
        };

        private readonly object sync = new object();
        private readonly string finalPath;
        private readonly string temporaryPath;
        private readonly string inputBasePath;
        private readonly StreamWriter writer;
        private readonly HashSet<string> emittedSchemas = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> emittedMonoScripts = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> emittedObjects = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<OrderedDictionary> errors = new List<OrderedDictionary>();
        private long objectCount;
        private long schemaCount;
        private long monoScriptCount;
        private long scalarCount;
        private long fieldCount;
        private long pptrCount;
        private long truncatedScalarObjectCount;
        private long truncatedFieldObjectCount;
        private long errorCount;
        private bool completed;
        private bool disposed;

        private ObjectIndexJsonlWriter(FileInfo output, FileInfo input)
        {
            finalPath = Path.GetFullPath(output.FullName);
            temporaryPath = finalPath + ".tmp";
            var outputDirectory = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            inputBasePath = ResolveInputBasePath(input);

            writer = new StreamWriter(
                new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false),
                64 * 1024
            );
            Current = this;
        }

        public static ObjectIndexJsonlWriter Current { get; private set; }

        public static ObjectIndexJsonlWriter Open(FileInfo output, FileInfo input)
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
                throw new InvalidOperationException("An object JSONL index writer is already active.");
            }
            return new ObjectIndexJsonlWriter(output, input);
        }

        public void WriteLoadedMonoScripts(IEnumerable<SerializedFile> serializedFiles)
        {
            if (serializedFiles == null)
            {
                return;
            }

            foreach (var script in serializedFiles
                .Where(file => file != null)
                .SelectMany(file => file.Objects)
                .OfType<MonoScript>())
            {
                WriteMonoScript(script);
            }
        }

        public void WriteObject(
            AssetItem item,
            object payload,
            OrderedDictionary metadata,
            string decodeStatus = "decoded",
            string decodeError = null
        )
        {
            if (item?.Asset == null || metadata == null)
            {
                return;
            }

            lock (sync)
            {
                EnsureWritable();

                var identity = BuildIdentity(item.Asset);
                var identityKey = JsonConvert.SerializeObject(identity, Formatting.None);
                if (!emittedObjects.Add(identityKey))
                {
                    return;
                }
                var schemaId = WriteSchemaIfNeeded(metadata);
                var scalars = new List<object>();
                var scalarTruncated = false;
                CollectScalars(payload, "$", scalars, ref scalarTruncated);
                var fields = new List<object>();
                var fieldsTruncated = false;
                if (!string.Equals(NormalizeDecodeStatus(decodeStatus), "metadata_only", StringComparison.Ordinal))
                {
                    CollectScalars(
                        payload,
                        "$",
                        fields,
                        ref fieldsTruncated,
                        includeAllScalars: true
                    );
                }
                var pptrs = CollectNonNullPPtrs(metadata);

                var row = new OrderedDictionary
                {
                    { "recordType", "object" },
                    { "schemaVersion", SchemaVersion },
                    { "object", identity },
                    { "type", item.TypeString ?? item.Asset.type.ToString() },
                    { "classId", (int)item.Type },
                    { "name", item.Text ?? "" },
                    { "container", item.Container ?? "" },
                    { "byteSize", item.Asset.byteSize },
                    { "decodeStatus", NormalizeDecodeStatus(decodeStatus) },
                    { "typeTreeSource", GetMetadataString(metadata, "typeTreeSource") },
                    { "schemaId", schemaId },
                    { "scalars", scalars },
                    { "fields", fields },
                    { "fieldsStatus", string.Equals(NormalizeDecodeStatus(decodeStatus), "metadata_only", StringComparison.Ordinal)
                        ? "unavailable"
                        : string.Equals(NormalizeDecodeStatus(decodeStatus), "partial", StringComparison.Ordinal)
                            ? "partial"
                            : "decoded" },
                    { "pptrs", pptrs },
                    { "opaque", new OrderedDictionary
                        {
                            { "rawLength", GetMetadataValue(metadata, "rawDataLength", null) },
                            { "rawSha256", EmptyToNull(GetMetadataString(metadata, "rawDataSha256")) },
                            { "error", EmptyToNull(decodeError) },
                        }
                    },
                };

                if (TryGetDictionaryInt64(metadata, "scriptFileId", out var scriptFileId)
                    && TryGetDictionaryInt64(metadata, "scriptPathId", out var scriptPathId))
                {
                    row["script"] = new OrderedDictionary
                    {
                        { "fileId", scriptFileId },
                        { "pathId", scriptPathId },
                        { "fullName", EmptyToNull(GetMetadataString(metadata, "scriptFullName")) },
                        { "assembly", EmptyToNull(GetMetadataString(metadata, "scriptAssemblyName")) },
                    };
                }
                CopyMetadataValue(metadata, row, "scriptDerivedMonoScriptResolved");
                CopyMetadataValue(metadata, row, "scriptDerivedTypeDefinitionResolved");
                CopyMetadataValue(metadata, row, "scriptDerivedTypeTreeStatus");
                var sceneContext = BuildComponentSceneContext(item.Asset);
                if (sceneContext != null)
                {
                    row["sceneContext"] = sceneContext;
                }

                if (scalarTruncated)
                {
                    row["scalarsTruncated"] = true;
                    truncatedScalarObjectCount++;
                }
                if (fieldsTruncated)
                {
                    row["fieldsTruncated"] = true;
                    truncatedFieldObjectCount++;
                }
                WriteRow(row);
                objectCount++;
                scalarCount += scalars.Count;
                fieldCount += fields.Count;
                pptrCount += pptrs.Count;
            }
        }

        private OrderedDictionary BuildComponentSceneContext(AnimeStudio.Object asset)
        {
            if (!(asset is Component component)
                || component.m_GameObject.m_PathID == 0
                || !component.m_GameObject.TryGet(out var gameObject))
            {
                return null;
            }

            var context = new OrderedDictionary
            {
                { "gameObject", BuildIdentity(gameObject) },
                { "gameObjectName", gameObject.m_Name ?? "" },
            };
            var transform = gameObject.m_Transform;
            if (transform == null)
            {
                context["worldPositionStatus"] = "transform_unavailable";
                return context;
            }

            context["transform"] = BuildIdentity(transform);
            context["localPosition"] = BuildVector3(transform.m_LocalPosition);

            const int MaxParentDepth = 256;
            var chain = new List<Transform>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = transform;
            var status = "exact_transform_hierarchy";
            while (current != null)
            {
                var identity = BuildIdentity(current);
                var marker = JsonConvert.SerializeObject(identity, Formatting.None);
                if (!seen.Add(marker))
                {
                    status = "transform_parent_cycle";
                    break;
                }
                chain.Add(current);
                if (chain.Count >= MaxParentDepth)
                {
                    status = "transform_parent_depth_limit";
                    break;
                }
                if (current.m_Father.m_PathID == 0)
                {
                    break;
                }
                if (!current.m_Father.TryGet(out var parent))
                {
                    status = "transform_parent_unresolved";
                    break;
                }
                current = parent;
            }

            var world = Matrix4x4.Translate(new Vector3(0, 0, 0));
            for (var index = chain.Count - 1; index >= 0; index--)
            {
                var node = chain[index];
                var local = Matrix4x4.Translate(node.m_LocalPosition)
                    * Matrix4x4.Rotate(node.m_LocalRotation)
                    * Matrix4x4.Scale(node.m_LocalScale);
                world = world * local;
            }
            context["worldPosition"] = BuildVector3(new Vector3(world.M03, world.M13, world.M23));
            context["worldPositionStatus"] = status;
            context["parentDepth"] = Math.Max(0, chain.Count - 1);

            var hierarchyPath = new List<string>();
            for (var index = chain.Count - 1; index >= 0; index--)
            {
                if (chain[index].m_GameObject.TryGet(out var chainObject))
                {
                    hierarchyPath.Add(chainObject.m_Name ?? "");
                }
                else
                {
                    hierarchyPath.Add("");
                }
            }
            context["hierarchyPath"] = hierarchyPath;
            return context;
        }

        private static OrderedDictionary BuildVector3(Vector3 value) => new OrderedDictionary
        {
            { "x", value.X },
            { "y", value.Y },
            { "z", value.Z },
        };

        public void RecordError(string code, string message)
        {
            lock (sync)
            {
                EnsureWritable();
                errorCount++;
                if (errors.Count < MaxRecordedErrors)
                {
                    errors.Add(new OrderedDictionary
                    {
                        { "code", code ?? "export_error" },
                        { "message", message ?? "" },
                    });
                }
            }
        }

        public void Complete(bool isComplete = true)
        {
            lock (sync)
            {
                if (completed)
                {
                    return;
                }
                EnsureWritable();
                WriteRow(new OrderedDictionary
                {
                    { "recordType", "summary" },
                    { "schemaVersion", SchemaVersion },
                    { "complete", isComplete && errorCount == 0 },
                    { "counts", new OrderedDictionary
                        {
                            { "objects", objectCount },
                            { "schemas", schemaCount },
                            { "monoScripts", monoScriptCount },
                            { "scalars", scalarCount },
                            { "fields", fieldCount },
                            { "pptrs", pptrCount },
                            { "objectsWithTruncatedScalars", truncatedScalarObjectCount },
                            { "objectsWithTruncatedFields", truncatedFieldObjectCount },
                            { "errors", errorCount },
                            { "suppressedErrors", Math.Max(0, errorCount - errors.Count) },
                        }
                    },
                    { "errors", errors },
                });
                writer.Flush();
                if (writer.BaseStream is FileStream fileStream)
                {
                    fileStream.Flush(true);
                }
                writer.Dispose();
                if (File.Exists(finalPath))
                {
                    File.Replace(temporaryPath, finalPath, null);
                }
                else
                {
                    File.Move(temporaryPath, finalPath);
                }
                completed = true;
                disposed = true;
                Current = null;
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
                disposed = true;
                if (ReferenceEquals(Current, this))
                {
                    Current = null;
                }
            }
        }

        private void WriteMonoScript(MonoScript script)
        {
            var identity = BuildIdentity(script);
            var identityKey = JsonConvert.SerializeObject(identity, Formatting.None);
            lock (sync)
            {
                EnsureWritable();
                if (!emittedMonoScripts.Add(identityKey))
                {
                    return;
                }

                var scriptNamespace = script.m_Namespace ?? "";
                var scriptClass = script.m_ClassName ?? "";
                WriteRow(new OrderedDictionary
                {
                    { "recordType", "monoScript" },
                    { "schemaVersion", SchemaVersion },
                    { "object", identity },
                    { "className", scriptClass },
                    { "namespace", scriptNamespace },
                    { "assemblyName", script.m_AssemblyName ?? "" },
                });
                monoScriptCount++;
            }
        }

        private string WriteSchemaIfNeeded(OrderedDictionary metadata)
        {
            if (!(metadata["typeTreeFieldPaths"] is IEnumerable fieldsValue))
            {
                return null;
            }

            var fields = fieldsValue.Cast<object>()
                .Select(value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
                .Where(value => value.Length > 0)
                .ToList();
            if (fields.Count == 0)
            {
                return null;
            }

            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", fields));
            var schemaId = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (emittedSchemas.Add(schemaId))
            {
                WriteRow(new OrderedDictionary
                {
                    { "recordType", "schema" },
                    { "schemaVersion", SchemaVersion },
                    { "schemaId", schemaId },
                    { "typeTreeSource", GetMetadataString(metadata, "typeTreeSource") },
                    { "fields", fields },
                });
                schemaCount++;
            }
            return schemaId;
        }

        private List<object> CollectNonNullPPtrs(OrderedDictionary metadata)
        {
            var result = new List<object>();
            if (!(metadata["pptrReferences"] is IEnumerable references))
            {
                return result;
            }

            foreach (var reference in references)
            {
                if (reference is OrderedDictionary ordered
                    && TryGetDictionaryInt64(ordered, "pathId", out var orderedPathId)
                    && orderedPathId != 0)
                {
                    result.Add(CopyPPtrReference(ordered));
                }
                else if (reference is IDictionary dictionary
                    && TryGetDictionaryInt64(dictionary, "pathId", out var dictionaryPathId)
                    && dictionaryPathId != 0)
                {
                    result.Add(CopyPPtrReference(dictionary));
                }
            }
            return result;
        }

        private OrderedDictionary CopyPPtrReference(IDictionary source)
        {
            TryGetDictionaryInt64(source, "fileId", out var fileId);
            TryGetDictionaryInt64(source, "pathId", out var pathId);
            var status = GetDictionaryString(source, "resolutionStatus");
            if (string.IsNullOrEmpty(status))
            {
                status = "legacy_unresolved";
            }

            var copy = new OrderedDictionary
            {
                { "path", EmptyToNull(GetDictionaryString(source, "path")) },
                { "fileId", fileId },
                { "pathId", pathId },
                { "status", status },
            };

            var expectedSourceFile = GetDictionaryString(source, "expectedTargetSourceFile");
            if (!string.IsNullOrEmpty(expectedSourceFile))
            {
                copy["expected"] = new OrderedDictionary
                {
                    { "serializedFile", expectedSourceFile },
                    { "externalPath", EmptyToNull(GetDictionaryString(source, "expectedTargetExternalPath")) },
                    { "externalGuid", EmptyToNull(GetDictionaryString(source, "expectedTargetExternalGuid")) },
                    { "externalType", GetNullableDictionaryInt64(source, "expectedTargetExternalType") },
                };
            }

            var targetSourceFile = GetDictionaryString(source, "targetSourceFile");
            if (!string.IsNullOrEmpty(targetSourceFile)
                && TryGetDictionaryInt64(source, "targetPathId", out var targetPathId))
            {
                copy["target"] = new OrderedDictionary
                {
                    { "serializedFile", targetSourceFile },
                    { "source", NormalizeSourcePath(GetDictionaryString(source, "targetSourceOriginalPath")) },
                    { "sourceOffset", GetDictionaryValue(source, "targetSourceOffset", 0L) },
                    { "pathId", targetPathId },
                    { "type", EmptyToNull(GetDictionaryString(source, "targetType")) },
                    { "name", GetDictionaryString(source, "targetName") },
                };
            }

            if (fileId > 0)
            {
                copy["requiresGlobalUniquenessCheck"] = true;
                copy["resolutionBasis"] = string.Equals(status, "resolved", StringComparison.Ordinal)
                    ? "runtime_loaded_dependency"
                    : "expected_external_identity";
            }
            return copy;
        }

        private static void CollectScalars(
            object value,
            string path,
            List<object> result,
            ref bool truncated,
            bool includeAllScalars = false
        )
        {
            if (value == null || truncated)
            {
                return;
            }
            var maxCount = includeAllScalars ? MaxFieldCount : MaxScalarCount;
            if (result.Count >= maxCount)
            {
                truncated = true;
                return;
            }

            if (value is string stringValue)
            {
                if (includeAllScalars)
                {
                    if (stringValue.Length <= MaxFieldValueLength)
                    {
                        result.Add(new object[] { path, "s", stringValue });
                    }
                    else
                    {
                        truncated = true;
                    }
                }
                else if (stringValue.Length > 0
                    && stringValue.Length <= MaxIdentifierLength
                    && IdentifierPattern.IsMatch(stringValue))
                {
                    result.Add(new object[] { path, "s", stringValue });
                }
                return;
            }
            if (value is bool booleanValue)
            {
                if (includeAllScalars || IsIdentityIntegerPath(path))
                {
                    result.Add(new object[] { path, "b", booleanValue });
                }
                return;
            }
            if (value is byte[])
            {
                return;
            }
            if (value is float floatValue)
            {
                if (includeAllScalars && !float.IsNaN(floatValue) && !float.IsInfinity(floatValue))
                {
                    result.Add(new object[] { path, "f", floatValue });
                }
                return;
            }
            if (value is double doubleValue)
            {
                if (includeAllScalars && !double.IsNaN(doubleValue) && !double.IsInfinity(doubleValue))
                {
                    result.Add(new object[] { path, "f", doubleValue });
                }
                return;
            }
            if (value is decimal decimalValue)
            {
                if (includeAllScalars)
                {
                    result.Add(new object[] { path, "d", decimalValue });
                }
                return;
            }
            if (TryConvertInteger(value, out var integerValue))
            {
                if (includeAllScalars || (!IsZeroInteger(integerValue) && IsIdentityIntegerPath(path)))
                {
                    result.Add(new object[] { path, includeAllScalars && IsUnsignedInteger(value) ? "u" : "i", integerValue });
                }
                return;
            }

            if (value is IDictionary dictionary)
            {
                if (IsPPtrDictionary(dictionary))
                {
                    return;
                }
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "";
                    if (key == "$animestudio")
                    {
                        continue;
                    }
                    CollectScalars(entry.Value, $"{path}.{key}", result, ref truncated, includeAllScalars);
                    if (truncated)
                    {
                        return;
                    }
                }
                return;
            }

            if (value is IEnumerable enumerable)
            {
                var index = 0;
                foreach (var item in enumerable)
                {
                    CollectScalars(item, $"{path}[{index++}]", result, ref truncated, includeAllScalars);
                    if (truncated)
                    {
                        return;
                    }
                }
            }
        }

        private OrderedDictionary BuildIdentity(Object asset)
        {
            return new OrderedDictionary
            {
                { "serializedFile", asset.assetsFile?.fileName ?? "" },
                { "source", NormalizeSourcePath(asset.assetsFile?.originalPath) },
                { "sourceOffset", asset.assetsFile?.offset ?? 0 },
                { "pathId", asset.m_PathID },
            };
        }

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
                return "";
            }
            try
            {
                var fullSource = Path.GetFullPath(source);
                return Path.GetRelativePath(inputBasePath, fullSource).Replace('\\', '/');
            }
            catch
            {
                return source.Replace('\\', '/');
            }
        }

        private void WriteRow(object row)
        {
            writer.WriteLine(JsonConvert.SerializeObject(row, Formatting.None));
        }

        private void EnsureWritable()
        {
            if (disposed || completed)
            {
                throw new ObjectDisposedException(nameof(ObjectIndexJsonlWriter));
            }
        }

        private static bool IsIdentityIntegerPath(string path)
        {
            var lower = path.ToLowerInvariant();
            return IntegerIdentityTerms.Any(lower.Contains);
        }

        private static bool IsPPtrDictionary(IDictionary dictionary) =>
            dictionary.Contains("m_FileID") && dictionary.Contains("m_PathID");

        private static bool TryConvertInteger(object value, out object number)
        {
            number = null;
            switch (value)
            {
                case sbyte signedByte:
                    number = signedByte;
                    return true;
                case byte unsignedByte:
                    number = unsignedByte;
                    return true;
                case short signedShort:
                    number = signedShort;
                    return true;
                case ushort unsignedShort:
                    number = unsignedShort;
                    return true;
                case int signedInt:
                    number = signedInt;
                    return true;
                case uint unsignedInt:
                    number = unsignedInt;
                    return true;
                case long signedLong:
                    number = signedLong;
                    return true;
                case ulong unsignedLong:
                    number = unsignedLong;
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsZeroInteger(object value) => value switch
        {
            sbyte number => number == 0,
            byte number => number == 0,
            short number => number == 0,
            ushort number => number == 0,
            int number => number == 0,
            uint number => number == 0,
            long number => number == 0,
            ulong number => number == 0,
            _ => false,
        };

        private static bool IsUnsignedInteger(object value) => value is byte || value is ushort || value is uint || value is ulong;

        private static bool TryGetDictionaryInt64(IDictionary dictionary, string key, out long value)
        {
            value = 0;
            if (!dictionary.Contains(key))
            {
                return false;
            }
            var raw = dictionary[key];
            switch (raw)
            {
                case long signedLong:
                    value = signedLong;
                    return true;
                case int signedInt:
                    value = signedInt;
                    return true;
                case uint unsignedInt:
                    value = unsignedInt;
                    return true;
                case ulong unsignedLong when unsignedLong <= long.MaxValue:
                    value = (long)unsignedLong;
                    return true;
                default:
                    return long.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out value);
            }
        }

        private static string GetDictionaryString(IDictionary dictionary, string key) =>
            dictionary.Contains(key)
                ? Convert.ToString(dictionary[key], CultureInfo.InvariantCulture) ?? ""
                : "";

        private static object GetDictionaryValue(IDictionary dictionary, string key, object fallback) =>
            dictionary.Contains(key) ? dictionary[key] : fallback;

        private static object GetNullableDictionaryInt64(IDictionary dictionary, string key) =>
            TryGetDictionaryInt64(dictionary, key, out var value) ? value : null;

        private static string NormalizeDecodeStatus(string value) =>
            string.Equals(value, "metadataOnly", StringComparison.OrdinalIgnoreCase)
                ? "metadata_only"
                : string.IsNullOrEmpty(value) ? "decoded" : value;

        private static string EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;

        private static string GetMetadataString(OrderedDictionary metadata, string key) =>
            metadata.Contains(key)
                ? Convert.ToString(metadata[key], CultureInfo.InvariantCulture) ?? ""
                : "";

        private static object GetMetadataValue(OrderedDictionary metadata, string key, object fallback) =>
            metadata.Contains(key) ? metadata[key] : fallback;

        private static void CopyMetadataValue(OrderedDictionary source, OrderedDictionary target, string key)
        {
            if (source.Contains(key))
            {
                target[key] = source[key];
            }
        }
    }
}
