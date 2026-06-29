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

        private static string Texture2DNoOutputReason(AssetItem item, Texture2D texture)
        {
            if (IsFontPlaceholderZeroSizeTexture(item, texture))
                return "font_placeholder_zero_size_texture";
            if (texture.m_Width <= 0 || texture.m_Height <= 0)
                return "zero_size_texture";
            if ((texture.image_data?.Size ?? 0) == 0)
                return "empty_image_payload";
            return "decode_failed";
        }

        private static bool IsFontPlaceholderZeroSizeTexture(AssetItem item, Texture2D texture)
        {
            var streamData = texture.m_StreamData;
            return string.Equals(item.Text, "Font Texture", StringComparison.Ordinal)
                && texture.m_Width == 0
                && texture.m_Height == 0
                && (texture.image_data?.Size ?? 0) == 0
                && (streamData?.size ?? 0) == 0
                && string.IsNullOrEmpty(streamData?.path);
        }

        private static string Texture2DMarkerExtension(ImageFormat type)
        {
            return "." + type.ToString().ToLowerInvariant() + ".empty.json";
        }

        private static string MeshNoOutputReason(Mesh mesh)
        {
            if (mesh.m_VertexCount <= 0)
                return "zero_vertex_count";
            if (mesh.m_Vertices == null)
                return "missing_vertex_buffer";
            if (mesh.m_Vertices.Length == 0)
                return "empty_vertex_buffer";
            return "unknown";
        }

        private static string EscapeLogField(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string QuoteLogField(string value)
        {
            return $"\"{EscapeLogField(value)}\"";
        }

        private static void LogTexture2DNoOutput(AssetItem item, Texture2D texture)
        {
            var streamData = texture.m_StreamData;
            Logger.Warning(
                "Texture2D no output " +
                $"reason={Texture2DNoOutputReason(item, texture)} " +
                $"name={QuoteLogField(item.Text)} " +
                $"PathID={item.m_PathID} " +
                $"SourceFile={QuoteLogField(item.SourceFile?.fileName)} " +
                $"SourceOriginalPath={QuoteLogField(item.SourceFile?.originalPath)} " +
                $"SourceOffset={item.SourceFile?.offset ?? -1} " +
                $"Container={QuoteLogField(item.Container)} " +
                $"Width={texture.m_Width} " +
                $"Height={texture.m_Height} " +
                $"Format={texture.m_TextureFormat} " +
                $"ImageSize={texture.image_data?.Size ?? 0} " +
                $"StreamSize={streamData?.size ?? 0} " +
                $"StreamOffset={streamData?.offset ?? 0} " +
                $"StreamPath={QuoteLogField(streamData?.path)}");
        }

        private static void LogMeshNoOutput(AssetItem item, Mesh mesh, string reason = null)
        {
            Logger.Warning(
                "Mesh no output " +
                $"reason={reason ?? MeshNoOutputReason(mesh)} " +
                $"name={QuoteLogField(item.Text)} " +
                $"PathID={item.m_PathID} " +
                $"SourceFile={QuoteLogField(item.SourceFile?.fileName)} " +
                $"SourceOriginalPath={QuoteLogField(item.SourceFile?.originalPath)} " +
                $"SourceOffset={item.SourceFile?.offset ?? -1} " +
                $"Container={QuoteLogField(item.Container)} " +
                $"VertexCount={mesh.m_VertexCount} " +
                $"VerticesLength={mesh.m_Vertices?.Length ?? 0} " +
                $"SubMeshCount={mesh.m_SubMeshes?.Count ?? 0} " +
                $"IndexCount={mesh.m_Indices?.Count ?? 0} " +
                $"ByteSize={item.FullSize}");
        }

        private static bool ExportEmptyTexture2DMarker(AssetItem item, Texture2D texture, string exportPath, ImageFormat type)
        {
            if (!TryExportFile(exportPath, item, Texture2DMarkerExtension(type), out var exportFullPath))
            {
                LogTexture2DNoOutput(item, texture);
                return false;
            }

            var streamData = texture.m_StreamData;
            var marker = new
            {
                animeStudio = new
                {
                    kind = "empty_texture2d_marker",
                    reason = Texture2DNoOutputReason(item, texture),
                    note = "Unity parsed this Texture2D, but it has zero dimensions and no image or stream payload. No PNG pixels exist to emit."
                },
                type = item.TypeString,
                name = item.Text,
                pathId = item.m_PathID,
                sourceFile = item.SourceFile?.fileName,
                sourceOriginalPath = item.SourceFile?.originalPath,
                sourceOffset = item.SourceFile?.offset ?? -1,
                container = item.Container,
                width = texture.m_Width,
                height = texture.m_Height,
                format = texture.m_TextureFormat.ToString(),
                imageSize = texture.image_data?.Size ?? 0,
                streamSize = streamData?.size ?? 0,
                streamOffset = streamData?.offset ?? 0,
                streamPath = streamData?.path ?? string.Empty,
                byteSize = item.FullSize
            };
            File.WriteAllText(exportFullPath, JsonConvert.SerializeObject(marker, Formatting.Indented));
            return true;
        }

        private static bool ExportEmptyMesh(AssetItem item, Mesh mesh, string exportPath, string reason)
        {
            if (!TryExportFile(exportPath, item, ".obj", out var exportFullPath))
            {
                LogMeshNoOutput(item, mesh, "output_path_unavailable");
                return false;
            }

            var sb = new StringBuilder();
            sb.AppendLine("# AnimeStudio empty Mesh");
            sb.AppendLine("# The Unity Mesh was parsed but has no vertices, so no OBJ faces can be emitted.");
            sb.AppendLine($"# reason: {reason}");
            sb.AppendLine($"# name: {mesh.m_Name}");
            sb.AppendLine($"# path_id: {item.m_PathID}");
            sb.AppendLine($"# source_file: {item.SourceFile?.fileName}");
            sb.AppendLine($"# source_offset: {item.SourceFile?.offset ?? -1}");
            sb.AppendLine($"# container: {item.Container}");
            sb.AppendLine($"# vertex_count: {mesh.m_VertexCount}");
            sb.AppendLine($"# vertices_length: {mesh.m_Vertices?.Length ?? 0}");
            sb.AppendLine($"# submesh_count: {mesh.m_SubMeshes?.Count ?? 0}");
            sb.AppendLine($"# index_count: {mesh.m_Indices?.Count ?? 0}");
            sb.AppendLine($"# byte_size: {item.FullSize}");
            sb.AppendLine("g " + mesh.m_Name);
            File.WriteAllText(exportFullPath, sb.ToString());
            return true;
        }

        private static void LogAnimatorNoOutput(AssetItem item, Animator animator, ModelConverter convert, string exportPath, string reason)
        {
            animator.m_GameObject.TryGet(out var gameObject);
            Logger.Warning(
                "Animator no output " +
                $"reason={reason} " +
                $"name={QuoteLogField(item.Text)} " +
                $"PathID={item.m_PathID} " +
                $"SourceFile={QuoteLogField(item.SourceFile?.fileName)} " +
                $"SourceOriginalPath={QuoteLogField(item.SourceFile?.originalPath)} " +
                $"SourceOffset={item.SourceFile?.offset ?? -1} " +
                $"Container={QuoteLogField(item.Container)} " +
                $"GameObjectName={QuoteLogField(gameObject?.m_Name)} " +
                $"GameObjectPathID={gameObject?.m_PathID ?? 0} " +
                $"GameObjectPointerPathID={animator.m_GameObject.m_PathID} " +
                $"AvatarPathID={animator.m_Avatar.m_PathID} " +
                $"ControllerPathID={animator.m_Controller.m_PathID} " +
                $"HasTransformHierarchy={animator.m_HasTransformHierarchy} " +
                $"MeshCount={convert.MeshList?.Count ?? 0} " +
                $"MaterialCount={convert.MaterialList?.Count ?? 0} " +
                $"TextureCount={convert.TextureList?.Count ?? 0} " +
                $"AnimationCount={convert.AnimationList?.Count ?? 0} " +
                $"ExportPath={QuoteLogField(exportPath)}");
        }

        private static bool ExportEmptyAnimatorMarker(AssetItem item, Animator animator, ModelConverter convert, string exportPath, string reason)
        {
            if (!TryExportFile(exportPath, item, ".fbx.empty.json", out var exportFullPath))
            {
                LogAnimatorNoOutput(item, animator, convert, exportFullPath, "output_path_unavailable");
                return false;
            }

            animator.m_GameObject.TryGet(out var gameObject);
            var marker = new
            {
                animeStudio = new
                {
                    kind = "empty_animator_marker",
                    reason,
                    note = "Unity parsed this Animator, but the resolved hierarchy has no Mesh objects, so no FBX geometry can be emitted."
                },
                type = item.TypeString,
                name = item.Text,
                pathId = item.m_PathID,
                sourceFile = item.SourceFile?.fileName,
                sourceOriginalPath = item.SourceFile?.originalPath,
                sourceOffset = item.SourceFile?.offset ?? -1,
                container = item.Container,
                gameObjectName = gameObject?.m_Name,
                gameObjectPathId = gameObject?.m_PathID ?? 0,
                gameObjectPointerPathId = animator.m_GameObject.m_PathID,
                avatarPathId = animator.m_Avatar.m_PathID,
                controllerPathId = animator.m_Controller.m_PathID,
                hasTransformHierarchy = animator.m_HasTransformHierarchy,
                meshCount = convert.MeshList?.Count ?? 0,
                materialCount = convert.MaterialList?.Count ?? 0,
                textureCount = convert.TextureList?.Count ?? 0,
                animationCount = convert.AnimationList?.Count ?? 0,
                byteSize = item.FullSize
            };
            File.WriteAllText(exportFullPath, JsonConvert.SerializeObject(marker, Formatting.Indented));
            return true;
        }

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
                {
                    if (IsFontPlaceholderZeroSizeTexture(item, m_Texture2D))
                    {
                        return ExportEmptyTexture2DMarker(item, m_Texture2D, exportPath, type);
                    }
                    LogTexture2DNoOutput(item, m_Texture2D);
                    return false;
                }
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
                var recoveredManagedReferencesFullyDecoded = false;
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
                        recoveredManagedReferencesFullyDecoded = !ContainsManagedReferenceRecoveryMarker(recoveredReferences);
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
                    recoveredManagedReferencesFullyDecoded ? null : builtInTypeTreeException,
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
                            { "status", recoveredManagedReferencesFullyDecoded ? "fullyDecoded" : "heuristic" },
                            { "source", partialTypeTreeSourceLabel ?? typeTreeSource },
                            { "bytesReadBeforeRecovery", partialTypeTreeBytesRead },
                            { "expectedRidCount", expectedManagedReferenceRids?.Count ?? 0 },
                        };
                        if (!recoveredManagedReferencesFullyDecoded)
                        {
                            recovery["decodeError"] = $"{partialTypeTreeException.GetType().Name}: {partialTypeTreeException.Message}";
                        }
                        if (partialTypeTreeStoppedAt != null && !recoveredManagedReferencesFullyDecoded)
                        {
                            recovery["stoppedAt"] = partialTypeTreeStoppedAt;
                        }
                        if (recoveredManagedReferences?["RefIds"] is ICollection recoveredRefIds)
                        {
                            recovery["recoveredRidCount"] = recoveredRefIds.Count;
                        }
                        meta["managedReferencesRegistryRecovered"] = true;
                        meta["managedReferencesRegistryFullyDecoded"] = recoveredManagedReferencesFullyDecoded;
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
                { "version", version },
                { "count", count },
                { "RefIds", entries },
            };
            if (headers.Any(header => !header.IsNullSentinel && !IsStrongManagedReferenceHeader(header))
                || entries.Any(ContainsManagedReferenceRecoveryMarker))
            {
                references["$heuristic"] = true;
                references["stringHintLimitPerReference"] = MaxHeuristicStringHintsPerReference;
                references["stringHintLimitPerObject"] = MaxHeuristicStringHintsPerObject;
                references["ridLinkLimitPerReference"] = MaxHeuristicRidLinksPerReference;
                references["ridLinkLimitPerObject"] = MaxHeuristicRidLinksPerObject;
            }
            else
            {
                references["$decoded"] = true;
            }
            return true;
        }

        private static bool ContainsManagedReferenceRecoveryMarker(object value)
        {
            if (value == null)
            {
                return false;
            }

            if (value is OrderedDictionary dictionary)
            {
                if ((dictionary.Contains("$heuristic") && dictionary["$heuristic"] is bool heuristic && heuristic)
                    || (dictionary.Contains("$unparsed") && dictionary["$unparsed"] is bool unparsed && unparsed)
                    || (dictionary.Contains("$partial") && dictionary["$partial"] is bool partial && partial))
                {
                    return true;
                }

                foreach (DictionaryEntry entry in dictionary)
                {
                    if (ContainsManagedReferenceRecoveryMarker(entry.Value))
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
                    if (ContainsManagedReferenceRecoveryMarker(item))
                    {
                        return true;
                    }
                }
            }

            return false;
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

            if (TryDecodeWeaponWallDisplayConfigData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeLuaManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeBattleMusicConfigManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeCoreGameplayManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeAIBehaviorManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                recoveredByRid,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeViewManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                recoveredByRid,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeUIManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeInteractiveBehitManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeSkeletalMorphMappingData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeSkeletalMorphShaderParamData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeSkeletalMorphShaderPropMappingData(
                header,
                rawData,
                offset,
                length,
                recoveredByRid,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeAnimationEventHandlerData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeStoryConfigManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeEnemySimpleComponentData(
                header,
                rawData,
                offset,
                length,
                recoveredByRid,
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

            if (TryDecodeDialogShortAnimActionData(
                header,
                rawData,
                offset,
                length,
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

            var rawWordHints = CollectHeuristicRawWordHints(rawData, offset, length, maxCount: 64);
            if (rawWordHints.Count > 0)
            {
                data["heuristicRawWordHints"] = rawWordHints;
            }

            return data;
        }

        private static List<OrderedDictionary> CollectHeuristicRawWordHints(
            byte[] rawData,
            int offset,
            int length,
            int maxCount
        )
        {
            var hints = new List<OrderedDictionary>();
            if (rawData == null
                || offset < 0
                || length <= 0
                || (length % 4) != 0
                || offset > rawData.Length
                || offset + length > rawData.Length)
            {
                return hints;
            }

            var reader = new ManagedReferencePayloadReader(rawData, offset, Math.Min(length, maxCount * 4));
            while (reader.Remaining >= 4)
            {
                hints.Add(BuildPayloadHash32(reader.ReadInt32("heuristicRawWordHints")));
            }
            return hints;
        }

        private sealed class ManagedReferencePayloadReader
        {
            private readonly byte[] rawData;
            private readonly int start;
            private readonly int end;

            public ManagedReferencePayloadReader(byte[] rawData, int offset, int length)
            {
                this.rawData = rawData ?? throw new InvalidDataException("payload bytes are missing");
                if (offset < 0 || length < 0 || offset > rawData.Length || offset + length > rawData.Length)
                {
                    throw new InvalidDataException("payload range is outside raw data");
                }
                start = offset;
                Position = offset;
                end = offset + length;
            }

            public byte[] RawData => rawData;

            public int Position { get; private set; }

            public int End => end;

            public int Remaining => end - Position;

            public void SetPosition(int position)
            {
                if (position < start || position > end)
                {
                    throw new InvalidDataException("payload reader position is outside payload bounds");
                }
                Position = position;
            }

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

            public string ReadAlignedUtf8String(string fieldName)
            {
                var stringOffset = Position;
                var length = ReadInt32(fieldName);
                if (length < 0 || length > 1024)
                {
                    throw new InvalidDataException($"invalid string length {length} in {fieldName}");
                }
                EnsureAvailable(length, fieldName);

                string value;
                try
                {
                    value = StrictUtf8Encoding.GetString(rawData, Position, length);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new InvalidDataException($"invalid UTF-8 bytes in {fieldName}", ex);
                }

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

        private static bool TryDecodeWeaponWallDisplayConfigData(
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
                || !string.Equals(header.ClassName, "WeaponWallDisplayConfig/WeaponDisplayConfig", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length <= 0
                || offset > rawData.Length
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
                    { "$inferred", true },
                    { "layout", "Beyond.Gameplay.WeaponWallDisplayConfig/WeaponDisplayConfig" },
                    { "offset", offset },
                    { "length", length },
                    { "weaponAppearEffectNames", ReadPayloadStringListFixed(reader, "weaponAppearEffectNames", ReadPayloadFixedCount(reader, "weaponAppearEffectNames.count", 3)) },
                    { "weaponDisappearEffectNames", ReadPayloadStringListFixed(reader, "weaponDisappearEffectNames", ReadPayloadFixedCount(reader, "weaponDisappearEffectNames.count", 3)) },
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

        private static bool TryDecodeLuaManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.AssemblyName, "Lua.Beyond", StringComparison.Ordinal)
                || !string.Equals(header.Namespace, "Beyond.Lua", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "LuaReference/RefExtraInfo", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length <= 0
                || offset > rawData.Length
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
                    { "$inferred", true },
                    { "layout", "Beyond.Lua.LuaReference/RefExtraInfo" },
                    { "offset", offset },
                    { "length", length },
                    { "customUIStyles", ReadLuaCustomUIStyleInfoList(reader, "customUIStyles", 64) },
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

        private static bool TryDecodeBattleMusicConfigManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                || !string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length <= 0
                || offset > rawData.Length
                || offset + length > rawData.Length)
            {
                return false;
            }

            try
            {
                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                if (string.Equals(header.ClassName, "BattleMusicConfig/PotentialEnemyRangeConfig/Circle", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "layout", "Beyond.Gameplay.BattleMusicConfig/PotentialEnemyRangeConfig/Circle" },
                        { "offset", offset },
                        { "length", length },
                        { "radius", reader.ReadFloat("radius") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.ClassName, "BattleMusicConfig/PotentialEnemyRangeConfig/Sector", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "layout", "Beyond.Gameplay.BattleMusicConfig/PotentialEnemyRangeConfig/Sector" },
                        { "offset", offset },
                        { "length", length },
                        { "radius", reader.ReadFloat("radius") },
                        { "angle", reader.ReadFloat("angle") },
                    };
                    reader.EnsureComplete();
                    return true;
                }
            }
            catch (InvalidDataException)
            {
                data = null;
                return false;
            }

            return false;
        }

        private static bool TryDecodeCoreGameplayManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                || !string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length <= 0
                || offset > rawData.Length
                || offset + length > rawData.Length)
            {
                return false;
            }

            try
            {
                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                if (string.Equals(header.ClassName, "ShowSquadTipsAction/Data", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.ShowSquadTipsAction/Data" },
                        { "offset", offset },
                        { "length", length },
                        { "isEnable", reader.ReadBool32("isEnable") },
                        { "priorityLevel", reader.ReadInt32("priorityLevel") },
                        { "priorityOffset", reader.ReadInt32("priorityOffset") },
                        { "serverActionIndex", reader.ReadInt32("serverActionIndex") },
                        { "textId", reader.ReadAlignedAsciiString("textId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }
            }
            catch (InvalidDataException)
            {
                data = null;
                return false;
            }

            return false;
        }

        private static bool TryDecodeAIBehaviorManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length < 0
                || offset > rawData.Length
                || offset + length > rawData.Length)
            {
                return false;
            }

            try
            {
                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyAttackBuildingGraph/EnemyAttackBuildingGraphDatta", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyAttackBuildingGraph/EnemyAttackBuildingGraphDatta" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "skillId", reader.ReadAlignedAsciiString("skillId") },
                        { "skillRange", reader.ReadFloat("skillRange") },
                        { "changeCooldown", reader.ReadBool32("changeCooldown") },
                        { "cooldown", reader.ReadFloat("cooldown") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NPCCoilbstEscapeBehavior/NPCCoilbstEscapeBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NPCCoilbstEscapeBehavior/NPCCoilbstEscapeBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "maxEscapeTime", reader.ReadFloat("maxEscapeTime") },
                        { "reachTolerance", reader.ReadFloat("reachTolerance") },
                        { "useDeco", reader.ReadBool32("useDeco") },
                        { "decoId", reader.ReadAlignedAsciiString("decoId") },
                        { "decoOffset", ReadPayloadVector3(reader, "decoOffset") },
                        { "decoMount", reader.ReadAlignedAsciiString("decoMount") },
                        { "performId", reader.ReadAlignedAsciiString("performId") },
                        { "hidePosKey", reader.ReadAlignedAsciiString("hidePosKey") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcRandomWalkBehavior/NpcRandomWalkBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcRandomWalkBehavior/NpcRandomWalkBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "radius", reader.ReadFloat("radius") },
                        { "angle", reader.ReadFloat("angle") },
                        { "idleTimeMin", reader.ReadFloat("idleTimeMin") },
                        { "idleTimeMax", reader.ReadFloat("idleTimeMax") },
                        { "distanceMin", reader.ReadFloat("distanceMin") },
                        { "distanceMax", reader.ReadFloat("distanceMax") },
                        { "tryCount", reader.ReadInt32("tryCount") },
                        { "idleWait", reader.ReadFloat("idleWait") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcHideBehavior/NpcHideBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcHideBehavior/NpcHideBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "resetRadius", reader.ReadFloat("resetRadius") },
                        { "fadeTime", reader.ReadFloat("fadeTime") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcBornBehavior/NpcBornBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcBornBehavior/NpcBornBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "bornAnim", reader.ReadAlignedAsciiString("bornAnim") },
                        { "trailingWord", BuildPayloadHash32(reader.ReadInt32("trailingWord")) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcRabbitGraph/NpcRabbitGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcRabbitGraph/NpcRabbitGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "bornTag", ReadPayloadGameplayTag(reader, "bornTag") },
                        { "idleTag", ReadPayloadGameplayTag(reader, "idleTag") },
                        { "escapeTag", ReadPayloadGameplayTag(reader, "escapeTag") },
                        { "hideTag", ReadPayloadGameplayTag(reader, "hideTag") },
                        { "escapeTriggerRadius", reader.ReadFloat("escapeTriggerRadius") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyBattleEventStimulus/EnemyBattleEventStimulusData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyBattleEventStimulus/EnemyBattleEventStimulusData" },
                        { "offset", offset },
                        { "length", length },
                        { "eventType", BuildPayloadHash32(reader.ReadInt32("eventType")) },
                        { "buffId", reader.ReadAlignedAsciiString("buffId") },
                        { "filterDamageDecorate", reader.ReadBool32("filterDamageDecorate") },
                        { "checkType", ReadPayloadNamedEnum32(reader, "checkType", new[] { "Exact", "HasAny", "HasAll", "ExceptAny", "ExceptAll" }) },
                        { "damageDecorateMask", BuildPayloadHash64(reader.ReadInt64("damageDecorateMask")) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyCastSkillResponse/EnemyCastSkillResponseData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyCastSkillResponse/EnemyCastSkillResponseData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "skillId", reader.ReadAlignedAsciiString("skillId") },
                        { "skillTarget", ReadPayloadNamedEnum32(reader, "skillTarget", new[] { "None", "Source", "Self", "Target", "MainChar" }) },
                        { "interruptSkill", reader.ReadBool32("interruptSkill") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyCheckBuffStackNum/EnemyCheckBuffStackNumData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyCheckBuffStackNum/EnemyCheckBuffStackNumData" },
                        { "offset", offset },
                        { "length", length },
                        { "buffId", reader.ReadAlignedAsciiString("buffId") },
                        { "compareType", BuildPayloadHash32(reader.ReadInt32("compareType")) },
                        { "layerCount", reader.ReadInt32("layerCount") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcFindMainCharBehavior/NpcFindMainCharBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcFindMainCharBehavior/NpcFindMainCharBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "radius", reader.ReadFloat("radius") },
                        { "angle", reader.ReadFloat("angle") },
                        { "height", reader.ReadFloat("height") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcFocusBehavior/NpcFocusBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcFocusBehavior/NpcFocusBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "focusBehavior", BuildPayloadHash32(reader.ReadInt32("focusBehavior")) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterFocusBehavior/CharacterFocusBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterFocusBehavior/CharacterFocusBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "focusBehavior", BuildPayloadHash32(reader.ReadInt32("focusBehavior")) },
                        { "focusTarget", ReadPayloadNamedEnum32(reader, "focusTarget", new[] { "MainChar", "MainCamera" }) },
                        { "autoLock", reader.ReadBool32("autoLock") },
                        { "focusInDis", reader.ReadFloat("focusInDis") },
                        { "focusOutDis", reader.ReadFloat("focusOutDis") },
                        { "focusDuration", reader.ReadFloat("focusDuration") },
                        { "duration", reader.ReadFloat("duration") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemySimpleAttackBehavior/EnemySimpleAttackBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemySimpleAttackBehavior/EnemySimpleAttackBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "skillId", reader.ReadAlignedAsciiString("skillId") },
                        { "skillRange", reader.ReadFloat("skillRange") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyResetPoiseResponse/EnemyResetPoiseResponseData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyResetPoiseResponse/EnemyResetPoiseResponseData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyCastSkillInRangeBehavior/EnemyCastSkillInRangeBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyCastSkillInRangeBehavior/EnemyCastSkillInRangeBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyCheckCanInterruptCurSkill/EnemyCheckCanInterruptCurSkillData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyCheckCanInterruptCurSkill/EnemyCheckCanInterruptCurSkillData" },
                        { "offset", offset },
                        { "length", length },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyFindTargetlBehavior/EnemyFindTargetlBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyFindTargetlBehavior/EnemyFindTargetlBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "forgetTime", reader.ReadFloat("forgetTime") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyHpChangeStimulus/EnemyHpChangeStimulusData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyHpChangeStimulus/EnemyHpChangeStimulusData" },
                        { "offset", offset },
                        { "length", length },
                        { "checkType", ReadPayloadNamedEnum32(reader, "checkType", new[] { "LT", "LE", "GT", "GE", "Equals" }) },
                        { "hpPct", reader.ReadFloat("hpPct") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyCheckHP/EnemyCheckHPData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyCheckHP/EnemyCheckHPData" },
                        { "offset", offset },
                        { "length", length },
                        { "targetType", ReadPayloadNamedEnum32(reader, "targetType", new[] { "Self", "Source" }) },
                        { "checkType", ReadPayloadNamedEnum32(reader, "checkType", new[] { "LT", "LE", "GT", "GE", "Equals" }) },
                        { "hpPct", reader.ReadFloat("hpPct") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyCheckInZeroPoise/EnemyCheckInZeroPoiseData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyCheckInZeroPoise/EnemyCheckInZeroPoiseData" },
                        { "offset", offset },
                        { "length", length },
                        { "invert", reader.ReadBool32("invert") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemySinglePatrolBehavior/EnemySinglePatrolBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemySinglePatrolBehavior/EnemySinglePatrolBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "enterRestart", reader.ReadBool32("enterRestart") },
                        { "moveMode", ReadPayloadNamedEnum32(reader, "moveMode", new[] { "NavMesh", "World", "TowerDefence" }) },
                        { "reachDis", reader.ReadFloat("reachDis") },
                        { "reachRunDis", reader.ReadFloat("reachRunDis") },
                        { "entityModeId", reader.ReadAlignedAsciiString("entityModeId") },
                        { "entityRunModeId", reader.ReadAlignedAsciiString("entityRunModeId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemySettlementBattleBehavior/EnemySettlementBattleBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemySettlementBattleBehavior/EnemySettlementBattleBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "skillData", ReadEnemySettlementAttackTargetSkillMap(reader) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcSpaceShipBehavior/NpcSpaceShipBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcSpaceShipBehavior/NpcSpaceShipBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "canvasGraph", ReadPayloadPPtr(reader, "canvasGraph") },
                        { "greetVirtualTag", ReadPayloadGameplayTag(reader, "greetVirtualTag") },
                        { "greetCD", reader.ReadFloat("greetCD") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && (string.Equals(header.ClassName, "CharacterSingleSwitchGraph/CharacterSingleSwitchGraphData", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "EnemySingleSwitchGraph/EnemySingleSwitchGraphData", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "NpcSingleSwitchGraph/NpcSingleSwitchGraphData", StringComparison.Ordinal)))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", $"Beyond.Gameplay.AI.{header.ClassName}" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "behavior", ReadPayloadGameplayTag(reader, "behavior") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterCheckBehavior/CharacterCheckBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterCheckBehavior/CharacterCheckBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "checkBehaviorType", ReadPayloadNamedEnum32(reader, "checkBehaviorType", new[] { "And", "Or" }) },
                        { "charBehaviorTags", ReadPayloadInvertGameplayTagList(reader, "charBehaviorTags", "behavior", 64) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyCheckGameplayTag/EnemyCheckGameplayTagData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyCheckGameplayTag/EnemyCheckGameplayTagData" },
                        { "offset", offset },
                        { "length", length },
                        { "targetType", ReadPayloadNamedEnum32(reader, "targetType", new[] { "Self", "Source" }) },
                        { "checkTagType", ReadPayloadNamedEnum32(reader, "checkTagType", new[] { "And", "Or" }) },
                        { "tagInfo", ReadPayloadInvertGameplayTagList(reader, "tagInfo", "tag", 64) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (IsKnownAIBaseIntervalOnlyManagedReferenceData(header))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", $"Beyond.Gameplay.AI.{header.ClassName}" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (IsKnownAIEmptyManagedReferenceData(header))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", $"Beyond.Gameplay.AI.{header.ClassName}" },
                        { "offset", offset },
                        { "length", length },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterIdleBehavior/CharacterIdleBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterIdleBehavior/CharacterIdleBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "stopMove", reader.ReadBool32("stopMove") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcBirdIdleBehavior/NpcBirdIdleBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcBirdIdleBehavior/NpcBirdIdleBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "searchRadius", reader.ReadFloat("searchRadius") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterCheckNeedDodgeAlert/CharacterCheckNeedDodgeAlertData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterCheckNeedDodgeAlert/CharacterCheckNeedDodgeAlertData" },
                        { "offset", offset },
                        { "length", length },
                        { "invert", reader.ReadBool32("invert") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterStayOutOfViewBehavior/CharacterStayOutOfViewBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterStayOutOfViewBehavior/CharacterStayOutOfViewBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "mode", ReadPayloadNamedEnum32(reader, "mode", new[] { "Bomb", "WaterDrone" }) },
                        { "step", reader.ReadFloat("step") },
                        { "tryCount", reader.ReadInt32("tryCount") },
                        { "dis", reader.ReadFloat("dis") },
                        { "xRange", ReadPayloadVector2(reader, "xRange") },
                        { "yRange", ReadPayloadVector2(reader, "yRange") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterSwitchFollowStateResponse/CharacterSwitchFollowStateResponseData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterSwitchFollowStateResponse/CharacterSwitchFollowStateResponseData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "state", BuildPayloadHash32(reader.ReadInt32("state")) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyLeaveBattleBehavior/EnemyLeaveBattleBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyLeaveBattleBehavior/EnemyLeaveBattleBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "animName", reader.ReadAlignedAsciiString("animName") },
                        { "waitTime", reader.ReadFloat("waitTime") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyGroupPatrolBehavior/EnemyGroupPatrolBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyGroupPatrolBehavior/EnemyGroupPatrolBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "clampRatio", reader.ReadFloat("clampRatio") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyCommonStimulus/EnemyCommonStimulusData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyCommonStimulus/EnemyCommonStimulusData" },
                        { "offset", offset },
                        { "length", length },
                        { "stimulusType", BuildPayloadHash32(reader.ReadInt32("stimulusType")) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyCheckAngleToSource/EnemyCheckAngleToSourceData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyCheckAngleToSource/EnemyCheckAngleToSourceData" },
                        { "offset", offset },
                        { "length", length },
                        { "revert", reader.ReadBool32("revert") },
                        { "angle", reader.ReadFloat("angle") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyCheckAIMarker/EnemyCheckAIMarkerData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyCheckAIMarker/EnemyCheckAIMarkerData" },
                        { "offset", offset },
                        { "length", length },
                        { "checkMarkerType", ReadPayloadNamedEnum32(reader, "checkMarkerType", new[] { "And", "Or" }) },
                        { "markerInfo", ReadPayloadInvertGameplayTagList(reader, "markerInfo", "marker", 64) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyFormationMoveBehavior/EnemyFormationMoveBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyFormationMoveBehavior/EnemyFormationMoveBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "timeout", reader.ReadFloat("timeout") },
                        { "soundName", reader.ReadAlignedAsciiString("soundName") },
                        { "delayEnd", ReadPayloadVector2(reader, "delayEnd") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyConfrontMoveBehavior/EnemyConfrontMoveBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyConfrontMoveBehavior/EnemyConfrontMoveBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "timeout", reader.ReadFloat("timeout") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterWaitBehavior/CharacterWaitBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterWaitBehavior/CharacterWaitBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "exitDis", reader.ReadFloat("exitDis") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterCastSkillBehavior/CharacterCastSkillBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterCastSkillBehavior/CharacterCastSkillBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "duration", reader.ReadFloat("duration") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterIdleDodgeBehavior/CharacterIdleDodgeBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterIdleDodgeBehavior/CharacterIdleDodgeBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "duration", reader.ReadFloat("duration") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcCommonAnimalGraph/NpcCommonAnimalGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcCommonAnimalGraph/NpcCommonAnimalGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "bornTag", ReadPayloadGameplayTag(reader, "bornTag") },
                        { "idleTag", ReadPayloadGameplayTag(reader, "idleTag") },
                        { "escapeTriggerRadius", reader.ReadFloat("escapeTriggerRadius") },
                        { "escapeEndRadius", reader.ReadFloat("escapeEndRadius") },
                        { "escapeTag", ReadPayloadGameplayTag(reader, "escapeTag") },
                        { "hideWhenEscaped", reader.ReadBool32("hideWhenEscaped") },
                        { "detectRadiusOnEscapeEnd", reader.ReadBool32("detectRadiusOnEscapeEnd") },
                        { "hideTag", ReadPayloadGameplayTag(reader, "hideTag") },
                        { "bornWhenHidden", reader.ReadBool32("bornWhenHidden") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcBirdGraph/NpcBirdGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcBirdGraph/NpcBirdGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "bornTag", ReadPayloadGameplayTag(reader, "bornTag") },
                        { "idleTag", ReadPayloadGameplayTag(reader, "idleTag") },
                        { "flyTag", ReadPayloadGameplayTag(reader, "flyTag") },
                        { "hideTag", ReadPayloadGameplayTag(reader, "hideTag") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcSnailGraph/NpcSnailGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcSnailGraph/NpcSnailGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "shrivelledTag", ReadPayloadGameplayTag(reader, "shrivelledTag") },
                        { "freeWalkTag", ReadPayloadGameplayTag(reader, "freeWalkTag") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyDodgeResponse/EnemyDodgeResponseData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyDodgeResponse/EnemyDodgeResponseData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "nearDis", reader.ReadFloat("nearDis") },
                        { "nearSkill", ReadPayloadStringList(reader, "nearSkill", 64) },
                        { "farSkill", ReadPayloadStringList(reader, "farSkill", 64) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterPatrolBehavior/CharacterPatrolBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterPatrolBehavior/CharacterPatrolBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "reachDis", reader.ReadFloat("reachDis") },
                        { "reachRunDis", reader.ReadFloat("reachRunDis") },
                        { "reachTeleportDis", reader.ReadFloat("reachTeleportDis") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyPlaySoundBehavior/EnemyPlaySoundBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyPlaySoundBehavior/EnemyPlaySoundBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "soundName", reader.ReadAlignedAsciiString("soundName") },
                        { "radius", reader.ReadFloat("radius") },
                        { "loop", reader.ReadBool32("loop") },
                        { "interval", reader.ReadFloat("interval") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcBirdFlyBehavior/NpcBirdFlyBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcBirdFlyBehavior/NpcBirdFlyBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "xzAngle", reader.ReadFloat("xzAngle") },
                        { "samplingNum", reader.ReadInt32("samplingNum") },
                        { "bestSamplingNum", reader.ReadInt32("bestSamplingNum") },
                        { "yAngle", reader.ReadFloat("yAngle") },
                        { "yAngleVariance", reader.ReadFloat("yAngleVariance") },
                        { "firstRayDis", reader.ReadFloat("firstRayDis") },
                        { "raycastRadius", reader.ReadFloat("raycastRadius") },
                        { "reboundCount", reader.ReadInt32("reboundCount") },
                        { "duration", reader.ReadFloat("duration") },
                        { "flyStartAnim", ReadPayloadGameplayTag(reader, "flyStartAnim") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcCommonStimulus/NpcCommonStimulusData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcCommonStimulus/NpcCommonStimulusData" },
                        { "offset", offset },
                        { "length", length },
                        { "stimulusType", BuildPayloadHash32(reader.ReadInt32("stimulusType")) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcCheckBehavior/NpcCheckBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcCheckBehavior/NpcCheckBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "checkBehaviorType", ReadPayloadNamedEnum32(reader, "checkBehaviorType", new[] { "And", "Or" }) },
                        { "npcBehaviorTags", ReadPayloadInvertGameplayTagList(reader, "npcBehaviorTags", "behavior", 64) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NPCRabbitEscapeBehavior/NPCRabbitEscapeBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NPCRabbitEscapeBehavior/NPCRabbitEscapeBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "calcTargetInterval", reader.ReadFloat("calcTargetInterval") },
                        { "angle", reader.ReadFloat("angle") },
                        { "duration", ReadPayloadVector2(reader, "duration") },
                        { "maxDistance", reader.ReadFloat("maxDistance") },
                        { "stepDistance", reader.ReadFloat("stepDistance") },
                        { "reachTolerance", reader.ReadFloat("reachTolerance") },
                        { "escapeMontageTag", ReadPayloadGameplayTag(reader, "escapeMontageTag") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcSlugToRigBodyBehavior/NpcSlugToRigBodyBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcSlugToRigBodyBehavior/NpcSlugToRigBodyBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "rigBodyObject", reader.ReadAlignedAsciiString("rigBodyObject") },
                        { "rigBodyInitVel", ReadPayloadVector3(reader, "rigBodyInitVel") },
                        { "rigBodyInitAngVel", ReadPayloadVector3(reader, "rigBodyInitAngVel") },
                        { "rigBodyMontageTag", ReadPayloadGameplayTag(reader, "rigBodyMontageTag") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcShrivelledBehavior/NpcShrivelledBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcShrivelledBehavior/NpcShrivelledBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "shrivelledAnim", ReadPayloadGameplayTag(reader, "shrivelledAnim") },
                        { "dropItemTag", ReadPayloadGameplayTag(reader, "dropItemTag") },
                    };
                    reader.EnsureComplete();
                    return true;
                }


                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterBattleActionStimulus/CharacterBattleActionStimulusData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterBattleActionStimulus/CharacterBattleActionStimulusData" },
                        { "offset", offset },
                        { "length", length },
                        { "eventType", BuildPayloadHash32(reader.ReadInt32("eventType")) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterCheckDodge/CharacterCheckDodgeData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterCheckDodge/CharacterCheckDodgeData" },
                        { "offset", offset },
                        { "length", length },
                        { "dodgeProp", BuildPayloadHash32(reader.ReadInt32("dodgeProp")) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterCommonStimulus/CharacterCommonStimulusData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterCommonStimulus/CharacterCommonStimulusData" },
                        { "offset", offset },
                        { "length", length },
                        { "stimulusType", BuildPayloadHash32(reader.ReadInt32("stimulusType")) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterDodgeResponse/CharacterDodgeResponseData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterDodgeResponse/CharacterDodgeResponseData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "dodgeCD", reader.ReadFloat("dodgeCD") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterCloseToHealTargetBehavior/CharacterCloseToHealTargetBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterCloseToHealTargetBehavior/CharacterCloseToHealTargetBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "closeToHealTargetTimeout", reader.ReadFloat("closeToHealTargetTimeout") },
                        { "closeToHealTargetStopDis", reader.ReadFloat("closeToHealTargetStopDis") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterFarmingFollowBehavior/CharacterFarmingFollowBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterFarmingFollowBehavior/CharacterFarmingFollowBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "duration", reader.ReadFloat("duration") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterNormalBattleBehavior/CharacterNormalBattleBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterNormalBattleBehavior/CharacterNormalBattleBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "escapeRadius", reader.ReadFloat("escapeRadius") },
                        { "escapeEnemyCount", reader.ReadFloat("escapeEnemyCount") },
                        { "escapeDis", reader.ReadFloat("escapeDis") },
                        { "attackDodgeDis", reader.ReadFloat("attackDodgeDis") },
                        { "attackDodgeAngle", reader.ReadFloat("attackDodgeAngle") },
                        { "attackDodgeCd", reader.ReadFloat("attackDodgeCd") },
                        { "rangeDodgeDis", reader.ReadFloat("rangeDodgeDis") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterSwitchBehaviorResponse/CharacterSwitchBehaviorResponseData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterSwitchBehaviorResponse/CharacterSwitchBehaviorResponseData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "behavior", ReadPayloadGameplayTag(reader, "behavior") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyCheckStringParam/EnemyCheckStringParamData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyCheckStringParam/EnemyCheckStringParamData" },
                        { "offset", offset },
                        { "length", length },
                        { "stringValue", reader.ReadAlignedAsciiString("stringValue") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterAttackResourceBehavior/CharacterAttackResourceBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterAttackResourceBehavior/CharacterAttackResourceBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "attackPQS", ReadPayloadPPtr(reader, "attackPQS") },
                        { "timeout", reader.ReadFloat("timeout") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterBattleCommandBehavior/CharacterBattleCommandBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterBattleCommandBehavior/CharacterBattleCommandBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "safeAreaPQS", ReadPayloadPPtr(reader, "safeAreaPQS") },
                        { "reactionDelay", ReadPayloadVector2(reader, "reactionDelay") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterBarkExploreBehavior/CharacterBarkExploreBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterBarkExploreBehavior/CharacterBarkExploreBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "gait", BuildPayloadHash32(reader.ReadInt32("gait")) },
                        { "talkId", reader.ReadAlignedAsciiString("talkId") },
                        { "helloTalkId", reader.ReadAlignedAsciiString("helloTalkId") },
                        { "callDis", reader.ReadFloat("callDis") },
                        { "callCD", ReadPayloadVector2(reader, "callCD") },
                        { "startMoveDis", reader.ReadFloat("startMoveDis") },
                        { "targetStartDis", reader.ReadFloat("targetStartDis") },
                        { "targetStopDis", reader.ReadFloat("targetStopDis") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterCheckSpIdle/CharacterCheckSpIdleData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterCheckSpIdle/CharacterCheckSpIdleData" },
                        { "offset", offset },
                        { "length", length },
                        { "revert", reader.ReadBool32("revert") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterFocusImportantBehavior/CharacterFocusImportantBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterFocusImportantBehavior/CharacterFocusImportantBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "walkDuration", reader.ReadFloat("walkDuration") },
                        { "exitRadius", reader.ReadFloat("exitRadius") },
                        { "returnWalkDuration", reader.ReadFloat("returnWalkDuration") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterFarmGraph/CharacterFarmGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterFarmGraph/CharacterFarmGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "farmTag", ReadPayloadGameplayTag(reader, "farmTag") },
                        { "attackResourceTag", ReadPayloadGameplayTag(reader, "attackResourceTag") },
                        { "followTag", ReadPayloadGameplayTag(reader, "followTag") },
                        { "teleportTag", ReadPayloadGameplayTag(reader, "teleportTag") },
                        { "forceTeleportTag", ReadPayloadGameplayTag(reader, "forceTeleportTag") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcBattleConfrontBehavior/NpcBattleConfrontBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcBattleConfrontBehavior/NpcBattleConfrontBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "animTag", ReadPayloadGameplayTag(reader, "animTag") },
                        { "needRot", reader.ReadBool32("needRot") },
                        { "randomDelay", ReadPayloadVector2(reader, "randomDelay") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcCleanPackAnimalBehavior/NpcCleanPackAnimalBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcCleanPackAnimalBehavior/NpcCleanPackAnimalBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "happyAnimTag", ReadPayloadGameplayTag(reader, "happyAnimTag") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcFecesPackAnimalBehavior/NpcFecesPackAnimalBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcFecesPackAnimalBehavior/NpcFecesPackAnimalBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "performId", reader.ReadAlignedAsciiString("performId") },
                        { "performIdWhenCantFeces", reader.ReadAlignedAsciiString("performIdWhenCantFeces") },
                        { "failedToast", reader.ReadAlignedAsciiString("failedToast") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcLeaveBattleBehavior/NpcLeaveBattleBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcLeaveBattleBehavior/NpcLeaveBattleBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "randomDelay", ReadPayloadVector2(reader, "randomDelay") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcSlugBehavior/NpcSlugBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcSlugBehavior/NpcSlugBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "lieAnimTag", ReadPayloadGameplayTag(reader, "lieAnimTag") },
                        { "hitAnimTag", ReadPayloadGameplayTag(reader, "hitAnimTag") },
                        { "duration", reader.ReadFloat("duration") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcSlugLieBehavior/NpcSlugLieBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcSlugLieBehavior/NpcSlugLieBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "lieAnimTag", ReadPayloadGameplayTag(reader, "lieAnimTag") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcSlugGraph/NpcSlugGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcSlugGraph/NpcSlugGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "idleTag", ReadPayloadGameplayTag(reader, "idleTag") },
                        { "patrolTag", ReadPayloadGameplayTag(reader, "patrolTag") },
                        { "idleShowTag", ReadPayloadGameplayTag(reader, "idleShowTag") },
                        { "slugTag", ReadPayloadGameplayTag(reader, "slugTag") },
                        { "slugLieTag", ReadPayloadGameplayTag(reader, "slugLieTag") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcSpaceShipGraph/NpcSpaceShipGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcSpaceShipGraph/NpcSpaceShipGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "idleTag", ReadPayloadGameplayTag(reader, "idleTag") },
                        { "hallCommonTag", ReadPayloadGameplayTag(reader, "hallCommonTag") },
                        { "hallSeatTag", ReadPayloadGameplayTag(reader, "hallSeatTag") },
                        { "controlCenterTag", ReadPayloadGameplayTag(reader, "controlCenterTag") },
                        { "manufacturingStationTag", ReadPayloadGameplayTag(reader, "manufacturingStationTag") },
                        { "growCabinTag", ReadPayloadGameplayTag(reader, "growCabinTag") },
                        { "guestRoomTag", ReadPayloadGameplayTag(reader, "guestRoomTag") },
                        { "leaveTag", ReadPayloadGameplayTag(reader, "leaveTag") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcSpaceShipLeaveBehavior/NpcSpaceShipLeaveBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcSpaceShipLeaveBehavior/NpcSpaceShipLeaveBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "greetVirtualTag", ReadPayloadGameplayTag(reader, "greetVirtualTag") },
                        { "greetCD", reader.ReadFloat("greetCD") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcSpaceShipWaitBehavior/NpcSpaceShipWaitBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcSpaceShipWaitBehavior/NpcSpaceShipWaitBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "waitTime", ReadPayloadVector2(reader, "waitTime") },
                    };
                    reader.EnsureComplete();
                    return true;
                }
                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterPickupBehavior/CharacterPickupBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterPickupBehavior/CharacterPickupBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "skillId", reader.ReadAlignedAsciiString("skillId") },
                        { "pickupTag", ReadPayloadGameplayTag(reader, "pickupTag") },
                        { "pickupInteractId", ReadPayloadStringList(reader, "pickupInteractId", 16) },
                        { "startMoveDis", reader.ReadFloat("startMoveDis") },
                        { "stopMoveDis", reader.ReadFloat("stopMoveDis") },
                        { "moveTimeout", reader.ReadFloat("moveTimeout") },
                        { "sprintDis", reader.ReadFloat("sprintDis") },
                        { "extraRadius", reader.ReadFloat("extraRadius") },
                        { "successEmoji", reader.ReadAlignedAsciiString("successEmoji") },
                        { "fullEmoji", reader.ReadAlignedAsciiString("fullEmoji") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterRepatriateBehavior/CharacterRepatriateBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterRepatriateBehavior/CharacterRepatriateBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "performId", reader.ReadAlignedAsciiString("performId") },
                        { "duration", reader.ReadFloat("duration") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterSeatBehavior/CharacterSeatBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterSeatBehavior/CharacterSeatBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "stopDis", reader.ReadFloat("stopDis") },
                        { "walkDis", reader.ReadFloat("walkDis") },
                        { "performId", reader.ReadAlignedAsciiString("performId") },
                        { "delay", ReadPayloadVector2(reader, "delay") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterSettlementBattleBehavior/CharacterSettlementBattleBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterSettlementBattleBehavior/CharacterSettlementBattleBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "attackDodgeDis", reader.ReadFloat("attackDodgeDis") },
                        { "attackDodgeAngle", reader.ReadFloat("attackDodgeAngle") },
                        { "attackDodgeCd", reader.ReadFloat("attackDodgeCd") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyDogEscapeBehavior/EnemyDogEscapeBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyDogEscapeBehavior/EnemyDogEscapeBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "skillId", reader.ReadAlignedAsciiString("skillId") },
                        { "skillCastChance", reader.ReadFloat("skillCastChance") },
                        { "calcTargetInterval", reader.ReadFloat("calcTargetInterval") },
                        { "forgetTargetTime", reader.ReadFloat("forgetTargetTime") },
                        { "escapeAngleRange", reader.ReadFloat("escapeAngleRange") },
                        { "escapeStepDis", reader.ReadFloat("escapeStepDis") },
                        { "escapeArrivalDis", reader.ReadFloat("escapeArrivalDis") },
                        { "escapeMaxRadius", reader.ReadFloat("escapeMaxRadius") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyDogGraph/EnemyDogGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyDogGraph/EnemyDogGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "singlePatrol", ReadPayloadGameplayTag(reader, "singlePatrol") },
                        { "groupPatrol", ReadPayloadGameplayTag(reader, "groupPatrol") },
                        { "randomWalk", ReadPayloadGameplayTag(reader, "randomWalk") },
                        { "escape", ReadPayloadGameplayTag(reader, "escape") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyEnvConfrontBehavior/EnemyEnvConfrontBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyEnvConfrontBehavior/EnemyEnvConfrontBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "idleBreakMinTime", reader.ReadFloat("idleBreakMinTime") },
                        { "idleBreakMaxTime", reader.ReadFloat("idleBreakMaxTime") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyLeaveBattleGraph/EnemyLeaveBattleGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyLeaveBattleGraph/EnemyLeaveBattleGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "leaveTag", ReadPayloadGameplayTag(reader, "leaveTag") },
                        { "teleportTag", ReadPayloadGameplayTag(reader, "teleportTag") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyMoveToValidPosBehavior/EnemyMoveToValidPosBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyMoveToValidPosBehavior/EnemyMoveToValidPosBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "radius", reader.ReadFloat("radius") },
                        { "stopDis", reader.ReadFloat("stopDis") },
                        { "timeout", reader.ReadFloat("timeout") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyRandomWalkBehavior/EnemyRandomWalkBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyRandomWalkBehavior/EnemyRandomWalkBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "entityModeId", reader.ReadAlignedAsciiString("entityModeId") },
                        { "radius", reader.ReadFloat("radius") },
                        { "angle", reader.ReadFloat("angle") },
                        { "idleTime", ReadPayloadVector2(reader, "idleTime") },
                        { "distance", ReadPayloadVector2(reader, "distance") },
                        { "tryCount", reader.ReadInt32("tryCount") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyScriptedMoveGraph/EnemyScriptedMoveGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyScriptedMoveGraph/EnemyScriptedMoveGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "checkInEnemyRadius", reader.ReadFloat("checkInEnemyRadius") },
                        { "checkInEnemyCount", reader.ReadInt32("checkInEnemyCount") },
                        { "checkOutEnemyRadius", reader.ReadFloat("checkOutEnemyRadius") },
                        { "checkOutEnemyCount", reader.ReadInt32("checkOutEnemyCount") },
                        { "checkInMainCharRadius", reader.ReadFloat("checkInMainCharRadius") },
                        { "checkOutMainCharRadius", reader.ReadFloat("checkOutMainCharRadius") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemySetBlackboardResponse/EnemySetBlackboardResponseData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemySetBlackboardResponse/EnemySetBlackboardResponseData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "key", reader.ReadAlignedAsciiString("key") },
                        { "global", reader.ReadBool32("global") },
                        { "valueType", BuildPayloadHash32(reader.ReadInt32("valueType")) },
                        { "boolValue", reader.ReadBool32("boolValue") },
                        { "intValue", reader.ReadInt32("intValue") },
                        { "floatValue", reader.ReadFloat("floatValue") },
                        { "stringValue", reader.ReadAlignedAsciiString("stringValue") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyVigilanceBehavior/EnemyVigilanceBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyVigilanceBehavior/EnemyVigilanceBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "extraWaitTime", reader.ReadFloat("extraWaitTime") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NPCCoilbstSitBehavior/NPCCoilbstSitBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NPCCoilbstSitBehavior/NPCCoilbstSitBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "sitMontageTag", ReadPayloadGameplayTag(reader, "sitMontageTag") },
                        { "sitInterval", ReadPayloadVector2(reader, "sitInterval") },
                        { "sitRandomMontageTag", ReadPayloadGameplayTagList(reader, "sitRandomMontageTag", 16) },
                        { "randomInterval", ReadPayloadVector2(reader, "randomInterval") },
                        { "sitMontageEndTag", ReadPayloadGameplayTag(reader, "sitMontageEndTag") },
                        { "rootmotionHeight", reader.ReadFloat("rootmotionHeight") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NPCCommonAnimalEscapeBehavior/NPCCommonAnimalEscapeBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NPCCommonAnimalEscapeBehavior/NPCCommonAnimalEscapeBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "escapeMovementStyle", BuildPayloadHash32(reader.ReadInt32("escapeMovementStyle")) },
                        { "calcTargetInterval", reader.ReadFloat("calcTargetInterval") },
                        { "angle", reader.ReadFloat("angle") },
                        { "duration", ReadPayloadVector2(reader, "duration") },
                        { "maxDistance", reader.ReadFloat("maxDistance") },
                        { "stepDistance", reader.ReadFloat("stepDistance") },
                        { "reachTolerance", reader.ReadFloat("reachTolerance") },
                        { "shouldPlayEscapeMontage", reader.ReadBool32("shouldPlayEscapeMontage") },
                        { "escapeMontageTag", ReadPayloadGameplayTag(reader, "escapeMontageTag") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NPCCommonAnimalLoopMontageBehavior/NPCCommonAnimalLoopMontageBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NPCCommonAnimalLoopMontageBehavior/NPCCommonAnimalLoopMontageBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "loopMontageTag", ReadPayloadGameplayTag(reader, "loopMontageTag") },
                        { "duration", reader.ReadFloat("duration") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NPCEnvConfrontBehavior/NPCEnvConfrontBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NPCEnvConfrontBehavior/NPCEnvConfrontBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "animTag", ReadPayloadGameplayTag(reader, "animTag") },
                        { "needRot", reader.ReadBool32("needRot") },
                        { "randomDelay", ReadPayloadVector2(reader, "randomDelay") },
                        { "idleBreakMinTime", reader.ReadFloat("idleBreakMinTime") },
                        { "idleBreakMaxTime", reader.ReadFloat("idleBreakMaxTime") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NPCLotusFrogEscapeBehavior/NPCLotusFrogEscapeBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NPCLotusFrogEscapeBehavior/NPCLotusFrogEscapeBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "duration", reader.ReadFloat("duration") },
                        { "escapeMontageTag", ReadPayloadGameplayTag(reader, "escapeMontageTag") },
                        { "backwardCorrection", reader.ReadFloat("backwardCorrection") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NPCPlayanimationBehavior/NPCPlayanimationBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NPCPlayanimationBehavior/NPCPlayanimationBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "animTag", ReadPayloadGameplayTag(reader, "animTag") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NPCPlayanimationHideBehavior/NPCPlayanimationHideBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NPCPlayanimationHideBehavior/NPCPlayanimationHideBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "animTag", ReadPayloadGameplayTag(reader, "animTag") },
                        { "fadeTime", reader.ReadFloat("fadeTime") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NPCResetToBornBehavior/NPCResetToBornBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NPCResetToBornBehavior/NPCResetToBornBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "disapearAnimTag", ReadPayloadGameplayTag(reader, "disapearAnimTag") },
                        { "appearAnimTag", ReadPayloadGameplayTag(reader, "appearAnimTag") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyCheckTag/EnemyCheckTagData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyCheckTag/EnemyCheckTagData" },
                        { "offset", offset },
                        { "length", length },
                        { "targetType", ReadPayloadNamedEnum32(reader, "targetType", new[] { "Self", "Source" }) },
                        { "checkTagType", ReadPayloadNamedEnum32(reader, "checkTagType", new[] { "And", "Or" }) },
                        { "tagInfo", ReadEnemyCheckTagInfoList(reader, "tagInfo", 16) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterFarmingBehavior/CharacterFarmingBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterFarmingBehavior/CharacterFarmingBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "stopDistance", reader.ReadFloat("stopDistance") },
                        { "walkDis", reader.ReadFloat("walkDis") },
                        { "walkRunDis", reader.ReadFloat("walkRunDis") },
                        { "runSprintDis", reader.ReadFloat("runSprintDis") },
                        { "relaxExTime", ReadPayloadVector2(reader, "relaxExTime") },
                        { "moveTimeOut", reader.ReadFloat("moveTimeOut") },
                        { "farmTimeOut", reader.ReadFloat("farmTimeOut") },
                        { "farmInfo", ReadPayloadIntStringDictionary(reader, "farmInfo", "farmType", "performId", 16) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NpcDailyGraph/NpcDailyGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NpcDailyGraph/NpcDailyGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "idleTag", ReadPayloadGameplayTag(reader, "idleTag") },
                        { "patrolTag", ReadPayloadGameplayTag(reader, "patrolTag") },
                        { "attractPointTag", ReadPayloadGameplayTag(reader, "attractPointTag") },
                        { "passiveAttractPointTag", ReadPayloadGameplayTag(reader, "passiveAttractPointTag") },
                        { "idleShowTag", ReadPayloadGameplayTag(reader, "idleShowTag") },
                        { "npcSR", ReadNpcStimulusResponseList(reader, "npcSR.cfg", 16, recoveredByRid) },
                    };
                    reader.EnsureComplete();
                    return true;
                }
            }
            catch (InvalidDataException)
            {
                data = null;
                return false;
            }

            return false;
        }

        private static bool IsKnownAIBaseIntervalOnlyManagedReferenceData(ManagedReferenceHeader header)
        {
            if (header == null || !string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal))
            {
                return false;
            }

            return string.Equals(header.ClassName, "NpcIdleBehavior/NpcIdleBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "NpcPatrolBehavior/NpcPatrolBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterNormalFollowBehavior/CharacterNormalFollowBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterDummyBehavior/CharacterDummyBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterWaitToCloseToHealTargetResponse/CharacterWaitToCloseToHealTargetResponseData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterIdleSpBehavior/CharacterIdleSpBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterCooperateGraph/CharacterCooperateGraphData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterTeleportBehavior/CharacterTeleportBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterMainBehavior/CharacterMainBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "EnemyImmobilizedBehavior/EnemyImmobilizedBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "EnemyBattleIdleBehavior/EnemyBattleIdleBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "EnemySimpleCastSequenceSkillBehavior/EnemySimpleCastSequenceSkillBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "EnemyPauseBehavior/EnemyPauseBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "EnemyCastSequenceSkillBehavior/EnemyCastSequenceSkillBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterBattleJumpBehavior/CharacterBattleJumpBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterForceTeleportBehavior/CharacterForceTeleportBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterJumpResponse/CharacterJumpResponseData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterSkillHoldBehavior/CharacterSkillHoldBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterCastSkillGraph/CharacterCastSkillGraphData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterEvadeBehavior/CharacterEvadeBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "NpcAttractBehavior/NpcAttractBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "NpcPassiveAttractBehavior/NpcPassiveAttractBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "NpcBattleConfrontResponse/NpcBattleConfrontResponseData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "NpcEnvConfrontResponse/NpcEnvConfrontResponseData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "NpcSettlementBehavior/NpcSettlementBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterPlungingAttackBehavior/CharacterPlungingAttackBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterSummonTeamBehavior/CharacterSummonTeamBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterHealTargetBehavior/CharacterHealTargetBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "EnemyEnvConfrontResponse/EnemyEnvConfrontResponseData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "EnemyIdleBehavior/EnemyIdleBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "EnemyLeaveBattleTeleportBehavior/EnemyLeaveBattleTeleportBehaviorData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "EnemyMainCharExceedRange/EnemyMainCharExceedRangeData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "EnemyMoveToOuterRadius/EnemyMoveToOuterRadiusData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "EnemyTargetInProximity/EnemyTargetInProximityData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "NpcIdleShowBehavior/NpcIdleShowBehaviorData", StringComparison.Ordinal);
        }

        private static bool IsKnownAIEmptyManagedReferenceData(ManagedReferenceHeader header)
        {
            if (header == null || !string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal))
            {
                return false;
            }

            return string.Equals(header.ClassName, "CharacterCloseToHealTargetStimulus/CharacterCloseToHealTargetStimulusData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterHealTargetStimulus/CharacterHealTargetStimulusData", StringComparison.Ordinal)
                || string.Equals(header.ClassName, "CharacterJumpStimulus/CharacterJumpStimulusData", StringComparison.Ordinal);
        }

        private static bool TryDecodeViewManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                || (!string.Equals(header.Namespace, "Beyond.Gameplay.View", StringComparison.Ordinal)
                    && !string.Equals(header.Namespace, "Beyond.Gameplay.View.Animation", StringComparison.Ordinal))
                || rawData == null
                || offset < 0
                || length <= 0
                || offset > rawData.Length
                || offset + length > rawData.Length)
            {
                return false;
            }

            try
            {
                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                if (string.Equals(header.ClassName, "ModelViewStateControllerBase/AnimationParamChangePack", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.View.ModelViewStateControllerBase/AnimationParamChangePack" },
                        { "offset", offset },
                        { "length", length },
                        { "useNewMVSC", reader.ReadBool32("useNewMVSC") },
                        { "paramName", reader.ReadAlignedAsciiString("paramName") },
                        { "paramType", ReadPayloadNamedEnum32(reader, "paramType", new[] { "Float", "Int", "Bool", "Trigger" }) },
                        { "boolValue", reader.ReadBool32("boolValue") },
                        { "floatValue", reader.ReadFloat("floatValue") },
                        { "intValue", reader.ReadInt32("intValue") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.ClassName, "ModelViewStateControllerBase/AnimationPackSetState", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.View.ModelViewStateControllerBase/AnimationPackSetState" },
                        { "offset", offset },
                        { "length", length },
                        { "stateName", reader.ReadAlignedAsciiString("stateName") },
                        { "layer", reader.ReadInt32("layer") },
                        { "normalizedTime", reader.ReadFloat("normalizedTime") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.View.Animation", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "WeaponAnimatorMono/PlayFollowEffect", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.View.Animation.WeaponAnimatorMono/PlayFollowEffect" },
                        { "offset", offset },
                        { "length", length },
                        { "effectName", reader.ReadAlignedAsciiString("effectName") },
                        { "restartIfExist", reader.ReadBool32("restartIfExist") },
                        { "mountPoint", BuildPayloadHash32(reader.ReadInt32("mountPoint")) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.View.Animation", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "WeaponAnimatorMono/StateActionEntry", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.View.Animation.WeaponAnimatorMono/StateActionEntry" },
                        { "offset", offset },
                        { "length", length },
                        { "actionsOnEnter", ReadPayloadRidLinkList(reader, "actionsOnEnter", 32, recoveredByRid) },
                        { "actionsOnExit", ReadPayloadRidLinkList(reader, "actionsOnExit", 32, recoveredByRid) },
                    };
                    reader.EnsureComplete();
                    return true;
                }
            }
            catch (InvalidDataException)
            {
                data = null;
                return false;
            }

            return false;
        }

        private static bool TryDecodeUIManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.AssemblyName, "UI.Gameplay.Beyond", StringComparison.Ordinal)
                || !string.Equals(header.Namespace, "Beyond.UI", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "UILevelMapCrane/CraneSpritePath", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length <= 0
                || offset > rawData.Length
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
                    { "$inferred", true },
                    { "layout", "Beyond.UI.UILevelMapCrane/CraneSpritePath" },
                    { "offset", offset },
                    { "length", length },
                    { "spritePath", reader.ReadAlignedAsciiString("spritePath") },
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

        private static bool TryDecodeInteractiveBehitManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                || !string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "InteractiveBehitPerformSetting/FightBehitBase", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length <= 0
                || offset > rawData.Length
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
                    { "$inferred", true },
                    { "layout", "Beyond.Gameplay.InteractiveBehitPerformSetting/FightBehitBase" },
                    { "offset", offset },
                    { "length", length },
                    { "cameraShake", ReadPayloadNamedEnum32(reader, "cameraShake", new[] { "Base", "Normal", "HighLevel" }) },
                    { "stopFrame", ReadPayloadNamedEnum32(reader, "stopFrame", new[] { "Base", "Normal", "HighLevel" }) },
                    { "entityAnim", ReadPayloadNamedEnum32(reader, "entityAnim", new[] { "Base", "Normal", "HighLevel" }) },
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
        private static bool TryDecodeSkeletalMorphMappingData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "SkeletalMorphMappingData", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length < 20
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
                    { "$inferred", true },
                    { "layout", "Beyond.Gameplay.Core.SkeletalMorphMappingData" },
                    { "offset", offset },
                    { "length", length },
                    { "id", reader.ReadInt32("id") },
                    { "nameHash", BuildPayloadHash32(reader.ReadInt32("nameHash")) },
                    { "tagHash", BuildPayloadHash32(reader.ReadInt32("tagHash")) },
                    { "partType", reader.ReadInt32("partType") },
                    { "bones", ReadPayloadObjectList(reader, "bones", 64, ReadSkeletalMorphBoneMappingData) },
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

        private static bool TryDecodeSkeletalMorphShaderParamData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length <= 0
                || offset + length > rawData.Length)
            {
                return false;
            }

            try
            {
                if (string.Equals(header.ClassName, "SkMorphShaderParamFloat", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.SkMorphShaderParamFloat" },
                        { "offset", offset },
                        { "length", length },
                        { "name", reader.ReadAlignedAsciiString("name") },
                        { "channelIndex", reader.ReadInt32("channelIndex") },
                        { "value", reader.ReadFloat("value") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.ClassName, "SkMorphShaderParamVector4", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.SkMorphShaderParamVector4" },
                        { "offset", offset },
                        { "length", length },
                        { "name", reader.ReadAlignedAsciiString("name") },
                        { "channelIndex", reader.ReadInt32("channelIndex") },
                        { "value", ReadPayloadVector4(reader, "value") },
                    };
                    reader.EnsureComplete();
                    return true;
                }
            }
            catch (InvalidDataException)
            {
                data = null;
                return false;
            }

            return false;
        }

        private static bool TryDecodeSkeletalMorphShaderPropMappingData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || !string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "SkeletalMorphShaderPropMappingData", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length != 32
                || offset + length > rawData.Length)
            {
                return false;
            }

            try
            {
                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                var paramRidOffset = offset + 24;
                var paramRid = default(long);
                data = new OrderedDictionary
                {
                    { "$decoded", true },
                    { "$inferred", true },
                    { "layout", "Beyond.Gameplay.Core.SkeletalMorphShaderPropMappingData" },
                    { "offset", offset },
                    { "length", length },
                    { "id", reader.ReadInt32("id") },
                    { "nameHash", BuildPayloadHash32(reader.ReadInt32("nameHash")) },
                    { "tagHash", BuildPayloadHash32(reader.ReadInt32("tagHash")) },
                    { "partType", reader.ReadInt32("partType") },
                    { "paramSetIndex", reader.ReadInt32("paramSetIndex") },
                    { "componentIndex", reader.ReadInt32("componentIndex") },
                };
                paramRid = reader.ReadInt64("shaderParamRid");
                data["shaderParam"] = BuildManagedReferenceRidValue(paramRid, recoveredByRid, paramRidOffset);
                reader.EnsureComplete();
                return true;
            }
            catch (InvalidDataException)
            {
                data = null;
                return false;
            }
        }

        private static bool TryDecodeAnimationEventHandlerData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || rawData == null
                || offset < 0
                || length != 4
                || offset + length > rawData.Length
                || !string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                || !string.Equals(header.Namespace, "Beyond.Gameplay.View.Animation", StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(header.ClassName, "FastAnimationEventHandler", StringComparison.Ordinal)
                && !string.Equals(header.ClassName, "CharPerformHandler", StringComparison.Ordinal)
                && !string.Equals(header.ClassName, "FootStepHandler", StringComparison.Ordinal)
                && !string.Equals(header.ClassName, "PostAudioHandler", StringComparison.Ordinal)
                && !string.Equals(header.ClassName, "WeaponVisibleHandler", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                data = new OrderedDictionary
                {
                    { "$decoded", true },
                    { "layout", $"Beyond.Gameplay.View.Animation.{header.ClassName}" },
                    { "baseLayout", "Beyond.Gameplay.View.Animation.FastAnimationEventHandler" },
                    { "offset", offset },
                    { "length", length },
                    { "_weightThreshold", reader.ReadFloat("_weightThreshold") },
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

        private static bool TryDecodeStoryConfigManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || rawData == null
                || offset < 0
                || length < 0
                || offset + length > rawData.Length
                || !string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                || !string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                if (string.Equals(header.ClassName, "CameraTrackData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "layout", "Beyond.Gameplay.CameraTrackData" },
                        { "offset", offset },
                        { "length", length },
                        { "modeDesc", reader.ReadAlignedUtf8String("modeDesc") },
                        { "camResName", reader.ReadAlignedUtf8String("camResName") },
                        { "useTarget", reader.ReadBool32("useTarget") },
                        { "mountPoint", reader.ReadInt32("mountPoint") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.ClassName, "I18NSubtitleAudioBean", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "layout", "Beyond.Gameplay.I18NSubtitleAudioBean" },
                        { "offset", offset },
                        { "length", length },
                        { "defaultPlayable", ReadPayloadPPtr(reader, "defaultPlayable") },
                        { "audioLangKey2SubtitleTrack", ReadPayloadIntPPtrDictionary(reader, "audioLangKey2SubtitleTrack", "audioLangKey", "subtitleTrack", 16) },
                    };
                    reader.EnsureComplete();
                    return true;
                }
            }
            catch (InvalidDataException)
            {
                data = null;
                return false;
            }

            return false;
        }

        private static bool TryDecodeEnemySimpleComponentData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null
                || rawData == null
                || offset < 0
                || length < 0
                || offset + length > rawData.Length)
            {
                return false;
            }

            try
            {
                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyTemplateData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    var modelKey = reader.ReadAlignedAsciiString("modelKey");
                    if (!TryFindEnemyTemplateTail(rawData, reader.Position, offset + length, out var postModelOffset))
                    {
                        throw new InvalidDataException("EnemyTemplateData tail layout was not recognized");
                    }
                    if (((postModelOffset - reader.Position) % 4) != 0)
                    {
                        throw new InvalidDataException("EnemyTemplateData attributes block is not word-aligned");
                    }

                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.EnemyTemplateData" },
                        { "offset", offset },
                        { "length", length },
                        { "modelKey", modelKey },
                        { "attributesDataRawWords", ReadPayloadRawInt32Words(reader, "attributesDataRawWords", (postModelOffset - reader.Position) / 4) },
                        { "postModelKey", reader.ReadAlignedAsciiString("postModelKey") },
                        { "rank", ReadPayloadEnum32(reader, "rank", 0, 8) },
                        { "subRank", ReadPayloadEnum32(reader, "subRank", 0, 16) },
                        { "dontBlockCharge", reader.ReadBool32("dontBlockCharge") },
                        { "animConfigPath", reader.ReadAlignedAsciiString("animConfigPath") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "AbilityEntityTemplateData", StringComparison.Ordinal))
                {
                    data = BuildPartialAbilityEntityPayloadData(
                        rawData,
                        offset,
                        length,
                        "Beyond.Gameplay.AbilityEntityTemplateData",
                        "metadata field order is known, but BB field-meta blocks, surrounding/follow configs, skillDataBundle, model/nav/physical/interactive tails are still preserved as raw words",
                        new[]
                        {
                            "maxStackingCnt", "maxStackingCntBB", "lifeType", "duration", "durationBB",
                            "maxDurationForServer", "canMove", "moveHeight", "moveRadius", "moveType",
                            "useFrameTick", "surroundingConfig", "followMountPointConfig", "hasSkill",
                            "skillDataBundle", "requiresCastSkillConfirm", "hasModel", "modelKey",
                            "mountPointDef", "modelParts", "hasNavObstacle", "navObstacleConfig",
                            "canBeSelect", "detectedHeight", "detectedRadius", "physical", "physicalData",
                            "hasBattlePhysicalComponents", "hasAirborneComponent", "hasKnockDownComponent",
                            "hasPullComponent", "hasMovementComponent", "hasAnimation", "animationPath",
                            "hasInteractiveAction", "maxPickUpTime", "interactiveActions", "isEnergySource",
                            "maxIgniteNum", "maxIgniteNumBB", "isUltimateShow", "hasSuperArmor",
                            "initialSuperArmor", "healthType", "headBarType", "overrideHeadBarDeltaTowardCamera",
                            "headBarDeltaTowardCamera", "headBar2DOffset", "useHeadBarGuideLine"
                        }
                    );
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "AbilityEntityRootComponentData", StringComparison.Ordinal))
                {
                    data = BuildPartialAbilityEntityPayloadData(
                        rawData,
                        offset,
                        length,
                        "Beyond.Gameplay.Core.AbilityEntityRootComponentData",
                        "metadata field order is known, but BB field-meta/string blocks are not yet field-accurate; payload is preserved as raw words",
                        new[]
                        {
                            "maxStackingCnt", "maxStackingCntBB", "lifeType", "duration", "durationBB",
                            "isEnergySource", "maxIgniteNum", "maxIgniteNumBB", "moveUseFrameTick", "headBarType"
                        }
                    );
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "AbilityEntityControllerData", StringComparison.Ordinal))
                {
                    if (length == 0)
                    {
                        data = new OrderedDictionary
                        {
                            { "$decoded", true },
                            { "layout", "Beyond.Gameplay.Core.AbilityEntityControllerData" },
                            { "offset", offset },
                            { "length", length },
                        };
                    }
                    else
                    {
                        data = BuildPartialAbilityEntityPayloadData(
                            rawData,
                            offset,
                            length,
                            "Beyond.Gameplay.Core.AbilityEntityControllerData",
                            "metadata has no own fields; observed payload contains nested movement/rotation serialized blocks and is preserved as raw words/string hints"
                        );
                    }
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyRootComponentData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    var locatorIds = ReadPayloadInt32List(reader, "locatorIds", 128);
                    var locatorNameCount = reader.ReadInt32("locatorNames.count");
                    if (locatorNameCount != locatorIds.Count)
                    {
                        throw new InvalidDataException("EnemyRootComponentData id/name count mismatch");
                    }

                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.EnemyRootComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "locatorIds", locatorIds },
                        { "locatorNames", ReadPayloadStringListFixed(reader, "locatorNames", locatorNameCount) },
                        { "unknown0", reader.ReadInt32("unknown0") },
                        { "transformRecords", ReadPayloadObjectList(reader, "transformRecords", 16, ReadEnemyRootTransformRecord) },
                        { "trailingWords", ReadRemainingPayloadRawInt32Words(reader, "trailingWords") },
                    };
                    reader.EnsureComplete();
                    return true;
                }
                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.View", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "ModelComponentData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "layout", "Beyond.Gameplay.View.ModelComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "modelId", reader.ReadAlignedAsciiString("modelId") },
                        { "modelScale", reader.ReadFloat("modelScale") },
                        { "enableBornFadeIn", reader.ReadBool32("enableBornFadeIn") },
                        { "bornFadeInTime", reader.ReadFloat("bornFadeInTime") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.View", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyAnimationComponentData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.View.EnemyAnimationComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "animationConfigPath", reader.ReadAlignedAsciiString("animationConfigPath") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "AbilitySystemData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "$partial", true },
                        { "layout", "Beyond.Gameplay.Core.AbilitySystemData" },
                        { "offset", offset },
                        { "length", length },
                        { "shapeData", new OrderedDictionary
                            {
                                { "detectedRadius", reader.ReadFloat("shapeData.detectedRadius") },
                                { "detectedHeight", reader.ReadFloat("shapeData.detectedHeight") },
                            }
                        },
                        { "modeConfig", ReadAbilitySystemModeConfig(reader) },
                    };
                    if (TryReadAbilitySystemSkillDataBundle(reader, out var skillDataBundle))
                    {
                        data["skillDataBundle"] = skillDataBundle;
                    }
                    data["remainingStringHints"] = CollectAbilitySystemRemainingStringHints(rawData, reader.Position, reader.Remaining, 128);
                    data["remainingRawWords"] = ReadRemainingPayloadRawInt32Words(reader, "remainingRawWords", 8192);
                    reader.EnsureComplete();
                    return true;
                }                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyAIComponentData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyAIComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "aiCfgPath", reader.ReadAlignedAsciiString("aiCfgPath") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "RotatorComponentData", StringComparison.Ordinal)
                    && length == 4)
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.RotatorComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "rawWord", BuildPayloadHash32(reader.ReadInt32("rawWord")) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterMovementComponentData", StringComparison.Ordinal)
                    && length == 48)
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.CharacterMovementComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "rawFloat32", ReadPayloadFloatArray(reader, "rawFloat32", 12) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterMovementComponentData", StringComparison.Ordinal)
                    && length > 0
                    && length != 48)
                {
                    data = BuildPartialAbilityEntityPayloadData(
                        rawData,
                        offset,
                        length,
                        "Beyond.Gameplay.Core.CharacterMovementComponentData",
                        "non-enemy payload length differs from the known 48-byte movement block; preserved as raw words until MovementData/proxyShape/list sections are decoded",
                        new[] { "movementData", "proxyShape", "overrideMoveMode", "abilityEntityMovementDataList" }
                    );
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "RVOComponentData", StringComparison.Ordinal)
                    && length == 12)
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.RVOComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "rvoCfgRawWords", ReadPayloadRawInt32Words(reader, "rvoCfgRawWords", 3) },
                    };
                    reader.EnsureComplete();
                    return true;
                }
                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyControllerData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "layout", "Beyond.Gameplay.Core.EnemyControllerData" },
                        { "offset", offset },
                        { "length", length },
                        { "deadEffectDelay", reader.ReadFloat("deadEffectDelay") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "ControlledStateComponentData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "layout", "Beyond.Gameplay.ControlledStateComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "airborneEnabled", reader.ReadBool32("airborneEnabled") },
                        { "knockDownEnabled", reader.ReadBool32("knockDownEnabled") },
                        { "blowOffEnabled", reader.ReadBool32("blowOffEnabled") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyPartsControllerComponentData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.EnemyPartsControllerComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "partsData", ReadPayloadObjectList(reader, "partsData", 64, itemReader => ReadEnemyPartsControllerData(itemReader, recoveredByRid)) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NavMeshObstacleComponentData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.NavMeshObstacleComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "configList", ReadPayloadObjectList(reader, "configList", 64, itemReader => ReadNavMeshObstacleConfigData(itemReader, recoveredByRid)) },
                    };
                    reader.EnsureComplete();
                    return true;
                }
                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "MeshAdjustComponentData", StringComparison.Ordinal)
                    && length == 96)
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.MeshAdjustComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "rawFloat32", ReadPayloadFloatArray(reader, "rawFloat32", 24) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.View", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyPivotComponentData", StringComparison.Ordinal)
                    && length == 20)
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.View.EnemyPivotComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "rawWords", ReadPayloadRawInt32Words(reader, "rawWords", 4) },
                        { "maxWarpRatio", reader.ReadFloat("maxWarpRatio") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.View", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyPartAnimatorComponentData", StringComparison.Ordinal)
                    && length == 4)
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.View.EnemyPartAnimatorComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "rawWord", BuildPayloadHash32(reader.ReadInt32("rawWord")) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyPartsRootComponentData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.EnemyPartsRootComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "prefixWords", ReadPayloadRawInt32Words(reader, "prefixWords", 8) },
                        { "partName", reader.ReadAlignedAsciiString("partName") },
                        { "partTags", ReadEnemyPartTagList(reader) },
                    };
                    reader.EnsureComplete();
                    return true;
                }
                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "AbilitySystemForEnemyPartData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    if ((length % 4) != 0)
                    {
                        throw new InvalidDataException("AbilitySystemForEnemyPartData payload is not word-aligned");
                    }

                    var wordCount = length / 4;
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.AbilitySystemForEnemyPartData" },
                        { "offset", offset },
                        { "length", length },
                    };

                    if (wordCount >= EnemyPartAbilityScalarWordCount
                        && CanDecodeEnemyPartAbilityScalarTail(rawData, offset + length - (EnemyPartAbilityScalarWordCount * 4), EnemyPartAbilityScalarWordCount * 4))
                    {
                        data["partAttributesRawWords"] = ReadPayloadRawInt32Words(
                            reader,
                            "partAttributesRawWords",
                            wordCount - EnemyPartAbilityScalarWordCount
                        );
                        data["fields"] = ReadEnemyPartAbilityScalarFields(reader);
                    }
                    else
                    {
                        data["$partial"] = true;
                        data["layoutNote"] = "word-aligned numeric payload; scalar tail did not match the known AbilitySystemForEnemyPartData field constraints";
                        data["rawWords"] = ReadPayloadRawInt32Words(reader, "rawWords", wordCount);
                    }

                    reader.EnsureComplete();
                    return true;
                }
                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NavMeshObstacleCapsuleData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "layout", "Beyond.Gameplay.Core.NavMeshObstacleCapsuleData" },
                        { "offset", offset },
                        { "length", length },
                        { "m_radius", reader.ReadFloat("m_radius") },
                        { "m_height", reader.ReadFloat("m_height") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NavMeshObstacleBoxData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "layout", "Beyond.Gameplay.Core.NavMeshObstacleBoxData" },
                        { "offset", offset },
                        { "length", length },
                        { "size", ReadPayloadVector3(reader, "size") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (length == 0
                    && string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && IsEmptyEnemyComponentType(header))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "layout", string.IsNullOrEmpty(header.Namespace) ? header.ClassName : $"{header.Namespace}.{header.ClassName}" },
                        { "offset", offset },
                        { "length", length },
                    };
                    return true;
                }
            }
            catch (InvalidDataException)
            {
                data = null;
                return false;
            }

            return false;
        }

        private static OrderedDictionary BuildPartialAbilityEntityPayloadData(
            byte[] rawData,
            int offset,
            int length,
            string layout,
            string layoutNote,
            string[] metadataFieldOrder = null
        )
        {
            var reader = new ManagedReferencePayloadReader(rawData, offset, length);
            var data = new OrderedDictionary
            {
                { "$decoded", true },
                { "$partial", true },
                { "$inferred", true },
                { "layout", layout },
                { "layoutNote", layoutNote },
                { "offset", offset },
                { "length", length },
            };
            if (metadataFieldOrder != null && metadataFieldOrder.Length > 0)
            {
                data["metadataFieldOrder"] = metadataFieldOrder;
            }

            var stringHintBudget = 64;
            var stringHints = CollectAlignedStringHints(rawData, offset, length, ref stringHintBudget);
            if (stringHints.Count > 0)
            {
                data["stringHints"] = stringHints;
            }
            data["rawWords"] = ReadRemainingPayloadRawInt32Words(reader, "rawWords", 8192);
            reader.EnsureComplete();
            return data;
        }

        private static bool IsEmptyEnemyComponentType(ManagedReferenceHeader header)
        {
            if (header == null)
            {
                return false;
            }

            return (string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && (string.Equals(header.ClassName, "NavigationComponentData", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "PullComponentData", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "EnemyAudioComponentData", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "EnemyHurtAnimComponentData", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "PushBackComponentData", StringComparison.Ordinal)))
                || (string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "AdditionalBattleShapeComponentData", StringComparison.Ordinal));
        }

        private const int EnemyPartAbilityScalarWordCount = 20;

        private static int ReadPayloadFixedCount(ManagedReferencePayloadReader reader, string fieldName, int expected)
        {
            var count = reader.ReadInt32(fieldName);
            if (count != expected)
            {
                throw new InvalidDataException($"invalid count {count} for {fieldName}; expected {expected}");
            }

            return count;
        }

        private static OrderedDictionary ReadPayloadGameplayTag(ManagedReferencePayloadReader reader, string fieldName)
        {
            return new OrderedDictionary
            {
                { "path", reader.ReadAlignedAsciiString($"{fieldName}.path") },
                { "tagId", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.tagId")) },
            };
        }

        private static List<OrderedDictionary> ReadPayloadInvertGameplayTagList(
            ManagedReferencePayloadReader reader,
            string fieldName,
            string tagFieldName,
            int maxCount
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
                items.Add(new OrderedDictionary
                {
                    { "invert", reader.ReadBool32($"{fieldName}[{i}].invert") },
                    { tagFieldName, ReadPayloadGameplayTag(reader, $"{fieldName}[{i}].{tagFieldName}") },
                });
            }
            return items;
        }


        private static List<OrderedDictionary> ReadPayloadGameplayTagList(
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

            var items = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                items.Add(ReadPayloadGameplayTag(reader, $"{fieldName}[{i}]"));
            }
            return items;
        }

        private static OrderedDictionary ReadEnemySettlementAttackTargetSkillMap(ManagedReferencePayloadReader reader)
        {
            var keyCount = reader.ReadInt32("skillData.keys.count");
            if (keyCount < 0 || keyCount > 16)
            {
                throw new InvalidDataException($"invalid count {keyCount} for skillData.keys");
            }

            var keys = new List<OrderedDictionary>(keyCount);
            for (var i = 0; i < keyCount; i++)
            {
                keys.Add(ReadPayloadNamedEnum32(reader, $"skillData.keys[{i}]", new[] { "Building", "Character", "Core" }));
            }

            var valueCount = reader.ReadInt32("skillData.values.count");
            if (valueCount != keyCount)
            {
                throw new InvalidDataException("skillData key/value count mismatch");
            }

            var values = new List<OrderedDictionary>(valueCount);
            var entries = new List<OrderedDictionary>(valueCount);
            for (var i = 0; i < valueCount; i++)
            {
                var value = new OrderedDictionary
                {
                    { "skillId", reader.ReadAlignedAsciiString($"skillData.values[{i}].skillId") },
                    { "skillRange", reader.ReadFloat($"skillData.values[{i}].skillRange") },
                };
                values.Add(value);
                entries.Add(new OrderedDictionary
                {
                    { "target", keys[i] },
                    { "skill", value },
                });
            }

            return new OrderedDictionary
            {
                { "keys", keys },
                { "values", values },
                { "entries", entries },
            };
        }

        private static OrderedDictionary ReadPayloadIntStringDictionary(
            ManagedReferencePayloadReader reader,
            string fieldName,
            string keyName,
            string valueName,
            int maxCount
        )
        {
            var keys = ReadPayloadInt32List(reader, $"{fieldName}.keys", maxCount);
            var valueCount = reader.ReadInt32($"{fieldName}.values.count");
            if (valueCount != keys.Count)
            {
                throw new InvalidDataException($"key/value count mismatch for {fieldName}");
            }

            var values = new List<string>(valueCount);
            var entries = new List<OrderedDictionary>(valueCount);
            for (var i = 0; i < valueCount; i++)
            {
                var value = reader.ReadAlignedAsciiString($"{fieldName}.values[{i}]");
                values.Add(value);
                entries.Add(new OrderedDictionary
                {
                    { keyName, BuildPayloadHash32(keys[i]) },
                    { valueName, value },
                });
            }

            return new OrderedDictionary
            {
                { "keys", keys },
                { "values", values },
                { "entries", entries },
            };
        }

        private static List<OrderedDictionary> ReadEnemyCheckTagInfoList(
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

            var items = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                items.Add(new OrderedDictionary
                {
                    { "invert", reader.ReadBool32($"{fieldName}[{i}].invert") },
                    { "query", ReadPredefinedQuery(reader, $"{fieldName}[{i}].query") },
                });
            }
            return items;
        }

        private static OrderedDictionary ReadPredefinedQuery(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            var value = reader.ReadInt32(fieldName);
            if (value < 0 || value > 1024)
            {
                throw new InvalidDataException($"invalid PredefinedQuery {value} in {fieldName}");
            }

            var item = BuildPayloadHash32(value);
            if (value == 7)
            {
                item["name"] = "InImmobilized";
            }
            return item;
        }

        private static List<OrderedDictionary> ReadNpcStimulusResponseList(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxCount,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid
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
                items.Add(new OrderedDictionary
                {
                    { "finishCount", reader.ReadInt32($"{fieldName}[{i}].finishCount") },
                    { "stimulusCfg", ReadPayloadRidLink(reader, $"{fieldName}[{i}].stimulusCfg", recoveredByRid) },
                    { "stimulusConditionCfg", ReadPayloadRidLinkList(reader, $"{fieldName}[{i}].stimulusConditionCfg", 16, recoveredByRid) },
                    { "responseCfg", ReadPayloadRidLink(reader, $"{fieldName}[{i}].responseCfg", recoveredByRid) },
                });
            }
            return items;
        }

        private static OrderedDictionary ReadPayloadRidLink(
            ManagedReferencePayloadReader reader,
            string fieldName,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid
        )
        {
            var ridOffset = reader.Position;
            var rid = reader.ReadInt64(fieldName);
            return BuildManagedReferenceRidValue(rid, recoveredByRid, ridOffset);
        }

        private static List<OrderedDictionary> ReadLuaCustomUIStyleInfoList(
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

            var items = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                items.Add(new OrderedDictionary
                {
                    { "style", ReadPayloadPPtr(reader, $"{fieldName}[{i}].style") },
                    { "component", ReadPayloadPPtr(reader, $"{fieldName}[{i}].component") },
                });
            }
            return items;
        }

        private static List<int> ReadPayloadInt32List(
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

            var items = new List<int>(count);
            for (var i = 0; i < count; i++)
            {
                items.Add(reader.ReadInt32($"{fieldName}[{i}]"));
            }
            return items;
        }

        private static OrderedDictionary ReadPayloadIntPPtrDictionary(
            ManagedReferencePayloadReader reader,
            string fieldName,
            string keyName,
            string valueName,
            int maxCount
        )
        {
            var keys = ReadPayloadInt32List(reader, $"{fieldName}.keys", maxCount);
            var valueCount = reader.ReadInt32($"{fieldName}.values.count");
            if (valueCount != keys.Count)
            {
                throw new InvalidDataException($"key/value count mismatch for {fieldName}");
            }

            var values = new List<OrderedDictionary>(valueCount);
            var entries = new List<OrderedDictionary>(valueCount);
            for (var i = 0; i < valueCount; i++)
            {
                var value = ReadPayloadPPtr(reader, $"{fieldName}.values[{i}]");
                values.Add(value);
                entries.Add(new OrderedDictionary
                {
                    { keyName, keys[i] },
                    { valueName, value },
                });
            }

            return new OrderedDictionary
            {
                { "keys", keys },
                { "values", values },
                { "entries", entries },
            };
        }

        private static List<string> ReadPayloadStringListFixed(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int count
        )
        {
            if (count < 0 || count > 256)
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

        private static List<float> ReadPayloadFloatArray(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int count
        )
        {
            if (count < 0 || count > 1024)
            {
                throw new InvalidDataException($"invalid float count {count} for {fieldName}");
            }

            var values = new List<float>(count);
            for (var i = 0; i < count; i++)
            {
                values.Add(reader.ReadFloat($"{fieldName}[{i}]"));
            }
            return values;
        }

        private static List<OrderedDictionary> ReadPayloadRawInt32Words(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int count
        )
        {
            if (count < 0 || count > 1024)
            {
                throw new InvalidDataException($"invalid word count {count} for {fieldName}");
            }

            var values = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                values.Add(BuildPayloadHash32(reader.ReadInt32($"{fieldName}[{i}]")));
            }
            return values;
        }

        private static bool TryFindEnemyTemplateTail(
            byte[] rawData,
            int searchStart,
            int payloadEnd,
            out int postModelOffset
        )
        {
            postModelOffset = -1;
            if (rawData == null || searchStart < 0 || payloadEnd < searchStart || payloadEnd > rawData.Length)
            {
                return false;
            }

            for (var candidate = searchStart; candidate <= payloadEnd - 16; candidate += 4)
            {
                var postEnd = candidate;
                if (!TryReadAlignedAsciiString(rawData, ref postEnd, out var postModelKey)
                    || postModelKey.Length == 0
                    || postEnd > payloadEnd - 12)
                {
                    continue;
                }

                var animOffset = postEnd + 12;
                var animEnd = animOffset;
                if (!TryReadAlignedAsciiString(rawData, ref animEnd, out var animConfigPath)
                    || animEnd != payloadEnd
                    || !animConfigPath.StartsWith("Assets/", StringComparison.Ordinal)
                    || !animConfigPath.EndsWith(".asset", StringComparison.Ordinal))
                {
                    continue;
                }

                postModelOffset = candidate;
                return true;
            }

            return false;
        }
        private static bool CanDecodeEnemyPartAbilityScalarTail(byte[] rawData, int offset, int length)
        {
            try
            {
                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                ReadEnemyPartAbilityScalarFields(reader);
                reader.EnsureComplete();
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        private static OrderedDictionary ReadEnemyPartAbilityScalarFields(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "defaultEnabled", reader.ReadBool32("defaultEnabled") },
                { "asIndividualInExcludeTargetProcessor", reader.ReadBool32("asIndividualInExcludeTargetProcessor") },
                { "useMainBodyHp", reader.ReadBool32("useMainBodyHp") },
                { "useMainBodyPoise", reader.ReadBool32("useMainBodyPoise") },
                { "showHpBar", reader.ReadBool32("showHpBar") },
                { "hpBarMountPoint", ReadPayloadEnum32(reader, "hpBarMountPoint", 0, 256) },
                { "hpBarEnemyRank", ReadPayloadEnum32(reader, "hpBarEnemyRank", 0, 2) },
                { "showPoise", reader.ReadBool32("showPoise") },
                { "canBeHitIndividually", reader.ReadBool32("canBeHitIndividually") },
                { "halfBlockAngle", ReadPayloadFloatRange(reader, "halfBlockAngle", -360f, 360f) },
                { "halfRecommendedAngle", ReadPayloadFloatRange(reader, "halfRecommendedAngle", -360f, 360f) },
                { "onlyHitByNormalAttack", reader.ReadBool32("onlyHitByNormalAttack") },
                { "canBeDirectlyBuffed", reader.ReadBool32("canBeDirectlyBuffed") },
                { "damageRatio", ReadPayloadFloatRange(reader, "damageRatio", -1000f, 1000f) },
                { "poiseRatio", ReadPayloadFloatRange(reader, "poiseRatio", -1000f, 1000f) },
                { "showDamageTextPart", reader.ReadBool32("showDamageTextPart") },
                { "showDamageTextTransferred", reader.ReadBool32("showDamageTextTransferred") },
                { "transferredDamageTextLocation", ReadPayloadEnum32(reader, "transferredDamageTextLocation", 0, 2) },
                { "overrideLockPoint", ReadPayloadEnum32(reader, "overrideLockPoint", 0, 256) },
                { "damageTransferType", ReadPayloadEnum32(reader, "damageTransferType", 0, 2) },
            };
        }

        private static OrderedDictionary ReadPayloadEnum32(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int min,
            int max
        )
        {
            var value = reader.ReadInt32(fieldName);
            if (value < min || value > max)
            {
                throw new InvalidDataException($"invalid enum32 {value} in {fieldName}");
            }

            return new OrderedDictionary
            {
                { "value", value },
            };
        }

        private static OrderedDictionary ReadPayloadNamedEnum32(
            ManagedReferencePayloadReader reader,
            string fieldName,
            string[] names
        )
        {
            var value = reader.ReadInt32(fieldName);
            if (value < 0 || value >= names.Length)
            {
                throw new InvalidDataException($"invalid enum32 {value} in {fieldName}");
            }

            return new OrderedDictionary
            {
                { "value", value },
                { "name", names[value] },
            };
        }

        private static float ReadPayloadFloatRange(
            ManagedReferencePayloadReader reader,
            string fieldName,
            float min,
            float max
        )
        {
            var value = reader.ReadFloat(fieldName);
            if (value < min || value > max)
            {
                throw new InvalidDataException($"float {value} in {fieldName} is outside [{min}, {max}]");
            }

            return value;
        }

        private static List<OrderedDictionary> ReadRemainingPayloadRawInt32Words(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            if ((reader.Remaining % 4) != 0)
            {
                throw new InvalidDataException($"remaining bytes for {fieldName} are not word-aligned");
            }

            return ReadPayloadRawInt32Words(reader, fieldName, reader.Remaining / 4);
        }

        private static OrderedDictionary ReadAbilitySystemModeConfig(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "modes", ReadPayloadObjectList(reader, "modeConfig.modes", 128, ReadAbilitySystemModeData) },
            };
        }

        private static OrderedDictionary ReadAbilitySystemModeData(ManagedReferencePayloadReader reader)
        {
            var item = new OrderedDictionary
            {
                { "modeId", reader.ReadAlignedAsciiString("modeConfig.modes.modeId") },
                { "defaultEnable", reader.ReadBool32("modeConfig.modes.defaultEnable") },
                { "modeLayer", reader.ReadAlignedAsciiString("modeConfig.modes.modeLayer") },
                { "parentModeId", reader.ReadAlignedAsciiString("modeConfig.modes.parentModeId") },
                { "addExtraPassiveSkill", reader.ReadBool32("modeConfig.modes.addExtraPassiveSkill") },
                { "extraPassiveSkillId", ReadPayloadStringList(reader, "modeConfig.modes.extraPassiveSkillId", 64) },
                { "overrideMoveSpeed", reader.ReadBool32("modeConfig.modes.overrideMoveSpeed") },
                { "moveSpeed", reader.ReadFloat("modeConfig.modes.moveSpeed") },
                { "overrideRotateRate", reader.ReadBool32("modeConfig.modes.overrideRotateRate") },
                { "rotateRate", reader.ReadFloat("modeConfig.modes.rotateRate") },
                { "isStrafing", reader.ReadBool32("modeConfig.modes.isStrafing") },
                { "moveInterruptAttack", reader.ReadBool32("modeConfig.modes.moveInterruptAttack") },
                { "overrideNormalAttackList", reader.ReadBool32("modeConfig.modes.overrideNormalAttackList") },
                { "normalAttackList", ReadPayloadStringList(reader, "modeConfig.modes.normalAttackList", 64) },
                { "applyAnimBool", reader.ReadBool32("modeConfig.modes.applyAnimBool") },
                { "animBoolName", reader.ReadAlignedAsciiString("modeConfig.modes.animBoolName") },
            };

            if (!TryReadAbilitySystemModeExtendedTail(reader, item))
            {
                var compactTail = ReadAbilitySystemModeCompactTail(reader);
                if (compactTail.Count > 0)
                {
                    item["compactTailRawWords"] = compactTail;
                }
            }
            return item;
        }

        private static bool TryReadAbilitySystemModeExtendedTail(
            ManagedReferencePayloadReader reader,
            OrderedDictionary item
        )
        {
            var local = new ManagedReferencePayloadReader(reader.RawData, reader.Position, reader.Remaining);
            var tail = new OrderedDictionary();
            try
            {
                var overrideStateClip = local.ReadBool32("modeConfig.modes.overrideStateClip");
                tail["overrideStateClip"] = overrideStateClip;
                if (overrideStateClip)
                {
                    tail["overrideClipMappingRawWords"] = ReadAbilitySystemRawWordList(
                        local,
                        "modeConfig.modes.overrideClipMappingRawWords",
                        64
                    );
                }

                tail["overrideAnimCfg"] = local.ReadBool32("modeConfig.modes.overrideAnimCfg");
                tail["animCfgPath"] = local.ReadAlignedAsciiString("modeConfig.modes.animCfgPath");
                tail["overrideModelKey"] = local.ReadBool32("modeConfig.modes.overrideModelKey");
                tail["modelKey"] = local.ReadAlignedAsciiString("modeConfig.modes.modelKey");
                tail["mountPointDefIndex"] = local.ReadInt32("modeConfig.modes.mountPointDefIndex");
                tail["overrideCmdMapping"] = local.ReadBool32("modeConfig.modes.overrideCmdMapping");
                tail["cmdMappingRawWords"] = ReadPayloadRawInt32Words(
                    local,
                    "modeConfig.modes.cmdMappingRawWords",
                    4
                );
                foreach (DictionaryEntry entry in tail)
                {
                    item[entry.Key] = entry.Value;
                }
                reader.SetPosition(local.Position);
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        private static bool TryReadAbilitySystemSkillDataBundle(
            ManagedReferencePayloadReader reader,
            out OrderedDictionary data
        )
        {
            data = null;
            var local = new ManagedReferencePayloadReader(reader.RawData, reader.Position, reader.Remaining);
            try
            {
                data = new OrderedDictionary
                {
                    { "$partial", true },
                    { "layout", "Beyond.Gameplay.Core.SkillDataBundle" },
                    { "layoutNote", "decoded through comboSkillSpecialNodeName; defaultCmdMapping and later AbilitySystemData fields remain in remainingRawWords" },
                    { "allNormalAttackId", ReadPayloadStringList(local, "skillDataBundle.allNormalAttackId", 256) },
                    { "allActiveSkillId", ReadPayloadStringList(local, "skillDataBundle.allActiveSkillId", 256) },
                    { "allPassiveSkillId", ReadPayloadStringList(local, "skillDataBundle.allPassiveSkillId", 256) },
                    { "normalAttackList", ReadPayloadStringList(local, "skillDataBundle.normalAttackList", 256) },
                    { "enabledBreakingNormalAttacks", ReadPayloadStringList(local, "skillDataBundle.enabledBreakingNormalAttacks", 256) },
                    { "enabledPassiveSkills", ReadPayloadStringList(local, "skillDataBundle.enabledPassiveSkills", 256) },
                    { "normalSkillId", local.ReadAlignedAsciiString("skillDataBundle.normalSkillId") },
                    { "ultimateSkillId", local.ReadAlignedAsciiString("skillDataBundle.ultimateSkillId") },
                    { "plungingAttackStartId", local.ReadAlignedAsciiString("skillDataBundle.plungingAttackStartId") },
                    { "plungingAttackEndId", local.ReadAlignedAsciiString("skillDataBundle.plungingAttackEndId") },
                    { "dodgeSkillId", local.ReadAlignedAsciiString("skillDataBundle.dodgeSkillId") },
                    { "comboSkillConditionsRawWords", ReadAbilitySystemRawWordList(local, "skillDataBundle.comboSkillConditionsRawWords", 64) },
                    { "comboSkillId", local.ReadAlignedAsciiString("skillDataBundle.comboSkillId") },
                    { "comboSkillSpecialNodeName", local.ReadAlignedAsciiString("skillDataBundle.comboSkillSpecialNodeName") },
                };
                reader.SetPosition(local.Position);
                return true;
            }
            catch (InvalidDataException)
            {
                data = null;
                return false;
            }
        }

        private static List<OrderedDictionary> ReadAbilitySystemRawWordList(
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

            return ReadPayloadRawInt32Words(reader, fieldName, count);
        }

        private static List<OrderedDictionary> ReadAbilitySystemModeCompactTail(ManagedReferencePayloadReader reader)
        {
            var words = new List<OrderedDictionary>();
            while (reader.Remaining >= 4)
            {
                if (LooksLikeAbilitySystemSectionString(reader, out _))
                {
                    break;
                }

                words.Add(BuildPayloadHash32(reader.ReadInt32("modeConfig.modes.compactTailRawWords")));
            }
            return words;
        }

        private static bool LooksLikeAbilitySystemSectionString(ManagedReferencePayloadReader reader, out string value)
        {
            value = null;
            if (reader.Remaining < 4)
            {
                return false;
            }

            var pos = reader.Position;
            if (!TryReadAlignedAsciiString(reader.RawData, ref pos, out value) || pos > reader.End || value.Length == 0)
            {
                value = null;
                return false;
            }

            return value.Length >= 3 && IsLikelyAbilitySystemSectionString(value);
        }

        private static bool IsLikelyAbilitySystemSectionString(string value)
        {
            return value.StartsWith("Skill", StringComparison.Ordinal)
                || value.StartsWith("Battle", StringComparison.Ordinal)
                || value.StartsWith("Patrol", StringComparison.Ordinal)
                || value.StartsWith("Vigilance", StringComparison.Ordinal)
                || value.StartsWith("eny_", StringComparison.Ordinal)
                || value.StartsWith("buff_", StringComparison.Ordinal)
                || value.StartsWith("common_", StringComparison.Ordinal)
                || value.StartsWith("EntityBB_", StringComparison.Ordinal);
        }

        private static List<OrderedDictionary> CollectAbilitySystemRemainingStringHints(
            byte[] rawData,
            int offset,
            int length,
            int maxCount
        )
        {
            var hints = new List<OrderedDictionary>();
            if (rawData == null || offset < 0 || length <= 0 || offset + length > rawData.Length)
            {
                return hints;
            }

            var end = offset + length;
            for (var pos = offset; pos <= end - 4 && hints.Count < maxCount; pos += 4)
            {
                var stringPos = pos;
                if (TryReadAlignedAsciiString(rawData, ref stringPos, out var value)
                    && stringPos <= end
                    && value.Length >= 3
                    && IsLikelyAbilitySystemSectionString(value))
                {
                    hints.Add(new OrderedDictionary
                    {
                        { "offset", pos },
                        { "value", value },
                    });
                }
            }
            return hints;
        }
        private static List<OrderedDictionary> ReadRemainingPayloadRawInt32Words(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxCount
        )
        {
            if ((reader.Remaining % 4) != 0)
            {
                throw new InvalidDataException($"remaining bytes for {fieldName} are not word-aligned");
            }

            var count = reader.Remaining / 4;
            if (count < 0 || count > maxCount)
            {
                throw new InvalidDataException($"invalid word count {count} for {fieldName}");
            }

            var values = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                values.Add(BuildPayloadHash32(reader.ReadInt32($"{fieldName}[{i}]")));
            }
            return values;
        }
        private static OrderedDictionary ReadEnemyRootTransformRecord(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "name", reader.ReadAlignedAsciiString("transformRecords.name") },
                { "rawFloat32", ReadPayloadFloatArray(reader, "transformRecords.rawFloat32", 7) },
            };
        }

        private static List<OrderedDictionary> ReadEnemyPartTagList(ManagedReferencePayloadReader reader)
        {
            var count = reader.ReadInt32("partTags.count");
            if (count < 0 || count > 16)
            {
                throw new InvalidDataException($"invalid count {count} for partTags");
            }

            var values = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                values.Add(new OrderedDictionary
                {
                    { "path", reader.ReadAlignedAsciiString($"partTags[{i}].path") },
                    { "hash", BuildPayloadHash32(reader.ReadInt32($"partTags[{i}].hash")) },
                });
            }
            return values;
        }

        private static OrderedDictionary ReadEnemyPartsControllerData(
            ManagedReferencePayloadReader reader,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid
        )
        {
            var item = new OrderedDictionary
            {
                { "partName", reader.ReadAlignedAsciiString("partsData.partName") },
                { "unknownName", reader.ReadAlignedAsciiString("partsData.unknownName") },
                { "unknownMode", reader.ReadInt32("partsData.unknownMode") },
                { "rawFloat32", ReadPayloadFloatArray(reader, "partsData.rawFloat32", 6) },
                { "componentRids", ReadPayloadRidLinkList(reader, "partsData.componentRids", 8, recoveredByRid) },
            };
            return item;
        }

        private static OrderedDictionary ReadNavMeshObstacleConfigData(
            ManagedReferencePayloadReader reader,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid
        )
        {
            var item = new OrderedDictionary
            {
                { "unknownName", reader.ReadAlignedAsciiString("configList.unknownName") },
                { "name", reader.ReadAlignedAsciiString("configList.name") },
                { "rawFloat32", ReadPayloadFloatArray(reader, "configList.rawFloat32", 10) },
            };
            var shapeRidOffset = reader.Position;
            var shapeRid = reader.ReadInt64("configList.shapeRid");
            item["shape"] = BuildManagedReferenceRidValue(shapeRid, recoveredByRid, shapeRidOffset);
            return item;
        }

        private static List<OrderedDictionary> ReadPayloadRidLinkList(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxCount,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid
        )
        {
            var count = reader.ReadInt32($"{fieldName}.count");
            if (count < 0 || count > maxCount)
            {
                throw new InvalidDataException($"invalid count {count} for {fieldName}");
            }

            var links = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                var ridOffset = reader.Position;
                var rid = reader.ReadInt64($"{fieldName}[{i}]");
                links.Add(BuildManagedReferenceRidValue(rid, recoveredByRid, ridOffset));
            }
            return links;
        }

        private static OrderedDictionary BuildManagedReferenceRidValue(
            long rid,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid,
            int offset
        )
        {
            if (recoveredByRid != null && recoveredByRid.TryGetValue(rid, out var target))
            {
                return BuildManagedReferenceRidLink(rid, target, offset);
            }

            return new OrderedDictionary
            {
                { "offset", offset },
                { "rid", rid },
            };
        }

        private static OrderedDictionary ReadSkeletalMorphBoneMappingData(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "nameHash", BuildPayloadHash32(reader.ReadInt32("bones.nameHash")) },
                { "index", reader.ReadInt32("bones.index") },
                { "position", ReadPayloadVector3(reader, "bones.position") },
                { "rotation", ReadPayloadVector3(reader, "bones.rotation") },
                { "scale", ReadPayloadVector3(reader, "bones.scale") },
            };
        }

        private static OrderedDictionary BuildPayloadHash32(int value)
        {
            return new OrderedDictionary
            {
                { "value", value },
                { "hex", $"0x{unchecked((uint)value):x8}" },
            };
        }

        private static OrderedDictionary BuildPayloadHash64(long value)
        {
            return new OrderedDictionary
            {
                { "value", value },
                { "hex", $"0x{unchecked((ulong)value):x16}" },
            };
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

        private static bool TryDecodeDialogShortAnimActionData(
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
                || !string.Equals(header.ClassName, "DialogAnimActData", StringComparison.Ordinal)
                || rawData == null
                || offset < 0
                || length != 240
                || offset + length > rawData.Length
                || !HasInt32Value(rawData, offset + 8, 54)
                || !IsZeroFilled(rawData, offset + 12, 16)
                || !TryReadBoundedInt32(rawData, offset + 28, 0, 1, out var selector0)
                || !IsZeroFilled(rawData, offset + 32, 20)
                || !HasInt32Value(rawData, offset + 52, 1)
                || !HasInt32Value(rawData, offset + 56, 1045220557)
                || !HasInt32Value(rawData, offset + 60, 1045220557)
                || !IsZeroFilled(rawData, offset + 64, 8)
                || !IsZeroFilled(rawData, offset + 76, 16)
                || !HasInt32Value(rawData, offset + 92, 1)
                || !IsZeroFilled(rawData, offset + 96, 16)
                || !TryReadFiniteTimelineFloat(rawData, offset + 112, out var value0)
                || value0 < 0f
                || value0 > 1f
                || !IsZeroFilled(rawData, offset + 116, 16)
                || !HasInt32Value(rawData, offset + 132, 1)
                || !HasInt32Value(rawData, offset + 136, 1)
                || !HasInt32Value(rawData, offset + 140, 0)
                || !HasInt32Value(rawData, offset + 144, 1065353216)
                || !IsZeroFilled(rawData, offset + 148, 32)
                || !HasInt32Value(rawData, offset + 180, 1)
                || !IsZeroFilled(rawData, offset + 184, 16)
                || !HasInt32Value(rawData, offset + 200, 1045220557)
                || !IsZeroFilled(rawData, offset + 204, 16)
                || !HasInt32Value(rawData, offset + 220, 1)
                || !HasInt32Value(rawData, offset + 224, 1)
                || !HasInt32Value(rawData, offset + 228, 0)
                || !HasInt32Value(rawData, offset + 232, 1065353216)
                || !TryReadBoundedInt32(rawData, offset + 236, 0, 1, out var selector1)
                || !TryBuildDialogActionTimingPrefix(header, rawData, offset, length, out var actionTimingPrefix))
            {
                return false;
            }

            var opaqueValueLike = BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(offset + 72, 4));
            data = new OrderedDictionary
            {
                { "$partialDecoded", true },
                { "$inferred", true },
                { "layout", "DialogAnimActDataShortScalarBlock" },
                { "offset", offset },
                { "length", length },
                { "selectorFieldsLike", new OrderedDictionary
                    {
                        { "selector0", BuildInferredIntField(offset + 28, selector0) },
                        { "opaqueValue", BuildInferredIntField(offset + 72, opaqueValueLike) },
                        { "selector1", BuildInferredIntField(offset + 236, selector1) },
                    }
                },
                { "parameterValuesLike", BuildInferredFloatList(
                    new[] { offset + 56, offset + 60, offset + 112, offset + 144, offset + 200, offset + 232 },
                    new[] { 0.2f, 0.2f, value0, 1.0f, 0.2f, 1.0f }) },
            };
            AddDialogActionTimingPrefix(data, actionTimingPrefix);
            return true;
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
            if (TryDecodeDialogCamLongActionData(header, rawData, offset, length, out data))
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

        private static bool TryDecodeDialogCamLongActionData(
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
                || length != 560
                || offset + length > rawData.Length
                || !HasDialogCamLongFixedMarkers(rawData, offset)
                || !TryReadFiniteTimelineFloat(rawData, offset + 56, out var value0)
                || !TryReadFiniteTimelineFloat(rawData, offset + 60, out var value1)
                || !TryReadFiniteTimelineFloat(rawData, offset + 64, out var value2)
                || !TryReadFiniteTimelineFloat(rawData, offset + 68, out var value3)
                || !TryReadFiniteTimelineFloat(rawData, offset + 72, out var value4)
                || !TryReadFiniteTimelineFloat(rawData, offset + 76, out var value5)
                || !TryReadFiniteTimelineFloat(rawData, offset + 80, out var value6)
                || !TryReadFiniteTimelineFloat(rawData, offset + 88, out var value7)
                || !TryReadFiniteTimelineFloat(rawData, offset + 104, out var value8)
                || !TryReadFiniteTimelineFloat(rawData, offset + 108, out var value9)
                || !TryReadFiniteTimelineFloat(rawData, offset + 120, out var value10)
                || !TryReadFiniteTimelineFloat(rawData, offset + 124, out var value11)
                || !TryReadFiniteTimelineFloat(rawData, offset + 128, out var value12)
                || !TryReadFiniteTimelineFloat(rawData, offset + 132, out var value13)
                || !TryReadFiniteTimelineFloat(rawData, offset + 136, out var value14)
                || !TryReadFiniteTimelineFloat(rawData, offset + 148, out var value15)
                || !TryReadFiniteTimelineFloat(rawData, offset + 160, out var value16)
                || !TryReadFiniteTimelineFloat(rawData, offset + 164, out var value17)
                || !TryReadFiniteTimelineFloat(rawData, offset + 172, out var value18)
                || !TryReadBoundedInt32(rawData, offset + 340, -1, 0, out var selector1)
                || !TryReadBoundedInt32(rawData, offset + 356, 0, 2, out var selector2)
                || !TryBuildDialogActionTimingPrefix(header, rawData, offset, length, out var actionTimingPrefix))
            {
                return false;
            }

            data = new OrderedDictionary
            {
                { "$partialDecoded", true },
                { "$inferred", true },
                { "layout", "DialogCamActDataLongScalarBlock" },
                { "offset", offset },
                { "length", length },
                { "selectorFieldsLike", new OrderedDictionary
                    {
                        { "selector0", BuildInferredIntField(offset + 32, 0) },
                        { "variantMarker", BuildInferredIntField(offset + 92, 3) },
                        { "tailSelector0", BuildInferredIntField(offset + 340, selector1) },
                        { "tailSelector1", BuildInferredIntField(offset + 356, selector2) },
                    }
                },
                { "primaryCameraValuesLike", BuildInferredFloatList(
                    new[] { offset + 56, offset + 60, offset + 64, offset + 68, offset + 72, offset + 76, offset + 80, offset + 88 },
                    new[] { value0, value1, value2, value3, value4, value5, value6, value7 }) },
                { "parameterValuesLike", BuildInferredFloatList(
                    new[] { offset + 104, offset + 108, offset + 120, offset + 124, offset + 128, offset + 132, offset + 136, offset + 144, offset + 148, offset + 152, offset + 156, offset + 160, offset + 164, offset + 172, offset + 332, offset + 556 },
                    new[] { value8, value9, value10, value11, value12, value13, value14, 0.33333334f, value15, 1.0f, 1.0f, value16, value17, value18, 0.5f, 0.5f }) },
            };
            AddDialogActionTimingPrefix(data, actionTimingPrefix);
            return true;
        }

        private static bool HasDialogCamLongFixedMarkers(byte[] rawData, int offset)
        {
            return HasInt32Value(rawData, offset + 8, 51)
                && IsZeroFilled(rawData, offset + 12, 16)
                && HasInt32Value(rawData, offset + 28, -1)
                && HasInt32Value(rawData, offset + 32, 0)
                && IsZeroFilled(rawData, offset + 36, 12)
                && HasInt32Value(rawData, offset + 48, 2)
                && HasInt32Value(rawData, offset + 52, 0)
                && HasInt32Value(rawData, offset + 84, 0)
                && HasInt32Value(rawData, offset + 92, 3)
                && IsZeroFilled(rawData, offset + 96, 8)
                && IsZeroFilled(rawData, offset + 112, 8)
                && HasInt32Value(rawData, offset + 140, 0)
                && HasInt32Value(rawData, offset + 144, 1051372203)
                && HasInt32Value(rawData, offset + 152, 1065353216)
                && HasInt32Value(rawData, offset + 156, 1065353216)
                && HasInt32Value(rawData, offset + 168, 0)
                && HasInt32Value(rawData, offset + 176, 0)
                && HasInt32Value(rawData, offset + 180, 2)
                && HasInt32Value(rawData, offset + 184, 2)
                && HasInt32Value(rawData, offset + 188, 4)
                && HasInt32Value(rawData, offset + 192, 1)
                && IsZeroFilled(rawData, offset + 196, 36)
                && HasInt32Value(rawData, offset + 232, 1)
                && HasInt32Value(rawData, offset + 236, 0)
                && HasInt32Value(rawData, offset + 240, 2)
                && HasInt32Value(rawData, offset + 244, 2)
                && HasInt32Value(rawData, offset + 248, 4)
                && HasInt32Value(rawData, offset + 252, -1)
                && HasInt32Value(rawData, offset + 256, -1)
                && HasInt32Value(rawData, offset + 260, 0)
                && HasInt32Value(rawData, offset + 264, -1)
                && HasInt32Value(rawData, offset + 268, 0)
                && HasInt32Value(rawData, offset + 272, -1082130432)
                && HasInt32Value(rawData, offset + 276, 0)
                && HasInt32Value(rawData, offset + 280, 0)
                && HasInt32Value(rawData, offset + 284, 2)
                && HasInt32Value(rawData, offset + 288, 2)
                && HasInt32Value(rawData, offset + 292, 4)
                && IsZeroFilled(rawData, offset + 296, 36)
                && HasInt32Value(rawData, offset + 332, 1056964608)
                && HasInt32Value(rawData, offset + 336, 0)
                && IsZeroFilled(rawData, offset + 344, 12)
                && IsZeroFilled(rawData, offset + 360, 40)
                && HasInt32Value(rawData, offset + 400, 0)
                && HasInt32Value(rawData, offset + 404, 2)
                && HasInt32Value(rawData, offset + 408, 2)
                && HasInt32Value(rawData, offset + 412, 4)
                && HasInt32Value(rawData, offset + 416, 1)
                && IsZeroFilled(rawData, offset + 420, 36)
                && HasInt32Value(rawData, offset + 456, 1)
                && HasInt32Value(rawData, offset + 460, 0)
                && HasInt32Value(rawData, offset + 464, 2)
                && HasInt32Value(rawData, offset + 468, 2)
                && HasInt32Value(rawData, offset + 472, 4)
                && HasInt32Value(rawData, offset + 476, -1)
                && HasInt32Value(rawData, offset + 480, -1)
                && HasInt32Value(rawData, offset + 484, 0)
                && HasInt32Value(rawData, offset + 488, -1)
                && HasInt32Value(rawData, offset + 492, 0)
                && HasInt32Value(rawData, offset + 496, -1082130432)
                && HasInt32Value(rawData, offset + 500, 0)
                && HasInt32Value(rawData, offset + 504, 0)
                && HasInt32Value(rawData, offset + 508, 2)
                && HasInt32Value(rawData, offset + 512, 2)
                && HasInt32Value(rawData, offset + 516, 4)
                && IsZeroFilled(rawData, offset + 520, 36)
                && HasInt32Value(rawData, offset + 556, 1056964608);
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
                        || !IsStrongManagedReferenceHeader(header)
                        || (preferExpectedRid && !expectedRids.Contains(header.Rid)))
                    {
                        continue;
                    }
                    if (CanParseRemainingManagedReferenceHeaders(
                        rawData,
                        candidate,
                        remainingHeaderCount,
                        usedRids,
                        requireStrongHeaders: true))
                    {
                        headerOffset = candidate;
                        return true;
                    }
                }
            }

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
                    if (CanParseRemainingManagedReferenceHeaders(
                        rawData,
                        candidate,
                        remainingHeaderCount,
                        usedRids,
                        requireStrongHeaders: false))
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
            IReadOnlySet<long> priorRids,
            bool requireStrongHeaders
        )
        {
            var used = new HashSet<long>(priorRids);
            var pos = start;

            for (var i = 0; i < remainingHeaderCount; i++)
            {
                if (!TryReadManagedReferenceHeader(rawData, pos, out var header)
                    || !used.Add(header.Rid)
                    || (requireStrongHeaders && !IsStrongManagedReferenceHeader(header)))
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
                        && !used.Contains(nextHeader.Rid)
                        && (!requireStrongHeaders || IsStrongManagedReferenceHeader(nextHeader)))
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

        private static bool IsStrongManagedReferenceHeader(ManagedReferenceHeader header)
        {
            if (header == null || header.IsNullSentinel || header.Rid <= 0)
            {
                return false;
            }

            var fullName = string.IsNullOrEmpty(header.Namespace)
                ? header.ClassName
                : $"{header.Namespace}.{header.ClassName}";
            if (Studio.assemblyLoader?.Loaded == true
                && Studio.assemblyLoader.GetTypeDefinition(header.AssemblyName, fullName) != null)
            {
                return true;
            }

            return LooksLikeRuntimeAssemblyName(header.AssemblyName)
                && LooksLikeRuntimeNamespace(header.Namespace);
        }

        private static bool LooksLikeRuntimeAssemblyName(string value)
        {
            return !string.IsNullOrEmpty(value)
                && value.Contains('.', StringComparison.Ordinal)
                && LooksLikeManagedReferenceAssemblyName(value);
        }

        private static bool LooksLikeRuntimeNamespace(string value)
        {
            return !string.IsNullOrEmpty(value)
                && value.Contains('.', StringComparison.Ordinal)
                && LooksLikeManagedReferenceNamespace(value);
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
                    || ch == '/'
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
            {
                return ExportEmptyMesh(item, m_Mesh, exportPath, MeshNoOutputReason(m_Mesh));
            }
            if (!TryExportFile(exportPath, item, ".obj", out var exportFullPath))
            {
                LogMeshNoOutput(item, m_Mesh, "output_path_unavailable");
                return false;
            }
            var sb = new StringBuilder();
            sb.AppendLine("g " + m_Mesh.m_Name);
            #region Vertices
            if (m_Mesh.m_Vertices == null || m_Mesh.m_Vertices.Length == 0)
            {
                LogMeshNoOutput(item, m_Mesh);
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
            var fbxExportPath = exportFullPath + ".fbx";
            if (File.Exists(fbxExportPath))
            {
                File.Delete(fbxExportPath);
            }
            if (convert.MeshList.Count == 0)
            {
                Directory.Delete(exportFullPath, true);
                return ExportEmptyAnimatorMarker(item, m_Animator, convert, exportPath, "no_mesh");
            }
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
            ExportFbx(convert, fbxExportPath);
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
