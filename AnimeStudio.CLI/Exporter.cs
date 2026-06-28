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
                MonoBehaviourTypeTreeConversion scriptTypeTreeConversion = null;
                Exception scriptTypeTreeDecodeException = null;
                Exception partialTypeTreeException = null;
                long partialTypeTreeBytesRead = 0;
                OrderedDictionary partialTypeTreeStoppedAt = null;
                OrderedDictionary recoveredManagedReferences = null;
                HashSet<long> expectedManagedReferenceRids = null;
                var recoveredManagedReferencesTail = false;
                string partialTypeTreeSourceLabel = null;

                if (Studio.MonoBehaviourTypeTreePriorityMode == MonoBehaviourTypeTreePriority.ScriptFirst && Studio.assemblyLoader.Loaded)
                {
                    TryDecodeMonoBehaviourWithScriptTypeTree(
                        item,
                        m_MonoBehaviour,
                        null,
                        out type,
                        out scriptTypeTreeConversion,
                        out scriptTypeTreeDecodeException
                    );
                    if (type != null)
                    {
                        exportTypeTree = scriptTypeTreeConversion.TypeTree;
                        typeTreeSource = "scriptDerived";
                        decodeException = null;
                    }
                    else if (scriptTypeTreeDecodeException != null)
                    {
                        decodeException = scriptTypeTreeDecodeException;
                    }
                }

                if (type == null)
                {
                    try
                    {
                        type = m_MonoBehaviour.ToType();
                        if (type != null)
                        {
                            exportTypeTree = m_MonoBehaviour.serializedType?.m_Type;
                            typeTreeSource = exportTypeTree != null ? "serializedType" : "none";
                            decodeException = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        builtInTypeTreeException = ex;
                        decodeException = ex;
                    }
                }

                if (type == null && Studio.MonoBehaviourTypeTreePriorityMode == MonoBehaviourTypeTreePriority.SerializedFirst && Studio.assemblyLoader.Loaded)
                {
                    TryDecodeMonoBehaviourWithScriptTypeTree(
                        item,
                        m_MonoBehaviour,
                        builtInTypeTreeException,
                        out type,
                        out scriptTypeTreeConversion,
                        out scriptTypeTreeDecodeException
                    );
                    if (type != null)
                    {
                        exportTypeTree = scriptTypeTreeConversion.TypeTree;
                        typeTreeSource = "scriptDerived";
                        decodeException = null;
                    }
                    else if (scriptTypeTreeDecodeException != null)
                    {
                        decodeException = scriptTypeTreeDecodeException;
                    }
                }

                if (type == null && builtInTypeTreeException != null && exportTypeTree != null)
                {
                    if (TryDecodeMonoBehaviourPartial(
                        item,
                        m_MonoBehaviour,
                        exportTypeTree,
                        builtInTypeTreeException,
                        out type,
                        out partialTypeTreeException,
                        out partialTypeTreeBytesRead
                    ))
                    {
                        partialTypeTreeSourceLabel = "serialized TypeTree";
                    }
                }

                if (type == null
                    && scriptTypeTreeDecodeException != null
                    && scriptTypeTreeConversion?.TypeTree?.m_Nodes?.Count > MonoBehaviourBaseTypeTreeNodeCount)
                {
                    if (TryDecodeMonoBehaviourPartial(
                        item,
                        m_MonoBehaviour,
                        scriptTypeTreeConversion.TypeTree,
                        scriptTypeTreeDecodeException,
                        out type,
                        out partialTypeTreeException,
                        out partialTypeTreeBytesRead))
                    {
                        exportTypeTree = scriptTypeTreeConversion.TypeTree;
                        typeTreeSource = "scriptDerivedPartial";
                        partialTypeTreeSourceLabel = "script-derived TypeTree";
                    }
                }

                var rawData = m_MonoBehaviour.GetRawData();
                var rawSidecar = ExportJsonRawSidecarIfRequested(exportFullPath, rawData);
                if (type != null
                    && partialTypeTreeException != null
                    && TryExtractPartialDecodeStoppedAt(type, out partialTypeTreeStoppedAt)
                    && TryGetPartialDecodeStart(partialTypeTreeStoppedAt, "references", "ManagedReferencesRegistry", out var referencesStartOffset)
                    && IsFinalTopLevelTypeTreeField(exportTypeTree, "references", "ManagedReferencesRegistry"))
                {
                    expectedManagedReferenceRids = CollectManagedReferenceRids(type);
                    if (TryRecoverManagedReferences(rawData, referencesStartOffset, expectedManagedReferenceRids, out var recoveredReferences))
                    {
                        recoveredManagedReferences = recoveredReferences;
                        type["references"] = recoveredReferences;
                        recoveredManagedReferencesTail = true;
                    }
                }

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
                                scriptTypeTreeConversion,
                                scriptTypeTreeDecodeException,
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
                if (partialTypeTreeException != null && !recoveredManagedReferencesTail)
                {
                    LogPartialMonoBehaviourDecode(
                        item,
                        partialTypeTreeSourceLabel ?? typeTreeSource,
                        partialTypeTreeException ?? decodeException
                    );
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
                    scriptTypeTreeConversion,
                    scriptTypeTreeDecodeException,
                    type
                );
                if (partialTypeTreeException != null)
                {
                    if (recoveredManagedReferencesTail)
                    {
                        var recovery = new OrderedDictionary
                        {
                            { "field", "references" },
                            { "type", "ManagedReferencesRegistry" },
                            { "source", partialTypeTreeSourceLabel ?? typeTreeSource },
                            { "bytesReadBeforeRecovery", partialTypeTreeBytesRead },
                            { "decodeError", $"{partialTypeTreeException.GetType().Name}: {partialTypeTreeException.Message}" },
                            { "expectedRidCount", expectedManagedReferenceRids?.Count ?? 0 },
                        };
                        if (partialTypeTreeStoppedAt != null)
                        {
                            recovery["stoppedAt"] = partialTypeTreeStoppedAt;
                        }
                        if (recoveredManagedReferences?["RefIds"] is ICollection recoveredRefIds)
                        {
                            recovery["recoveredRidCount"] = recoveredRefIds.Count;
                        }
                        meta["managedReferencesRegistryRecovered"] = true;
                        meta["managedReferencesRegistryRecovery"] = recovery;
                    }
                    else
                    {
                        meta["partialTypeTreeDecode"] = true;
                        meta["partialTypeTreeBytesRead"] = partialTypeTreeBytesRead;
                        meta["partialTypeTreeError"] = $"{partialTypeTreeException.GetType().Name}: {partialTypeTreeException.Message}";
                        if (partialTypeTreeStoppedAt != null)
                        {
                            meta["partialTypeTreeStoppedAt"] = partialTypeTreeStoppedAt;
                        }
                        if (recoveredManagedReferences != null)
                        {
                            meta["recoveredManagedReferences"] = recoveredManagedReferences;
                        }
                    }
                }
                type.Insert(0, "$animestudio", meta);
                var str = JsonConvert.SerializeObject(type, Formatting.Indented);
                File.WriteAllText(exportFullPath, str);
            }

             return true;
        }

        private sealed class ManagedReferenceHeader
        {
            public long Rid { get; set; }
            public string ClassName { get; set; }
            public string Namespace { get; set; }
            public string AssemblyName { get; set; }
            public bool IsNullSentinel { get; set; }
            public int HeaderStart { get; set; }
            public int DataStart { get; set; }
        }

        private const int MinManagedReferenceHeaderBytes = 20;
        private const int MaxHeuristicStringHintsPerReference = 16;
        private const int MaxHeuristicStringHintsPerObject = 256;
        private const int MaxHeuristicRidLinksPerReference = 64;
        private const int MaxHeuristicRidLinksPerObject = 512;
        private static readonly Encoding StrictUtf8Encoding = new UTF8Encoding(false, true);

        private static bool TryExtractPartialDecodeStoppedAt(
            OrderedDictionary type,
            out OrderedDictionary stoppedAt
        )
        {
            stoppedAt = null;
            if (type == null || !type.Contains("$partialDecodeStoppedAt"))
            {
                return false;
            }

            stoppedAt = type["$partialDecodeStoppedAt"] as OrderedDictionary;
            type.Remove("$partialDecodeStoppedAt");
            return stoppedAt != null;
        }

        private static bool TryGetPartialDecodeStart(
            OrderedDictionary stoppedAt,
            string fieldName,
            string fieldType,
            out long startOffset
        )
        {
            startOffset = 0;
            if (stoppedAt == null)
            {
                return false;
            }
            if (!string.Equals(stoppedAt["field"] as string, fieldName, StringComparison.Ordinal)
                || !string.Equals(stoppedAt["type"] as string, fieldType, StringComparison.Ordinal)
                || !stoppedAt.Contains("startOffset"))
            {
                return false;
            }
            startOffset = Convert.ToInt64(stoppedAt["startOffset"]);
            return startOffset >= 0;
        }

        private static bool IsFinalTopLevelTypeTreeField(TypeTree typeTree, string fieldName, string fieldType)
        {
            var nodes = typeTree?.m_Nodes;
            if (nodes == null)
            {
                return false;
            }

            for (var i = 1; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (!string.Equals(node.m_Name, fieldName, StringComparison.Ordinal)
                    || !string.Equals(node.m_Type, fieldType, StringComparison.Ordinal))
                {
                    continue;
                }

                for (var j = i + 1; j < nodes.Count; j++)
                {
                    if (nodes[j].m_Level <= node.m_Level)
                    {
                        return false;
                    }
                }
                return true;
            }

            return false;
        }

        private static HashSet<long> CollectManagedReferenceRids(object value)
        {
            var rids = new HashSet<long>();
            CollectManagedReferenceRids(value, rids);
            return rids;
        }

        private static void CollectManagedReferenceRids(object value, HashSet<long> rids)
        {
            if (value == null)
            {
                return;
            }

            if (value is OrderedDictionary dictionary)
            {
                if (dictionary.Count == 1
                    && dictionary.Contains("rid")
                    && TryConvertToInt64(dictionary["rid"], out var managedReferenceRid)
                    && managedReferenceRid != 0)
                {
                    rids.Add(managedReferenceRid);
                    return;
                }

                foreach (DictionaryEntry entry in dictionary)
                {
                    CollectManagedReferenceRids(entry.Value, rids);
                }
                return;
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                foreach (var item in enumerable)
                {
                    CollectManagedReferenceRids(item, rids);
                }
            }
        }

        private static bool TryConvertToInt64(object value, out long result)
        {
            result = 0;
            try
            {
                switch (value)
                {
                    case byte v:
                        result = v;
                        return true;
                    case sbyte v:
                        result = v;
                        return true;
                    case short v:
                        result = v;
                        return true;
                    case ushort v:
                        result = v;
                        return true;
                    case int v:
                        result = v;
                        return true;
                    case uint v:
                        result = v;
                        return true;
                    case long v:
                        result = v;
                        return true;
                    case ulong v when v <= long.MaxValue:
                        result = (long)v;
                        return true;
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryRecoverManagedReferences(
            byte[] rawData,
            long startOffset,
            IReadOnlySet<long> expectedRids,
            out OrderedDictionary references
        )
        {
            references = null;
            expectedRids ??= new HashSet<long>();
            if (rawData == null || startOffset < 0 || startOffset > rawData.Length - 8)
            {
                return false;
            }

            var pos = (int)startOffset;
            var version = BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(pos, 4));
            pos += 4;
            var count = BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(pos, 4));
            pos += 4;
            if (version < 1 || version > 3 || count < 0 || count > 10000)
            {
                return false;
            }
            if (count < expectedRids.Count)
            {
                return false;
            }

            if (!TryParseManagedReferenceHeaders(rawData, pos, count, expectedRids, out var headers))
            {
                return false;
            }

            var entries = new List<OrderedDictionary>(count);
            var recoveredRids = new HashSet<long>();
            var recoveredByRid = headers.ToDictionary(header => header.Rid);
            var remainingStringHintBudget = MaxHeuristicStringHintsPerObject;
            var remainingRidLinkBudget = MaxHeuristicRidLinksPerObject;
            for (var i = 0; i < headers.Count; i++)
            {
                var header = headers[i];
                var nextPos = i == headers.Count - 1 ? rawData.Length : headers[i + 1].HeaderStart;
                if (!recoveredRids.Add(header.Rid) || nextPos < header.DataStart)
                {
                    return false;
                }

                var dataLength = nextPos - header.DataStart;
                entries.Add(new OrderedDictionary
                {
                    { "rid", header.Rid },
                    { "type", BuildManagedReferenceType(header) },
                    { "dataOffset", header.DataStart },
                    { "dataLength", dataLength },
                    { "data", BuildManagedReferenceData(
                        header,
                        rawData,
                        header.DataStart,
                        dataLength,
                        recoveredByRid,
                        ref remainingStringHintBudget,
                        ref remainingRidLinkBudget) },
                });
            }

            foreach (var expectedRid in expectedRids)
            {
                if (!recoveredRids.Contains(expectedRid))
                {
                    return false;
                }
            }

            references = new OrderedDictionary
            {
                { "$recovered", true },
                { "$heuristic", true },
                { "stringHintLimitPerReference", MaxHeuristicStringHintsPerReference },
                { "stringHintLimitPerObject", MaxHeuristicStringHintsPerObject },
                { "ridLinkLimitPerReference", MaxHeuristicRidLinksPerReference },
                { "ridLinkLimitPerObject", MaxHeuristicRidLinksPerObject },
                { "version", version },
                { "count", count },
                { "RefIds", entries },
            };
            return true;
        }

        private static bool TryParseManagedReferenceHeaders(
            byte[] rawData,
            int firstHeaderOffset,
            int count,
            IReadOnlySet<long> expectedRids,
            out List<ManagedReferenceHeader> headers
        )
        {
            headers = new List<ManagedReferenceHeader>(count);
            var usedRids = new HashSet<long>();
            var pos = firstHeaderOffset;

            for (var i = 0; i < count; i++)
            {
                if (!TryReadManagedReferenceHeader(rawData, pos, out var header)
                    || !usedRids.Add(header.Rid))
                {
                    return false;
                }
                headers.Add(header);

                if (i == count - 1)
                {
                    break;
                }

                if (!TryFindNextManagedReferenceHeader(
                    rawData,
                    header.DataStart,
                    count - i - 1,
                    expectedRids,
                    usedRids,
                    out pos))
                {
                    return false;
                }
            }

            return true;
        }

        private static OrderedDictionary BuildManagedReferenceType(ManagedReferenceHeader header)
        {
            return new OrderedDictionary
            {
                { "class", header.ClassName },
                { "ns", header.Namespace },
                { "asm", header.AssemblyName },
            };
        }

        private static OrderedDictionary BuildManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid,
            ref int remainingStringHintBudget,
            ref int remainingRidLinkBudget
        )
        {
            if (header?.IsNullSentinel == true && length == 0)
            {
                return new OrderedDictionary
                {
                    { "$null", true },
                    { "$inferred", true },
                    { "offset", offset },
                    { "length", length },
                };
            }

            if (TryDecodeDialogMainFlowData(
                header,
                rawData,
                offset,
                length,
                recoveredByRid,
                ref remainingRidLinkBudget,
                out var decodedData))
            {
                return decodedData;
            }

            if (TryDecodeCharacterDisplayData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }
            if (TryDecodeDialogTeleportEntityActionData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeDialogStringActionData(
                header,
                rawData,
                offset,
                length,
                ref remainingStringHintBudget,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeDialogEmptyTailActionData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeDialogSmallFixedActionData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeDialogMoveToActionData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeDialogLookAtActionData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeDialogTurnToActionData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeDialogCameraEffectActionData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            var data = new OrderedDictionary
            {
                { "$unparsed", true },
                { "$heuristic", true },
                { "offset", offset },
                { "length", length },
            };
            if (TryBuildDialogActionTimingPrefix(header, rawData, offset, length, out var actionTimingPrefix))
            {
                data["inferredActionTimingPrefix"] = actionTimingPrefix;
            }

            var stringHints = CollectAlignedStringHints(rawData, offset, length, ref remainingStringHintBudget);
            if (stringHints.Count > 0)
            {
                data["heuristicStringHints"] = stringHints;
            }

            var ridLinks = CollectHeuristicRidLinks(rawData, offset, length, recoveredByRid, ref remainingRidLinkBudget);
            if (ridLinks.Count > 0)
            {
                data["heuristicRidLinks"] = ridLinks;
            }

            return data;
        }

        private sealed class ManagedReferencePayloadReader
        {
            private readonly byte[] rawData;
            private readonly int end;

            public ManagedReferencePayloadReader(byte[] rawData, int offset, int length)
            {
                this.rawData = rawData ?? throw new InvalidDataException("payload bytes are missing");
                if (offset < 0 || length < 0 || offset > rawData.Length || offset + length > rawData.Length)
                {
                    throw new InvalidDataException("payload range is outside raw data");
                }
                Position = offset;
                end = offset + length;
            }

            public int Position { get; private set; }

            public int End => end;

            public void EnsureComplete()
            {
                if (Position != end)
                {
                    throw new InvalidDataException($"payload parser stopped at {Position}, expected {end}");
                }
            }

            public int ReadInt32(string fieldName)
            {
                EnsureAvailable(4, fieldName);
                var value = BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(Position, 4));
                Position += 4;
                return value;
            }

            public long ReadInt64(string fieldName)
            {
                EnsureAvailable(8, fieldName);
                var value = BinaryPrimitives.ReadInt64LittleEndian(rawData.AsSpan(Position, 8));
                Position += 8;
                return value;
            }

            public float ReadFloat(string fieldName)
            {
                var value = BitConverter.Int32BitsToSingle(ReadInt32(fieldName));
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    throw new InvalidDataException($"invalid float in {fieldName}");
                }
                return value;
            }

            public bool ReadBool32(string fieldName)
            {
                var value = ReadInt32(fieldName);
                if (value != 0 && value != 1)
                {
                    throw new InvalidDataException($"invalid bool32 {value} in {fieldName}");
                }
                return value != 0;
            }

            public string ReadAlignedAsciiString(string fieldName)
            {
                var stringOffset = Position;
                var length = ReadInt32(fieldName);
                if (length < 0 || length > 512)
                {
                    throw new InvalidDataException($"invalid string length {length} in {fieldName}");
                }
                EnsureAvailable(length, fieldName);
                for (var i = Position; i < Position + length; i++)
                {
                    if (rawData[i] < 0x20 || rawData[i] > 0x7E)
                    {
                        throw new InvalidDataException($"non-ASCII byte in {fieldName} at {i}");
                    }
                }

                var value = Encoding.UTF8.GetString(rawData, Position, length);
                Position = (Position + length + 3) & ~3;
                if (Position > end)
                {
                    throw new InvalidDataException($"aligned string {fieldName} at {stringOffset} passes payload end");
                }
                return value;
            }

            private void EnsureAvailable(int byteCount, string fieldName)
            {
                if (byteCount < 0 || Position > end - byteCount)
                {
                    throw new InvalidDataException($"not enough bytes for {fieldName}");
                }
            }
        }

        private static bool TryDecodeCharacterDisplayData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "CharacterDisplayData", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length <= 0
                || offset + length > rawData.Length)
            {
                return false;
            }

            try
            {
                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                data = new OrderedDictionary
                {
                    { "$decoded", true },
                    { "layout", "Beyond.Gameplay.CharacterDisplayData" },
                    { "offset", offset },
                    { "length", length },
                    { "decoItemConfig", ReadCharacterDisplayDecoItemConfig(reader) },
                    { "potentialEffectConfig", ReadCharacterDisplayPotentialEffectConfig(reader) },
                    { "weaponConfig", ReadCharacterDisplayWeaponConfig(reader) },
                    { "height", BuildCharacterHeightEnum(reader.ReadInt32("height")) },
                    { "cameraConfig", new OrderedDictionary
                        {
                            { "charFormationOverride", reader.ReadAlignedAsciiString("cameraConfig.charFormationOverride") },
                        }
                    },
                    { "charInfoCameraGroup", reader.ReadAlignedAsciiString("charInfoCameraGroup") },
                    { "charInfoLightGroup", reader.ReadAlignedAsciiString("charInfoLightGroup") },
                    { "talentPanelRotate", ReadPayloadVector4(reader, "talentPanelRotate") },
                    { "talentPanelScale", ReadPayloadVector3(reader, "talentPanelScale") },
                    { "overviewImgOffset", ReadPayloadVector3(reader, "overviewImgOffset") },
                    { "overrideSpIdleConfig", reader.ReadBool32("overrideSpIdleConfig") },
                    { "charRelaxSpIdleConfig", ReadCharacterDisplayCharRelaxSpIdleConfig(reader) },
                    { "charRelaxReactConfig", ReadCharacterDisplayCharRelaxReactConfig(reader) },
                    { "charId", reader.ReadAlignedAsciiString("charId") },
                };
                reader.EnsureComplete();
                return true;
            }
            catch (InvalidDataException)
            {
                data = null;
                return false;
            }
        }

        private static OrderedDictionary ReadCharacterDisplayDecoItemConfig(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "decoItemData", ReadPayloadObjectList(reader, "decoItemConfig.decoItemData", 32, ReadCharacterDisplayDecoItemData) },
            };
        }

        private static OrderedDictionary ReadCharacterDisplayDecoItemData(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "prefabPath", reader.ReadAlignedAsciiString("decoItemData.prefabPath") },
                { "mountPoint", reader.ReadAlignedAsciiString("decoItemData.mountPoint") },
            };
        }

        private static OrderedDictionary ReadCharacterDisplayPotentialEffectConfig(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "potentialEffects", ReadPayloadObjectList(reader, "potentialEffectConfig.potentialEffects", 32, ReadCharacterDisplayEffectData) },
            };
        }

        private static OrderedDictionary ReadCharacterDisplayEffectData(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "name", reader.ReadAlignedAsciiString("effect.name") },
                { "mountPoint", reader.ReadAlignedAsciiString("effect.mountPoint") },
                { "followScale", reader.ReadBool32("effect.followScale") },
                { "followRotation", reader.ReadBool32("effect.followRotation") },
                { "offset", ReadPayloadVector3(reader, "effect.offset") },
            };
        }

        private static OrderedDictionary ReadCharacterDisplayWeaponConfig(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "weaponData", ReadPayloadObjectList(reader, "weaponConfig.weaponData", 16, ReadCharacterDisplayDynamicWeaponData) },
                { "staticWeaponData", ReadPayloadObjectList(reader, "weaponConfig.staticWeaponData", 16, ReadCharacterDisplayStaticWeaponData) },
                { "weaponAppearEffectName", ReadPayloadStringList(reader, "weaponConfig.weaponAppearEffectName", 32) },
                { "weaponDisappearEffectName", ReadPayloadStringList(reader, "weaponConfig.weaponDisappearEffectName", 32) },
                { "weaponAppearEffectDuration", reader.ReadFloat("weaponConfig.weaponAppearEffectDuration") },
                { "weaponDisappearEffectDuration", reader.ReadFloat("weaponConfig.weaponDisappearEffectDuration") },
                { "weaponChangeEffects", ReadPayloadObjectList(reader, "weaponConfig.weaponChangeEffects", 16, ReadCharacterDisplayEffectData) },
            };
        }

        private static OrderedDictionary ReadCharacterDisplayDynamicWeaponData(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "weaponIndex", reader.ReadInt32("weaponData.weaponIndex") },
                { "vfxKey", reader.ReadAlignedAsciiString("weaponData.vfxKey") },
                { "weaponScale", reader.ReadFloat("weaponData.weaponScale") },
                { "showWhenIdle", reader.ReadBool32("weaponData.showWhenIdle") },
                { "idleMountPoint", reader.ReadInt32("weaponData.idleMountPoint") },
                { "showWhenFight", reader.ReadBool32("weaponData.showWhenFight") },
                { "fightMountPoint", reader.ReadInt32("weaponData.fightMountPoint") },
                { "overrideAnimation", reader.ReadBool32("weaponData.overrideAnimation") },
                { "overrideController", ReadPayloadPPtr(reader, "weaponData.overrideController") },
                { "weaponPath", reader.ReadAlignedAsciiString("weaponData.weaponPath") },
            };
        }

        private static OrderedDictionary ReadCharacterDisplayStaticWeaponData(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "weaponIndex", reader.ReadInt32("staticWeaponData.weaponIndex") },
                { "vfxKey", reader.ReadAlignedAsciiString("staticWeaponData.vfxKey") },
                { "weaponScale", reader.ReadFloat("staticWeaponData.weaponScale") },
                { "weaponPath", reader.ReadAlignedAsciiString("staticWeaponData.weaponPath") },
                { "showWhenIdle", reader.ReadBool32("staticWeaponData.showWhenIdle") },
                { "idleMountPoint", reader.ReadInt32("staticWeaponData.idleMountPoint") },
                { "showWhenFight", reader.ReadBool32("staticWeaponData.showWhenFight") },
                { "fightMountPoint", reader.ReadInt32("staticWeaponData.fightMountPoint") },
                { "overrideAnimation", reader.ReadBool32("staticWeaponData.overrideAnimation") },
                { "overrideController", ReadPayloadPPtr(reader, "staticWeaponData.overrideController") },
                { "nodeUIIdle", reader.ReadAlignedAsciiString("staticWeaponData.nodeUIIdle") },
            };
        }

        private static OrderedDictionary ReadCharacterDisplayCharRelaxSpIdleConfig(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "minIdleTime", reader.ReadFloat("charRelaxSpIdleConfig.minIdleTime") },
                { "sp1IdleWeight", reader.ReadFloat("charRelaxSpIdleConfig.sp1IdleWeight") },
                { "sp2IdleWeight", reader.ReadFloat("charRelaxSpIdleConfig.sp2IdleWeight") },
            };
        }

        private static OrderedDictionary ReadCharacterDisplayCharRelaxReactConfig(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "relativeAngleDegreeRange", ReadPayloadVector2(reader, "charRelaxReactConfig.relativeAngleDegreeRange") },
                { "invertRange", reader.ReadBool32("charRelaxReactConfig.invertRange") },
                { "cameraZoomScaleRange", ReadPayloadVector2(reader, "charRelaxReactConfig.cameraZoomScaleRange") },
                { "triggerOnce", reader.ReadBool32("charRelaxReactConfig.triggerOnce") },
            };
        }

        private static List<OrderedDictionary> ReadPayloadObjectList(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxCount,
            Func<ManagedReferencePayloadReader, OrderedDictionary> readItem
        )
        {
            var count = reader.ReadInt32($"{fieldName}.count");
            if (count < 0 || count > maxCount)
            {
                throw new InvalidDataException($"invalid count {count} for {fieldName}");
            }

            var items = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                items.Add(readItem(reader));
            }
            return items;
        }

        private static List<string> ReadPayloadStringList(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxCount
        )
        {
            var count = reader.ReadInt32($"{fieldName}.count");
            if (count < 0 || count > maxCount)
            {
                throw new InvalidDataException($"invalid count {count} for {fieldName}");
            }

            var items = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                items.Add(reader.ReadAlignedAsciiString($"{fieldName}[{i}]"));
            }
            return items;
        }

        private static OrderedDictionary ReadPayloadPPtr(ManagedReferencePayloadReader reader, string fieldName)
        {
            return new OrderedDictionary
            {
                { "fileId", reader.ReadInt32($"{fieldName}.fileId") },
                { "pathId", reader.ReadInt64($"{fieldName}.pathId") },
            };
        }

        private static OrderedDictionary ReadPayloadVector2(ManagedReferencePayloadReader reader, string fieldName)
        {
            return new OrderedDictionary
            {
                { "x", reader.ReadFloat($"{fieldName}.x") },
                { "y", reader.ReadFloat($"{fieldName}.y") },
            };
        }

        private static OrderedDictionary ReadPayloadVector3(ManagedReferencePayloadReader reader, string fieldName)
        {
            return new OrderedDictionary
            {
                { "x", reader.ReadFloat($"{fieldName}.x") },
                { "y", reader.ReadFloat($"{fieldName}.y") },
                { "z", reader.ReadFloat($"{fieldName}.z") },
            };
        }

        private static OrderedDictionary ReadPayloadVector4(ManagedReferencePayloadReader reader, string fieldName)
        {
            return new OrderedDictionary
            {
                { "x", reader.ReadFloat($"{fieldName}.x") },
                { "y", reader.ReadFloat($"{fieldName}.y") },
                { "z", reader.ReadFloat($"{fieldName}.z") },
                { "w", reader.ReadFloat($"{fieldName}.w") },
            };
        }

        private static OrderedDictionary BuildCharacterHeightEnum(int value)
        {
            return new OrderedDictionary
            {
                { "value", value },
                { "name", value switch
                    {
                        0 => "GirlFlattie",
                        1 => "GirlHighHeel",
                        2 => "Female",
                        3 => "Male",
                        _ => "",
                    }
                },
            };
        }
        private static bool TryDecodeDialogMainFlowData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid,
            ref int remainingRidLinkBudget,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.ClassName, "DialogMainFlowData", StringComparison.Ordinal)
                || !string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length < 12
                || offset + length > rawData.Length
                || recoveredByRid == null
                || recoveredByRid.Count == 0)
            {
                return false;
            }

            var pos = offset;
            var leadRid = BinaryPrimitives.ReadInt64LittleEndian(rawData.AsSpan(pos, 8));
            pos += 8;
            var linkedRidCount = BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(pos, 4));
            pos += 4;

            if (linkedRidCount < 0
                || linkedRidCount > MaxHeuristicRidLinksPerReference
                || linkedRidCount + 1 > remainingRidLinkBudget
                || 12 + linkedRidCount * 8 != length
                || !recoveredByRid.TryGetValue(leadRid, out var leadHeader))
            {
                return false;
            }

            var linkedRids = new List<OrderedDictionary>(linkedRidCount);
            for (var i = 0; i < linkedRidCount; i++)
            {
                var linkOffset = pos;
                var linkedRid = BinaryPrimitives.ReadInt64LittleEndian(rawData.AsSpan(pos, 8));
                pos += 8;
                if (!recoveredByRid.TryGetValue(linkedRid, out var linkedHeader))
                {
                    return false;
                }
                linkedRids.Add(BuildManagedReferenceRidLink(linkedRid, linkedHeader, linkOffset));
            }

            remainingRidLinkBudget -= linkedRidCount + 1;
            data = new OrderedDictionary
            {
                { "$decoded", true },
                { "$inferred", true },
                { "layout", "DialogMainFlowDataRidArray" },
                { "offset", offset },
                { "length", length },
                { "leadRid", BuildManagedReferenceRidLink(leadRid, leadHeader, offset) },
                { "linkedRids", linkedRids },
            };
            return true;
        }

        private static bool TryDecodeDialogStringActionData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            ref int remainingStringHintBudget,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || rawData == null
                || offset < 0
                || length <= 4
                || offset + length > rawData.Length
                || remainingStringHintBudget <= 0)
            {
                return false;
            }

            TryBuildDialogActionTimingPrefix(header, rawData, offset, length, out var actionTimingPrefix);
            if (string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "DialogMFTrunkActionData", StringComparison.Ordinal)
                && length >= 296
                && length <= 300
                && HasInt32Value(rawData, offset + 8, 307)
                && TryReadNamedStringField(rawData, offset + 32, offset + length, out var lineId)
                && StringFieldStartsWith(lineId, "dlg_"))
            {
                remainingStringHintBudget--;
                data = BuildPartialDialogStringActionData(
                    "DialogMFTrunkActionDataLineId",
                    offset,
                    length,
                    "lineId",
                    lineId
                );
                AddDialogActionTimingPrefix(data, actionTimingPrefix);
                return true;
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "DialogAnimActData", StringComparison.Ordinal)
                && length >= 268
                && length <= 292
                && HasInt32Value(rawData, offset + 8, 54)
                && TryReadNamedStringField(rawData, offset + 68, offset + length, out var animationPath)
                && StringFieldStartsWith(animationPath, "Montage/"))
            {
                remainingStringHintBudget--;
                data = BuildPartialDialogStringActionData(
                    "DialogAnimActDataAnimationPath",
                    offset,
                    length,
                    "animationPath",
                    animationPath
                );
                AddDialogActionTimingPrefix(data, actionTimingPrefix);
                return true;
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "DialogEmotionActData", StringComparison.Ordinal)
                && length >= 240
                && length <= 272
                && HasInt32Value(rawData, offset + 8, 122)
                && TryReadNamedStringField(rawData, offset + 44, offset + length, out var facialMorphPath))
            {
                remainingStringHintBudget--;
                data = BuildPartialDialogStringActionData(
                    "DialogEmotionActDataFacialMorphPath",
                    offset,
                    length,
                    "facialMorphPath",
                    facialMorphPath
                );
                AddDialogActionTimingPrefix(data, actionTimingPrefix);
                return true;
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "DialogSummaryActData", StringComparison.Ordinal)
                && length == 52
                && HasInt32Value(rawData, offset + 8, 127)
                && IsZeroFilled(rawData, offset + 12, 16)
                && TryReadNamedStringField(rawData, offset + 28, offset + length, out var summaryId)
                && StringFieldStartsWith(summaryId, "summary_"))
            {
                remainingStringHintBudget--;
                data = BuildPartialDialogStringActionData(
                    "DialogSummaryActDataSummaryId",
                    offset,
                    length,
                    "summaryId",
                    summaryId
                );
                AddDialogActionTimingPrefix(data, actionTimingPrefix);
                return true;
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "DialogMorphAnimActData", StringComparison.Ordinal)
                && remainingStringHintBudget >= 2
                && length == 132
                && HasInt32Value(rawData, offset + 8, 306)
                && IsZeroFilled(rawData, offset + 12, 16)
                && HasInt32Value(rawData, offset + 28, 5)
                && HasInt32Value(rawData, offset + 32, 1)
                && HasInt32Value(rawData, offset + 36, 0)
                && TryReadNamedStringField(rawData, offset + 40, offset + length, out var morphAnimPath)
                && StringFieldStartsWith(morphAnimPath, "FacialMorph/MorphAnim/")
                && TryReadNamedStringField(rawData, offset + 88, offset + length, out var morphStateName)
                && HasInt32Value(rawData, offset + 108, 1065353216)
                && IsZeroFilled(rawData, offset + 112, 20))
            {
                remainingStringHintBudget -= 2;
                data = new OrderedDictionary
                {
                    { "$partialDecoded", true },
                    { "$inferred", true },
                    { "layout", "DialogMorphAnimActDataPaths" },
                    { "offset", offset },
                    { "length", length },
                    { "morphAnimPath", morphAnimPath },
                    { "morphStateName", morphStateName },
                };
                AddDialogActionTimingPrefix(data, actionTimingPrefix);
                return true;
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "DialogEmotionPoseActData", StringComparison.Ordinal)
                && length == 416
                && HasInt32Value(rawData, offset + 8, 305))
            {
                var poseControlNames = CollectAlignedStringHints(rawData, offset, length, ref remainingStringHintBudget);
                if (poseControlNames.Count > 0)
                {
                    data = new OrderedDictionary
                    {
                        { "$partialDecoded", true },
                        { "$inferred", true },
                        { "layout", "DialogEmotionPoseActDataControlNames" },
                        { "offset", offset },
                        { "length", length },
                        { "poseControlNames", poseControlNames },
                    };
                    AddDialogActionTimingPrefix(data, actionTimingPrefix);
                    return true;
                }
            }

            return false;
        }

        private static bool TryDecodeDialogMoveToActionData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "DialogMoveToActData", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length != 128
                || offset + length > rawData.Length
                || !HasInt32Value(rawData, offset + 8, 105)
                || !IsZeroFilled(rawData, offset + 12, 16)
                || !TryReadBoundedInt32(rawData, offset + 28, 0, 1024, out var targetIndexLike)
                || !IsZeroFilled(rawData, offset + 32, 44)
                || !TryReadFiniteTimelineFloat(rawData, offset + 76, out var positionX)
                || !TryReadFiniteTimelineFloat(rawData, offset + 80, out var positionY)
                || !TryReadFiniteTimelineFloat(rawData, offset + 84, out var positionZ)
                || !TryReadFiniteTimelineFloat(rawData, offset + 88, out var rotationX)
                || !TryReadFiniteTimelineFloat(rawData, offset + 92, out var rotationY)
                || !TryReadFiniteTimelineFloat(rawData, offset + 96, out var rotationZ)
                || !IsZeroFilled(rawData, offset + 100, 16)
                || !HasInt32Value(rawData, offset + 116, 2)
                || !HasInt32Value(rawData, offset + 120, 2)
                || !HasInt32Value(rawData, offset + 124, 4)
                || !TryBuildDialogActionTimingPrefix(header, rawData, offset, length, out var actionTimingPrefix))
            {
                return false;
            }

            data = new OrderedDictionary
            {
                { "$partialDecoded", true },
                { "$inferred", true },
                { "layout", "DialogMoveToActDataTransformLike" },
                { "offset", offset },
                { "length", length },
                { "targetIndexLike", BuildInferredIntField(offset + 28, targetIndexLike) },
                { "positionLike", BuildInferredVector3Field(offset + 76, positionX, positionY, positionZ) },
                { "rotationLike", BuildInferredVector3Field(offset + 88, rotationX, rotationY, rotationZ) },
            };
            AddDialogActionTimingPrefix(data, actionTimingPrefix);
            return true;
        }

        private static bool TryDecodeDialogLookAtActionData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "DialogLookAtActData", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length != 144
                || offset + length > rawData.Length
                || !HasInt32Value(rawData, offset + 8, 52)
                || !IsZeroFilled(rawData, offset + 12, 8)
                || !TryReadBoundedInt32(rawData, offset + 20, 0, 1, out var selector0)
                || !HasInt32Value(rawData, offset + 24, 0)
                || !TryReadBoundedInt32(rawData, offset + 28, 0, 3, out var selector1)
                || !TryReadBoundedInt32(rawData, offset + 32, 0, 1, out var selector2)
                || !HasInt32Value(rawData, offset + 36, 0)
                || !TryReadBoundedInt32(rawData, offset + 40, 0, 2, out var selector3)
                || !TryReadBoundedInt32(rawData, offset + 44, -1, 3, out var selector4)
                || !TryReadFiniteTimelineFloat(rawData, offset + 48, out var vectorAX)
                || !TryReadFiniteTimelineFloat(rawData, offset + 52, out var vectorAY)
                || !TryReadFiniteTimelineFloat(rawData, offset + 56, out var vectorAZ)
                || !TryReadFiniteTimelineFloat(rawData, offset + 60, out var vectorBX)
                || !TryReadFiniteTimelineFloat(rawData, offset + 64, out var vectorBY)
                || !TryReadFiniteTimelineFloat(rawData, offset + 68, out var vectorBZ)
                || !TryReadFiniteTimelineFloat(rawData, offset + 72, out var value0)
                || !TryReadBoundedInt32(rawData, offset + 76, 0, 1, out var selector5)
                || !IsZeroFilled(rawData, offset + 80, 8)
                || !HasInt32Value(rawData, offset + 88, 2)
                || !HasInt32Value(rawData, offset + 92, 2)
                || !HasInt32Value(rawData, offset + 96, 4)
                || !TryReadBoundedInt32(rawData, offset + 100, 0, 1, out var selector6)
                || !HasInt32Value(rawData, offset + 104, 1065353216)
                || !TryReadFiniteTimelineFloat(rawData, offset + 108, out var value2)
                || !TryReadFiniteTimelineFloat(rawData, offset + 112, out var value3)
                || !TryReadBoundedInt32(rawData, offset + 116, 0, 1, out var selector7)
                || !HasInt32Value(rawData, offset + 120, 1065353216)
                || !TryReadFiniteTimelineFloat(rawData, offset + 124, out var value5)
                || !TryReadFiniteTimelineFloat(rawData, offset + 128, out var value6)
                || !TryReadFiniteTimelineFloat(rawData, offset + 132, out var value7)
                || !HasInt32Value(rawData, offset + 136, 0)
                || !HasInt32Value(rawData, offset + 140, 1)
                || !TryBuildDialogActionTimingPrefix(header, rawData, offset, length, out var actionTimingPrefix))
            {
                return false;
            }

            data = new OrderedDictionary
            {
                { "$partialDecoded", true },
                { "$inferred", true },
                { "layout", "DialogLookAtActDataScalarBlock" },
                { "offset", offset },
                { "length", length },
                { "selectorFieldsLike", new OrderedDictionary
                    {
                        { "selector0", BuildInferredIntField(offset + 20, selector0) },
                        { "selector1", BuildInferredIntField(offset + 28, selector1) },
                        { "selector2", BuildInferredIntField(offset + 32, selector2) },
                        { "selector3", BuildInferredIntField(offset + 40, selector3) },
                        { "selector4", BuildInferredIntField(offset + 44, selector4) },
                        { "selector5", BuildInferredIntField(offset + 76, selector5) },
                        { "selector6", BuildInferredIntField(offset + 100, selector6) },
                        { "selector7", BuildInferredIntField(offset + 116, selector7) },
                    }
                },
                { "vectorALike", BuildInferredVector3Field(offset + 48, vectorAX, vectorAY, vectorAZ) },
                { "vectorBLike", BuildInferredVector3Field(offset + 60, vectorBX, vectorBY, vectorBZ) },
                { "parameterValuesLike", BuildInferredFloatList(
                    new[] { offset + 72, offset + 104, offset + 108, offset + 112, offset + 120, offset + 124, offset + 128, offset + 132 },
                    new[] { value0, 1.0f, value2, value3, 1.0f, value5, value6, value7 }) },
            };
            AddDialogActionTimingPrefix(data, actionTimingPrefix);
            return true;
        }

        private static bool TryDecodeDialogTurnToActionData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "DialogTurnToActData", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length != 96
                || offset + length > rawData.Length
                || !HasInt32Value(rawData, offset + 8, 53)
                || !IsZeroFilled(rawData, offset + 12, 16)
                || !TryReadBoundedInt32(rawData, offset + 28, 0, 1024, out var targetIndexLike)
                || !TryReadBoundedInt32(rawData, offset + 32, -1, 1024, out var modeLike)
                || !TryReadFiniteTimelineFloat(rawData, offset + 36, out var angleLike)
                || angleLike < -360f
                || angleLike > 360f
                || !IsZeroFilled(rawData, offset + 40, 40)
                || !HasInt32Value(rawData, offset + 80, 2)
                || !HasInt32Value(rawData, offset + 84, 2)
                || !HasInt32Value(rawData, offset + 88, 4)
                || !HasInt32Value(rawData, offset + 92, 0)
                || !TryBuildDialogActionTimingPrefix(header, rawData, offset, length, out var actionTimingPrefix))
            {
                return false;
            }

            data = new OrderedDictionary
            {
                { "$partialDecoded", true },
                { "$inferred", true },
                { "layout", "DialogTurnToActDataAngleBlock" },
                { "offset", offset },
                { "length", length },
                { "targetIndexLike", BuildInferredIntField(offset + 28, targetIndexLike) },
                { "modeLike", BuildInferredIntField(offset + 32, modeLike) },
                { "angleLike", BuildInferredFloatField(offset + 36, angleLike) },
            };
            AddDialogActionTimingPrefix(data, actionTimingPrefix);
            return true;
        }

        private static bool TryDecodeDialogCameraEffectActionData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (TryDecodeDialogCamActionData(header, rawData, offset, length, out data))
            {
                return true;
            }
            if (TryDecodeDialogCamDofActionData(header, rawData, offset, length, out data))
            {
                return true;
            }
            if (TryDecodeDialogMaskActionData(header, rawData, offset, length, out data))
            {
                return true;
            }
            if (TryDecodeDialogCamPpActionData(header, rawData, offset, length, out data))
            {
                return true;
            }
            return false;
        }

        private static bool TryDecodeDialogCamActionData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "DialogCamActData", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length != 476
                || offset + length > rawData.Length
                || !HasInt32Value(rawData, offset + 8, 51)
                || !IsZeroFilled(rawData, offset + 12, 16)
                || !HasInt32Value(rawData, offset + 28, -1)
                || !TryReadBoundedInt32(rawData, offset + 32, -1, 0, out var selector0)
                || !IsZeroFilled(rawData, offset + 36, 12)
                || !HasInt32Value(rawData, offset + 48, 2)
                || !HasInt32Value(rawData, offset + 52, 0)
                || !TryReadFiniteTimelineFloat(rawData, offset + 56, out var value0)
                || !TryReadFiniteTimelineFloat(rawData, offset + 60, out var value1)
                || !TryReadFiniteTimelineFloat(rawData, offset + 64, out var value2)
                || !TryReadFiniteTimelineFloat(rawData, offset + 68, out var value3)
                || !TryReadFiniteTimelineFloat(rawData, offset + 72, out var value4)
                || !TryReadFiniteTimelineFloat(rawData, offset + 76, out var value5)
                || !TryReadFiniteTimelineFloat(rawData, offset + 80, out var value6)
                || !HasInt32Value(rawData, offset + 84, 0)
                || !TryReadFiniteTimelineFloat(rawData, offset + 88, out var value7)
                || !HasInt32Value(rawData, offset + 92, 0)
                || !HasInt32Value(rawData, offset + 96, 2)
                || !HasInt32Value(rawData, offset + 100, 2)
                || !HasInt32Value(rawData, offset + 104, 4)
                || !HasInt32Value(rawData, offset + 108, 1)
                || !IsZeroFilled(rawData, offset + 112, 36)
                || !HasInt32Value(rawData, offset + 148, 1)
                || !HasInt32Value(rawData, offset + 152, 0)
                || !HasInt32Value(rawData, offset + 156, 2)
                || !HasInt32Value(rawData, offset + 160, 2)
                || !HasInt32Value(rawData, offset + 164, 4)
                || !HasInt32Value(rawData, offset + 168, -1)
                || !HasInt32Value(rawData, offset + 172, -1)
                || !HasInt32Value(rawData, offset + 176, 0)
                || !HasInt32Value(rawData, offset + 180, -1)
                || !HasInt32Value(rawData, offset + 184, 0)
                || !HasInt32Value(rawData, offset + 188, -1082130432)
                || !IsZeroFilled(rawData, offset + 192, 8)
                || !HasInt32Value(rawData, offset + 200, 2)
                || !HasInt32Value(rawData, offset + 204, 2)
                || !HasInt32Value(rawData, offset + 208, 4)
                || !IsZeroFilled(rawData, offset + 212, 36)
                || !HasInt32Value(rawData, offset + 248, 1056964608)
                || !HasInt32Value(rawData, offset + 252, 0)
                || !TryReadBoundedInt32(rawData, offset + 256, -1, 0, out var selector1)
                || !IsZeroFilled(rawData, offset + 260, 12)
                || !TryReadBoundedInt32(rawData, offset + 272, 0, 2, out var selector2)
                || !IsZeroFilled(rawData, offset + 276, 44)
                || !HasInt32Value(rawData, offset + 320, 2)
                || !HasInt32Value(rawData, offset + 324, 2)
                || !HasInt32Value(rawData, offset + 328, 4)
                || !HasInt32Value(rawData, offset + 332, 1)
                || !IsZeroFilled(rawData, offset + 336, 36)
                || !HasInt32Value(rawData, offset + 372, 1)
                || !HasInt32Value(rawData, offset + 376, 0)
                || !HasInt32Value(rawData, offset + 380, 2)
                || !HasInt32Value(rawData, offset + 384, 2)
                || !HasInt32Value(rawData, offset + 388, 4)
                || !HasInt32Value(rawData, offset + 392, -1)
                || !HasInt32Value(rawData, offset + 396, -1)
                || !HasInt32Value(rawData, offset + 400, 0)
                || !HasInt32Value(rawData, offset + 404, -1)
                || !HasInt32Value(rawData, offset + 408, 0)
                || !HasInt32Value(rawData, offset + 412, -1082130432)
                || !IsZeroFilled(rawData, offset + 416, 8)
                || !HasInt32Value(rawData, offset + 424, 2)
                || !HasInt32Value(rawData, offset + 428, 2)
                || !HasInt32Value(rawData, offset + 432, 4)
                || !IsZeroFilled(rawData, offset + 436, 36)
                || !HasInt32Value(rawData, offset + 472, 1056964608)
                || !TryBuildDialogActionTimingPrefix(header, rawData, offset, length, out var actionTimingPrefix))
            {
                return false;
            }

            data = new OrderedDictionary
            {
                { "$partialDecoded", true },
                { "$inferred", true },
                { "layout", "DialogCamActDataScalarBlock" },
                { "offset", offset },
                { "length", length },
                { "selectorFieldsLike", new OrderedDictionary
                    {
                        { "selector0", BuildInferredIntField(offset + 32, selector0) },
                        { "selector1", BuildInferredIntField(offset + 256, selector1) },
                        { "selector2", BuildInferredIntField(offset + 272, selector2) },
                    }
                },
                { "parameterValuesLike", BuildInferredFloatList(
                    new[] { offset + 56, offset + 60, offset + 64, offset + 68, offset + 72, offset + 76, offset + 80, offset + 88 },
                    new[] { value0, value1, value2, value3, value4, value5, value6, value7 }) },
            };
            AddDialogActionTimingPrefix(data, actionTimingPrefix);
            return true;
        }

        private static bool TryDecodeDialogCamDofActionData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "DialogCamDOFActionData", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length != 96
                || offset + length > rawData.Length
                || !HasInt32Value(rawData, offset + 8, 115)
                || !IsZeroFilled(rawData, offset + 12, 16)
                || !HasInt32Value(rawData, offset + 28, 1)
                || !HasInt32Value(rawData, offset + 32, -1082130432)
                || !HasInt32Value(rawData, offset + 36, 0)
                || !HasInt32Value(rawData, offset + 40, 0)
                || !HasInt32Value(rawData, offset + 44, 2)
                || !HasInt32Value(rawData, offset + 48, 2)
                || !HasInt32Value(rawData, offset + 52, 4)
                || !IsZeroFilled(rawData, offset + 56, 12)
                || !TryReadFiniteTimelineFloat(rawData, offset + 68, out var value0)
                || !TryReadFiniteTimelineFloat(rawData, offset + 72, out var value1)
                || !TryReadFiniteTimelineFloat(rawData, offset + 76, out var value2)
                || !TryReadFiniteTimelineFloat(rawData, offset + 80, out var value3)
                || !TryReadFiniteTimelineFloat(rawData, offset + 84, out var value4)
                || !TryReadFiniteTimelineFloat(rawData, offset + 88, out var value5)
                || !TryReadFiniteTimelineFloat(rawData, offset + 92, out var value6)
                || !HasInt32Value(rawData, offset + 92, 1056964608)
                || !TryBuildDialogActionTimingPrefix(header, rawData, offset, length, out var actionTimingPrefix))
            {
                return false;
            }

            data = new OrderedDictionary
            {
                { "$partialDecoded", true },
                { "$inferred", true },
                { "layout", "DialogCamDOFActionDataScalarBlock" },
                { "offset", offset },
                { "length", length },
                { "parameterValuesLike", BuildInferredFloatList(
                    new[] { offset + 68, offset + 72, offset + 76, offset + 80, offset + 84, offset + 88, offset + 92 },
                    new[] { value0, value1, value2, value3, value4, value5, value6 }) },
            };
            AddDialogActionTimingPrefix(data, actionTimingPrefix);
            return true;
        }

        private static bool TryDecodeDialogMaskActionData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "DialogMaskActionData", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length != 96
                || offset + length > rawData.Length
                || !HasInt32Value(rawData, offset + 8, 116)
                || !IsZeroFilled(rawData, offset + 12, 16)
                || !TryReadBoundedInt32(rawData, offset + 28, 0, 16, out var modeLike)
                || !TryReadBoundedInt32(rawData, offset + 32, 0, 16, out var targetLike)
                || !HasInt32Value(rawData, offset + 36, 1)
                || !TryReadFiniteTimelineFloat(rawData, offset + 40, out var blendValueLike)
                || blendValueLike < 0f
                || blendValueLike > 1f
                || !IsZeroFilled(rawData, offset + 44, 8)
                || !HasInt32Value(rawData, offset + 52, 2)
                || !HasInt32Value(rawData, offset + 56, 2)
                || !HasInt32Value(rawData, offset + 60, 4)
                || !IsZeroFilled(rawData, offset + 64, 8)
                || !HasInt32Value(rawData, offset + 72, 1)
                || !HasInt32Value(rawData, offset + 76, 2)
                || !IsZeroFilled(rawData, offset + 80, 16)
                || !TryBuildDialogActionTimingPrefix(header, rawData, offset, length, out var actionTimingPrefix))
            {
                return false;
            }

            data = new OrderedDictionary
            {
                { "$partialDecoded", true },
                { "$inferred", true },
                { "layout", "DialogMaskActionDataParameterBlock" },
                { "offset", offset },
                { "length", length },
                { "modeLike", BuildInferredIntField(offset + 28, modeLike) },
                { "targetLike", BuildInferredIntField(offset + 32, targetLike) },
                { "blendValueLike", BuildInferredFloatField(offset + 40, blendValueLike) },
            };
            AddDialogActionTimingPrefix(data, actionTimingPrefix);
            return true;
        }

        private static bool TryDecodeDialogCamPpActionData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "DialogCamPPActionData", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length != 232
                || offset + length > rawData.Length
                || !HasInt32Value(rawData, offset + 8, 118)
                || !IsZeroFilled(rawData, offset + 12, 16)
                || !HasInt32Value(rawData, offset + 28, 1)
                || !TryReadBoundedInt32(rawData, offset + 32, 0, 1, out var modeLike)
                || !TryReadFiniteTimelineFloat(rawData, offset + 36, out var value0)
                || !HasInt32Value(rawData, offset + 48, 2)
                || !HasInt32Value(rawData, offset + 52, 2)
                || !HasInt32Value(rawData, offset + 56, 4)
                || !HasInt32Value(rawData, offset + 60, 300)
                || !TryReadFiniteTimelineFloat(rawData, offset + 64, out var value1)
                || !TryReadFiniteTimelineFloat(rawData, offset + 76, out var value2)
                || !TryReadFiniteTimelineFloat(rawData, offset + 88, out var value3)
                || !HasInt32Value(rawData, offset + 100, 2)
                || !HasInt32Value(rawData, offset + 104, 2)
                || !HasInt32Value(rawData, offset + 108, 4)
                || !HasInt32Value(rawData, offset + 112, 300)
                || !TryReadFiniteTimelineFloat(rawData, offset + 156, out var value4)
                || !HasInt32Value(rawData, offset + 168, 2)
                || !HasInt32Value(rawData, offset + 172, 2)
                || !HasInt32Value(rawData, offset + 176, 4)
                || !HasInt32Value(rawData, offset + 180, 300)
                || !HasInt32Value(rawData, offset + 184, 1036831949)
                || !HasInt32Value(rawData, offset + 204, 300)
                || !HasInt32Value(rawData, offset + 216, 1065353216)
                || !HasInt32Value(rawData, offset + 220, 1065353216)
                || !TryBuildDialogActionTimingPrefix(header, rawData, offset, length, out var actionTimingPrefix))
            {
                return false;
            }

            data = new OrderedDictionary
            {
                { "$partialDecoded", true },
                { "$inferred", true },
                { "layout", "DialogCamPPActionDataScalarBlock" },
                { "offset", offset },
                { "length", length },
                { "modeLike", BuildInferredIntField(offset + 32, modeLike) },
                { "parameterValuesLike", BuildInferredFloatList(
                    new[] { offset + 36, offset + 64, offset + 76, offset + 88, offset + 156, offset + 184, offset + 216, offset + 220 },
                    new[] { value0, value1, value2, value3, value4, 0.1f, 1.0f, 1.0f }) },
            };
            AddDialogActionTimingPrefix(data, actionTimingPrefix);
            return true;
        }

        private static bool TryDecodeDialogSmallFixedActionData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || offset + length > rawData.Length
                || !TryBuildDialogActionTimingPrefix(header, rawData, offset, length, out var actionTimingPrefix))
            {
                return false;
            }

            if (string.Equals(header.ClassName, "DialogMuteAutoBlinkActData", StringComparison.Ordinal)
                && length == 44
                && HasInt32Value(rawData, offset + 8, 304)
                && IsZeroFilled(rawData, offset + 12, 20)
                && TryReadBoundedInt32(rawData, offset + 32, 0, 1, out var muteFlagLike)
                && IsZeroFilled(rawData, offset + 36, 8))
            {
                data = new OrderedDictionary
                {
                    { "$partialDecoded", true },
                    { "$inferred", true },
                    { "layout", "DialogMuteAutoBlinkActDataFlagLike" },
                    { "offset", offset },
                    { "length", length },
                    { "muteFlagLike", BuildInferredIntField(offset + 32, muteFlagLike) },
                };
                AddDialogActionTimingPrefix(data, actionTimingPrefix);
                return true;
            }

            if (string.Equals(header.ClassName, "DialogShowOrHideSingleActorActionData", StringComparison.Ordinal)
                && length == 36
                && HasInt32Value(rawData, offset + 8, 301)
                && IsZeroFilled(rawData, offset + 12, 16)
                && TryReadBoundedInt32(rawData, offset + 28, 0, 1024, out var actorIndexLike)
                && HasInt32Value(rawData, offset + 32, 0))
            {
                data = new OrderedDictionary
                {
                    { "$partialDecoded", true },
                    { "$inferred", true },
                    { "layout", "DialogShowOrHideSingleActorActionDataActorIndexLike" },
                    { "offset", offset },
                    { "length", length },
                    { "actorIndexLike", BuildInferredIntField(offset + 28, actorIndexLike) },
                };
                AddDialogActionTimingPrefix(data, actionTimingPrefix);
                return true;
            }

            return false;
        }

        private static bool TryDecodeDialogEmptyTailActionData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length != 28
                || offset + length > rawData.Length
                || !TryBuildDialogActionTimingPrefix(header, rawData, offset, length, out var actionTimingPrefix)
                || !TryGetDialogEmptyTailActionLayout(header.ClassName, out var expectedActionCode, out var layout)
                || !HasInt32Value(rawData, offset + 8, expectedActionCode)
                || !IsZeroFilled(rawData, offset + 12, length - 12))
            {
                return false;
            }

            data = new OrderedDictionary
            {
                { "$partialDecoded", true },
                { "$inferred", true },
                { "layout", layout },
                { "offset", offset },
                { "length", length },
                { "zeroTail", new OrderedDictionary
                    {
                        { "$inferred", true },
                        { "offset", offset + 12 },
                        { "length", length - 12 },
                    }
                },
            };
            AddDialogActionTimingPrefix(data, actionTimingPrefix);
            return true;
        }

        private static bool TryGetDialogEmptyTailActionLayout(string className, out int expectedActionCode, out string layout)
        {
            expectedActionCode = 0;
            layout = null;
            switch (className)
            {
                case "DialogSetDisableClickActionData":
                    expectedActionCode = 124;
                    layout = "DialogSetDisableClickActionDataEmptyTail";
                    return true;
                case "DialogMFTransitionActionData":
                    expectedActionCode = 308;
                    layout = "DialogMFTransitionActionDataEmptyTail";
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryDecodeDialogTeleportEntityActionData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "DialogTeleportEntityActionData", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length != 60
                || offset + length > rawData.Length
                || !HasInt32Value(rawData, offset + 8, 107)
                || !HasInt32Value(rawData, offset + 12, 0)
                || !HasInt32Value(rawData, offset + 16, 0)
                || !HasInt32Value(rawData, offset + 20, 0)
                || !HasInt32Value(rawData, offset + 24, 0)
                || !HasInt32Value(rawData, offset + 56, 0)
                || !TryReadBoundedInt32(rawData, offset + 28, 0, 1024, out var entityIndex)
                || !TryReadFiniteTimelineFloat(rawData, offset + 32, out var positionX)
                || !TryReadFiniteTimelineFloat(rawData, offset + 36, out var positionY)
                || !TryReadFiniteTimelineFloat(rawData, offset + 40, out var positionZ)
                || !TryReadFiniteTimelineFloat(rawData, offset + 44, out var rotationX)
                || !TryReadFiniteTimelineFloat(rawData, offset + 48, out var rotationY)
                || !TryReadFiniteTimelineFloat(rawData, offset + 52, out var rotationZ)
                || !TryBuildDialogActionTimingPrefix(header, rawData, offset, length, out var actionTimingPrefix))
            {
                return false;
            }

            data = new OrderedDictionary
            {
                { "$partialDecoded", true },
                { "$inferred", true },
                { "layout", "DialogTeleportEntityActionDataTransformLike" },
                { "offset", offset },
                { "length", length },
                { "entityIndex", BuildInferredIntField(offset + 28, entityIndex) },
                { "positionLike", BuildInferredVector3Field(offset + 32, positionX, positionY, positionZ) },
                { "rotationLike", BuildInferredVector3Field(offset + 44, rotationX, rotationY, rotationZ) },
            };
            AddDialogActionTimingPrefix(data, actionTimingPrefix);
            return true;
        }

        private static OrderedDictionary BuildPartialDialogStringActionData(
            string layout,
            int offset,
            int length,
            string fieldName,
            OrderedDictionary fieldValue
        )
        {
            return new OrderedDictionary
            {
                { "$partialDecoded", true },
                { "$inferred", true },
                { "layout", layout },
                { "offset", offset },
                { "length", length },
                { fieldName, fieldValue },
            };
        }

        private static void AddDialogActionTimingPrefix(OrderedDictionary data, OrderedDictionary actionTimingPrefix)
        {
            if (data != null && actionTimingPrefix != null)
            {
                data["inferredActionTimingPrefix"] = actionTimingPrefix;
            }
        }

        private static bool TryBuildDialogActionTimingPrefix(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary prefix
        )
        {
            prefix = null;
            if (header == null
                || rawData == null
                || offset < 0
                || length < 12
                || offset + length > rawData.Length
                || !string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !LooksLikeDialogActionPayloadClass(header.ClassName))
            {
                return false;
            }

            var value0Seconds = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(offset, 4)));
            var value1Seconds = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(offset + 4, 4)));
            var actionCode = BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(offset + 8, 4));
            if (!LooksLikeFiniteTimelineSeconds(value0Seconds)
                || !LooksLikeFiniteTimelineSeconds(value1Seconds)
                || actionCode <= 0
                || actionCode > 10000)
            {
                return false;
            }

            prefix = new OrderedDictionary
            {
                { "$inferred", true },
                { "offset", offset },
                { "value0Seconds", BuildInferredFloatField(offset, value0Seconds) },
                { "value1Seconds", BuildInferredFloatField(offset + 4, value1Seconds) },
                { "actionCode", BuildInferredIntField(offset + 8, actionCode) },
            };
            return true;
        }

        private static bool LooksLikeDialogActionPayloadClass(string className)
        {
            return !string.IsNullOrEmpty(className)
                && className.StartsWith("Dialog", StringComparison.Ordinal)
                && (className.EndsWith("ActData", StringComparison.Ordinal)
                    || className.EndsWith("ActionData", StringComparison.Ordinal));
        }

        private static bool LooksLikeFiniteTimelineSeconds(float value)
        {
            return !float.IsNaN(value)
                && !float.IsInfinity(value)
                && value >= -100000f
                && value <= 100000f;
        }

        private static bool TryReadFiniteTimelineFloat(byte[] rawData, int offset, out float value)
        {
            value = 0;
            if (rawData == null || offset < 0 || offset > rawData.Length - 4)
            {
                return false;
            }

            value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(offset, 4)));
            return LooksLikeFiniteTimelineSeconds(value);
        }

        private static bool TryReadBoundedInt32(byte[] rawData, int offset, int min, int max, out int value)
        {
            value = 0;
            if (rawData == null || offset < 0 || offset > rawData.Length - 4)
            {
                return false;
            }

            value = BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(offset, 4));
            return value >= min && value <= max;
        }

        private static bool HasInt32Value(byte[] rawData, int offset, int expected)
        {
            return TryReadBoundedInt32(rawData, offset, expected, expected, out _);
        }

        private static bool IsZeroFilled(byte[] rawData, int offset, int length)
        {
            if (rawData == null || offset < 0 || length < 0 || offset + length > rawData.Length)
            {
                return false;
            }

            for (var i = offset; i < offset + length; i++)
            {
                if (rawData[i] != 0)
                {
                    return false;
                }
            }
            return true;
        }

        private static OrderedDictionary BuildInferredFloatField(int offset, float value)
        {
            return new OrderedDictionary
            {
                { "offset", offset },
                { "value", value },
            };
        }

        private static OrderedDictionary BuildInferredIntField(int offset, int value)
        {
            return new OrderedDictionary
            {
                { "offset", offset },
                { "value", value },
            };
        }

        private static OrderedDictionary BuildInferredVector3Field(int offset, float x, float y, float z)
        {
            return new OrderedDictionary
            {
                { "offset", offset },
                { "x", BuildInferredFloatField(offset, x) },
                { "y", BuildInferredFloatField(offset + 4, y) },
                { "z", BuildInferredFloatField(offset + 8, z) },
            };
        }

        private static List<OrderedDictionary> BuildInferredFloatList(int[] offsets, float[] values)
        {
            var fields = new List<OrderedDictionary>();
            if (offsets == null || values == null || offsets.Length != values.Length)
            {
                return fields;
            }

            for (var i = 0; i < offsets.Length; i++)
            {
                fields.Add(BuildInferredFloatField(offsets[i], values[i]));
            }
            return fields;
        }

        private static bool TryReadNamedStringField(
            byte[] rawData,
            int stringOffset,
            int end,
            out OrderedDictionary fieldValue
        )
        {
            fieldValue = null;
            if (rawData == null || stringOffset < 0 || stringOffset > rawData.Length - 4)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(stringOffset, 4));
            if (!TryDecodeStringHint(rawData, stringOffset + 4, length, end, out var value))
            {
                return false;
            }

            fieldValue = new OrderedDictionary
            {
                { "offset", stringOffset },
                { "value", value },
            };
            return true;
        }

        private static bool StringFieldStartsWith(OrderedDictionary fieldValue, string prefix)
        {
            return fieldValue != null
                && fieldValue["value"] is string value
                && value.StartsWith(prefix, StringComparison.Ordinal);
        }

        private static List<OrderedDictionary> CollectAlignedStringHints(byte[] rawData, int offset, int length, ref int remainingStringHintBudget)
        {
            var hints = new List<OrderedDictionary>();
            if (rawData == null || offset < 0 || length <= 4 || offset + length > rawData.Length || remainingStringHintBudget <= 0)
            {
                return hints;
            }

            var end = offset + length;
            var pos = (offset + 3) & ~3;
            while (pos <= end - 4 && hints.Count < MaxHeuristicStringHintsPerReference && remainingStringHintBudget > 0)
            {
                var stringLength = BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(pos, 4));
                if (TryDecodeStringHint(rawData, pos + 4, stringLength, end, out var value))
                {
                    hints.Add(new OrderedDictionary
                    {
                        { "offset", pos },
                        { "value", value },
                    });
                    remainingStringHintBudget--;
                    pos = (pos + 4 + stringLength + 3) & ~3;
                    continue;
                }
                pos += 4;
            }

            return hints;
        }

        private static List<OrderedDictionary> CollectHeuristicRidLinks(
            byte[] rawData,
            int offset,
            int length,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid,
            ref int remainingRidLinkBudget
        )
        {
            var links = new List<OrderedDictionary>();
            if (rawData == null
                || offset < 0
                || length < 8
                || offset + length > rawData.Length
                || recoveredByRid == null
                || recoveredByRid.Count == 0
                || remainingRidLinkBudget <= 0)
            {
                return links;
            }

            var end = offset + length;
            var pos = (offset + 3) & ~3;
            while (pos <= end - 8
                && links.Count < MaxHeuristicRidLinksPerReference
                && remainingRidLinkBudget > 0)
            {
                var rid = BinaryPrimitives.ReadInt64LittleEndian(rawData.AsSpan(pos, 8));
                if (recoveredByRid.TryGetValue(rid, out var target))
                {
                    links.Add(BuildManagedReferenceRidLink(rid, target, pos));
                    remainingRidLinkBudget--;
                    pos += 8;
                    continue;
                }

                pos += 4;
            }

            return links;
        }

        private static OrderedDictionary BuildManagedReferenceRidLink(long rid, ManagedReferenceHeader target, int offset)
        {
            return new OrderedDictionary
            {
                { "offset", offset },
                { "rid", rid },
                { "type", BuildManagedReferenceType(target) },
            };
        }

        private static bool TryDecodeStringHint(byte[] rawData, int offset, int length, int end, out string value)
        {
            value = null;
            if (length < 3 || length > 256 || offset < 0 || offset + length > end || offset + length > rawData.Length)
            {
                return false;
            }

            try
            {
                value = StrictUtf8Encoding.GetString(rawData, offset, length);
            }
            catch (DecoderFallbackException)
            {
                value = null;
                return false;
            }

            var hasLetterOrDigit = false;
            foreach (var ch in value)
            {
                if (char.IsControl(ch))
                {
                    value = null;
                    return false;
                }
                if (char.IsLetterOrDigit(ch))
                {
                    hasLetterOrDigit = true;
                }
            }

            if (!hasLetterOrDigit)
            {
                value = null;
                return false;
            }
            return true;
        }

        private static bool TryFindNextManagedReferenceHeader(
            byte[] rawData,
            int start,
            int remainingHeaderCount,
            IReadOnlySet<long> expectedRids,
            IReadOnlySet<long> usedRids,
            out int headerOffset
        )
        {
            foreach (var preferExpectedRid in new[] { true, false })
            {
                var candidate = (start + 3) & ~3;
                var lastCandidate = rawData.Length - (remainingHeaderCount * MinManagedReferenceHeaderBytes);
                for (; candidate <= lastCandidate; candidate += 4)
                {
                    if (!TryReadManagedReferenceHeader(rawData, candidate, out var header)
                        || usedRids.Contains(header.Rid)
                        || (preferExpectedRid && !expectedRids.Contains(header.Rid)))
                    {
                        continue;
                    }
                    if (CanParseRemainingManagedReferenceHeaders(rawData, candidate, remainingHeaderCount, usedRids))
                    {
                        headerOffset = candidate;
                        return true;
                    }
                }
            }

            headerOffset = -1;
            return false;
        }

        private static bool CanParseRemainingManagedReferenceHeaders(
            byte[] rawData,
            int start,
            int remainingHeaderCount,
            IReadOnlySet<long> priorRids
        )
        {
            var used = new HashSet<long>(priorRids);
            var pos = start;

            for (var i = 0; i < remainingHeaderCount; i++)
            {
                if (!TryReadManagedReferenceHeader(rawData, pos, out var header)
                    || !used.Add(header.Rid))
                {
                    return false;
                }

                if (i == remainingHeaderCount - 1)
                {
                    return true;
                }

                var candidate = (header.DataStart + 3) & ~3;
                var lastCandidate = rawData.Length - ((remainingHeaderCount - i - 1) * MinManagedReferenceHeaderBytes);
                var found = false;
                for (; candidate <= lastCandidate; candidate += 4)
                {
                    if (TryReadManagedReferenceHeader(rawData, candidate, out var nextHeader)
                        && !used.Contains(nextHeader.Rid))
                    {
                        pos = candidate;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryReadManagedReferenceHeader(byte[] rawData, int offset, out ManagedReferenceHeader header)
        {
            header = null;
            if (offset < 0 || offset > rawData.Length - 12)
            {
                return false;
            }

            var pos = offset;
            var rid = BinaryPrimitives.ReadInt64LittleEndian(rawData.AsSpan(pos, 8));
            pos += 8;
            if (!TryReadAlignedAsciiString(rawData, ref pos, out var className)
                || !TryReadAlignedAsciiString(rawData, ref pos, out var namespaceName)
                || !TryReadAlignedAsciiString(rawData, ref pos, out var assemblyName))
            {
                return false;
            }

            var isNullSentinel = rid < 0
                && string.IsNullOrEmpty(className)
                && string.IsNullOrEmpty(namespaceName)
                && string.IsNullOrEmpty(assemblyName);
            if (rid == 0 || (rid < 0 && !isNullSentinel))
            {
                return false;
            }
            if (!isNullSentinel
                && (!LooksLikeManagedReferenceClassName(className)
                    || !LooksLikeManagedReferenceNamespace(namespaceName)
                    || !LooksLikeManagedReferenceAssemblyName(assemblyName)))
            {
                return false;
            }

            header = new ManagedReferenceHeader
            {
                Rid = rid,
                ClassName = className,
                Namespace = namespaceName,
                AssemblyName = assemblyName,
                IsNullSentinel = isNullSentinel,
                HeaderStart = offset,
                DataStart = pos,
            };
            return true;
        }

        private static bool LooksLikeManagedReferenceClassName(string value)
        {
            if (string.IsNullOrEmpty(value) || !(char.IsLetter(value[0]) || value[0] == '_'))
            {
                return false;
            }

            foreach (var ch in value)
            {
                if (!(char.IsLetterOrDigit(ch)
                    || ch == '_'
                    || ch == '`'
                    || ch == '<'
                    || ch == '>'
                    || ch == '+'
                    || ch == '['
                    || ch == ']'
                    || ch == ','))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool LooksLikeManagedReferenceNamespace(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }

            return value.Split('.').All(part => LooksLikeManagedReferenceClassName(part));
        }

        private static bool LooksLikeManagedReferenceAssemblyName(string value)
        {
            if (string.IsNullOrEmpty(value) || !(char.IsLetter(value[0]) || value[0] == '_'))
            {
                return false;
            }

            foreach (var ch in value)
            {
                if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '.' || ch == '-'))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryReadAlignedAsciiString(byte[] rawData, ref int pos, out string value)
        {
            value = "";
            if (pos > rawData.Length - 4)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(pos, 4));
            pos += 4;
            if (length < 0 || length > 512 || pos + length > rawData.Length)
            {
                return false;
            }

            for (var i = pos; i < pos + length; i++)
            {
                if (rawData[i] < 0x20 || rawData[i] > 0x7E)
                {
                    return false;
                }
            }

            value = Encoding.UTF8.GetString(rawData, pos, length);
            pos = (pos + length + 3) & ~3;
            return pos <= rawData.Length;
        }

        private static void TryDecodeMonoBehaviourWithScriptTypeTree(
            AssetItem item,
            MonoBehaviour m_MonoBehaviour,
            Exception builtInTypeTreeException,
            out OrderedDictionary type,
            out MonoBehaviourTypeTreeConversion scriptTypeTreeConversion,
            out Exception scriptTypeTreeDecodeException
        )
        {
            type = null;
            scriptTypeTreeDecodeException = null;
            scriptTypeTreeConversion = Studio.MonoBehaviourToTypeTreeWithDiagnostics(m_MonoBehaviour);
            if (scriptTypeTreeConversion?.TypeTree?.m_Nodes?.Count <= MonoBehaviourBaseTypeTreeNodeCount)
            {
                return;
            }

            try
            {
                if (builtInTypeTreeException != null)
                {
                    Logger.Warning(
                        $"Retrying MonoBehaviour {item.Text} with a script-derived type tree after " +
                        $"{builtInTypeTreeException.GetType().Name}: {builtInTypeTreeException.Message}"
                    );
                }
                type = m_MonoBehaviour.ToType(scriptTypeTreeConversion.TypeTree);
            }
            catch (Exception ex)
            {
                scriptTypeTreeDecodeException = ex;
                Logger.Warning(
                    $"Script-derived MonoBehaviour decode failed for {item.Text}: " +
                    $"{ex.GetType().Name}: {ex.Message}"
                );
            }
        }

        private static void LogPartialMonoBehaviourDecode(AssetItem item, string sourceLabel, Exception reason)
        {
            var itemLocation =
                $" [PathID={item.m_PathID}, SourceFile={item.SourceFile?.fileName ?? ""}, " +
                $"SourceOriginalPath={item.SourceFile?.originalPath ?? ""}, Container={item.Container ?? ""}]";
            if (reason != null)
            {
                Logger.Warning(
                    $"Partially decoded MonoBehaviour {item.Text} with {sourceLabel}{itemLocation} after " +
                    $"{reason.GetType().Name}: {reason.Message}"
                );
            }
            else
            {
                Logger.Warning($"Partially decoded MonoBehaviour {item.Text} with {sourceLabel}{itemLocation}");
            }
        }

        private static bool TryDecodeMonoBehaviourPartial(
            AssetItem item,
            MonoBehaviour m_MonoBehaviour,
            TypeTree typeTree,
            Exception decodeException,
            out OrderedDictionary type,
            out Exception partialTypeTreeException,
            out long partialTypeTreeBytesRead
        )
        {
            type = null;
            partialTypeTreeException = null;
            partialTypeTreeBytesRead = 0;
            if (typeTree == null)
            {
                return false;
            }

            try
            {
                var partialType = m_MonoBehaviour.ToTypePartial(
                    typeTree,
                    out partialTypeTreeException,
                    out partialTypeTreeBytesRead
                );
                if (partialType == null || partialType.Count <= 1)
                {
                    return false;
                }

                type = partialType;
                partialTypeTreeException ??= decodeException;
                return true;
            }
            catch (Exception ex)
            {
                partialTypeTreeException = ex;
                partialTypeTreeBytesRead = m_MonoBehaviour.reader.Position - m_MonoBehaviour.reader.byteStart;
                return false;
            }
        }

        private static OrderedDictionary BuildMonoBehaviourExportMetadata(
            AssetItem item,
            MonoBehaviour m_MonoBehaviour,
            byte[] rawData,
            TypeTree exportTypeTree,
            string typeTreeSource,
            string rawSidecar,
            Exception builtInTypeTreeException,
            MonoBehaviourTypeTreeConversion scriptTypeTreeConversion,
            Exception scriptTypeTreeDecodeException,
            OrderedDictionary payload
        )
        {
            var meta = BuildObjectExportMetadata(item, rawData, exportTypeTree, typeTreeSource, rawSidecar, payload);
            meta["scriptFileId"] = m_MonoBehaviour.m_Script.m_FileID;
            meta["scriptPathId"] = m_MonoBehaviour.m_Script.m_PathID;

            var includeScriptDiagnostics = scriptTypeTreeConversion != null
                || Studio.MonoBehaviourTypeTreePriorityMode != MonoBehaviourTypeTreePriority.SerializedFirst
                || (typeTreeSource?.StartsWith("scriptDerived", StringComparison.OrdinalIgnoreCase) ?? false);
            if (includeScriptDiagnostics && m_MonoBehaviour.m_Script.TryGet(out var m_Script))
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

            if (includeScriptDiagnostics)
            {
                meta["monoBehaviourTypeTreePriority"] = Studio.MonoBehaviourTypeTreePriorityMode.ToString();
                meta["scriptDerivedTypeTreeAttempted"] = scriptTypeTreeConversion != null;
                if (scriptTypeTreeConversion != null)
                {
                    meta["scriptDerivedTypeTreeStatus"] = scriptTypeTreeConversion.Status ?? "";
                    meta["scriptDerivedScriptIdentitySource"] = scriptTypeTreeConversion.ScriptIdentitySource ?? "";
                    meta["scriptDerivedMonoScriptResolved"] = scriptTypeTreeConversion.MonoScriptResolved;
                    meta["scriptDerivedTypeDefinitionResolved"] = scriptTypeTreeConversion.TypeDefinitionResolved;
                    meta["scriptDerivedTypeTreeNodeCount"] = scriptTypeTreeConversion.NodeCount;
                    meta["scriptDerivedTypeTreeUsable"] = scriptTypeTreeConversion.NodeCount > MonoBehaviourBaseTypeTreeNodeCount;
                    if (!string.IsNullOrEmpty(scriptTypeTreeConversion.ScriptClassName) && !meta.Contains("scriptClassName"))
                    {
                        meta["scriptClassName"] = scriptTypeTreeConversion.ScriptClassName;
                        meta["scriptNamespace"] = scriptTypeTreeConversion.ScriptNamespace;
                        meta["scriptFullName"] = scriptTypeTreeConversion.ScriptFullName;
                        meta["scriptAssemblyName"] = scriptTypeTreeConversion.ScriptAssemblyName;
                    }
                    if (scriptTypeTreeConversion.Exception != null)
                    {
                        meta["scriptDerivedTypeTreeError"] = $"{scriptTypeTreeConversion.Exception.GetType().Name}: {scriptTypeTreeConversion.Exception.Message}";
                    }
                }
                if (scriptTypeTreeDecodeException != null)
                {
                    meta["scriptDerivedDecodeError"] = $"{scriptTypeTreeDecodeException.GetType().Name}: {scriptTypeTreeDecodeException.Message}";
                }
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
