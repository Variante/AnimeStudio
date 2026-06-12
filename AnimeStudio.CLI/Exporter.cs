using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Buffers.Binary;
using System;
using System.Collections;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeStudio.CLI
{
    internal static class Exporter
    {
        private const int MaxSafeFileNameLength = 120;
        private const int MonoBehaviourBaseTypeTreeNodeCount = 12;
        private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9",
        };

        public static bool ExportTexture2D(AssetItem item, string exportPath)
        {
            var m_Texture2D = (Texture2D)item.Asset;
            if (Properties.Settings.Default.convertTexture)
            {
                var type = Properties.Settings.Default.convertType;
                if (!TryExportFile(exportPath, item, "." + type.ToString().ToLower(), out var exportFullPath))
                    return false;
                var image = m_Texture2D.ConvertToImage(true);
                if (image == null)
                    return false;
                using (image)
                {
                    using (var file = File.Create(exportFullPath))
                    {
                        image.WriteToStream(file, type);
                    }
                    return true;
                }
            }
            else
            {
                if (!TryExportFile(exportPath, item, ".tex", out var exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, m_Texture2D.image_data.GetData());
                return true;
            }
        }

        public static bool ExportAudioClip(AssetItem item, string exportPath)
        {
            var m_AudioClip = (AudioClip)item.Asset;
            var m_AudioData = m_AudioClip.m_AudioData.GetData();
            if (m_AudioData == null || m_AudioData.Length == 0)
                return false;
            var converter = new AudioClipConverter(m_AudioClip);
            if (Properties.Settings.Default.convertAudio && converter.IsSupport)
            {
                if (!TryExportFile(exportPath, item, ".wav", out var exportFullPath))
                    return false;
                var buffer = converter.ConvertToWav();
                if (buffer == null)
                    return false;
                File.WriteAllBytes(exportFullPath, buffer);
            }
            else
            {
                if (!TryExportFile(exportPath, item, converter.GetExtensionName(), out var exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, m_AudioData);
            }
            return true;
        }

        public static bool ExportShader(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".shader", out var exportFullPath))
                return false;
            var m_Shader = (Shader)item.Asset;
            var str = m_Shader.Convert();
            File.WriteAllText(exportFullPath, str);
            return true;
        }

        public static bool ExportTextAsset(AssetItem item, string exportPath)
        {
            var m_TextAsset = (TextAsset)(item.Asset);
            var extension = ".txt";
            if (Properties.Settings.Default.restoreExtensionName)
            {
                if (!string.IsNullOrEmpty(item.Container))
                {
                    extension = Path.GetExtension(item.Container);
                }
            }
            if (!TryExportFile(exportPath, item, extension, out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, m_TextAsset.m_Script);
            return true;
        }

        public static bool ExportMonoBehaviour(AssetItem item, string exportPath)
        {
            var option = new Options();
            var m_MonoBehaviour = (MonoBehaviour)item.Asset;

            string folderPattern = $@"(?:Assets|UI|IconRole|Data|Scenes|OriginalResRepos|Comic|Weapon)(?:/[^\s"",]+)*";
            string filePattern = $@"(?:Assets|UI|IconRole|Data|Scenes|OriginalResRepos|Comic|Weapon)/[^\s"",]+?\.(?:.*)";
            string voPattern = @"(?:VO|Breath|Tips)_[^""\s;]+";
            string eventPattern = @"(?:Ev|Play|Stop|StateGroup|State|VO|SFX)_[a-zA-Z0-9/_-\{\}]{2,}";

            var folderRegex = new Regex(folderPattern, RegexOptions.IgnoreCase);
            var fileRegex = new Regex(filePattern, RegexOptions.IgnoreCase);
            var voRegex = new Regex(voPattern, RegexOptions.IgnoreCase);
            var eventRegex = new Regex(eventPattern, RegexOptions.IgnoreCase);

            if (Properties.Settings.Default.scrapeMonos)
            {
                var s = m_MonoBehaviour.GetRawData();
                var cleanedBytes = new List<byte>(s.Length);
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] == 0x00)
                    {
                        bool precededByNull = (i > 0) && (s[i - 1] == 0x00);
                        bool followedByNull = (i < s.Length - 1) && (s[i + 1] == 0x00);

                        if (precededByNull || followedByNull)
                        {
                            cleanedBytes.Add(s[i]);
                        }
                    }
                    else
                    {
                        cleanedBytes.Add(s[i]);
                    }
                }
                var s_cleaned = cleanedBytes.ToArray();

                var idx = Search(s_cleaned, 0);

                while (idx != -1)
                {
                    try
                    {
                        int len = BinaryPrimitives.ReadInt32LittleEndian(s_cleaned.AsSpan(idx - 4));
                        string str = Encoding.UTF8.GetString(s_cleaned.AsSpan(idx, len));

                        foreach (Match match in folderRegex.Matches(str))
                        {
                            Studio.PathStrings.Add(match.Value.Trim());
                        }

                        foreach (Match match in fileRegex.Matches(str))
                        {
                            string subMatch = match.Value.Trim();

                            if (subMatch.StartsWith("UI"))
                                subMatch = $"Assets/NapResources/{subMatch}";
                            else if (subMatch.StartsWith("IconRole"))
                                subMatch = $"Assets/NapResources/UI/Sprite/A1DynamicLoad/{subMatch}";
                            else if (subMatch.StartsWith("Data"))
                                subMatch = $"Assets/NapResources/{subMatch}";

                            Studio.PathStrings.Add(subMatch);
                        }

                        foreach (Match match in voRegex.Matches(str))
                        {
                            Studio.VOStrings.Add(match.Value.Trim());
                        }
                        foreach (Match match in eventRegex.Matches(str))
                        {
                            Studio.EventStrings.Add(match.Value.Trim());
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error processing MonoBehaviour segment: {ex.Message}");
                    }

                    idx = Search(s_cleaned, idx + 4);
                }
            }
            else
            {
                if (!TryExportFile(exportPath, item, ".json", out var exportFullPath))
                    return false;
                OrderedDictionary type = null;
                TypeTree exportTypeTree = m_MonoBehaviour.serializedType?.m_Type;
                string typeTreeSource = exportTypeTree != null ? "serializedType" : "none";
                Exception builtInTypeTreeException = null;
                Exception decodeException = null;
                try
                {
                    type = m_MonoBehaviour.ToType();
                }
                catch (Exception ex)
                {
                    builtInTypeTreeException = ex;
                    decodeException = ex;
                }

                if (type == null && Studio.assemblyLoader.Loaded)
                {
                    try
                    {
                        var scriptTypeTree = Studio.MonoBehaviourToTypeTree(m_MonoBehaviour);
                        if (scriptTypeTree?.m_Nodes?.Count > MonoBehaviourBaseTypeTreeNodeCount)
                        {
                            if (builtInTypeTreeException != null)
                            {
                                Logger.Warning(
                                    $"Retrying MonoBehaviour {item.Text} with a script-derived type tree after " +
                                    $"{builtInTypeTreeException.GetType().Name}: {builtInTypeTreeException.Message}"
                                );
                            }
                            type = m_MonoBehaviour.ToType(scriptTypeTree);
                            if (type != null)
                            {
                                exportTypeTree = scriptTypeTree;
                                typeTreeSource = "scriptDerived";
                                decodeException = null;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        decodeException = ex;
                        Logger.Warning(
                            $"Script-derived MonoBehaviour decode failed for {item.Text}: " +
                            $"{ex.GetType().Name}: {ex.Message}"
                        );
                    }
                }

                var rawData = m_MonoBehaviour.GetRawData();
                var rawSidecar = ExportJsonRawSidecarIfRequested(exportFullPath, rawData);

                if (type == null)
                {
                    if (decodeException != null)
                    {
                        Logger.Warning(
                            $"Exporting MonoBehaviour {item.Text} as metadata-only JSON after " +
                            $"{decodeException.GetType().Name}: {decodeException.Message}"
                        );
                        var fallback = new OrderedDictionary
                        {
                            { "$animestudio", BuildMonoBehaviourExportMetadata(
                                item,
                                m_MonoBehaviour,
                                rawData,
                                exportTypeTree,
                                typeTreeSource,
                                rawSidecar,
                                decodeException,
                                null
                            ) },
                            { "type", item.TypeString },
                            { "name", item.Text ?? "" },
                            { "pathId", item.m_PathID },
                            { "decodeError", $"{decodeException.GetType().Name}: {decodeException.Message}" },
                        };
                        var fallbackText = JsonConvert.SerializeObject(fallback, Formatting.Indented);
                        File.WriteAllText(exportFullPath, fallbackText);
                        return true;
                    }
                    return false;
                }
                // Embed export metadata so consumers can rebuild PathID links and
                // tie script-derived MonoBehaviours back to their runtime class.
                // Stored under "$animestudio" to avoid colliding with real fields.
                var meta = BuildMonoBehaviourExportMetadata(
                    item,
                    m_MonoBehaviour,
                    rawData,
                    exportTypeTree,
                    typeTreeSource,
                    rawSidecar,
                    builtInTypeTreeException,
                    type
                );
                type.Insert(0, "$animestudio", meta);
                var str = JsonConvert.SerializeObject(type, Formatting.Indented);
                File.WriteAllText(exportFullPath, str);
            }

             return true;
        }

        private static OrderedDictionary BuildMonoBehaviourExportMetadata(
            AssetItem item,
            MonoBehaviour m_MonoBehaviour,
            byte[] rawData,
            TypeTree exportTypeTree,
            string typeTreeSource,
            string rawSidecar,
            Exception builtInTypeTreeException,
            OrderedDictionary payload
        )
        {
            var meta = BuildObjectExportMetadata(item, rawData, exportTypeTree, typeTreeSource, rawSidecar, payload);
            meta["scriptFileId"] = m_MonoBehaviour.m_Script.m_FileID;
            meta["scriptPathId"] = m_MonoBehaviour.m_Script.m_PathID;

            if (m_MonoBehaviour.m_Script.TryGet(out var m_Script))
            {
                var scriptNamespace = m_Script.m_Namespace ?? "";
                var scriptClass = m_Script.m_ClassName ?? "";
                meta["scriptClassName"] = scriptClass;
                meta["scriptNamespace"] = scriptNamespace;
                meta["scriptFullName"] = string.IsNullOrEmpty(scriptNamespace)
                    ? scriptClass
                    : $"{scriptNamespace}.{scriptClass}";
                meta["scriptAssemblyName"] = m_Script.m_AssemblyName ?? "";
            }

            if (builtInTypeTreeException != null)
            {
                meta["serializedTypeTreeError"] = $"{builtInTypeTreeException.GetType().Name}: {builtInTypeTreeException.Message}";
            }

            return meta;
        }

        private static OrderedDictionary BuildObjectExportMetadata(
            AssetItem item,
            byte[] rawData,
            TypeTree exportTypeTree,
            string typeTreeSource,
            string rawSidecar,
            object payload
        )
        {
            var meta = new OrderedDictionary
            {
                { "pathId", item.m_PathID },
                { "type", item.TypeString },
                { "classId", (int)item.Type },
                { "name", item.Text ?? "" },
                { "sourceFile", item.SourceFile?.fileName ?? "" },
                { "sourceOriginalPath", item.SourceFile?.originalPath ?? "" },
                { "container", item.Container ?? "" },
                { "byteSize", item.Asset.byteSize },
                { "rawDataLength", rawData?.Length ?? 0 },
                { "rawDataSha256", rawData != null ? Convert.ToHexString(SHA256.HashData(rawData)).ToLowerInvariant() : "" },
                { "typeTreeSource", typeTreeSource ?? "none" },
                { "typeTreeNodeCount", exportTypeTree?.m_Nodes?.Count ?? 0 },
            };

            var fieldPaths = BuildTypeTreeFieldPaths(exportTypeTree);
            if (fieldPaths.Count > 0)
            {
                meta["typeTreeFieldPaths"] = fieldPaths;
            }

            var refs = CollectPPtrReferences(payload, item.Asset);
            if (refs.Count > 0)
            {
                meta["pptrReferences"] = refs;
            }

            if (!string.IsNullOrEmpty(rawSidecar))
            {
                meta["rawDataSidecar"] = rawSidecar;
            }

            return meta;
        }

        private static List<string> BuildTypeTreeFieldPaths(TypeTree typeTree)
        {
            var fields = new List<string>();
            var nodes = typeTree?.m_Nodes;
            if (nodes == null || nodes.Count == 0)
            {
                return fields;
            }

            var stack = new List<string>();
            foreach (var node in nodes)
            {
                var level = Math.Max(0, node.m_Level);
                while (stack.Count > level)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
                while (stack.Count < level)
                {
                    stack.Add("");
                }

                if (stack.Count == level)
                {
                    stack.Add(node.m_Name ?? "");
                }
                else
                {
                    stack[level] = node.m_Name ?? "";
                }

                if (level == 0)
                {
                    continue;
                }

                var pathParts = stack
                    .Take(level + 1)
                    .Skip(1)
                    .Where(part => !string.IsNullOrEmpty(part));
                fields.Add($"{string.Join(".", pathParts)}:{node.m_Type}");
            }

            return fields;
        }

        private static List<OrderedDictionary> CollectPPtrReferences(object payload, Object owner)
        {
            var refs = new List<OrderedDictionary>();
            CollectPPtrReferences(payload, owner, "$", refs);
            return refs;
        }

        private static void CollectPPtrReferences(object value, Object owner, string path, List<OrderedDictionary> refs)
        {
            if (value == null || value is string || value is byte[])
            {
                return;
            }

            if (value is OrderedDictionary ordered)
            {
                if (TryGetDictionaryNumber(ordered, "m_FileID", out var fileId)
                    && TryGetDictionaryNumber(ordered, "m_PathID", out var pathId))
                {
                    var refInfo = new OrderedDictionary
                    {
                        { "path", path },
                        { "fileId", fileId },
                        { "pathId", pathId },
                    };
                    AddResolvedPPtrTarget(refInfo, owner, fileId, pathId);
                    refs.Add(refInfo);
                }

                foreach (DictionaryEntry entry in ordered)
                {
                    CollectPPtrReferences(entry.Value, owner, $"{path}.{entry.Key}", refs);
                }
                return;
            }

            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    CollectPPtrReferences(entry.Value, owner, $"{path}.{entry.Key}", refs);
                }
                return;
            }

            if (value is IEnumerable enumerable)
            {
                var index = 0;
                foreach (var item in enumerable)
                {
                    CollectPPtrReferences(item, owner, $"{path}[{index++}]", refs);
                }
            }
        }

        private static void AddResolvedPPtrTarget(OrderedDictionary refInfo, Object owner, long fileId, long pathId)
        {
            if (owner?.assetsFile == null || pathId == 0 || fileId < int.MinValue || fileId > int.MaxValue)
            {
                return;
            }

            var pptr = new PPtr<Object>((int)fileId, pathId, owner.assetsFile);
            if (!pptr.TryGet(out var target))
            {
                return;
            }

            refInfo["targetType"] = target.type.ToString();
            refInfo["targetPathId"] = target.m_PathID;
            refInfo["targetName"] = target.Name ?? "";
            refInfo["targetSourceFile"] = target.assetsFile?.fileName ?? "";
            refInfo["targetSourceOriginalPath"] = target.assetsFile?.originalPath ?? "";
        }

        private static bool TryGetDictionaryNumber(OrderedDictionary dictionary, string key, out long value)
        {
            value = 0;
            if (!dictionary.Contains(key))
            {
                return false;
            }

            var raw = dictionary[key];
            switch (raw)
            {
                case long longValue:
                    value = longValue;
                    return true;
                case int intValue:
                    value = intValue;
                    return true;
                case uint uintValue:
                    value = uintValue;
                    return true;
                case ulong ulongValue when ulongValue <= long.MaxValue:
                    value = (long)ulongValue;
                    return true;
                case string strValue when long.TryParse(strValue, out var parsed):
                    value = parsed;
                    return true;
                default:
                    return false;
            }
        }

        private static string ExportJsonRawSidecarIfRequested(string exportFullPath, byte[] rawData)
        {
            if (rawData == null || rawData.Length == 0 || !ShouldExportJsonRawSidecars())
            {
                return null;
            }

            var sidecarPath = Path.ChangeExtension(exportFullPath, ".raw.bin");
            File.WriteAllBytes(sidecarPath, rawData);
            return Path.GetFileName(sidecarPath);
        }

        private static bool ShouldExportJsonRawSidecars()
        {
            return Properties.Settings.Default.exportJsonRawSidecars || IsEnabledEnvironmentFlag("ANIMESTUDIO_EXPORT_JSON_RAW");
        }

        private static bool IsEnabledEnvironmentFlag(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static int Search(byte[] bytes, int startIndex)
        {
            string[] keys = { "Assets", "UI", "IconRole", "Data", "Scenes", "State_", "VO_", "Play_", "Stop_", "SFX_" };
            foreach (var key in keys)
            {
                int idx = bytes.Search(key, startIndex);
                if (idx != -1) return idx;
            }
            return -1;
        }

        public static bool ExportMiHoYoBinData(AssetItem item, string exportPath)
        {
            string exportFullPath;
            if (item.Asset is MiHoYoBinData m_MiHoYoBinData)
            {
                switch (m_MiHoYoBinData.Type)
                {
                    case MiHoYoBinDataType.JSON:

                        if (!TryExportFile(exportPath, item, ".json", out exportFullPath))
                            return false;
                        var json = m_MiHoYoBinData.Dump() as string;
                        if (json.Length != 0)
                        {
                            File.WriteAllText(exportFullPath, json);
                            return true;
                        }
                        break;
                    case MiHoYoBinDataType.Bytes:
                        var extension = ".bin";
                        if (Properties.Settings.Default.restoreExtensionName)
                        {
                            if (!string.IsNullOrEmpty(item.Container))
                            {
                                extension = Path.GetExtension(item.Container);
                            }
                        }
                        if (!TryExportFile(exportPath, item, extension, out exportFullPath))
                            return false;
                        var bytes = m_MiHoYoBinData.Dump() as byte[];
                        if (!bytes.IsNullOrEmpty())
                        {
                            File.WriteAllBytes(exportFullPath, bytes);
                            return true;
                        }
                        break;
                }
            }
            return false;
        }

        public static bool ExportFont(AssetItem item, string exportPath)
        {
            var m_Font = (Font)item.Asset;
            if (m_Font.m_FontData != null)
            {
                var extension = ".ttf";
                if (m_Font.m_FontData[0] == 79 && m_Font.m_FontData[1] == 84 && m_Font.m_FontData[2] == 84 && m_Font.m_FontData[3] == 79)
                {
                    extension = ".otf";
                }
                if (!TryExportFile(exportPath, item, extension, out var exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, m_Font.m_FontData);
                return true;
            }
            return false;
        }

        public static bool ExportMesh(AssetItem item, string exportPath)
        {
            var m_Mesh = (Mesh)item.Asset;
            if (m_Mesh.m_VertexCount <= 0)
                return false;
            if (!TryExportFile(exportPath, item, ".obj", out var exportFullPath))
                return false;
            var sb = new StringBuilder();
            sb.AppendLine("g " + m_Mesh.m_Name);
            #region Vertices
            if (m_Mesh.m_Vertices == null || m_Mesh.m_Vertices.Length == 0)
            {
                return false;
            }
            int c = 3;
            if (m_Mesh.m_Vertices.Length == m_Mesh.m_VertexCount * 4)
            {
                c = 4;
            }
            for (int v = 0; v < m_Mesh.m_VertexCount; v++)
            {
                sb.AppendFormat("v {0} {1} {2}\r\n", -m_Mesh.m_Vertices[v * c], m_Mesh.m_Vertices[v * c + 1], m_Mesh.m_Vertices[v * c + 2]);
            }
            #endregion

            #region UV
            if (m_Mesh.m_UV0?.Length > 0)
            {
                c = 4;
                if (m_Mesh.m_UV0.Length == m_Mesh.m_VertexCount * 2)
                {
                    c = 2;
                }
                else if (m_Mesh.m_UV0.Length == m_Mesh.m_VertexCount * 3)
                {
                    c = 3;
                }
                for (int v = 0; v < m_Mesh.m_VertexCount; v++)
                {
                    sb.AppendFormat("vt {0} {1}\r\n", m_Mesh.m_UV0[v * c], m_Mesh.m_UV0[v * c + 1]);
                }
            }
            #endregion

            #region Normals
            if (m_Mesh.m_Normals?.Length > 0)
            {
                if (m_Mesh.m_Normals.Length == m_Mesh.m_VertexCount * 3)
                {
                    c = 3;
                }
                else if (m_Mesh.m_Normals.Length == m_Mesh.m_VertexCount * 4)
                {
                    c = 4;
                }
                for (int v = 0; v < m_Mesh.m_VertexCount; v++)
                {
                    sb.AppendFormat("vn {0} {1} {2}\r\n", -m_Mesh.m_Normals[v * c], m_Mesh.m_Normals[v * c + 1], m_Mesh.m_Normals[v * c + 2]);
                }
            }
            #endregion

            #region Face
            int sum = 0;
            for (var i = 0; i < m_Mesh.m_SubMeshes.Count; i++)
            {
                sb.AppendLine($"g {m_Mesh.m_Name}_{i}");
                int indexCount = (int)m_Mesh.m_SubMeshes[i].indexCount;
                var end = sum + indexCount / 3;
                for (int f = sum; f < end; f++)
                {
                    sb.AppendFormat("f {0}/{0}/{0} {1}/{1}/{1} {2}/{2}/{2}\r\n", m_Mesh.m_Indices[f * 3 + 2] + 1, m_Mesh.m_Indices[f * 3 + 1] + 1, m_Mesh.m_Indices[f * 3] + 1);
                }
                sum = end;
            }
            #endregion

            sb.Replace("NaN", "0");
            File.WriteAllText(exportFullPath, sb.ToString());
            return true;
        }

        public static bool ExportVideoClip(AssetItem item, string exportPath)
        {
            var m_VideoClip = (VideoClip)item.Asset;
            if (m_VideoClip.m_ExternalResources.m_Size > 0)
            {
                if (!TryExportFile(exportPath, item, Path.GetExtension(m_VideoClip.m_OriginalPath), out var exportFullPath))
                    return false;
                m_VideoClip.m_VideoData.WriteData(exportFullPath);
                return true;
            }
            return false;
        }

        public static bool ExportMovieTexture(AssetItem item, string exportPath)
        {
            var m_MovieTexture = (MovieTexture)item.Asset;
            if (!TryExportFile(exportPath, item, ".ogv", out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, m_MovieTexture.m_MovieData);
            return true;
        }

        public static bool ExportSprite(AssetItem item, string exportPath)
        {
            var type = Properties.Settings.Default.convertType;
            if (!TryExportFile(exportPath, item, "." + type.ToString().ToLower(), out var exportFullPath))
                return false;
            var image = ((Sprite)item.Asset).GetImage();
            if (image != null)
            {
                using (image)
                {
                    using (var file = File.Create(exportFullPath))
                    {
                        image.WriteToStream(file, type);
                    }
                    return true;
                }
            }
            return false;
        }

        public static bool ExportRawFile(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".dat", out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, item.Asset.GetRawData());
            return true;
        }

        private static bool TryExportFile(string dir, AssetItem item, string extension, out string fullPath)
        {
            Directory.CreateDirectory(dir);
            var fileName = FixFileName(item.Text);
            var pathIdFileName = $"{fileName}_p{item.m_PathID:X16}";
            fullPath = Path.Combine(dir, $"{pathIdFileName}{extension}");
            if (!Properties.Settings.Default.allowDuplicates)
            {
                if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, true);
                }
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
                return true;
            }
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                return true;
            }
            if (Properties.Settings.Default.allowDuplicates)
            {
                for (int i = 0; ; i++)
                {
                    fullPath = Path.Combine(dir, $"{pathIdFileName} ({i}){extension}");
                    if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool TryExportFolder(string dir, AssetItem item, out string fullPath)
        {
            var fileName = FixFileName(item.Text);
            fullPath = Path.Combine(dir, fileName);
            if (!Properties.Settings.Default.allowDuplicates)
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
                if (Directory.Exists(fullPath))
                {
                    // Recreate the fixed export folder so stale files from prior runs do not linger.
                    Directory.Delete(fullPath, true);
                }
                Directory.CreateDirectory(fullPath);
                return true;
            }
            if (Properties.Settings.Default.allowDuplicates)
            {
                if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                    return true;
                }
                for (int i = 0; ; i++)
                {
                    fullPath = Path.Combine(dir, $"{fileName} ({i})");
                    if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
                    {
                        Directory.CreateDirectory(fullPath);
                        return true;
                    }
                }
            }
            return false;
        }

        public static bool ExportAnimationClip(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".anim", out var exportFullPath))
                return false;
            var m_AnimationClip = (AnimationClip)item.Asset;
            var str = m_AnimationClip.Convert();
            if (string.IsNullOrEmpty(str)) 
                return false;
            File.WriteAllText(exportFullPath, str);
            return true;
        }

        public static bool ExportAnimator(AssetItem item, string exportPath, List<AssetItem> animationList = null)
        {
            if (!TryExportFolder(exportPath, item, out var exportFullPath))
                return false;

            var m_Animator = (Animator)item.Asset;
            var options = new ModelConverter.Options()
            {
                imageFormat = Properties.Settings.Default.convertType,
                game = Studio.Game,
                collectAnimations = Properties.Settings.Default.collectAnimations,
                exportMaterials = Properties.Settings.Default.exportMaterials,
                materials = new HashSet<Material>(),
                uvs = JsonConvert.DeserializeObject<Dictionary<string, (bool, int)>>(Properties.Settings.Default.uvs),
                texs = JsonConvert.DeserializeObject<Dictionary<string, int>>(Properties.Settings.Default.texs),
            };
            var convert = animationList != null
                ? new ModelConverter(m_Animator, options, animationList.Select(x => (AnimationClip)x.Asset).ToArray())
                : new ModelConverter(m_Animator, options);
            if (options.exportMaterials)
            {
                var materialExportPath = Path.Combine(Path.GetDirectoryName(exportFullPath), "Materials");
                Directory.CreateDirectory(materialExportPath);
                foreach (var material in options.materials)
                {
                    var matItem = new AssetItem(material);
                    ExportJSONFile(matItem, materialExportPath);
                }
            }
            ExportFbx(convert, exportFullPath);
            return true;
        }

        public static bool ExportGameObject(AssetItem item, string exportPath, List <AssetItem> animationList = null)
        {
            if (!TryExportFolder(exportPath, item, out var exportFullPath))
                return false;

            var m_GameObject = (GameObject)item.Asset;
            return ExportGameObject(m_GameObject, exportFullPath + Path.DirectorySeparatorChar, animationList);
        }

        public static bool ExportGameObject(GameObject gameObject, string exportPath, List<AssetItem> animationList = null)
        {
            var options = new ModelConverter.Options()
            {
                imageFormat = Properties.Settings.Default.convertType,
                game = Studio.Game,
                collectAnimations = Properties.Settings.Default.collectAnimations,
                exportMaterials = Properties.Settings.Default.exportMaterials,
                materials = new HashSet<Material>(),
                uvs = JsonConvert.DeserializeObject<Dictionary<string, (bool, int)>>(Properties.Settings.Default.uvs),
                texs = JsonConvert.DeserializeObject<Dictionary<string, int>>(Properties.Settings.Default.texs),
            };
            var convert = animationList != null
                ? new ModelConverter(gameObject, options, animationList.Select(x => (AnimationClip)x.Asset).ToArray())
                : new ModelConverter(gameObject, options);
            
            if (convert.MeshList.Count == 0)
            {
                Logger.Info($"GameObject {gameObject.m_Name} has no mesh, skipping...");
                return false;
            }
            if (options.exportMaterials)
            {
                var materialExportPath = Path.Combine(exportPath, "Materials");
                Directory.CreateDirectory(materialExportPath);
                foreach (var material in options.materials)
                {
                    var matItem = new AssetItem(material);
                    ExportJSONFile(matItem, materialExportPath);
                }
            }
            exportPath = exportPath + FixFileName(gameObject.m_Name) + ".fbx";
            ExportFbx(convert, exportPath);
            return true;
        }

        private static void ExportFbx(IImported convert, string exportPath)
        {
            var exportOptions = new Fbx.ExportOptions()
            {
                eulerFilter = Properties.Settings.Default.eulerFilter,
                filterPrecision = (float)Properties.Settings.Default.filterPrecision,
                exportAllNodes = Properties.Settings.Default.exportAllNodes,
                exportSkins = Properties.Settings.Default.exportSkins,
                exportAnimations = Properties.Settings.Default.exportAnimations,
                exportBlendShape = Properties.Settings.Default.exportBlendShape,
                castToBone = Properties.Settings.Default.castToBone,
                boneSize = (int)Properties.Settings.Default.boneSize,
                scaleFactor = (float)Properties.Settings.Default.scaleFactor,
                fbxVersion = Properties.Settings.Default.fbxVersion,
                fbxFormat = Properties.Settings.Default.fbxFormat
            };
            ModelExporter.ExportFbx(exportPath, convert, exportOptions);
        }

        public static bool ExportDumpFile(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".txt", out var exportFullPath))
                return false;
            var str = item.Asset.Dump();
            if (str != null)
            {
                File.WriteAllText(exportFullPath, str);
                return true;
            }
            return false;
        }

        public static bool ExportConvertFile(AssetItem item, string exportPath)
        {
            switch (item.Type)
            {
                case ClassIDType.GameObject:
                    return ExportGameObject(item, exportPath);
                case ClassIDType.Texture2D:
                    return ExportTexture2D(item, exportPath);
                case ClassIDType.AudioClip:
                    return ExportAudioClip(item, exportPath);
                case ClassIDType.Shader:
                    return ExportShader(item, exportPath);
                case ClassIDType.TextAsset:
                    return ExportTextAsset(item, exportPath);
                case ClassIDType.MonoBehaviour:
                    return ExportMonoBehaviour(item, exportPath);
                case ClassIDType.Font:
                    return ExportFont(item, exportPath);
                case ClassIDType.Mesh:
                    return ExportMesh(item, exportPath);
                case ClassIDType.VideoClip:
                    return ExportVideoClip(item, exportPath);
                case ClassIDType.MovieTexture:
                    return ExportMovieTexture(item, exportPath);
                case ClassIDType.Sprite:
                    return ExportSprite(item, exportPath);
                case ClassIDType.Animator:
                    return ExportAnimator(item, exportPath);
                case ClassIDType.AnimationClip:
                    return ExportAnimationClip(item, exportPath);
                case ClassIDType.MiHoYoBinData:
                    return ExportMiHoYoBinData(item, exportPath);
                case ClassIDType.Material:
                    return ExportJSONFile(item, exportPath);
                default:
                    return ExportRawFile(item, exportPath);
            }
        }

        public static bool ExportJSONFile(AssetItem item, string exportPath)
        {
            if (item.Asset is MonoBehaviour)
            {
                return ExportMonoBehaviour(item, exportPath);
            }

            if (!TryExportFile(exportPath, item, ".json", out var exportFullPath))
                return false;

            var settings = new JsonSerializerSettings();
            settings.Converters.Add(new StringEnumConverter());
            object payload = item.Asset;
            TypeTree exportTypeTree = item.Asset.serializedType?.m_Type;
            string typeTreeSource = exportTypeTree != null ? "serializedType" : "none";
            if (item.Asset.GetType() == typeof(Object))
            {
                var typedPayload = item.Asset.ToType();
                if (typedPayload != null)
                {
                    var rawData = item.Asset.GetRawData();
                    var rawSidecar = ExportJsonRawSidecarIfRequested(exportFullPath, rawData);
                    typedPayload.Insert(0, "$animestudio", BuildObjectExportMetadata(
                        item,
                        rawData,
                        exportTypeTree,
                        typeTreeSource,
                        rawSidecar,
                        typedPayload
                    ));
                    payload = typedPayload;
                }
                else
                {
                    var rawData = item.Asset.GetRawData();
                    var rawSidecar = ExportJsonRawSidecarIfRequested(exportFullPath, rawData);
                    var dump = item.Asset.Dump();
                    payload = !string.IsNullOrWhiteSpace(dump)
                        ? new Dictionary<string, object>
                        {
                            ["$animestudio"] = BuildObjectExportMetadata(item, rawData, exportTypeTree, typeTreeSource, rawSidecar, null),
                            ["type"] = item.TypeString,
                            ["name"] = item.Text,
                            ["pathId"] = item.m_PathID,
                            ["dump"] = dump,
                        }
                        : new Dictionary<string, object>
                        {
                            ["$animestudio"] = BuildObjectExportMetadata(item, rawData, exportTypeTree, typeTreeSource, rawSidecar, null),
                            ["type"] = item.TypeString,
                            ["name"] = item.Text,
                            ["pathId"] = item.m_PathID,
                        };
                }
            }

            var str = JsonConvert.SerializeObject(payload, Formatting.Indented, settings);
            File.WriteAllText(exportFullPath, str);
            return true;
        }

        public static string FixFileName(string str)
        {
            var value = string.IsNullOrWhiteSpace(str) ? "unnamed" : str;
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);

            foreach (var ch in value)
            {
                builder.Append(Array.IndexOf(invalidChars, ch) >= 0 || char.IsControl(ch) ? '_' : ch);
            }

            var sanitized = builder.ToString().Trim().TrimEnd('.', ' ');
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "unnamed";
            }

            if (ReservedFileNames.Contains(sanitized))
            {
                sanitized = "_" + sanitized;
            }

            if (sanitized.Length > MaxSafeFileNameLength)
            {
                var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sanitized))).ToLowerInvariant()[..10];
                var prefixLength = Math.Max(16, MaxSafeFileNameLength - hash.Length - 1);
                sanitized = $"{sanitized[..prefixLength].TrimEnd('.', ' ')}_{hash}";
            }

            return sanitized;
        }
    }
}
