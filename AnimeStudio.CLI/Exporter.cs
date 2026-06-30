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
                OrderedDictionary managedReferenceRecoveryFailure = null;
                OrderedDictionary rawPayloadDecodeDiagnostic = null;
                HashSet<long> expectedManagedReferenceRids = null;
                var recoveredManagedReferencesTail = false;
                var recoveredManagedReferencesFullyDecoded = false;
                var recoveredManagedReferencesStatus = "notRecovered";
                var rawPayloadDecodedEntryCount = 0;
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
                    if (TryRecoverManagedReferences(rawData, referencesStartOffset, expectedManagedReferenceRids, out var recoveredReferences, out var recoveryFailure))
                    {
                        recoveredManagedReferences = recoveredReferences;
                        type["references"] = recoveredReferences;
                        recoveredManagedReferencesTail = true;
                        recoveredManagedReferencesStatus = GetManagedReferenceRecoveryStatus(recoveredReferences);
                        recoveredManagedReferencesFullyDecoded = string.Equals(recoveredManagedReferencesStatus, "fullyDecoded", StringComparison.Ordinal);
                    }
                    else
                    {
                        managedReferenceRecoveryFailure = recoveryFailure;
                    }
                }
                if (type != null
                    && !recoveredManagedReferencesTail
                    && TryEnrichKnownManagedReferencePayloadsFromRawData(
                        type,
                        rawData,
                        out var enrichedReferences,
                        out rawPayloadDecodeDiagnostic,
                        out rawPayloadDecodedEntryCount))
                {
                    type["references"] = enrichedReferences;
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
                            { "status", recoveredManagedReferencesStatus },
                            { "source", partialTypeTreeSourceLabel ?? typeTreeSource },
                            { "bytesReadBeforeRecovery", partialTypeTreeBytesRead },
                            { "preRegistryRidCount", expectedManagedReferenceRids?.Count ?? 0 },
                            { "expectedRidCount", expectedManagedReferenceRids?.Count ?? 0 },
                        };
                        if (string.Equals(recoveredManagedReferencesStatus, "heuristic", StringComparison.Ordinal))
                        {
                            recovery["decodeError"] = $"{partialTypeTreeException.GetType().Name}: {partialTypeTreeException.Message}";
                        }
                        else if (!recoveredManagedReferencesFullyDecoded)
                        {
                            recovery["typeTreeDecodeError"] = $"{partialTypeTreeException.GetType().Name}: {partialTypeTreeException.Message}";
                        }
                        if (partialTypeTreeStoppedAt != null && !recoveredManagedReferencesFullyDecoded)
                        {
                            recovery["stoppedAt"] = partialTypeTreeStoppedAt;
                            if (partialTypeTreeStoppedAt.Contains("startOffset"))
                            {
                                recovery["registryStartOffset"] = partialTypeTreeStoppedAt["startOffset"];
                            }
                            if (partialTypeTreeStoppedAt.Contains("offset"))
                            {
                                recovery["typeTreeFailureOffset"] = partialTypeTreeStoppedAt["offset"];
                            }
                        }
                        if (recoveredManagedReferences?.Contains("count") == true)
                        {
                            recovery["registryCount"] = recoveredManagedReferences["count"];
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
                        if (managedReferenceRecoveryFailure != null)
                        {
                            managedReferenceRecoveryFailure["source"] = partialTypeTreeSourceLabel ?? typeTreeSource;
                            managedReferenceRecoveryFailure["partialTypeTreeBytesRead"] = partialTypeTreeBytesRead;
                            managedReferenceRecoveryFailure["partialTypeTreeError"] = $"{partialTypeTreeException.GetType().Name}: {partialTypeTreeException.Message}";
                            if (partialTypeTreeStoppedAt != null)
                            {
                                managedReferenceRecoveryFailure["stoppedAt"] = partialTypeTreeStoppedAt;
                            }
                            meta["managedReferencesRegistryRecoveryAttempted"] = true;
                            meta["managedReferencesRegistryRecoveryFailure"] = managedReferenceRecoveryFailure;
                        }
                    }
                }
                if (rawPayloadDecodedEntryCount > 0)
                {
                    meta["managedReferencesRawPayloadDecoded"] = true;
                    meta["managedReferencesRawPayloadDecodedEntryCount"] = rawPayloadDecodedEntryCount;
                    if (rawPayloadDecodeDiagnostic != null)
                    {
                        meta["managedReferencesRawPayloadDecode"] = rawPayloadDecodeDiagnostic;
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

        private static bool TryEnrichKnownManagedReferencePayloadsFromRawData(
            OrderedDictionary type,
            byte[] rawData,
            out OrderedDictionary enrichedReferences,
            out OrderedDictionary diagnostic,
            out int decodedEntryCount
        )
        {
            enrichedReferences = null;
            diagnostic = null;
            decodedEntryCount = 0;
            if (type == null
                || rawData == null
                || rawData.Length < 8
                || !type.Contains("references")
                || type["references"] is not OrderedDictionary references)
            {
                return false;
            }

            if (!TryGetManagedReferenceEntries(references, out var existingEntries) || existingEntries.Count == 0)
            {
                return false;
            }

            var candidateEntries = existingEntries
                .Where(entry => IsEmptyManagedReferenceData(entry) && IsKnownRawPayloadMergeCandidate(entry))
                .ToList();
            if (candidateEntries.Count == 0)
            {
                return false;
            }

            var existingByRid = new Dictionary<long, OrderedDictionary>();
            foreach (var entry in existingEntries)
            {
                if (!TryGetManagedReferenceRid(entry, out var rid) || existingByRid.ContainsKey(rid))
                {
                    return false;
                }
                existingByRid[rid] = entry;
            }

            var expectedRids = new HashSet<long>(existingByRid.Keys);
            var expectedCount = existingEntries.Count;
            int? expectedVersion = null;
            if (TryGetDictionaryValue(references, "version", out var versionValue)
                && TryConvertToInt64(versionValue, out var version64)
                && version64 >= int.MinValue
                && version64 <= int.MaxValue)
            {
                expectedVersion = (int)version64;
            }

            for (var startOffset = 0; startOffset <= rawData.Length - 8; startOffset += 4)
            {
                var version = BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(startOffset, 4));
                if (version < 1 || version > 3 || (expectedVersion.HasValue && version != expectedVersion.Value))
                {
                    continue;
                }

                var count = BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(startOffset + 4, 4));
                if (count != expectedCount)
                {
                    continue;
                }

                if (!TryRecoverManagedReferences(rawData, startOffset, expectedRids, out var recoveredReferences, out _)
                    || !TryGetManagedReferenceEntries(recoveredReferences, out var recoveredEntries)
                    || recoveredEntries.Count != expectedCount)
                {
                    continue;
                }

                var recoveredByRid = new Dictionary<long, OrderedDictionary>();
                var typeMismatch = false;
                foreach (var recoveredEntry in recoveredEntries)
                {
                    if (!TryGetManagedReferenceRid(recoveredEntry, out var rid)
                        || recoveredByRid.ContainsKey(rid)
                        || !existingByRid.TryGetValue(rid, out var existingEntry)
                        || !ManagedReferenceTypesEqual(existingEntry, recoveredEntry))
                    {
                        typeMismatch = true;
                        break;
                    }
                    recoveredByRid[rid] = recoveredEntry;
                }
                if (typeMismatch)
                {
                    continue;
                }

                var mergedCount = 0;
                foreach (var existingEntry in candidateEntries)
                {
                    if (!TryGetManagedReferenceRid(existingEntry, out var rid)
                        || !recoveredByRid.TryGetValue(rid, out var recoveredEntry)
                        || !TryGetDictionaryValue(recoveredEntry, "data", out var recoveredData)
                        || !IsDecodedManagedReferenceData(recoveredData))
                    {
                        continue;
                    }

                    SetOrderedDictionaryValue(existingEntry, "data", recoveredData);
                    if (TryGetDictionaryValue(recoveredEntry, "dataOffset", out var dataOffset))
                    {
                        SetOrderedDictionaryValue(existingEntry, "dataOffset", dataOffset);
                    }
                    if (TryGetDictionaryValue(recoveredEntry, "dataLength", out var dataLength))
                    {
                        SetOrderedDictionaryValue(existingEntry, "dataLength", dataLength);
                    }
                    mergedCount++;
                }

                if (mergedCount == 0)
                {
                    continue;
                }

                SetOrderedDictionaryValue(references, "$rawPayloadDecoded", true);
                SetOrderedDictionaryValue(references, "rawPayloadDecodedEntryCount", mergedCount);
                SetOrderedDictionaryValue(references, "rawPayloadRegistryStartOffset", startOffset);
                enrichedReferences = references;
                decodedEntryCount = mergedCount;
                diagnostic = new OrderedDictionary
                {
                    { "source", "raw MonoBehaviour payload" },
                    { "registryStartOffset", startOffset },
                    { "registryCount", expectedCount },
                    { "decodedEntryCount", mergedCount },
                    { "scope", "empty known managed-reference payloads only" },
                };
                return true;
            }

            var directMergedCount = 0;
            foreach (var existingEntry in candidateEntries.Where(IsEmptyManagedReferenceData))
            {
                if (!TryDecodeKnownManagedReferencePayloadByHeaderScan(
                    existingEntry,
                    rawData,
                    expectedRids,
                    out var decodedData,
                    out var dataOffset,
                    out var dataLength))
                {
                    continue;
                }

                SetOrderedDictionaryValue(existingEntry, "data", decodedData);
                SetOrderedDictionaryValue(existingEntry, "dataOffset", dataOffset);
                SetOrderedDictionaryValue(existingEntry, "dataLength", dataLength);
                directMergedCount++;
            }

            if (directMergedCount > 0)
            {
                SetOrderedDictionaryValue(references, "$rawPayloadDecoded", true);
                SetOrderedDictionaryValue(references, "rawPayloadDecodedEntryCount", directMergedCount);
                enrichedReferences = references;
                decodedEntryCount = directMergedCount;
                diagnostic = new OrderedDictionary
                {
                    { "source", "raw MonoBehaviour payload header scan" },
                    { "decodedEntryCount", directMergedCount },
                    { "scope", "empty known managed-reference payloads only" },
                };
                return true;
            }

            return false;
        }

        private static bool TryDecodeKnownManagedReferencePayloadByHeaderScan(
            OrderedDictionary existingEntry,
            byte[] rawData,
            IReadOnlySet<long> expectedRids,
            out OrderedDictionary decodedData,
            out int dataOffset,
            out int dataLength
        )
        {
            decodedData = null;
            dataOffset = 0;
            dataLength = 0;
            if (!TryGetManagedReferenceRid(existingEntry, out var expectedRid)
                || !TryGetManagedReferenceTypeStrings(existingEntry, out var expectedClass, out var expectedNamespace, out var expectedAssembly))
            {
                return false;
            }

            for (var candidate = 0; candidate <= rawData.Length - MinManagedReferenceHeaderBytes; candidate += 4)
            {
                if (!TryReadManagedReferenceHeader(rawData, candidate, out var header)
                    || header.Rid != expectedRid
                    || !string.Equals(header.ClassName, expectedClass, StringComparison.Ordinal)
                    || !string.Equals(header.Namespace, expectedNamespace, StringComparison.Ordinal)
                    || !string.Equals(header.AssemblyName, expectedAssembly, StringComparison.Ordinal))
                {
                    continue;
                }

                var nextOffset = FindNextExpectedManagedReferenceHeaderOffset(rawData, header.DataStart, expectedRids, expectedRid);
                if (nextOffset < 0)
                {
                    nextOffset = rawData.Length;
                }
                if (nextOffset < header.DataStart)
                {
                    continue;
                }

                dataOffset = header.DataStart;
                dataLength = nextOffset - header.DataStart;
                var recoveredByRid = new Dictionary<long, ManagedReferenceHeader> { { header.Rid, header } };
                var remainingStringHintBudget = MaxHeuristicStringHintsPerReference;
                var remainingRidLinkBudget = MaxHeuristicRidLinksPerReference;
                var data = BuildManagedReferenceData(
                    header,
                    rawData,
                    dataOffset,
                    dataLength,
                    recoveredByRid,
                    ref remainingStringHintBudget,
                    ref remainingRidLinkBudget);
                if (!IsDecodedManagedReferenceData(data))
                {
                    continue;
                }

                decodedData = data;
                return true;
            }

            return false;
        }

        private static int FindNextExpectedManagedReferenceHeaderOffset(
            byte[] rawData,
            int start,
            IReadOnlySet<long> expectedRids,
            long currentRid
        )
        {
            if (expectedRids == null || expectedRids.Count <= 1)
            {
                return -1;
            }

            var candidate = (start + 3) & ~3;
            for (; candidate <= rawData.Length - MinManagedReferenceHeaderBytes; candidate += 4)
            {
                if (TryReadManagedReferenceHeader(rawData, candidate, out var header)
                    && header.Rid != currentRid
                    && expectedRids.Contains(header.Rid))
                {
                    return candidate;
                }
            }

            return -1;
        }

        private static bool TryGetManagedReferenceEntries(OrderedDictionary references, out List<OrderedDictionary> entries)
        {
            entries = new List<OrderedDictionary>();
            if (references == null
                || !TryGetDictionaryValue(references, "RefIds", out var refIds)
                || refIds is string
                || refIds is not IEnumerable enumerable)
            {
                return false;
            }

            foreach (var item in enumerable)
            {
                if (item is OrderedDictionary entry)
                {
                    entries.Add(entry);
                }
            }
            return entries.Count > 0;
        }

        private static bool TryGetManagedReferenceRid(OrderedDictionary entry, out long rid)
        {
            rid = 0;
            return entry != null
                && TryGetDictionaryValue(entry, "rid", out var value)
                && TryConvertToInt64(value, out rid);
        }

        private static bool IsEmptyManagedReferenceData(OrderedDictionary entry)
        {
            return entry != null
                && TryGetDictionaryValue(entry, "data", out var data)
                && data is OrderedDictionary dictionary
                && dictionary.Count == 0;
        }

        private static bool IsKnownRawPayloadMergeCandidate(OrderedDictionary entry)
        {
            if (!TryGetManagedReferenceTypeStrings(entry, out var className, out var namespaceName, out var assemblyName)
                || !string.Equals(assemblyName, "Gameplay.Beyond", StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(namespaceName, "Beyond.Gameplay", StringComparison.Ordinal)
                && (string.Equals(className, "PlaySound", StringComparison.Ordinal)
                    || string.Equals(className, "PlaySingleSound", StringComparison.Ordinal)
                    || string.Equals(className, "PlaySoundByParticleCount", StringComparison.Ordinal)))
            {
                return true;
            }

            return string.Equals(namespaceName, "Beyond.Gameplay", StringComparison.Ordinal)
                || string.Equals(namespaceName, "Beyond.Gameplay.Conditions", StringComparison.Ordinal)
                || string.Equals(namespaceName, "Beyond.Gameplay.Actions", StringComparison.Ordinal);
        }

        private static bool ManagedReferenceTypesEqual(OrderedDictionary left, OrderedDictionary right)
        {
            return TryGetManagedReferenceTypeStrings(left, out var leftClass, out var leftNamespace, out var leftAssembly)
                && TryGetManagedReferenceTypeStrings(right, out var rightClass, out var rightNamespace, out var rightAssembly)
                && string.Equals(leftClass, rightClass, StringComparison.Ordinal)
                && string.Equals(leftNamespace, rightNamespace, StringComparison.Ordinal)
                && string.Equals(leftAssembly, rightAssembly, StringComparison.Ordinal);
        }

        private static bool TryGetManagedReferenceTypeStrings(
            OrderedDictionary entry,
            out string className,
            out string namespaceName,
            out string assemblyName
        )
        {
            className = null;
            namespaceName = null;
            assemblyName = null;
            if (entry == null
                || !TryGetDictionaryValue(entry, "type", out var typeObject)
                || typeObject is not OrderedDictionary type)
            {
                return false;
            }

            className = TryGetDictionaryValue(type, "class", out var classValue) ? classValue?.ToString() ?? string.Empty : string.Empty;
            namespaceName = TryGetDictionaryValue(type, "ns", out var namespaceValue) ? namespaceValue?.ToString() ?? string.Empty : string.Empty;
            assemblyName = TryGetDictionaryValue(type, "asm", out var assemblyValue) ? assemblyValue?.ToString() ?? string.Empty : string.Empty;
            return true;
        }

        private static bool IsDecodedManagedReferenceData(object data)
        {
            return data is OrderedDictionary dictionary
                && IsTrueDictionaryFlag(dictionary, "$decoded")
                && !IsTrueDictionaryFlag(dictionary, "$unparsed")
                && !IsTrueDictionaryFlag(dictionary, "$heuristic");
        }

        private static bool IsTrueDictionaryFlag(OrderedDictionary dictionary, string key)
        {
            return dictionary != null
                && TryGetDictionaryValue(dictionary, key, out var value)
                && value is bool flag
                && flag;
        }

        private static bool TryGetDictionaryValue(OrderedDictionary dictionary, string key, out object value)
        {
            value = null;
            if (dictionary == null || !dictionary.Contains(key))
            {
                return false;
            }
            value = dictionary[key];
            return true;
        }

        private static void SetOrderedDictionaryValue(OrderedDictionary dictionary, string key, object value)
        {
            if (dictionary.Contains(key))
            {
                dictionary[key] = value;
            }
            else
            {
                dictionary.Add(key, value);
            }
        }

        private static bool TryRecoverManagedReferences(
            byte[] rawData,
            long startOffset,
            IReadOnlySet<long> expectedRids,
            out OrderedDictionary references,
            out OrderedDictionary diagnostic
        )
        {
            references = null;
            diagnostic = null;
            expectedRids ??= new HashSet<long>();
            if (rawData == null)
            {
                diagnostic = BuildManagedReferenceRecoveryFailure("rawDataMissing", startOffset, -1, expectedRidCount: expectedRids.Count);
                return false;
            }
            if (startOffset < 0 || startOffset > rawData.Length - 8)
            {
                diagnostic = BuildManagedReferenceRecoveryFailure("registryStartOffsetOutOfRange", startOffset, rawData.Length, expectedRidCount: expectedRids.Count);
                return false;
            }

            var pos = (int)startOffset;
            var version = BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(pos, 4));
            pos += 4;
            var count = BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(pos, 4));
            pos += 4;
            if (version < 1 || version > 3)
            {
                diagnostic = BuildManagedReferenceRecoveryFailure("invalidRegistryVersion", startOffset, rawData.Length, version, count, expectedRids.Count);
                return false;
            }
            if (count < 0 || count > 10000)
            {
                diagnostic = BuildManagedReferenceRecoveryFailure("invalidRegistryCount", startOffset, rawData.Length, version, count, expectedRids.Count);
                return false;
            }
            if (count < expectedRids.Count)
            {
                diagnostic = BuildManagedReferenceRecoveryFailure("registryCountLessThanExpectedRidCount", startOffset, rawData.Length, version, count, expectedRids.Count);
                return false;
            }

            if (!TryParseManagedReferenceHeaders(rawData, pos, count, expectedRids, out var headers, out diagnostic))
            {
                if (diagnostic != null)
                {
                    diagnostic["registryStartOffset"] = startOffset;
                    diagnostic["version"] = version;
                    diagnostic["count"] = count;
                    diagnostic["expectedRidCount"] = expectedRids.Count;
                }
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
                if (!recoveredRids.Add(header.Rid))
                {
                    diagnostic = BuildManagedReferenceRecoveryFailure("duplicateRecoveredRid", startOffset, rawData.Length, version, count, expectedRids.Count, headerIndex: i, headerOffset: header.HeaderStart, rid: header.Rid);
                    return false;
                }
                if (nextPos < header.DataStart)
                {
                    diagnostic = BuildManagedReferenceRecoveryFailure("entryDataRangeInvalid", startOffset, rawData.Length, version, count, expectedRids.Count, headerIndex: i, headerOffset: header.HeaderStart, rid: header.Rid, detail: $"next header offset {nextPos} is before data start {header.DataStart}");
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
                    diagnostic = BuildManagedReferenceRecoveryFailure("missingExpectedRid", startOffset, rawData.Length, version, count, expectedRids.Count, rid: expectedRid);
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
            var hasWeakHeader = headers.Any(header => !header.IsNullSentinel && !IsStrongManagedReferenceHeader(header));
            var hasHeuristicPayload = entries.Any(ContainsManagedReferenceHeuristicMarker);
            var hasPartialPayload = entries.Any(ContainsManagedReferencePartialMarker);
            if (hasWeakHeader || hasHeuristicPayload)
            {
                references["$heuristic"] = true;
                references["stringHintLimitPerReference"] = MaxHeuristicStringHintsPerReference;
                references["stringHintLimitPerObject"] = MaxHeuristicStringHintsPerObject;
                references["ridLinkLimitPerReference"] = MaxHeuristicRidLinksPerReference;
                references["ridLinkLimitPerObject"] = MaxHeuristicRidLinksPerObject;
            }
            else if (hasPartialPayload)
            {
                references["$partial"] = true;
            }
            else
            {
                references["$decoded"] = true;
            }
            diagnostic = null;
            return true;
        }

        private static OrderedDictionary BuildManagedReferenceRecoveryFailure(
            string reason,
            long registryStartOffset,
            int rawDataLength,
            int? version = null,
            int? count = null,
            int? expectedRidCount = null,
            int? headerIndex = null,
            int? headerOffset = null,
            int? searchStartOffset = null,
            int? remainingHeaderCount = null,
            long? rid = null,
            string detail = null
        )
        {
            var diagnostic = new OrderedDictionary
            {
                { "reason", reason },
                { "field", "references" },
                { "type", "ManagedReferencesRegistry" },
                { "registryStartOffset", registryStartOffset },
                { "rawDataLength", rawDataLength },
            };
            if (version.HasValue)
            {
                diagnostic["version"] = version.Value;
            }
            if (count.HasValue)
            {
                diagnostic["count"] = count.Value;
            }
            if (expectedRidCount.HasValue)
            {
                diagnostic["expectedRidCount"] = expectedRidCount.Value;
            }
            if (headerIndex.HasValue)
            {
                diagnostic["headerIndex"] = headerIndex.Value;
            }
            if (headerOffset.HasValue)
            {
                diagnostic["headerOffset"] = headerOffset.Value;
            }
            if (searchStartOffset.HasValue)
            {
                diagnostic["searchStartOffset"] = searchStartOffset.Value;
            }
            if (remainingHeaderCount.HasValue)
            {
                diagnostic["remainingHeaderCount"] = remainingHeaderCount.Value;
            }
            if (rid.HasValue)
            {
                diagnostic["rid"] = rid.Value;
            }
            if (!string.IsNullOrEmpty(detail))
            {
                diagnostic["detail"] = detail;
            }
            return diagnostic;
        }

        private static string GetManagedReferenceRecoveryStatus(object value)
        {
            if (ContainsManagedReferenceHeuristicMarker(value))
            {
                return "heuristic";
            }
            if (ContainsManagedReferencePartialMarker(value))
            {
                return "partialDecoded";
            }
            return "fullyDecoded";
        }

        private static bool ContainsManagedReferenceRecoveryMarker(object value)
        {
            return ContainsManagedReferenceHeuristicMarker(value)
                || ContainsManagedReferencePartialMarker(value);
        }

        private static bool ContainsManagedReferenceHeuristicMarker(object value)
        {
            return ContainsManagedReferenceMarker(value, includeHeuristic: true, includePartial: false);
        }

        private static bool ContainsManagedReferencePartialMarker(object value)
        {
            return ContainsManagedReferenceMarker(value, includeHeuristic: false, includePartial: true);
        }

        private static bool ContainsManagedReferenceMarker(object value, bool includeHeuristic, bool includePartial)
        {
            if (value == null)
            {
                return false;
            }

            if (value is OrderedDictionary dictionary)
            {
                if (includeHeuristic
                    && ((dictionary.Contains("$heuristic") && dictionary["$heuristic"] is bool heuristic && heuristic)
                        || (dictionary.Contains("$unparsed") && dictionary["$unparsed"] is bool unparsed && unparsed)))
                {
                    return true;
                }
                if (includePartial
                    && dictionary.Contains("$partial")
                    && dictionary["$partial"] is bool partial
                    && partial)
                {
                    return true;
                }

                foreach (DictionaryEntry entry in dictionary)
                {
                    if (ContainsManagedReferenceMarker(entry.Value, includeHeuristic, includePartial))
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
                    if (ContainsManagedReferenceMarker(item, includeHeuristic, includePartial))
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
            out List<ManagedReferenceHeader> headers,
            out OrderedDictionary diagnostic
        )
        {
            headers = new List<ManagedReferenceHeader>(count);
            diagnostic = null;
            expectedRids ??= new HashSet<long>();
            var registryStartOffset = firstHeaderOffset - 8;
            var usedRids = new HashSet<long>();
            var pos = firstHeaderOffset;

            for (var i = 0; i < count; i++)
            {
                if (!TryReadManagedReferenceHeader(rawData, pos, out var header))
                {
                    diagnostic = BuildManagedReferenceRecoveryFailure(
                        i == 0 ? "firstHeaderInvalid" : "remainingHeaderChainInvalid",
                        registryStartOffset,
                        rawData?.Length ?? -1,
                        expectedRidCount: expectedRids.Count,
                        headerIndex: i,
                        headerOffset: pos
                    );
                    return false;
                }
                if (!usedRids.Add(header.Rid))
                {
                    diagnostic = BuildManagedReferenceRecoveryFailure(
                        "duplicateHeaderRid",
                        registryStartOffset,
                        rawData?.Length ?? -1,
                        expectedRidCount: expectedRids.Count,
                        headerIndex: i,
                        headerOffset: pos,
                        rid: header.Rid
                    );
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
                    diagnostic = BuildManagedReferenceRecoveryFailure(
                        "nextHeaderNotFound",
                        registryStartOffset,
                        rawData?.Length ?? -1,
                        expectedRidCount: expectedRids.Count,
                        headerIndex: i,
                        headerOffset: header.HeaderStart,
                        searchStartOffset: header.DataStart,
                        remainingHeaderCount: count - i - 1,
                        rid: header.Rid
                    );
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

            if (TryDecodeGuideManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                recoveredByRid,
                out decodedData))
            {
                return decodedData;
            }

            if (TryDecodeCoreGameplayManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                recoveredByRid,
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

            if (TryDecodeGeneralGameplayManagedReferenceData(
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

            if (ShouldEmitFullManagedReferencePayloadTrace(header))
            {
                data["diagnosticNote"] = "Full raw payload trace for unresolved managed-reference layout recovery; this entry is intentionally still marked $unparsed.";
                data["diagnosticFullPayloadHex"] = BuildPayloadHex(rawData, offset, length);
                data["diagnosticRawWordTrace"] = CollectDiagnosticRawWordTrace(rawData, offset, length);
                if (TryBuildTargetSettingsStructuredDiagnostic(header, rawData, offset, length, recoveredByRid, out var structuredLayout))
                {
                    data["diagnosticStructuredLayout"] = structuredLayout;
                }
            }

            return data;
        }

        private static bool ShouldEmitFullManagedReferencePayloadTrace(ManagedReferenceHeader header)
        {
            if (header == null || !string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal))
            {
                return string.Equals(header.ClassName, "CharacterRootComponentData", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "CreateBuffAction/Data", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "ModifyDynamicBlackboard/Data", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "StoreBuffCount/Data", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "CheckBuffStackNumAdvanced/Data", StringComparison.Ordinal);
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal))
            {
                return string.Equals(header.ClassName, "CheckObjectTypeMatch/Data", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "CheckMainCharacterCondition/Data", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "CheckTargetsEqual/Data", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "CheckBuffStackNum/Data", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "CheckBuffStackNumByTag/Data", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "CheckHp/Data", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "CheckTagMatch/Data", StringComparison.Ordinal);
            }

            return false;
        }

        private static string BuildPayloadHex(byte[] rawData, int offset, int length)
        {
            if (rawData == null || offset < 0 || length <= 0 || offset > rawData.Length || offset + length > rawData.Length)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(length * 2);
            for (var i = 0; i < length; i++)
            {
                builder.Append(rawData[offset + i].ToString("x2"));
            }
            return builder.ToString();
        }

        private static bool TryBuildTargetSettingsStructuredDiagnostic(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid,
            out OrderedDictionary diagnostic
        )
        {
            diagnostic = null;
            if (header == null
                || rawData == null
                || offset < 0
                || length <= 0
                || (length % 4) != 0
                || offset > rawData.Length
                || offset + length > rawData.Length)
            {
                return false;
            }

            try
            {
                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                diagnostic = new OrderedDictionary
                {
                    { "$partial", true },
                    { "status", "structured-diagnostic" },
                    { "offset", offset },
                    { "length", length },
                    { "layoutNote", "This structure is emitted for byte-level TargetSettings recovery only. The parent payload remains $unparsed because TargetSettings selector-data suffix fields are not fully named semantically." },
                    { "abilityActionData", ReadDiagnosticAbilityActionDataPrefix(reader) },
                };

                if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CheckMainCharacterCondition/Data", StringComparison.Ordinal))
                {
                    diagnostic["checkTarget"] = ReadDiagnosticTargetSettings(reader, "checkMainCharacterCondition.checkTarget", offset, recoveredByRid);
                }
                else if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CheckObjectTypeMatch/Data", StringComparison.Ordinal))
                {
                    diagnostic["checkTarget"] = ReadDiagnosticTargetSettings(reader, "checkObjectTypeMatch.checkTarget", offset, recoveredByRid);
                    diagnostic["objectTypeMask"] = BuildPayloadHash32(reader.ReadInt32("checkObjectTypeMatch.objectTypeMask"));
                }
                else if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CheckTargetsEqual/Data", StringComparison.Ordinal))
                {
                    diagnostic["source"] = ReadDiagnosticTargetSettings(reader, "checkTargetsEqual.source", offset, recoveredByRid);
                    diagnostic["target"] = ReadDiagnosticTargetSettings(reader, "checkTargetsEqual.target", offset, recoveredByRid);
                }
                else if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CheckBuffStackNum/Data", StringComparison.Ordinal))
                {
                    diagnostic["checkTarget"] = ReadDiagnosticTargetSettings(reader, "checkBuffStackNum.checkTarget", offset, recoveredByRid);
                    diagnostic["buffIdCandidate"] = ReadPayloadAlignedAsciiStringWithZeroPadding(reader, "checkBuffStackNum.buffId", 128);
                    diagnostic["compareTypeCandidate"] = ReadPayloadNamedEnum32(reader, "checkBuffStackNum.compareType", new[] { "LT", "LE", "GT", "GE", "Equals" });
                    diagnostic["valueCandidate"] = ReadPayloadBlackboardDoubleWithZeroPadding(reader, "checkBuffStackNum.value", 128);
                    diagnostic["tailNote"] = "Metadata leaves the buffId field type unresolved locally; current bytes look like one aligned string plus compareType and BlackboardDouble.";
                }
                else if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CheckHp/Data", StringComparison.Ordinal))
                {
                    diagnostic["hpOwner"] = ReadDiagnosticTargetSettings(reader, "checkHp.hpOwner", offset, recoveredByRid);
                    diagnostic["compareCandidate"] = ReadPayloadNamedEnum32(reader, "checkHp.compare", new[] { "LT", "LE", "GT", "GE", "Equals" });
                    diagnostic["isRatioCandidate"] = reader.ReadBool32("checkHp.isRatio");
                    diagnostic["valueCandidate"] = ReadPayloadBlackboardDoubleWithZeroPadding(reader, "checkHp.value", 128);
                    diagnostic["tailNote"] = "Installed IL2CPP/MemoryPack metadata exposes hpOwner, compare, isRatio, and BlackboardDouble value. hpOwner remains partial because TargetSettings selector/suffix semantics are unresolved.";
                }
                else if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CheckTagMatch/Data", StringComparison.Ordinal))
                {
                    diagnostic["checkTarget"] = ReadDiagnosticTargetSettings(reader, "checkTagMatch.checkTarget", offset, recoveredByRid);
                    diagnostic["queryCandidate"] = ReadPayloadGameplayTagQueryWithZeroPadding(reader, "checkTagMatch.query", 16, 256);
                    diagnostic["tailNote"] = "Installed IL2CPP/MemoryPack metadata exposes checkTarget and GameplayTagQuery. checkTarget remains partial because TargetSettings selector/suffix semantics are unresolved.";
                }
                else if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CheckBuffStackNumByTag/Data", StringComparison.Ordinal))
                {
                    diagnostic["checkTarget"] = ReadDiagnosticTargetSettings(reader, "checkBuffStackNumByTag.checkTarget", offset, recoveredByRid);
                    diagnostic["tagQueryCandidate"] = ReadPayloadGameplayTagQueryWithZeroPadding(reader, "checkBuffStackNumByTag.tagQuery", 8, 256);
                    diagnostic["buffStackNumTypeCandidate"] = ReadPayloadEnum32(reader, "checkBuffStackNumByTag.buffStackNumType", 0, 16);
                    diagnostic["compareTypeCandidate"] = ReadPayloadNamedEnum32(reader, "checkBuffStackNumByTag.compareType", new[] { "LT", "LE", "GT", "GE", "Equals" });
                    diagnostic["valueCandidate"] = ReadPayloadBlackboardDoubleWithZeroPadding(reader, "checkBuffStackNumByTag.value", 128);
                    diagnostic["tailNote"] = "Tag query bytes are structured, but generic/list metadata remains unresolved locally; this remains a candidate semantic layout.";
                }
                else if (string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CheckBuffStackNumAdvanced/Data", StringComparison.Ordinal))
                {
                    diagnostic["checkTarget"] = ReadDiagnosticTargetSettings(reader, "checkBuffStackNumAdvanced.checkTarget", offset, recoveredByRid);
                    diagnostic["buffSettingsCandidate"] = ReadDiagnosticBuffFindSettingsCandidate(reader, "checkBuffStackNumAdvanced.buffSettings");
                    diagnostic["buffStackNumTypeCandidate"] = ReadPayloadEnum32(reader, "checkBuffStackNumAdvanced.buffStackNumType", 0, 16);
                    diagnostic["compareTypeCandidate"] = ReadPayloadNamedEnum32(reader, "checkBuffStackNumAdvanced.compareType", new[] { "LT", "LE", "GT", "GE", "Equals" });
                    diagnostic["valueCandidate"] = ReadPayloadBlackboardDoubleWithZeroPadding(reader, "checkBuffStackNumAdvanced.value", 128);
                    diagnostic["limitSkillCastIdCandidate"] = reader.ReadBool32("checkBuffStackNumAdvanced.limitSkillCastId");
                    diagnostic["tailNote"] = "Installed IL2CPP metadata names checkTarget, buffSettings, buffStackNumType, compareType, value, and limitSkillCastId. BuffFindSettings is still emitted as partial because Environment/Context variants and generic list type names are not fully proven locally.";
                }
                else if (string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CreateBuffAction/Data", StringComparison.Ordinal))
                {
                    diagnostic["buffsCandidate"] = ReadDiagnosticCreateBuffActionBuffs(reader, "createBuffAction.buffs");
                    diagnostic["countCandidate"] = ReadPayloadBlackboardDoubleWithZeroPadding(reader, "createBuffAction.count", 128);
                    diagnostic["targetSettings"] = ReadDiagnosticTargetSettings(reader, "createBuffAction.targetSettings", offset, recoveredByRid);
                    diagnostic["buffSourceCandidate"] = BuildPayloadHash32(reader.ReadInt32("createBuffAction.buffSource"));
                    diagnostic["contextKeyCandidate"] = ReadPayloadAlignedAsciiStringWithZeroPadding(reader, "createBuffAction.contextKey", 128);
                    diagnostic["postContextTailCandidate"] = ReadDiagnosticCreateBuffActionPostContextTail(reader, "createBuffAction.postContextTail");
                    diagnostic["tailNote"] = "IL2CPP metadata names the fields after contextKey, but the current bytes do not yet prove how inheritSkillIdList and BuffIconDurationSourceSetting divide the raw tail.";
                }
                else if (string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "ModifyDynamicBlackboard/Data", StringComparison.Ordinal))
                {
                    diagnostic["key"] = ReadPayloadAlignedAsciiStringWithZeroPadding(reader, "modifyDynamicBlackboard.key", 128);
                    diagnostic["operation"] = ReadPayloadNamedEnum32(reader, "modifyDynamicBlackboard.operation", new[] { "Assign", "Add", "Multiply", "Divide" });
                    diagnostic["directValue"] = reader.ReadBool32("modifyDynamicBlackboard.directValue");
                    diagnostic["value"] = ReadPayloadBlackboardDoubleWithZeroPadding(reader, "modifyDynamicBlackboard.value", 128);
                    diagnostic["calculationTarget"] = ReadDiagnosticTargetSettings(reader, "modifyDynamicBlackboard.calculationTarget", offset, recoveredByRid);
                    diagnostic["calculateType"] = ReadPayloadNamedEnum32(reader, "modifyDynamicBlackboard.calculateType", new[] { "HpRatio" });
                }
                else if (string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "StoreBuffCount/Data", StringComparison.Ordinal))
                {
                    diagnostic["useCurrentBuff"] = reader.ReadBool32("storeBuffCount.useCurrentBuff");
                    diagnostic["buffOwners"] = ReadDiagnosticTargetSettings(reader, "storeBuffCount.buffOwners", offset, recoveredByRid);
                    diagnostic["buffId"] = ReadPayloadAlignedAsciiStringWithZeroPadding(reader, "storeBuffCount.buffId", 128);
                    diagnostic["blackboardKey"] = ReadPayloadAlignedAsciiStringWithZeroPadding(reader, "storeBuffCount.blackboardKey", 128);
                }
                else
                {
                    diagnostic = null;
                    return false;
                }

                reader.EnsureComplete();
                return true;
            }
            catch (InvalidDataException)
            {
                diagnostic = null;
                return false;
            }
        }

        private static OrderedDictionary ReadDiagnosticCreateBuffActionBuffs(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            var start = reader.Position;
            var count = reader.ReadInt32($"{fieldName}.count");
            if (count < 0 || count > 8)
            {
                throw new InvalidDataException($"invalid buff count {count} in {fieldName}");
            }

            var items = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                items.Add(ReadPayloadAlignedAsciiStringWithZeroPadding(reader, $"{fieldName}[{i}]", 128));
            }

            var data = new OrderedDictionary
            {
                { "$partial", true },
                { "count", count },
                { "items", items },
                { "reservedZeroWords", ReadDiagnosticZeroWords(reader, $"{fieldName}.reservedZeroWords", 4) },
            };
            data["length"] = reader.Position - start;
            data["layoutNote"] = "Current bytes show a count-prefixed aligned buff-id string list followed by four reserved zero words; the exact generic field type remains unresolved locally.";
            return data;
        }

        private static OrderedDictionary ReadDiagnosticCreateBuffActionPostContextTail(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            var start = reader.Position;
            var data = new OrderedDictionary
            {
                { "$partial", true },
                { "metadataFieldOrder", new[]
                    {
                        "autoFinishByAction",
                        "inheritSkillIdList",
                        "asChildBuff",
                        "inheritSourceSkillCastId",
                        "inheritSourceSkillCastInfo",
                        "isExtra",
                        "overrideBuffIconDuration",
                        "buffIconDurationSource",
                    }
                },
                { "rawWords", ReadDiagnosticRawWords(reader, $"{fieldName}.rawWords", reader.Remaining / 4) },
            };
            data["length"] = reader.Position - start;
            data["layoutNote"] = "Installed IL2CPP metadata names the post-context fields, but current samples only prove the combined raw tail. Keep this as a field-order diagnostic until inheritSkillIdList and BuffIconDurationSourceSetting byte boundaries are proven.";
            return data;
        }

        private static OrderedDictionary ReadDiagnosticBuffFindSettingsCandidate(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            var start = reader.Position;
            var data = new OrderedDictionary
            {
                { "$partial", true },
                { "checkType", ReadPayloadNamedEnum32(reader, $"{fieldName}.checkType", new[] { "Id", "Tag", "Environment", "Context" }) },
                { "buffIdList", ReadPayloadStringListWithZeroPadding(reader, $"{fieldName}.buffIdList", 8, 128) },
                { "tagQuery", ReadPayloadGameplayTagQueryWithZeroPadding(reader, $"{fieldName}.tagQuery", 8, 256) },
            };
            data["length"] = reader.Position - start;
            data["layoutNote"] = "Installed IL2CPP metadata exposes BuffFindSettings as checkType, buffIdList, and tagQuery. The bytes are consumed in that order, but this remains partial until the generic list type name and unobserved Environment/Context variants are proven across broader samples.";
            return data;
        }

        private static OrderedDictionary ReadDiagnosticAbilityActionDataPrefix(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "isEnable", reader.ReadBool32("abilityActionData.isEnable") },
                { "priorityLevel", reader.ReadInt32("abilityActionData.priorityLevel") },
                { "priorityOffset", reader.ReadInt32("abilityActionData.priorityOffset") },
                { "serverActionIndex", reader.ReadInt32("abilityActionData.serverActionIndex") },
            };
        }

        private static OrderedDictionary ReadDiagnosticTargetSettings(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int payloadOffset,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid
        )
        {
            var start = reader.Position;
            var data = new OrderedDictionary
            {
                { "$partial", true },
                { "layout", "Beyond.Gameplay.Core.TargetSettings" },
                { "observedPayloadStatus", "TargetSettings is byte-consumed in current focused samples, but selector/post-selector semantics remain diagnostic." },
                { "partialReasons", new List<string>
                    {
                        "SelectorData postProcessorData ownership remains unproven because observed late RID candidates are empty.",
                        "Post-selector compact eight-word tail is consumed but exact field widths and enum meanings remain unproven.",
                        "No focused sample proves a non-empty targetContextKey or non-default direction/target variant.",
                    }
                },
                { "relativeOffset", start - payloadOffset },
                { "absoluteOffset", start },
                { "targetSource", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.targetSource")) },
                { "targetGroupKey", ReadPayloadAlignedAsciiStringWithZeroPadding(reader, $"{fieldName}.targetGroupKey", 128) },
                { "selectorOwner", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.selectorOwner")) },
                { "ownerContextKey", ReadPayloadAlignedAsciiStringWithZeroPadding(reader, $"{fieldName}.ownerContextKey", 128) },
                { "centerType", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.centerType")) },
                { "centerContextKey", ReadPayloadAlignedAsciiStringWithZeroPadding(reader, $"{fieldName}.centerContextKey", 128) },
                { "centerToGround", reader.ReadBool32($"{fieldName}.centerToGround") },
                { "selectorData", ReadDiagnosticSelectorData(reader, $"{fieldName}.selectorData", payloadOffset, recoveredByRid) },
                { "postSelectorFields", ReadDiagnosticTargetSettingsPostSelectorFields(reader, $"{fieldName}.postSelectorFields") },
            };
            data["length"] = reader.Position - start;
            data["layoutNote"] = "Field order up through selectorData is metadata-backed. Post-selector fields are preserved raw because their exact byte widths and enum meanings are not yet fully proven.";
            return data;
        }

        private static OrderedDictionary ReadDiagnosticSelectorData(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int payloadOffset,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid
        )
        {
            var start = reader.Position;
            var data = new OrderedDictionary
            {
                { "$partial", true },
                { "layout", "Beyond.Gameplay.Core.Selector/SelectorData" },
                { "relativeOffset", start - payloadOffset },
                { "absoluteOffset", start },
            };

            var finderDataCandidate = ReadPayloadRidLink(reader, $"{fieldName}.finderDataRid", recoveredByRid);
            if (IsNullRidLink(finderDataCandidate) || ManagedReferenceLinkClassEndsWith(finderDataCandidate, "Finder/Data"))
            {
                data["finderDataRid"] = finderDataCandidate;
            }
            else
            {
                data["selectorDataRid"] = finderDataCandidate;
                data["selectorDataRoleNote"] = "Installed IL2CPP metadata names this slot finderData, but the linked class is not a proven Finder/Data sample yet.";
            }

            var validatorDataCount = reader.ReadInt32($"{fieldName}.validatorDataCount");
            data["validatorDataCount"] = BuildPayloadHash32(validatorDataCount);
            if (validatorDataCount == 1)
            {
                var validatorDataCandidate = ReadPayloadRidLink(reader, $"{fieldName}.validatorDataRid", recoveredByRid);
                if (ManagedReferenceLinkClassEndsWith(validatorDataCandidate, "Validator/Data"))
                {
                    data["validatorDataRid"] = validatorDataCandidate;
                }
                else
                {
                    data["extraSelectorRid"] = validatorDataCandidate;
                    data["validatorDataRoleNote"] = "Installed IL2CPP metadata names this optional slot validatorData, but the linked class is not a proven Validator/Data sample yet.";
                }
            }
            else if (validatorDataCount != 0)
            {
                throw new InvalidDataException($"unsupported validatorData count {validatorDataCount} in {fieldName}");
            }

            data["reservedZeroWords"] = ReadDiagnosticZeroWords(reader, $"{fieldName}.reservedZeroWords", 3);
            data["postProcessorDataCandidates"] = new OrderedDictionary
            {
                { "$partial", true },
                { "metadataField", "postProcessorData" },
                { "observedPayloadStatus", "candidate postProcessorData RID slots are preserved but not promoted until a linked non-empty sample proves the container shape." },
                { "partialReasons", new List<string>
                    {
                        "Installed IL2CPP metadata names postProcessorData, but observed focused samples do not contain a linked post-processor RID.",
                        "The current two late RID candidates are retained so a future non-empty sample can prove ownership without losing bytes.",
                    }
                },
                { "lateRidA", ReadPayloadRidLink(reader, $"{fieldName}.lateRidA", recoveredByRid) },
                { "lateRidB", ReadPayloadRidLink(reader, $"{fieldName}.lateRidB", recoveredByRid) },
                { "layoutNote", "Installed IL2CPP metadata names the third SelectorData field postProcessorData, but current samples have not proven which late RID slot or container shape owns it." },
            };
            data["length"] = reader.Position - start;
            data["layoutNote"] = "RID slots are proven by byte offsets and recovered registry links. finderData and validatorData names are emitted only when null/proven by linked class suffix; postProcessorData remains a raw candidate.";
            return data;
        }

        private static OrderedDictionary ReadDiagnosticTargetSettingsPostSelectorFields(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "$partial", true },
                { "observedPayloadStatus", "compact eight-word post-selector tail consumed as raw diagnostics" },
                { "partialReasons", new List<string>
                    {
                        "Current focused samples only prove two zero-heavy raw word patterns.",
                        "The split between enableAdvancedDirection, advancedDirection, selectorDirection, target, targetContextKey, and Default is not field-width proven.",
                        "No non-empty targetContextKey or non-default direction sample has been observed in focused validation.",
                    }
                },
                { "metadataFieldOrder", new[]
                    {
                        "enableAdvancedDirection",
                        "advancedDirection",
                        "selectorDirection",
                        "target",
                        "targetContextKey",
                        "Default",
                    }
                },
                { "rawWords", ReadDiagnosticRawWords(reader, $"{fieldName}.rawWords", 8) },
                { "layoutNote", "Installed IL2CPP metadata names the post-selector TargetSettings fields, but current byte evidence only proves this combined eight-word tail." },
            };
        }

        private static bool IsNullRidLink(OrderedDictionary link)
        {
            if (link == null || !link.Contains("rid") || !TryConvertToInt64(link["rid"], out var rid))
            {
                return false;
            }

            return rid == 0 || ManagedReferenceLinkHasEmptyType(link);
        }

        private static bool ManagedReferenceLinkClassEndsWith(OrderedDictionary link, string suffix)
        {
            return TryGetManagedReferenceLinkType(link, out var type)
                && type.Contains("class")
                && type["class"] is string className
                && className.EndsWith(suffix, StringComparison.Ordinal);
        }

        private static bool ManagedReferenceLinkHasEmptyType(OrderedDictionary link)
        {
            return TryGetManagedReferenceLinkType(link, out var type)
                && IsManagedReferenceTypeFieldEmpty(type, "class")
                && IsManagedReferenceTypeFieldEmpty(type, "ns")
                && IsManagedReferenceTypeFieldEmpty(type, "asm");
        }

        private static bool TryGetManagedReferenceLinkType(OrderedDictionary link, out OrderedDictionary type)
        {
            type = null;
            if (link == null || !link.Contains("type") || link["type"] is not OrderedDictionary linkType)
            {
                return false;
            }

            type = linkType;
            return true;
        }

        private static bool IsManagedReferenceTypeFieldEmpty(OrderedDictionary type, string fieldName)
        {
            return !type.Contains(fieldName)
                || type[fieldName] == null
                || string.Equals(type[fieldName] as string, string.Empty, StringComparison.Ordinal);
        }

        private static List<OrderedDictionary> ReadDiagnosticRawWords(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int count
        )
        {
            var words = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                words.Add(BuildPayloadHash32(reader.ReadInt32($"{fieldName}[{i}]")));
            }
            return words;
        }

        private static List<OrderedDictionary> ReadDiagnosticZeroWords(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int count
        )
        {
            var words = ReadDiagnosticRawWords(reader, fieldName, count);
            for (var i = 0; i < words.Count; i++)
            {
                if ((int)words[i]["value"] != 0)
                {
                    throw new InvalidDataException($"non-zero reserved word in {fieldName}[{i}]");
                }
            }
            return words;
        }

        private static List<OrderedDictionary> CollectDiagnosticRawWordTrace(byte[] rawData, int offset, int length)
        {
            var words = new List<OrderedDictionary>();
            if (rawData == null
                || offset < 0
                || length <= 0
                || (length % 4) != 0
                || offset > rawData.Length
                || offset + length > rawData.Length)
            {
                return words;
            }

            for (var pos = offset; pos < offset + length; pos += 4)
            {
                var value = BinaryPrimitives.ReadInt32LittleEndian(rawData.AsSpan(pos, 4));
                var word = BuildPayloadHash32(value);
                word["relativeOffset"] = pos - offset;
                word["absoluteOffset"] = pos;
                var floatValue = BitConverter.Int32BitsToSingle(value);
                if (!float.IsNaN(floatValue) && !float.IsInfinity(floatValue))
                {
                    word["float32"] = floatValue;
                }
                if (TryBuildAsciiWord(rawData, pos, out var ascii))
                {
                    word["ascii4"] = ascii;
                }
                words.Add(word);
            }
            return words;
        }

        private static bool TryBuildAsciiWord(byte[] rawData, int offset, out string value)
        {
            value = null;
            if (rawData == null || offset < 0 || offset > rawData.Length - 4)
            {
                return false;
            }

            for (var i = 0; i < 4; i++)
            {
                var b = rawData[offset + i];
                if (b < 0x20 || b > 0x7e)
                {
                    return false;
                }
            }

            value = Encoding.ASCII.GetString(rawData, offset, 4);
            return true;
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

            public double ReadDouble(string fieldName)
            {
                EnsureAvailable(8, fieldName);
                var value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(rawData.AsSpan(Position, 8)));
                Position += 8;
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    throw new InvalidDataException($"invalid double in {fieldName}");
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

        private static bool TryDecodeGuideManagedReferenceData(
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
                || length <= 0
                || offset > rawData.Length
                || offset + length > rawData.Length)
            {
                return false;
            }

            var isRootGuideCondition = string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal);
            var isGuideCondition = string.Equals(header.Namespace, "Beyond.Gameplay.Conditions", StringComparison.Ordinal);
            var isGuideAction = string.Equals(header.Namespace, "Beyond.Gameplay.Actions", StringComparison.Ordinal);
            if (!isRootGuideCondition && !isGuideCondition && !isGuideAction)
            {
                return false;
            }

            try
            {
                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                if (IsKnownGuideConditionBaseOnlyManagedReferenceData(header))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", $"{header.Namespace}.{header.ClassName}" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CombineCondition", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CombineCondition" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "conditionEvalString", reader.ReadAlignedAsciiString("conditionEvalString") },
                        { "subConditions", ReadPayloadRidLinkList(reader, "subConditions", 32, recoveredByRid) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckMissionState", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckMissionState" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "missionId", ReadGuideStringParam(reader, "missionId") },
                        { "comparer", ReadGuideIntParam(reader, "comparer") },
                        { "targetMissionState", ReadGuideIntParam(reader, "targetMissionState") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckGuideGroupComplete", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckGuideGroupComplete" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "guideGroupId", ReadGuideStringParam(reader, "guideGroupId") },
                        { "completeType", ReadGuideIntParam(reader, "completeType") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnPlayerActionTriggerOnly", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnPlayerActionTriggerOnly" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "actionId", ReadGuideStringParam(reader, "actionId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnUIPanelOpen", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnUIPanelOpen" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "panelId", ReadGuideStringParam(reader, "panelId") },
                        { "needWaitAnimation", ReadGuideBoolParam(reader, "needWaitAnimation") },
                        { "topPhaseId", ReadGuideStringParam(reader, "topPhaseId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckScriptTaskStateEqual", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckScriptTaskStateEqual" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "levelId", ReadGuideStringParam(reader, "levelId") },
                        { "scriptId", ReadGuideIntParam(reader, "scriptId") },
                        { "taskKey", ReadGuideStringParamWithExtraRawWord(reader, "taskKey") },
                        { "comparer", ReadGuideIntParam(reader, "comparer") },
                        { "targetValue", ReadGuideIntParam(reader, "targetValue") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnBuildingPanelOpen", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnBuildingPanelOpen" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "buildingId", ReadGuideStringParam(reader, "buildingId") },
                        { "needWaitAnimation", ReadGuideBoolParam(reader, "needWaitAnimation") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnUIPanelClose", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnUIPanelClose" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "panelId", ReadGuideStringParam(reader, "panelId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "PlayerHasItemInItemBag", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.PlayerHasItemInItemBag" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "unknownString0", reader.ReadAlignedAsciiString("unknownString0") },
                        { "itemId", ReadGuideStringParam(reader, "itemId") },
                        { "compareOperator", ReadGuideIntParam(reader, "compareOperator") },
                        { "targetItemCount", ReadGuideIntParam(reader, "targetItemCount") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckQuestState", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckQuestState" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "questId", ReadGuideStringParam(reader, "questId") },
                        { "comparer", ReadGuideIntParam(reader, "comparer") },
                        { "targetQuestState", ReadGuideIntParam(reader, "targetQuestState") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "PlayerHasItem", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.PlayerHasItem" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "unknownString0", reader.ReadAlignedAsciiString("unknownString0") },
                        { "itemId", ReadGuideStringParam(reader, "itemId") },
                        { "comparer", ReadGuideIntParam(reader, "comparer") },
                        { "progressToCompare", ReadGuideIntParam(reader, "progressToCompare") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "CheckIsInFactoryMode", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.CheckIsInFactoryMode" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "isInFactoryMode", ReadGuideBoolParam(reader, "isInFactoryMode") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnFacPrepareBuildingEnterArea", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnFacPrepareBuildingEnterArea" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "buildingId", ReadGuideStringParam(reader, "buildingId") },
                        { "worldPos", ReadGuideVector3Param(reader, "worldPos") },
                        { "range", ReadGuideFloatParam(reader, "range") },
                        { "angle", ReadGuideFloatParam(reader, "angle") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnFacPlaceBuilding", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnFacPlaceBuilding" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "buildingId", ReadGuideStringParam(reader, "buildingId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "DepotHasItem", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.DepotHasItem" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "itemId", ReadGuideStringParam(reader, "itemId") },
                        { "compareOperator", ReadGuideIntParam(reader, "compareOperator") },
                        { "targetItemCount", ReadGuideIntParam(reader, "targetItemCount") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckActivityStageInTimeOffset", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckActivityStageInTimeOffset" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "activityId", ReadGuideStringParam(reader, "activityId") },
                        { "stageId", ReadGuideStringParam(reader, "stageId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "CheckIsInGeneralAbilitySelectMode", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.CheckIsInGeneralAbilitySelectMode" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "isInSelectMode", ReadGuideBoolParam(reader, "isInSelectMode") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if ((isRootGuideCondition || isGuideCondition)
                    && string.Equals(header.ClassName, "CheckCurrentMap", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", $"{header.Namespace}.{header.ClassName}" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "mapId", ReadGuideStringParam(reader, "mapId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnInteractOptionShow", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnInteractOptionShow" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "optionObjectName", ReadGuideStringParam(reader, "optionObjectName") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnQuickMenuSystemHover", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnQuickMenuSystemHover" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "systemId", ReadGuideStringParam(reader, "systemId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "CheckIsInFacMainRegion", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.CheckIsInFacMainRegion" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "isInFacMainRegion", ReadGuideBoolParam(reader, "isInFacMainRegion") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckHasInteractOption", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckHasInteractOption" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "optionObjectName", ReadGuideStringParam(reader, "optionObjectName") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "CheckCurrentLevel", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.CheckCurrentLevel" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "levelId", ReadGuideStringParam(reader, "levelId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnOpenFacUnloaderPanel", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnOpenFacUnloaderPanel" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "selectItemId", ReadGuideStringParam(reader, "selectItemId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnFacCurMachineCacheAddItem", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnFacCurMachineCacheAddItem" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "itemId", ReadGuideStringParam(reader, "itemId") },
                        { "num", ReadGuideIntParam(reader, "num") },
                        { "isIn", ReadGuideBoolParam(reader, "isIn") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnFacQuickBarAddItem", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnFacQuickBarAddItem" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "itemId", ReadGuideStringParam(reader, "itemId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnUIScrollListGraduallyShowFinished", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnUIScrollListGraduallyShowFinished" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "listPath", ReadGuideStringParam(reader, "listPath") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "CheckIsInFacLinkingMode", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.CheckIsInFacLinkingMode" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "isInFacLinkingMode", ReadGuideBoolParam(reader, "isInFacLinkingMode") },
                        { "targetFacLinkingModeType", ReadGuideIntParam(reader, "targetFacLinkingModeType") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnGeneralAbilityHover", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnGeneralAbilityHover" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "abilityType", ReadGuideIntParam(reader, "abilityType") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckActivityCompletedOrNull", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckActivityCompletedOrNull" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "activityId", ReadGuideStringParam(reader, "activityId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckUnlockTech", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckUnlockTech" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "facTechId", ReadGuideStringParam(reader, "facTechId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckPlayerInMap", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckPlayerInMap" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "mapId", ReadGuideStringParam(reader, "mapId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }
                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckBuildingStateInArea", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckBuildingStateInArea" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "facBuildingId", ReadGuideStringParam(reader, "facBuildingId") },
                        { "facStateType", ReadGuideIntParam(reader, "facStateType") },
                        { "targetCount", ReadGuideIntParam(reader, "targetCount") },
                        { "targetAreaId", ReadGuideStringParam(reader, "targetAreaId") },
                        { "targetMapId", ReadGuideStringParam(reader, "targetMapId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }
                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnDungeonCommonEntryPanelOpen", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnDungeonCommonEntryPanelOpen" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "dungeonSeriesId", ReadGuideStringParam(reader, "dungeonSeriesId") },
                        { "needWaitAnimation", ReadGuideBoolParam(reader, "needWaitAnimation") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnFacConveyorOperated", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnFacConveyorOperated" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "isEndPoint", ReadGuideBoolParam(reader, "isEndPoint") },
                        { "position", ReadGuideIntPairParam(reader, "position") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckAdventureLevel", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckAdventureLevel" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "_", BuildPayloadHash32(reader.ReadInt32("_")) },
                        { "comparer", ReadGuideIntParam(reader, "comparer") },
                        { "progressToCompare", ReadGuideIntParam(reader, "progressToCompare") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckIsSquadInFight", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckIsSquadInFight" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "isInFight", ReadGuideBoolParam(reader, "isInFight") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "CheckIsInFacTopView", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.CheckIsInFacTopView" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "isInTopView", ReadGuideBoolParam(reader, "isInTopView") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "CheckSelectGeneralAbility", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.CheckSelectGeneralAbility" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "type", ReadGuideIntParam(reader, "type") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "CheckIsItemInQuickBar", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.CheckIsItemInQuickBar" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "itemId", ReadGuideStringParam(reader, "itemId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "CheckMapMissionTrackingState", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.CheckMapMissionTrackingState" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "targetLevelId", ReadGuideStringParam(reader, "targetLevelId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnUILevelMapEnterLevel", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnUILevelMapEnterLevel" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "levelId", ReadGuideStringParam(reader, "levelId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckBuildingConnectedSpecify", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckBuildingConnectedSpecify" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "levelId", ReadGuideStringParam(reader, "levelId") },
                        { "instKeyA", ReadGuideStringParam(reader, "instKeyA") },
                        { "instKeyB", ReadGuideStringParam(reader, "instKeyB") },
                        { "connected", ReadGuideBoolParam(reader, "connected") },
                        { "conveyorType", ReadGuideIntParam(reader, "conveyorType") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "CheckIsOpenDomainMain", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.CheckIsOpenDomainMain" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "domainId", ReadGuideStringParam(reader, "domainId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckBlackboxComplete", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckBlackboxComplete" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "blackboxId", ReadGuideStringParam(reader, "blackboxId") },
                        { "completeState", ReadGuideIntParam(reader, "completeState") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnOtherPlayerSocialBuildingPanelOpen", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnOtherPlayerSocialBuildingPanelOpen" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "buildingId", ReadGuideStringParam(reader, "buildingId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnMainHudActionFinished", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnMainHudActionFinished" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "isInterrupt", ReadGuideBoolParam(reader, "isInterrupt") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckSimulationTrainingHandCardCount", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckSimulationTrainingHandCardCount" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "count", ReadGuideIntParam(reader, "count") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckSpaceshipRoomLevel", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckSpaceshipRoomLevel" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "roomType", ReadGuideIntParam(reader, "roomType") },
                        { "comparer", ReadGuideIntParam(reader, "comparer") },
                        { "progressToCompare", ReadGuideIntParam(reader, "progressToCompare") },
                    };
                    reader.EnsureComplete();
                    return true;
                }
                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckRepairBuilding", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckRepairBuilding" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "levelId", ReadGuideStringParam(reader, "levelId") },
                        { "repairId", ReadGuideStringParam(reader, "repairId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "FacBuildingProducingCountInScene", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.FacBuildingProducingCountInScene" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "comparer", ReadGuideIntParam(reader, "comparer") },
                        { "progressToCompare", ReadGuideIntParam(reader, "progressToCompare") },
                        { "levelId", ReadGuideStringParam(reader, "levelId") },
                        { "facBuildingId", ReadGuideStringParam(reader, "facBuildingId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnGetItem", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnGetItem" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "itemId", ReadGuideStringParam(reader, "itemId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "HasItemCount", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.HasItemCount" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "comparer", ReadGuideIntParam(reader, "comparer") },
                        { "progressToCompare", ReadGuideIntParam(reader, "progressToCompare") },
                        { "itemId", ReadGuideStringParam(reader, "itemId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "OnTechTreeNodeUnlock", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.OnTechTreeNodeUnlock" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "techId", ReadGuideStringParam(reader, "techId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckWorldLevel", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckWorldLevel" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "compareOperator", ReadGuideIntParam(reader, "compareOperator") },
                        { "progressToCompare", ReadGuideIntParam(reader, "progressToCompare") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckItemBagCanPutInServer", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckItemBagCanPutInServer" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "itemId", ReadGuideStringParam(reader, "itemId") },
                        { "count", ReadGuideIntParam(reader, "count") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "CheckWireLinkAvailable", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.CheckWireLinkAvailable" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "isAvailable", ReadGuideBoolParam(reader, "isAvailable") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckPlayerPin", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckPlayerPin" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "formulaId", ReadGuideStringParam(reader, "formulaId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckCharInMainTeam", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckCharInMainTeam" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "charId", ReadGuideStringParam(reader, "charId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "CheckIsWeaponEquipped", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.CheckIsWeaponEquipped" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "charId", ReadGuideStringParam(reader, "charId") },
                        { "weaponId", ReadGuideStringParam(reader, "weaponId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckUnlockTechLayer", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckUnlockTechLayer" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "facLayerId", ReadGuideStringParam(reader, "facLayerId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckSpaceshipRoomStationCount", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckSpaceshipRoomStationCount" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "roomType", ReadGuideIntParam(reader, "roomType") },
                        { "comparer", ReadGuideIntParam(reader, "comparer") },
                        { "progressToCompare", ReadGuideIntParam(reader, "progressToCompare") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckDomainShopChannelLevel", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckDomainShopChannelLevel" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "channelId", ReadGuideStringParam(reader, "channelId") },
                        { "level", ReadGuideIntParam(reader, "level") },
                        { "comparer", ReadGuideIntParam(reader, "comparer") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "FacStatisticItemGenRate", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.FacStatisticItemGenRate" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "comparer", ReadGuideIntParam(reader, "comparer") },
                        { "progressToCompare", ReadGuideIntParam(reader, "progressToCompare") },
                        { "levelId", ReadGuideStringParam(reader, "levelId") },
                        { "itemId", ReadGuideStringParam(reader, "itemId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "FacStatisticItemGen", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.FacStatisticItemGen" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "comparer", ReadGuideIntParam(reader, "comparer") },
                        { "progressToCompare", ReadGuideIntParam(reader, "progressToCompare") },
                        { "levelId", ReadGuideStringParam(reader, "levelId") },
                        { "itemId", ReadGuideStringParam(reader, "itemId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "FacProducePowerReach", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.FacProducePowerReach" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "levelId", ReadGuideStringParam(reader, "levelId") },
                        { "power", ReadGuideIntParam(reader, "power") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "FacProducingFormulaCountInScene", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.FacProducingFormulaCountInScene" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "comparer", ReadGuideIntParam(reader, "comparer") },
                        { "progressToCompare", ReadGuideIntParam(reader, "progressToCompare") },
                        { "levelId", ReadGuideStringParam(reader, "levelId") },
                        { "facFormulaId", ReadGuideStringParam(reader, "facFormulaId") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isGuideCondition
                    && string.Equals(header.ClassName, "CheckDomainShopPanelHasSoldOutGroup", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Conditions.CheckDomainShopPanelHasSoldOutGroup" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "checkHasGroup", ReadGuideBoolParam(reader, "checkHasGroup") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckGachaWeaponTopCount", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckGachaWeaponTopCount" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "count", ReadGuideIntParam(reader, "count") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (isRootGuideCondition
                    && string.Equals(header.ClassName, "CheckSpaceshipRoomBuiltById", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CheckSpaceshipRoomBuiltById" },
                        { "offset", offset },
                        { "length", length },
                        { "conditionBase", ReadGuideConditionBase(reader, "conditionBase") },
                        { "roomId", ReadGuideStringParam(reader, "roomId") },
                        { "isBuild", ReadGuideBoolParam(reader, "isBuild") },
                        { "roomType", ReadGuideIntParam(reader, "roomType") },
                    };
                    reader.EnsureComplete();
                    return true;
                }
                if (isGuideAction
                    && TryDecodeGuideActionManagedReferenceData(header, reader, offset, length, out data))
                {
                    return true;
                }

            }
            catch (InvalidDataException ex)
            {
                data = BuildKnownManagedReferenceDecodeFailureData(
                    rawData,
                    offset,
                    length,
                    $"{header.Namespace}.{header.ClassName}",
                    ex,
                    recoveredByRid);
                return true;
            }

            return false;
        }

        private static bool TryDecodeGuideActionManagedReferenceData(
            ManagedReferenceHeader header,
            ManagedReferencePayloadReader reader,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (header == null || !string.Equals(header.Namespace, "Beyond.Gameplay.Actions", StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(header.ClassName, "ToggleGeneralAbilityHide", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["closeSelectAbilityType"] = ReadGuideActionParamInt(reader, "closeSelectAbilityType");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "SelectQuickMenuSystem", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["systemId"] = ReadGuideActionParamString(reader, "systemId");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "BuildingPosHintHide", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["handle"] = ReadGuideActionParamInt(reader, "handle");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "FacBlockOtherHubUnloaderInteract", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["targetIndex"] = ReadGuideActionParamInt(reader, "targetIndex");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "ToggleAbandonDropValid", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["dropValid"] = ReadGuideActionParamBool(reader, "dropValid");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "FocusTechTreeNode", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["techId"] = ReadGuideActionParamString(reader, "techId");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "CorrectPlayerPosTeleport", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["id"] = ReadGuideActionParamString(reader, "id");
                data["levelId"] = ReadGuideActionParamString(reader, "levelId");
                data["position"] = ReadGuideActionParamVector3(reader, "position");
                data["startCorrectDist"] = ReadGuideActionParamFloat(reader, "startCorrectDist");
                data["startCorrectMaxDist"] = ReadGuideActionParamFloat(reader, "startCorrectMaxDist");
                data["correctedDist"] = ReadGuideActionParamFloat(reader, "correctedDist");
                data["maxAngle"] = ReadGuideActionParamFloat(reader, "maxAngle");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "BuildingPosHintShow", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["buildingId"] = ReadGuideActionParamString(reader, "buildingId");
                data["worldPos"] = ReadGuideActionParamVector3(reader, "worldPos");
                data["worldDir"] = ReadGuideActionParamInt(reader, "worldDir");
                data["handleOutput"] = ReadGuideActionParamOutputInt(reader, "handleOutput");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "ScrollToBuildListTargetItem", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["buildingId"] = ReadGuideActionParamString(reader, "buildingId");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "ForceEnableControllerNavi", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["forceEnableNavi"] = ReadGuideActionParamBool(reader, "forceEnableNavi");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "SetMainHudCanAutoStopExpand", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["canAutoStop"] = ReadGuideActionParamBool(reader, "canAutoStop");
                reader.EnsureComplete();
                return true;
            }
            if (IsKnownGuideActionNoField(header.ClassName))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                reader.EnsureComplete();
                return true;
            }

            var boolFieldName = GetGuideActionBoolFieldName(header.ClassName);
            if (!string.IsNullOrEmpty(boolFieldName))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data[boolFieldName] = ReadGuideActionParamBool(reader, boolFieldName);
                reader.EnsureComplete();
                return true;
            }

            var intFieldName = GetGuideActionIntFieldName(header.ClassName);
            if (!string.IsNullOrEmpty(intFieldName))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data[intFieldName] = ReadGuideActionParamInt(reader, intFieldName);
                reader.EnsureComplete();
                return true;
            }

            var stringFieldName = GetGuideActionStringFieldName(header.ClassName);
            if (!string.IsNullOrEmpty(stringFieldName))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data[stringFieldName] = ReadGuideActionParamString(reader, stringFieldName);
                reader.EnsureComplete();
                return true;
            }
            if (string.Equals(header.ClassName, "ToggleSideMenuItemForceValid", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["itemName"] = ReadGuideActionParamString(reader, "itemName");
                data["forceValid"] = ReadGuideActionParamBool(reader, "forceValid");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "FacOverrideCullingSetting", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["overrideSetting"] = ReadGuideActionParamBool(reader, "overrideSetting");
                data["sqrUI"] = ReadGuideActionParamFloat(reader, "sqrUI");
                data["sqrCullDis"] = ReadGuideActionParamFloat(reader, "sqrCullDis");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "BlackScreenFadeOut", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["duration"] = ReadGuideActionParamFloat(reader, "duration");
                data["blockInput"] = ReadGuideActionParamBool(reader, "blockInput");
                data["black"] = ReadGuideActionParamBool(reader, "black");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "FacHighlightBuildingInArea", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["buildingId"] = ReadGuideActionParamString(reader, "buildingId");
                data["worldPos"] = ReadGuideActionParamVector3(reader, "worldPos");
                data["range"] = ReadGuideActionParamFloat(reader, "range");
                data["active"] = ReadGuideActionParamBool(reader, "active");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "UIScrollRectScrollTo", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["rectPath"] = ReadGuideActionParamString(reader, "rectPath");
                data["targetGameObjectPath"] = ReadGuideActionParamString(reader, "targetGameObjectPath");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "BlendToCameraTransform", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["pos"] = ReadGuideActionParamVector3(reader, "pos");
                data["rot"] = ReadGuideActionParamVector3(reader, "rot");
                data["fov"] = ReadGuideActionParamFloat(reader, "fov");
                data["duration"] = ReadGuideActionParamFloat(reader, "duration");
                data["useBlackScreen"] = ReadGuideActionParamBool(reader, "useBlackScreen");
                data["tweenTime"] = ReadGuideActionParamFloat(reader, "tweenTime");
                data["overrideBlend"] = ReadGuideActionParamBool(reader, "overrideBlend");
                data["blendStyle"] = ReadGuideActionParamInt(reader, "blendStyle");
                data["useYawCheck"] = ReadGuideActionParamBool(reader, "useYawCheck");
                data["needInterruptMainHudAction"] = ReadGuideActionParamBool(reader, "needInterruptMainHudAction");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "Split", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["idList"] = ReadGuideRawInt32List(reader, "idList", 64);
                reader.EnsureComplete();
                return true;
            }
            if (string.Equals(header.ClassName, "SetEnablePlayerAction", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["advanced"] = ReadGuideActionParamBool(reader, "advanced");
                data["enablePlayerInput"] = ReadGuideActionParamBool(reader, "enablePlayerInput");
                data["actionMask"] = ReadGuideActionParamInt(reader, "actionMask");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "ShowLimitedGuide", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["textId"] = ReadGuideActionParamString(reader, "textId");
                data["duration"] = ReadGuideActionParamFloat(reader, "duration");
                data["type"] = ReadGuideActionParamInt(reader, "type");
                data["iconType"] = ReadGuideActionParamInt(reader, "iconType");
                data["needIgnoreWhenConflict"] = ReadGuideActionParamBool(reader, "needIgnoreWhenConflict");
                data["mediaGuideGroupId"] = ReadGuideActionParamString(reader, "mediaGuideGroupId");
                data["wikiId"] = ReadGuideActionParamString(reader, "wikiId");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "UIScrollListScrollTo", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["listPath"] = ReadGuideActionParamString(reader, "listPath");
                data["cellGameObjectName"] = ReadGuideActionParamString(reader, "cellGameObjectName");
                reader.EnsureComplete();
                return true;
            }
            if (string.Equals(header.ClassName, "SetAtbValue", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["atbValue"] = ReadGuideActionParamFloat(reader, "atbValue");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "GuideFreezeWorld", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["handle"] = ReadGuideActionParamOutputInt(reader, "handle");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "GuideUnFreezeWorld", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["handle"] = ReadGuideActionParamInt(reader, "handle");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "FinishEffect", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["effectSaveId"] = ReadGuideActionParamPathRawWords(reader, "effectSaveId", 2);
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "BlendOutFromCamera", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["blendTime"] = ReadGuideActionParamFloat(reader, "blendTime");
                data["overrideBlend"] = ReadGuideActionParamBool(reader, "overrideBlend");
                data["blendStyle"] = ReadGuideActionParamInt(reader, "blendStyle");
                data["useBlackScreen"] = ReadGuideActionParamBool(reader, "useBlackScreen");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "BlendIntoCameraNoReturn", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["camPoseId"] = ReadGuideActionParamInt(reader, "camPoseId");
                data["blendTime"] = ReadGuideActionParamFloat(reader, "blendTime");
                data["duration"] = ReadGuideActionParamFloat(reader, "duration");
                data["needInterruptMainHudAction"] = ReadGuideActionParamBool(reader, "needInterruptMainHudAction");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "BlendToCameraTransformWithoutBack", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["pos"] = ReadGuideActionParamVector3(reader, "pos");
                data["rot"] = ReadGuideActionParamVector3(reader, "rot");
                data["fov"] = ReadGuideActionParamFloat(reader, "fov");
                data["duration"] = ReadGuideActionParamFloat(reader, "duration");
                data["needInterruptMainHudAction"] = ReadGuideActionParamBool(reader, "needInterruptMainHudAction");
                data["useBlackScreen"] = ReadGuideActionParamBool(reader, "useBlackScreen");
                data["tweenTime"] = ReadGuideActionParamFloat(reader, "tweenTime");
                data["overrideBlend"] = ReadGuideActionParamBool(reader, "overrideBlend");
                data["blendStyle"] = ReadGuideActionParamInt(reader, "blendStyle");
                data["useYawCheck"] = ReadGuideActionParamBool(reader, "useYawCheck");
                data["advancedMode"] = ReadGuideActionParamBool(reader, "advancedMode");
                data["ignoreProtect"] = ReadGuideActionParamBool(reader, "ignoreProtect");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "RemoveTrackingPoint", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["trackingPointId"] = ReadGuideActionParamString(reader, "trackingPointId");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "FacHighlightBuilding", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["instKey"] = ReadGuideActionParamString(reader, "instKey");
                data["active"] = ReadGuideActionParamBool(reader, "active");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "AddTrackingPoint", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["trackingPointId"] = ReadGuideActionParamString(reader, "trackingPointId");
                data["levelId"] = ReadGuideActionParamString(reader, "levelId");
                data["guidingArea"] = ReadGuideActionParamInt(reader, "guidingArea");
                data["styleType"] = ReadGuideActionParamInt(reader, "styleType");
                data["trackingType"] = ReadGuideActionParamInt(reader, "trackingType");
                data["buildingInstKey"] = ReadGuideActionParamString(reader, "buildingInstKey");
                data["entityLogicId"] = ReadGuideActionParamInt64(reader, "entityLogicId");
                data["pos"] = ReadGuideActionParamVector3(reader, "pos");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "FacGuideHintEnable", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["hintId"] = ReadGuideActionParamString(reader, "hintId");
                data["enable"] = ReadGuideActionParamBool(reader, "enable");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "CreateEffectAtPosition", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["effectKey"] = ReadGuideActionParamString(reader, "effectKey");
                data["position"] = ReadGuideActionParamVector3(reader, "position");
                data["eulerAngles"] = ReadGuideActionParamVector3(reader, "eulerAngles");
                data["scale"] = ReadGuideActionParamVector3(reader, "scale");
                data["isUnique"] = ReadGuideActionParamBool(reader, "isUnique");
                data["effectSaveId"] = ReadGuideActionParamOutputInt(reader, "effectSaveId");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "FacSetInteractLockedState", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["isLocked"] = ReadGuideActionParamBool(reader, "isLocked");
                data["instKey"] = ReadGuideActionParamString(reader, "instKey");
                data["radioId"] = ReadGuideActionParamString(reader, "radioId");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "ToggleScrollRect", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["scrollEnabled"] = ReadGuideActionParamBool(reader, "scrollEnabled");
                data["scrollRectPath"] = ReadGuideActionParamString(reader, "scrollRectPath");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "FacConveyorInteractRangeRestrict", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["active"] = ReadGuideActionParamBool(reader, "active");
                data["x0"] = ReadGuideActionParamInt(reader, "x0");
                data["y0"] = ReadGuideActionParamInt(reader, "y0");
                data["width0"] = ReadGuideActionParamInt(reader, "width0");
                data["height0"] = ReadGuideActionParamInt(reader, "height0");
                data["x1"] = ReadGuideActionParamInt(reader, "x1");
                data["y1"] = ReadGuideActionParamInt(reader, "y1");
                data["width1"] = ReadGuideActionParamInt(reader, "width1");
                data["height1"] = ReadGuideActionParamInt(reader, "height1");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "ScrollToItemBagTargetItem", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["listPath"] = ReadGuideActionParamString(reader, "listPath");
                data["targetItemId"] = ReadGuideActionParamString(reader, "targetItemId");
                data["needClamp"] = ReadGuideActionParamBool(reader, "needClamp");
                data["clampMinIndex"] = ReadGuideActionParamInt(reader, "clampMinIndex");
                data["clampMaxIndex"] = ReadGuideActionParamInt(reader, "clampMaxIndex");
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.ClassName, "FocusOnInteractOption", StringComparison.Ordinal))
            {
                data = CreateGuideActionData(header, offset, length);
                data["actionBase"] = ReadGuideActionBase(reader, "actionBase");
                data["optionName"] = ReadGuideActionParamString(reader, "optionName");
                reader.EnsureComplete();
                return true;
            }

            return false;
        }

        private static OrderedDictionary CreateGuideActionData(
            ManagedReferenceHeader header,
            int offset,
            int length
        )
        {
            return new OrderedDictionary
            {
                { "$decoded", true },
                { "$inferred", true },
                { "layout", $"{header.Namespace}.{header.ClassName}" },
                { "offset", offset },
                { "length", length },
            };
        }

        private static bool IsKnownGuideActionNoField(string className)
        {
            return string.Equals(className, "RecoverMainHud", StringComparison.Ordinal)
                || string.Equals(className, "ExitFacBuildMode", StringComparison.Ordinal)
                || string.Equals(className, "EnterFacBeltBuildMode", StringComparison.Ordinal)
                || string.Equals(className, "FacMainHudCloseMobileBox", StringComparison.Ordinal)
                || string.Equals(className, "HideItemTips", StringComparison.Ordinal)
                || string.Equals(className, "FacMainHudRightStopFocus", StringComparison.Ordinal)
                || string.Equals(className, "ZoomToFullTechTree", StringComparison.Ordinal)
                || string.Equals(className, "ExitCharInfoTalentExpandNode", StringComparison.Ordinal);
        }

        private static string GetGuideActionBoolFieldName(string className)
        {
            switch (className)
            {
                case "DisableHudFade":
                    return "showHud";
                case "FacToggleCanDeactiveQuickBar":
                    return "isLock";
                case "FacSetEnableExitFactoryMode":
                    return "enable";
                case "ToggleItemTipsAutoClose":
                    return "active";
                case "FacLockBuildPos":
                    return "lockBuildPos";
                case "FacSetEnableConfirmBuild":
                    return "enable";
                case "FacSetEnableExitBuildMode":
                    return "enable";
                case "SetEnablePlayerMove":
                    return "enable";
                case "SetEnablePlayerMoveCamera":
                    return "enable";
                case "SetFacMode":
                    return "toFacMode";
                case "SetFacTopView":
                    return "isInTopView";
                case "SetGeneralAbilityReleaseClose":
                    return "canReleaseClose";
                case "ToggleClearScreen":
                    return "isShow";
                case "ToggleGeneralAbilityClick":
                    return "clickEnabled";
                case "ToggleGeneralAbilityLoneClick":
                    return "clickEnabled";
                case "ToggleQuickMenuReleaseClose":
                    return "releaseCloseEnabled";
                default:
                    return null;
            }
        }

        private static string GetGuideActionIntFieldName(string className)
        {
            switch (className)
            {
                case "ClearFacPin":
                    return "pinType";
                case "NaviToMixPoolTargetItem":
                    return "index";
                default:
                    return null;
            }
        }

        private static string GetGuideActionStringFieldName(string className)
        {
            switch (className)
            {
                case "EquipProduceScrollToItem":
                    return "itemId";
                case "SelectAdventureBookTab":
                    return "tabId";
                case "SelectMapMark":
                    return "markInstId";
                case "FacOpenBuildingPanel":
                    return "buildingId";
                case "CharInfoSwitchChar":
                    return "charId";
                case "CharInfoWeaponScrollToTop":
                    return "itemId";
                case "ClickUI":
                    return "uiPath";
                case "FocusTechTreeLayer":
                    return "layerId";
                case "FocusTechTreeCategory":
                    return "categoryId";
                default:
                    return null;
            }
        }
        private static bool IsKnownGuideConditionBaseOnlyManagedReferenceData(ManagedReferenceHeader header)
        {
            if (header == null || !string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal))
            {
                return false;
            }

            return (string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                    && (string.Equals(header.ClassName, "InMainHud", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "CheckPlayerOnGround", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "CheckInWeaponUpgradePanel", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "CheckInCharInfoUpgradePanel", StringComparison.Ordinal)))
                || (string.Equals(header.Namespace, "Beyond.Gameplay.Conditions", StringComparison.Ordinal)
                    && (string.Equals(header.ClassName, "OnCastUltimateSkill", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "OnCastNormalSkill", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "OnCharInfoModelInitFinish", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "OnSTTAllOpenProgressFinished", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "OnCastComboSkill", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "OnOpenFacHubPanelWithoutNotify", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "CheckIsPhaseCharInfoDefaultChar", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "OnNormalFriendPanelOpen", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "OnEnterMainHud", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "OnFacReachFastTravel", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "OnComboSkillReady", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "OnMixPoolSelectFinish", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "OnGeneralAbilityUse", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "OnLiquidInteractInDumpMode", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "OnFacPendingSlotChanged", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "OnFacMainPinHintShow", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "OnWeekRaidIntroCharFormationOpen", StringComparison.Ordinal)));
        }

        private static bool TryDecodeCoreGameplayManagedReferenceData(
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
                || !header.Namespace.StartsWith("Beyond.Gameplay.Core", StringComparison.Ordinal)
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
                if (length == 0 && IsKnownEmptyCoreGameplayManagedReferenceData(header))
                {
                    data = BuildEmptyManagedReferenceData(header, offset, length);
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "ProjectileRootComponentData", StringComparison.Ordinal)
                    && length == 32)
                {
                    data = BuildReservedZeroWordsManagedReferenceData(
                        header,
                        rawData,
                        offset,
                        length,
                        8,
                        "Current installed data serializes this managed-reference payload as eight reserved zero int32 words; no nonzero field bytes are present to decode.");
                    return true;
                }

                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                if (TryDecodeProjectileComponentData(header, reader, offset, length, out data))
                {
                    return true;
                }

                if (TryDecodeCoreActionConditionManagedReferenceData(
                    header,
                    reader,
                    offset,
                    length,
                    recoveredByRid,
                    out data))
                {
                    return true;
                }
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

                if (string.Equals(header.ClassName, "FinishGlobalBuffAction/Data", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.AbilityActions.FinishGlobalBuffAction/Data" },
                        { "offset", offset },
                        { "length", length },
                        { "isEnable", reader.ReadBool32("isEnable") },
                        { "priorityLevel", reader.ReadInt32("priorityLevel") },
                        { "priorityOffset", reader.ReadInt32("priorityOffset") },
                        { "serverActionIndex", reader.ReadInt32("serverActionIndex") },
                        { "finishParent", reader.ReadBool32("finishParent") },
                        { "globalBuffIds", ReadPayloadStringList(reader, "globalBuffIds", 16) },
                        { "finishAll", reader.ReadBool32("finishAll") },
                        { "finishCount", ReadPayloadBlackboardDouble(reader, "finishCount") },
                        { "isFinishedEarly", reader.ReadBool32("isFinishedEarly") },
                    };
                    reader.EnsureComplete();
                    return true;
                }
            }
            catch (InvalidDataException ex) when (IsCoreProjectileComponentData(header))
            {
                data = BuildKnownManagedReferenceDecodeFailureData(
                    rawData,
                    offset,
                    length,
                    "Beyond.Gameplay.Core.ProjectileComponentData",
                    ex,
                    recoveredByRid);
                return true;
            }
            catch (InvalidDataException)
            {
                data = null;
                return false;
            }

            return false;
        }

        private static bool IsCoreProjectileComponentData(ManagedReferenceHeader header)
        {
            return header != null
                && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "ProjectileComponentData", StringComparison.Ordinal);
        }

        private static OrderedDictionary BuildKnownManagedReferenceDecodeFailureData(
            byte[] rawData,
            int offset,
            int length,
            string layout,
            Exception decodeException,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid
        )
        {
            var data = new OrderedDictionary
            {
                { "$unparsed", true },
                { "$partial", true },
                { "$inferred", true },
                { "layout", layout },
                { "layoutNote", "Known managed-reference type matched a focused decoder, but the focused layout reader rejected this payload. Heuristic hints are preserved so the failing variant can be recovered without losing the type identity." },
                { "offset", offset },
                { "length", length },
                { "decodeError", $"{decodeException.GetType().Name}: {decodeException.Message}" },
            };

            var stringHintBudget = 64;
            var stringHints = CollectAlignedStringHints(rawData, offset, length, ref stringHintBudget);
            if (stringHints.Count > 0)
            {
                data["heuristicStringHints"] = stringHints;
            }

            var ridLinkBudget = 64;
            var ridLinks = CollectHeuristicRidLinks(rawData, offset, length, recoveredByRid, ref ridLinkBudget);
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
        private static bool TryDecodeProjectileComponentData(
            ManagedReferenceHeader header,
            ManagedReferencePayloadReader reader,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (!string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "ProjectileComponentData", StringComparison.Ordinal)
                || length < 256)
            {
                return false;
            }

            data = new OrderedDictionary
            {
                { "$decoded", true },
                { "$partial", true },
                { "$inferred", true },
                { "layout", "Beyond.Gameplay.Core.ProjectileComponentData" },
                { "offset", offset },
                { "length", length },
                { "id", reader.ReadAlignedAsciiString("projectileComponent.id") },
                { "finishDuration", ReadAbilitySystemBlackboardDouble(reader, "projectileComponent.finishDuration") },
                { "finishDistance", ReadAbilitySystemBlackboardDouble(reader, "projectileComponent.finishDistance") },
                { "finishOnReach", reader.ReadBool32("projectileComponent.finishOnReach") },
                { "hitOnReach", reader.ReadBool32("projectileComponent.hitOnReach") },
                { "colliderShapeData", ReadProjectileShapeData(reader, "projectileComponent.colliderShapeData") },
                { "blockLayerDef", ReadPayloadSparseNamedEnum32(reader, "projectileComponent.blockLayerDef", true, (0, "Custom"), (1, "Nothing"), (2, "WallAndGround")) },
                { "blockLayer", BuildPayloadHash32(reader.ReadInt32("projectileComponent.blockLayer")) },
                { "targetFilter", ReadProjectileTargetFilter(reader, "projectileComponent.targetFilter") },
                { "ignoreImmuneLevel", ReadPayloadSparseNamedEnum32(reader, "projectileComponent.ignoreImmuneLevel", true, (0, "Default"), (1, "IgnoreDashImmune")) },
                { "maxHitCount", ReadAbilitySystemBlackboardInt(reader, "projectileComponent.maxHitCount") },
                { "allowHitSameTarget", reader.ReadBool32("projectileComponent.allowHitSameTarget") },
                { "hitIntervalPerTarget", reader.ReadFloat("projectileComponent.hitIntervalPerTarget") },
                { "keepMoveOnReach", reader.ReadBool32("projectileComponent.keepMoveOnReach") },
                { "presetPointKeys", ReadPayloadStringList(reader, "projectileComponent.presetPointKeys", 64) },
                { "useSegmentMove", reader.ReadBool32("projectileComponent.useSegmentMove") },
                { "moveSegments", ReadPayloadObjectList(reader, "projectileComponent.moveSegments", 64, ReadProjectileMoveSegment) },
                { "tail", ReadProjectileComponentTailDiagnostic(reader, "projectileComponent.tail") },
            };
            data["layoutNote"] = "Installed IL2CPP metadata supplies ProjectileComponentData field order. The prefix through moveSegments plus the tail moveModeDict and mainEffect finish fields are decoded from current byte evidence; effect lists, sound fields, and final scalar fields remain raw metadata-ordered tail diagnostics until their nested collection shapes are proven across broader samples.";
            reader.EnsureComplete();
            return true;
        }

        private static OrderedDictionary ReadProjectileShapeData(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "$decoded", true },
                { "layout", "Beyond.Gameplay.Core.ProjectileComponentData/ShapeData" },
                { "shapeType", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.shapeType", true, (0, "None"), (1, "Sphere"), (2, "Box"), (3, "Ring")) },
                { "radius", ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.radius") },
                { "center", ReadAbilitySystemBlackboardVector3(reader, $"{fieldName}.center") },
                { "extent", ReadAbilitySystemBlackboardVector3(reader, $"{fieldName}.extent") },
                { "initOuterRadius", ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.initOuterRadius") },
                { "initInnerRadius", ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.initInnerRadius") },
                { "outerRadiusIncreaseSpeed", ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.outerRadiusIncreaseSpeed") },
                { "innerRadiusIncreaseSpeed", ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.innerRadiusIncreaseSpeed") },
                { "height", ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.height") },
                { "isSector", reader.ReadBool32($"{fieldName}.isSector") },
                { "sectorDirection", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.sectorDirection", true, (0, "SelfForward"), (1, "StartPosToTarget"), (2, "SelfToTarget")) },
                { "sectorAngle", ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.sectorAngle") },
            };
        }

        private static OrderedDictionary ReadProjectileTargetFilter(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "$decoded", true },
                { "layout", "Beyond.Gameplay.Core.TargetFilter" },
                { "checkAlive", reader.ReadBool32($"{fieldName}.checkAlive") },
                { "autoSetTargetFaction", reader.ReadBool32($"{fieldName}.autoSetTargetFaction") },
                { "factionTarget", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.factionTarget")) },
                { "targetFactionType", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.targetFactionType")) },
                { "filterSlot", reader.ReadBool32($"{fieldName}.filterSlot") },
                { "slotIndex", reader.ReadInt32($"{fieldName}.slotIndex") },
                { "filterGameplayTag", reader.ReadBool32($"{fieldName}.filterGameplayTag") },
                { "tagQuery", ReadPayloadGameplayTagQueryWithZeroPadding(reader, $"{fieldName}.tagQuery", 32, 256) },
            };
        }

        private static OrderedDictionary ReadProjectileMoveSegment(
            ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "$decoded", true },
                { "layout", "Beyond.Gameplay.Core.ProjectileComponentData/MoveSegment" },
                { "startPointKey", reader.ReadAlignedAsciiString("projectileComponent.moveSegments.startPointKey") },
                { "moveModeId", reader.ReadAlignedAsciiString("projectileComponent.moveSegments.moveModeId") },
                { "endPointKey", reader.ReadAlignedAsciiString("projectileComponent.moveSegments.endPointKey") },
                { "earlyNextByDuration", reader.ReadBool32("projectileComponent.moveSegments.earlyNextByDuration") },
                { "segmentDuration", ReadAbilitySystemBlackboardDouble(reader, "projectileComponent.moveSegments.segmentDuration") },
                { "speedLerpTime", ReadAbilitySystemBlackboardDouble(reader, "projectileComponent.moveSegments.speedLerpTime") },
            };
        }

        private static OrderedDictionary ReadProjectileComponentTailDiagnostic(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            var start = reader.Position;
            var data = new OrderedDictionary
            {
                { "$partial", true },
                { "relativeOffset", start },
                { "moveModeDict", ReadProjectileMoveModeDictDiagnostic(reader, $"{fieldName}.moveModeDict") },
            };

            ReadProjectileMainEffectFinishDiagnostics(reader, fieldName, data);
            data["remainingMetadataFieldOrder"] = new[]
            {
                "mainEffects",
                "launchEffects",
                "showReachEffectOnlyWithTarget",
                "reachEffects",
                "hitEffects",
                "blockEffects",
                "showFinishEffectOnlyWhenUnblockAndNotHit",
                "finishEffects",
                "showAlertEffect",
                "alertEffect",
                "launchSound",
                "loopSound",
                "reachSound",
                "hitSound",
                "blockSound",
                "finishedSound",
                "sizzleSound",
                "sizzleSoundTriggerDistance",
                "ringProjectileSoundSmoothFactor",
            };
            data["structuredRemainingTail"] = ReadProjectileComponentRemainingTailDiagnostic(reader.RawData, reader.Position, reader.Remaining, $"{fieldName}.structuredRemainingTail");
            data["remainingRawWords"] = ReadRemainingPayloadRawInt32Words(reader, $"{fieldName}.remainingRawWords", 8192);
            data["layoutNote"] = "Tail begins at moveModeDict. The dictionary, current fixed-size MoveModeData records, guarded mainEffect finish fields, and guarded end-relative showAlertEffect/alertEffect view are decoded from current samples; effect-list assignment and separate audio/sound field bytes remain raw until validated.";
            data["length"] = reader.Position - start;
            return data;
        }

        private static void ReadProjectileMainEffectFinishDiagnostics(
            ManagedReferencePayloadReader reader,
            string fieldName,
            OrderedDictionary data
        )
        {
            var start = reader.Position;
            if (TryReadProjectileMainEffectFinishWithType(reader, fieldName, out var finishType, out var finishDistance))
            {
                data["mainEffectFinishTypeSerialized"] = true;
                data["mainEffectFinishType"] = finishType;
                data["mainEffectFinishDistance"] = finishDistance;
                return;
            }

            reader.SetPosition(start);
            if (TryReadProjectileMainEffectFinishDistanceOnly(reader, fieldName, out finishDistance))
            {
                data["mainEffectFinishTypeSerialized"] = false;
                data["mainEffectFinishType"] = new OrderedDictionary
                {
                    { "$omitted", true },
                    { "enumType", "Beyond.Gameplay.ProjectileMainEffectFinishType" },
                    { "layoutNote", "This payload starts mainEffectFinishDistance immediately after moveModeDict; the metadata-listed mainEffectFinishType word is not serialized in this observed variant." },
                };
                data["mainEffectFinishDistance"] = finishDistance;
                return;
            }

            reader.SetPosition(start);
            data["mainEffectFinishTypeSerialized"] = null;
            data["mainEffectFinishType"] = new OrderedDictionary
            {
                { "$unparsed", true },
                { "relativeOffset", start },
                { "enumType", "Beyond.Gameplay.ProjectileMainEffectFinishType" },
                { "layoutNote", "Neither the metadata-listed mainEffectFinishType + BlackboardDouble pair nor the observed distance-only variant could be validated; the remaining tail is preserved raw from this offset." },
            };
        }

        private static bool TryReadProjectileMainEffectFinishWithType(
            ManagedReferencePayloadReader reader,
            string fieldName,
            out OrderedDictionary finishType,
            out OrderedDictionary finishDistance
        )
        {
            finishType = null;
            finishDistance = null;
            var local = new ManagedReferencePayloadReader(reader.RawData, reader.Position, reader.Remaining);
            try
            {
                var value = local.ReadInt32($"{fieldName}.mainEffectFinishType");
                if (value < 0 || value > 2)
                {
                    throw new InvalidDataException($"invalid ProjectileMainEffectFinishType {value}");
                }

                finishDistance = ReadAbilitySystemBlackboardDouble(local, $"{fieldName}.mainEffectFinishDistance");
                finishType = BuildProjectileMainEffectFinishType(value);
                reader.SetPosition(local.Position);
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        private static bool TryReadProjectileMainEffectFinishDistanceOnly(
            ManagedReferencePayloadReader reader,
            string fieldName,
            out OrderedDictionary finishDistance
        )
        {
            finishDistance = null;
            var local = new ManagedReferencePayloadReader(reader.RawData, reader.Position, reader.Remaining);
            try
            {
                finishDistance = ReadAbilitySystemBlackboardDouble(local, $"{fieldName}.mainEffectFinishDistance");
                reader.SetPosition(local.Position);
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        private static OrderedDictionary BuildProjectileMainEffectFinishType(int value)
        {
            var data = BuildPayloadHash32(value);
            data["enumType"] = "Beyond.Gameplay.ProjectileMainEffectFinishType";
            data["layoutNote"] = "Enum field type is known from IL2CPP metadata; observed constants are Default=0, ByTargetPosition=1, ByMaxDistance=2.";
            switch (value)
            {
                case 0:
                    data["name"] = "Default";
                    break;
                case 1:
                    data["name"] = "ByTargetPosition";
                    break;
                case 2:
                    data["name"] = "ByMaxDistance";
                    break;
            }
            return data;
        }
        private static OrderedDictionary ReadProjectileComponentRemainingTailDiagnostic(
            byte[] rawData,
            int offset,
            int length,
            string fieldName
        )
        {
            var data = new OrderedDictionary
            {
                { "$partial", true },
                { "layout", "Beyond.Gameplay.Core.ProjectileComponentData/RemainingTail" },
                { "relativeOffset", offset },
                { "wordCount", length / 4 },
                { "layoutNote", "Guarded end-relative view of the ProjectileComponentData tail after mainEffectFinishDistance. The variable prefix is still raw effect-list data; the stable suffix is showAlertEffect, a projectile EffectActionCfg variant, and a 9-word projectile sound tail." },
            };

            if ((length % 4) != 0 || length < 116 * 4)
            {
                var fallback = new ManagedReferencePayloadReader(rawData, offset, length - (length % 4));
                data["structuredDecodeStatus"] = "notEnoughWords";
                data["fallbackRawWords"] = ReadRemainingPayloadRawInt32Words(fallback, $"{fieldName}.fallbackRawWords", 8192);
                return data;
            }

            try
            {
                var totalWords = length / 4;
                var suffix = FindProjectileAlertEffectSuffix(rawData, offset, length, fieldName);
                if (suffix == null)
                {
                    throw new InvalidDataException("no end-relative showAlertEffect + projectile EffectActionCfg suffix candidate consumed the tail exactly");
                }

                var prefixReader = new ManagedReferencePayloadReader(rawData, offset, length);
                data["effectListAndFinishPrefixWordCount"] = suffix.PrefixWordCount;
                data["effectListAndFinishPrefixRawWords"] = ReadPayloadRawInt32Words(prefixReader, $"{fieldName}.effectListAndFinishPrefixRawWords", suffix.PrefixWordCount);
                data["showAlertEffect"] = suffix.ShowAlertEffect;
                data["alertEffect"] = suffix.AlertEffect;
                data["postAlertEffectSoundTail"] = suffix.PostAlertEffectSoundTail;
                data["alertEffectSuffixWordCount"] = suffix.SuffixWordCount;
                data["alertEffectCandidateCount"] = suffix.CandidateCount;
                data["structuredDecodeStatus"] = "decoded";
                data["consumedWordCount"] = totalWords;
            }
            catch (InvalidDataException ex)
            {
                data["structuredDecodeStatus"] = "failed";
                data["structuredDecodeError"] = ex.Message;
                var fallback = new ManagedReferencePayloadReader(rawData, offset, length);
                data["fallbackRawWords"] = ReadPayloadRawInt32Words(fallback, $"{fieldName}.fallbackRawWords", length / 4);
            }

            return data;
        }

        private sealed class ProjectileAlertEffectSuffix
        {
            public int PrefixWordCount { get; set; }
            public int SuffixWordCount { get; set; }
            public int CandidateCount { get; set; }
            public bool ShowAlertEffect { get; set; }
            public OrderedDictionary AlertEffect { get; set; }
            public OrderedDictionary PostAlertEffectSoundTail { get; set; }
        }

        private static ProjectileAlertEffectSuffix FindProjectileAlertEffectSuffix(
            byte[] rawData,
            int offset,
            int length,
            string fieldName
        )
        {
            var totalWords = length / 4;
            ProjectileAlertEffectSuffix best = null;
            var candidateCount = 0;
            for (var prefixWords = 0; prefixWords <= totalWords - 3; prefixWords++)
            {
                try
                {
                    var local = new ManagedReferencePayloadReader(
                        rawData,
                        offset + prefixWords * 4,
                        length - prefixWords * 4
                    );
                    var suffixWords = totalWords - prefixWords;
                    if (suffixWords < 13)
                    {
                        continue;
                    }

                    var showAlertEffect = local.ReadBool32($"{fieldName}.showAlertEffect");
                    var alertEffectWords = suffixWords - 1 - 9;
                    var alertEffectReader = new ManagedReferencePayloadReader(
                        rawData,
                        local.Position,
                        alertEffectWords * 4
                    );
                    var alertEffect = ReadProjectileAlertEffectActionCfgDiagnostic(alertEffectReader, $"{fieldName}.alertEffect");
                    alertEffectReader.EnsureComplete();
                    local.SetPosition(alertEffectReader.Position);
                    var postAlertEffectSoundTail = ReadProjectilePostAlertEffectSoundTail(local, $"{fieldName}.postAlertEffectSoundTail");
                    local.EnsureComplete();
                    if (!TryReadPayloadInt64(alertEffect, "fxType", "value", out var fxType) || fxType != 1)
                    {
                        continue;
                    }

                    var effectName = alertEffect.Contains("effectName") ? alertEffect["effectName"] as string : null;
                    if (!IsObservedProjectileAlertEffectSuffix(suffixWords, effectName))
                    {
                        continue;
                    }

                    candidateCount++;
                    best = new ProjectileAlertEffectSuffix
                    {
                        PrefixWordCount = prefixWords,
                        SuffixWordCount = suffixWords,
                        ShowAlertEffect = showAlertEffect,
                        AlertEffect = alertEffect,
                        PostAlertEffectSoundTail = postAlertEffectSoundTail,
                    };
                }
                catch (InvalidDataException)
                {
                }
            }

            if (best != null)
            {
                best.CandidateCount = candidateCount;
            }

            return best;
        }

        private static bool IsObservedProjectileAlertEffectSuffix(int suffixWords, string effectName)
        {
            if (string.IsNullOrEmpty(effectName))
            {
                return suffixWords == 116;
            }

            if (string.Equals(effectName, "P_skillalert_circle_01", StringComparison.Ordinal))
            {
                return suffixWords == 122;
            }

            if (string.Equals(effectName, "P_skillalert_circle_01_02", StringComparison.Ordinal))
            {
                return suffixWords == 123;
            }

            return false;
        }

        private static bool TryReadPayloadInt64(
            OrderedDictionary parent,
            string childKey,
            string valueKey,
            out long value
        )
        {
            value = 0;
            if (parent == null || !parent.Contains(childKey) || parent[childKey] is not OrderedDictionary child || !child.Contains(valueKey))
            {
                return false;
            }

            return TryConvertToInt64(child[valueKey], out value);
        }

        private static OrderedDictionary ReadProjectilePostAlertEffectSoundTail(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            var start = reader.Position;
            var data = new OrderedDictionary
            {
                { "$inferred", true },
                { "layout", "Beyond.Gameplay.Core.ProjectileComponentData/PostAlertEffectSoundTail" },
                { "layoutNote", "Final 9 words after projectile alertEffect. Seven hash-like sound fields are named from ProjectileComponentData metadata; the last two words are float distances/factors proven by the 300-projectile focused slice." },
                { "launchSound", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.launchSound")) },
                { "loopSound", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.loopSound")) },
                { "reachSound", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.reachSound")) },
                { "hitSound", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.hitSound")) },
                { "blockSound", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.blockSound")) },
                { "finishedSound", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.finishedSound")) },
                { "sizzleSound", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.sizzleSound")) },
                { "sizzleSoundTriggerDistance", reader.ReadFloat($"{fieldName}.sizzleSoundTriggerDistance") },
                { "ringProjectileSoundSmoothFactor", reader.ReadFloat($"{fieldName}.ringProjectileSoundSmoothFactor") },
            };
            var wordCount = (reader.Position - start) / 4;
            if (wordCount != 9)
            {
                throw new InvalidDataException($"projectile post-alert sound tail consumed {wordCount} words instead of 9");
            }
            data["serializedWordCount"] = wordCount;
            return data;
        }

        private static OrderedDictionary ReadProjectileAlertEffectActionCfgTail(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            var start = reader.Position;
            var data = new OrderedDictionary
            {
                { "$partial", true },
                { "$inferred", true },
                { "layout", "Beyond.Gameplay.EffectActionCfg/ProjectileAlertTail" },
                { "layoutNote", "80-word tail after projectile alertEffect.effectPosData. Field order follows the proven AbilitySystem EffectActionCfg tail while omitting centerOffset, matching all 300 focused projectile samples." },
                { "isShowInDialog", reader.ReadBool32($"{fieldName}.isShowInDialog") },
                { "isLimitEffectCount", reader.ReadBool32($"{fieldName}.isLimitEffectCount") },
                { "limitCount", reader.ReadInt32($"{fieldName}.limitCount") },
                { "protectTime", reader.ReadFloat($"{fieldName}.protectTime") },
                { "limitTime", reader.ReadFloat($"{fieldName}.limitTime") },
                { "limitKey", reader.ReadAlignedAsciiString($"{fieldName}.limitKey") },
                { "assetOnlyAffectModelRoot", reader.ReadBool32($"{fieldName}.assetOnlyAffectModelRoot") },
                { "isUltimateShow", reader.ReadBool32($"{fieldName}.isUltimateShow") },
                { "visibleWithEntity", reader.ReadBool32($"{fieldName}.visibleWithEntity") },
                { "visibleWithEntityType", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.visibleWithEntityType", true, (0, "Source"), (2, "Target")) },
                { "moveType", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.moveType", true, (0, "Stationary"), (2, "FollowTarget"), (4, "FollowCamera"), (6, "FollowSlot")) },
                { "positionRef", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.positionRef", true, (0, "Target"), (2, "Source")) },
                { "grounded", reader.ReadBool32($"{fieldName}.grounded") },
                { "followGrounded", reader.ReadBool32($"{fieldName}.followGrounded") },
                { "followGroundedMaxDistance", reader.ReadFloat($"{fieldName}.followGroundedMaxDistance") },
                { "followHideTarget", reader.ReadBool32($"{fieldName}.followHideTarget") },
                { "visibleWhenHideTarget", reader.ReadBool32($"{fieldName}.visibleWhenHideTarget") },
                { "slotIndex", reader.ReadInt32($"{fieldName}.slotIndex") },
                { "useWeaponMountPoint", reader.ReadBool32($"{fieldName}.useWeaponMountPoint") },
                { "mountPoint", ReadAbilitySystemEffectMountPoint(reader, $"{fieldName}.mountPoint") },
                { "useAccurateMp", reader.ReadBool32($"{fieldName}.useAccurateMp") },
                { "isClothMountPoint", reader.ReadBool32($"{fieldName}.isClothMountPoint") },
                { "weaponIndex", reader.ReadInt32($"{fieldName}.weaponIndex") },
                { "weaponMountPoint", ReadAbilitySystemWeaponMountPoint(reader, $"{fieldName}.weaponMountPoint") },
                { "showHideWithWeapon", reader.ReadBool32($"{fieldName}.showHideWithWeapon") },
                { "offsetDir", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.offsetDir", true, (0, "Self"), (2, "Source"), (4, "Target"), (6, "SelfToSource"), (8, "SelfToTarget"), (10, "SourceToTarget"), (12, "Camera")) },
                { "offsetDirRevert", reader.ReadBool32($"{fieldName}.offsetDirRevert") },
                { "usePositionOffsetBB", reader.ReadBool32($"{fieldName}.usePositionOffsetBB") },
                { "positionOffset", ReadPayloadVector3(reader, $"{fieldName}.positionOffset") },
                { "positionOffsetBB", ReadAbilitySystemBlackboardVector3(reader, $"{fieldName}.positionOffsetBB") },
                { "useTargetRotation", reader.ReadBool32($"{fieldName}.useTargetRotation") },
                { "scaleWithTargetSize", reader.ReadBool32($"{fieldName}.scaleWithTargetSize") },
                { "fxSize", reader.ReadFloat($"{fieldName}.fxSize") },
                { "unpackPosDelayFrame", reader.ReadInt32($"{fieldName}.unpackPosDelayFrame") },
                { "unpackFollowTargetOnRelease", reader.ReadBool32($"{fieldName}.unpackFollowTargetOnRelease") },
                { "rotType", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.rotType", true, (0, "Stationary"), (2, "FollowTarget")) },
                { "rotRef", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.rotRef", true, (0, "Target"), (2, "Source")) },
                { "directionRef", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.directionRef", true, (1, "None"), (0, "Target"), (2, "Source"), (4, "SourceToTarget"), (6, "TargetToSource"), (8, "CurrentPosToTarget"), (10, "CurrentPosToInputTarget"), (12, "CurPosToCamera"), (14, "CameraForward")) },
                { "rotUseWeaponMountPoint", reader.ReadBool32($"{fieldName}.rotUseWeaponMountPoint") },
                { "rotMountPoint", ReadAbilitySystemEffectMountPoint(reader, $"{fieldName}.rotMountPoint") },
                { "rotWeaponIndex", reader.ReadInt32($"{fieldName}.rotWeaponIndex") },
                { "rotWeaponMountPoint", ReadAbilitySystemWeaponMountPoint(reader, $"{fieldName}.rotWeaponMountPoint") },
                { "revertDir", reader.ReadBool32($"{fieldName}.revertDir") },
                { "useSelfRotationBB", reader.ReadBool32($"{fieldName}.useSelfRotationBB") },
                { "selfRotation", ReadPayloadVector3(reader, $"{fieldName}.selfRotation") },
                { "selfRotationBB", ReadAbilitySystemBlackboardVector3(reader, $"{fieldName}.selfRotationBB") },
                { "lockYRotation", reader.ReadBool32($"{fieldName}.lockYRotation") },
                { "unpackRotDelayFrame", reader.ReadInt32($"{fieldName}.unpackRotDelayFrame") },
                { "unpackFollowTargetRotOnRelease", reader.ReadBool32($"{fieldName}.unpackFollowTargetRotOnRelease") },
                { "weaponVfxKey", reader.ReadAlignedAsciiString($"{fieldName}.weaponVfxKey") },
                { "weaponVfxIndex", reader.ReadInt32($"{fieldName}.weaponVfxIndex") },
                { "weaponVfxPersistent", reader.ReadBool32($"{fieldName}.weaponVfxPersistent") },
                { "alertType", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.alertType", true, (0, "Decal"), (2, "Particle")) },
                { "animateAlert", reader.ReadBool32($"{fieldName}.animateAlert") },
                { "alertAnimateDuration", reader.ReadFloat($"{fieldName}.alertAnimateDuration") },
                { "isAlertAnimateReverse", reader.ReadBool32($"{fieldName}.isAlertAnimateReverse") },
                { "angle", reader.ReadFloat($"{fieldName}.angle") },
                { "hollow", reader.ReadFloat($"{fieldName}.hollow") },
                { "modifyType", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.modifyType", true, (0, "StartLifeTime")) },
                { "value", reader.ReadFloat($"{fieldName}.value") },
            };
            var wordCount = (reader.Position - start) / 4;
            if (wordCount != 80)
            {
                throw new InvalidDataException($"projectile alertEffect tail consumed {wordCount} words instead of 80");
            }
            data["serializedWordCount"] = wordCount;
            return data;
        }
        private static OrderedDictionary ReadProjectileAlertEffectActionCfgDiagnostic(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            var start = reader.Position;
            var data = new OrderedDictionary
            {
                { "$partial", true },
                { "$inferred", true },
                { "layout", "Beyond.Gameplay.EffectActionCfg" },
                { "relativeOffset", start },
                { "layoutNote", "Projectile alertEffect is bounded as an end-relative EffectActionCfg variant. The 24-word prefix after effectName and the following 80-word EffectActionCfg tail are decoded only for the byte-proven 104-word post-name shape." },
                { "observedPayloadStatus", "projectile alertEffect suffix consumes through the observed ProjectileComponentData tail end" },
                { "partialReasons", new List<string>
                    {
                        "Projectile alertEffect inner field variants differ from the focused AbilitySystemData deadEffect variant.",
                        "The 300-sample projectile slice proves fxType, effectName, the 24-word post-name prefix, and the 80-word EffectActionCfg tail.",
                        "BlackboardDouble internals and sound hash semantics remain diagnostic rather than fully named.",
                    }
                },
                { "fxType", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.fxType", true, (0, "Normal"), (1, "Alert"), (2, "Alert"), (4, "BottomScreen"), (6, "WeaponVfx")) },
                { "effectName", reader.ReadAlignedAsciiString($"{fieldName}.effectName") },
            };

            if (reader.Remaining == 104 * 4
                && TryReadProjectileAlertEffectActionCfgPrefix(reader, fieldName, out var prefix))
            {
                data["projectilePrefixStatus"] = "decoded 24-word projectile alertEffect prefix";
                data["projectilePrefixWordCount"] = prefix.WordCount;
                data["omittedSerializedFields"] = new List<string> { "useScaleBB", "centerOffset" };
                foreach (DictionaryEntry entry in prefix.Fields)
                {
                    data[entry.Key] = entry.Value;
                }
                data["projectileEffectActionTailStatus"] = "decoded 80-word projectile alertEffect tail";
                data["effectActionTail"] = ReadProjectileAlertEffectActionCfgTail(reader, $"{fieldName}.effectActionTail");
            }
            else
            {
                data["projectilePrefixStatus"] = "raw fallback";
            }

            data["remainingRawWordCount"] = reader.Remaining / 4;
            if (reader.Remaining > 0)
            {
                data["remainingRawWords"] = ReadPayloadRawInt32Words(reader, $"{fieldName}.remainingRawWords", reader.Remaining / 4);
            }
            data["serializedWordCount"] = (reader.Position - start) / 4;
            return data;
        }

        private sealed class ProjectileAlertEffectPrefix
        {
            public int WordCount { get; set; }
            public OrderedDictionary Fields { get; } = new OrderedDictionary();
        }

        private static bool TryReadProjectileAlertEffectActionCfgPrefix(
            ManagedReferencePayloadReader reader,
            string fieldName,
            out ProjectileAlertEffectPrefix prefix
        )
        {
            prefix = null;
            var start = reader.Position;
            var local = new ManagedReferencePayloadReader(reader.RawData, reader.Position, reader.Remaining);
            try
            {
                var fields = new OrderedDictionary
                {
                    { "guardEffect", local.ReadBool32($"{fieldName}.guardEffect") },
                    { "forceGuardEffect", local.ReadBool32($"{fieldName}.forceGuardEffect") },
                    { "isCenterChangeLod", local.ReadBool32($"{fieldName}.isCenterChangeLod") },
                    { "useScaleBBSerialized", false },
                    { "scale", ReadPayloadVector3(local, $"{fieldName}.scale") },
                    { "scaleBB", ReadAbilitySystemBlackboardVector3(local, $"{fieldName}.scaleBB") },
                    { "useLengthBB", local.ReadBool32($"{fieldName}.useLengthBB") },
                    { "lengthBB", ReadAbilitySystemBlackboardDouble(local, $"{fieldName}.lengthBB") },
                    { "releaseByAction", local.ReadBool32($"{fieldName}.releaseByAction") },
                    { "ignoreOwnerTimeScale", local.ReadBool32($"{fieldName}.ignoreOwnerTimeScale") },
                    { "interruptTime", local.ReadFloat($"{fieldName}.interruptTime") },
                    { "terrainPrefab", local.ReadBool32($"{fieldName}.terrainPrefab") },
                    { "effectPosData", ReadAbilitySystemTerrainEffectDataArray(local, $"{fieldName}.effectPosData") },
                };
                var wordCount = (local.Position - start) / 4;
                if (wordCount != 24 || local.Remaining != 80 * 4)
                {
                    throw new InvalidDataException($"projectile alertEffect prefix consumed {wordCount} words with {local.Remaining / 4} words remaining");
                }

                reader.SetPosition(local.Position);
                prefix = new ProjectileAlertEffectPrefix { WordCount = wordCount };
                foreach (DictionaryEntry entry in fields)
                {
                    prefix.Fields[entry.Key] = entry.Value;
                }
                return true;
            }
            catch (InvalidDataException)
            {
                reader.SetPosition(start);
                prefix = null;
                return false;
            }
        }
        private static OrderedDictionary ReadProjectileMoveModeDictDiagnostic(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            var start = reader.Position;
            var keys = ReadPayloadStringList(reader, $"{fieldName}.keys", 32);
            var valueCount = reader.ReadInt32($"{fieldName}.values.count");
            if (valueCount != keys.Count)
            {
                throw new InvalidDataException($"mismatched key/value counts {keys.Count}/{valueCount} for {fieldName}");
            }

            var values = new List<OrderedDictionary>(valueCount);
            for (var i = 0; i < valueCount; i++)
            {
                values.Add(ReadProjectileMoveModeDataDiagnostic(reader, $"{fieldName}.values[{i}]", keys[i]));
            }

            return new OrderedDictionary
            {
                { "$decoded", true },
                { "layout", "Dictionary<string, Beyond.Gameplay.Core.ProjectileComponentData/MoveModeData>" },
                { "observedPayloadStatus", "dictionary key/value counts and fixed-size value boundaries are fully consumed by this reader" },
                { "nestedPartialReasons", new List<string>
                    {
                        "Nested MoveModeData records still contain raw speed-info, animation-curve, and BezierPoint internals.",
                        "The enclosing ProjectileComponentData tail still contains effect/sound/scalar collections after moveModeDict.",
                    }
                },
                { "relativeOffset", start },
                { "keyCount", keys.Count },
                { "keys", keys },
                { "valueCount", valueCount },
                { "values", values },
                { "length", reader.Position - start },
                { "layoutNote", "Dictionary header and current fixed-size MoveModeData records are decoded. The first MoveModeData fields are named from IL2CPP metadata; speed-info, animation-curve, and BezierPoint internals remain raw inside each value record." },
            };
        }

        private static OrderedDictionary ReadProjectileMoveModeDataDiagnostic(
            ManagedReferencePayloadReader reader,
            string fieldName,
            string key
        )
        {
            const int wordCount = 124;
            const int decodedPrefixWords = 9;
            var start = reader.Position;
            if (reader.Remaining < wordCount * 4)
            {
                throw new InvalidDataException($"not enough bytes for {fieldName}; need {wordCount * 4}, have {reader.Remaining}");
            }

            var data = new OrderedDictionary
            {
                { "$partial", true },
                { "layout", "Beyond.Gameplay.Core.ProjectileComponentData/MoveModeData" },
                { "observedPayloadStatus", "fixed 124-word MoveModeData value consumed by this reader; prefix through parabolaDef and guarded suffix view are decoded while the original suffix remains raw" },
                { "partialReasons", new List<string>
                    {
                        "The suffix after parabolaDef is decoded through scalar, bool, AnimationCurve, and full BezierPoint boundaries, but the original raw suffix is preserved because one observed bezierMidPoint2 record is truncated.",
                        "IL2CPP metadata lists m_parabolaSpeedInfo, m_bezierSpeedInfo, and m_speedCurveInfo, but current payload samples show no serialized bytes for those fields.",
                        "bezierMidPoint2 is a full 21-word record in most focused samples but is truncated to raw trailing words in one sample, so this record remains partial.",
                        "Enum numeric values are emitted with enum type names, but member names are withheld until independently validated.",
                    }
                },
                { "key", key },
                { "relativeOffset", start },
                { "decodedPrefixWordCount", decodedPrefixWords },
                { "remainingRawWordCount", wordCount - decodedPrefixWords },
                { "traceType", ReadPayloadEnum32Candidate(reader, $"{fieldName}.traceType", "Beyond.Gameplay.ProjectileTraceType") },
                { "traceTime", ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.traceTime") },
                { "traceUntilDistance", ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.traceUntilDistance") },
                { "moveType", ReadPayloadEnum32Candidate(reader, $"{fieldName}.moveType", "Beyond.Gameplay.ProjectileMoveType") },
                { "parabolaDef", ReadPayloadEnum32Candidate(reader, $"{fieldName}.parabolaDef", "Beyond.Gameplay.ProjectileParabolaDef") },
                { "remainingMetadataFieldOrder", new[]
                    {
                        "m_parabolaSpeedInfo",
                        "m_bezierSpeedInfo",
                        "speed",
                        "m_speedCurveInfo",
                        "speedCurve",
                        "useSpeedScaleWithDistance",
                        "speedScaleWithDistance",
                        "lockVelocityToXZ",
                        "groundedMove",
                        "limitAngularSpeed",
                        "angularSpeed",
                        "angularSpeedCurve",
                        "travelDuration",
                        "vertexYOffset",
                        "gravity",
                        "bezierMidPoint1",
                        "bezierMidPoint2",
                    }
                },
            };

            var suffixStart = reader.Position;
            var suffixLength = (wordCount - decodedPrefixWords) * 4;
            data["structuredSuffix"] = ReadProjectileMoveModeDataSuffixDiagnostic(reader.RawData, suffixStart, suffixLength, $"{fieldName}.structuredSuffix");
            data["remainingRawWords"] = ReadPayloadRawInt32Words(reader, $"{fieldName}.remainingRawWords", wordCount - decodedPrefixWords);
            data["layoutNote"] = "Current samples serialize each MoveModeData value as 124 int32 words. The prefix through parabolaDef and guarded suffix boundaries are decoded from IL2CPP field order and byte evidence; the original suffix remains raw because one BezierPoint variant is truncated and non-serialized speed-info metadata fields are not fully proven.";
            data["wordCount"] = (reader.Position - start) / 4;
            data["length"] = reader.Position - start;
            return data;
        }

        private static OrderedDictionary ReadProjectileMoveModeDataSuffixDiagnostic(
            byte[] rawData,
            int offset,
            int length,
            string fieldName
        )
        {
            var data = new OrderedDictionary
            {
                { "$partial", true },
                { "layout", "Beyond.Gameplay.Core.ProjectileComponentData/MoveModeData/StructuredSuffix" },
                { "relativeOffset", offset },
                { "wordCount", length / 4 },
                { "nonSerializedMetadataFields", new[]
                    {
                        "m_parabolaSpeedInfo",
                        "m_bezierSpeedInfo",
                        "m_speedCurveInfo",
                    }
                },
                { "layoutNote", "Guarded structured view of the fixed MoveModeData suffix. Scalar, bool, AnimationCurve, complete BezierPoint records, and terminal BezierPoint prefixes are decoded; any unproven terminal bytes stay bounded raw records." },
            };

            try
            {
                var local = new ManagedReferencePayloadReader(rawData, offset, length);
                data["speed"] = ReadAbilitySystemBlackboardDouble(local, $"{fieldName}.speed");
                data["speedCurve"] = ReadPayloadAnimationCurveFloat(local, $"{fieldName}.speedCurve");
                data["useSpeedScaleWithDistance"] = local.ReadBool32($"{fieldName}.useSpeedScaleWithDistance");
                data["speedScaleWithDistance"] = ReadPayloadAnimationCurveFloat(local, $"{fieldName}.speedScaleWithDistance");
                data["lockVelocityToXZ"] = local.ReadBool32($"{fieldName}.lockVelocityToXZ");
                data["groundedMove"] = local.ReadBool32($"{fieldName}.groundedMove");
                data["limitAngularSpeed"] = local.ReadBool32($"{fieldName}.limitAngularSpeed");
                data["angularSpeed"] = ReadAbilitySystemBlackboardDouble(local, $"{fieldName}.angularSpeed");
                data["angularSpeedCurve"] = ReadPayloadAnimationCurveFloat(local, $"{fieldName}.angularSpeedCurve");
                data["travelDuration"] = ReadAbilitySystemBlackboardDouble(local, $"{fieldName}.travelDuration");
                data["vertexYOffset"] = ReadAbilitySystemBlackboardDouble(local, $"{fieldName}.vertexYOffset");
                data["gravity"] = ReadAbilitySystemBlackboardDouble(local, $"{fieldName}.gravity");
                if (TryReadProjectileBezierPointDiagnostic(local, $"{fieldName}.bezierMidPoint1", out var bezierMidPoint1))
                {
                    data["bezierMidPoint1Status"] = GetProjectileBezierPointStatus(bezierMidPoint1);
                    data["bezierMidPoint1"] = bezierMidPoint1;
                }
                else
                {
                    var remainingWords = Math.Min(21, local.Remaining / 4);
                    data["bezierMidPoint1Status"] = $"raw fallback; {remainingWords} words preserved";
                    data["bezierMidPoint1RawWords"] = ReadPayloadRawInt32Words(local, $"{fieldName}.bezierMidPoint1RawWords", remainingWords);
                }

                if (local.Remaining > 0 && TryReadProjectileBezierPointDiagnostic(local, $"{fieldName}.bezierMidPoint2", out var bezierMidPoint2))
                {
                    data["bezierMidPoint2Status"] = GetProjectileBezierPointStatus(bezierMidPoint2);
                    data["bezierMidPoint2"] = bezierMidPoint2;
                }
                else if (local.Remaining > 0 && IsZeroFilled(local.RawData, local.Position, local.Remaining))
                {
                    var remainingWords = local.Remaining / 4;
                    data["bezierMidPoint2Status"] = $"absent; {remainingWords} zero terminal words preserved";
                    data["bezierMidPoint2PaddingWords"] = ReadPayloadRawInt32Words(local, $"{fieldName}.bezierMidPoint2PaddingWords", remainingWords);
                }
                else if (local.Remaining > 0)
                {
                    var remainingWords = local.Remaining / 4;
                    data["bezierMidPoint2Status"] = $"raw or truncated; {remainingWords} raw words preserved";
                    data["bezierMidPoint2RawWords"] = ReadPayloadRawInt32Words(local, $"{fieldName}.bezierMidPoint2RawWords", remainingWords);
                }
                else
                {
                    data["bezierMidPoint2Status"] = "absent";
                    data["bezierMidPoint2RawWords"] = new List<OrderedDictionary>();
                }

                if (local.Remaining > 0)
                {
                    data["trailingRawWords"] = ReadRemainingPayloadRawInt32Words(local, $"{fieldName}.trailingRawWords", 128);
                }

                data["structuredDecodeStatus"] = "decoded";
                data["consumedWordCount"] = (local.Position - offset) / 4;
                data["remainingWordCount"] = local.Remaining / 4;
            }
            catch (InvalidDataException ex)
            {
                data["structuredDecodeStatus"] = "failed";
                data["structuredDecodeError"] = ex.Message;
                var fallback = new ManagedReferencePayloadReader(rawData, offset, length);
                data["fallbackRawWords"] = ReadPayloadRawInt32Words(fallback, $"{fieldName}.fallbackRawWords", length / 4);
            }

            return data;
        }

        private static string GetProjectileBezierPointStatus(OrderedDictionary data)
        {
            if (data != null && data.Contains("decodeStatus") && data["decodeStatus"] is string status)
            {
                return status;
            }
            return "decoded";
        }

        private static bool TryReadProjectileBezierPointDiagnostic(
            ManagedReferencePayloadReader reader,
            string fieldName,
            out OrderedDictionary data
        )
        {
            var start = reader.Position;
            if (TryReadProjectileBezierPointComplete(reader, fieldName, out data))
            {
                return true;
            }

            reader.SetPosition(start);
            return TryReadProjectileBezierPointTerminalPrefix(reader, fieldName, out data);
        }

        private static bool TryReadProjectileBezierPointComplete(
            ManagedReferencePayloadReader reader,
            string fieldName,
            out OrderedDictionary data
        )
        {
            data = null;
            var start = reader.Position;
            var local = new ManagedReferencePayloadReader(reader.RawData, reader.Position, reader.Remaining);
            try
            {
                var result = BuildProjectileBezierPointHeader(start);
                result["usePresetPoint"] = local.ReadBool32($"{fieldName}.usePresetPoint");
                result["presetPointKey"] = local.ReadAlignedAsciiString($"{fieldName}.presetPointKey");
                result["xRatioRange"] = ReadProjectileBlackboardDoubleRange(local, $"{fieldName}.xRatioRange");
                result["yzAngleRange"] = ReadProjectileBlackboardDoubleRange(local, $"{fieldName}.yzAngleRange");
                result["yzRadiusRange"] = ReadProjectileBlackboardDoubleRange(local, $"{fieldName}.yzRadiusRange");
                if (local.Remaining >= 4)
                {
                    result["scaledYzRadius"] = local.ReadBool32($"{fieldName}.scaledYzRadius");
                    result["scaledYzRadiusSerialized"] = true;
                    result["decodeStatus"] = "decoded";
                }
                else
                {
                    result["$partial"] = true;
                    result["scaledYzRadiusSerialized"] = false;
                    result["partialReasons"] = new[]
                    {
                        "scaledYzRadius is omitted at the terminal MoveModeData suffix boundary in this observed payload.",
                    };
                    result["decodeStatus"] = "decoded terminal BezierPoint; scaledYzRadius omitted";
                }

                reader.SetPosition(local.Position);
                result["length"] = local.Position - start;
                result["serializedWordCount"] = (local.Position - start) / 4;
                result["layoutNote"] = "Decoded from IL2CPP BezierPoint field order. Range values use adaptive BlackboardDouble decoding, so records may be longer than the earlier 21-word empty-key shape; terminal payloads may omit scaledYzRadius.";
                data = result;
                return true;
            }
            catch (InvalidDataException)
            {
                data = null;
                reader.SetPosition(start);
                return false;
            }
        }

        private static bool TryReadProjectileBezierPointTerminalPrefix(
            ManagedReferencePayloadReader reader,
            string fieldName,
            out OrderedDictionary data
        )
        {
            data = null;
            var start = reader.Position;
            var local = new ManagedReferencePayloadReader(reader.RawData, reader.Position, reader.Remaining);
            try
            {
                var result = BuildProjectileBezierPointHeader(start);
                result["$partial"] = true;
                result["usePresetPoint"] = local.ReadBool32($"{fieldName}.usePresetPoint");
                result["presetPointKey"] = local.ReadAlignedAsciiString($"{fieldName}.presetPointKey");
                if (!TryReadProjectileBlackboardDoubleRangeTerminalPrefix(local, $"{fieldName}.xRatioRange", out var xRatioRange))
                {
                    reader.SetPosition(start);
                    return false;
                }

                result["xRatioRange"] = xRatioRange;
                if (local.Remaining > 0 && TryReadProjectileBlackboardDoubleRangeTerminalPrefix(local, $"{fieldName}.yzAngleRange", out var yzAngleRange))
                {
                    result["yzAngleRange"] = yzAngleRange;
                }
                if (local.Remaining > 0 && TryReadProjectileBlackboardDoubleRangeTerminalPrefix(local, $"{fieldName}.yzRadiusRange", out var yzRadiusRange))
                {
                    result["yzRadiusRange"] = yzRadiusRange;
                }

                if (local.Remaining >= 4)
                {
                    var scaledStart = local.Position;
                    try
                    {
                        result["scaledYzRadius"] = local.ReadBool32($"{fieldName}.scaledYzRadius");
                        result["scaledYzRadiusSerialized"] = true;
                    }
                    catch (InvalidDataException)
                    {
                        local.SetPosition(scaledStart);
                    }
                }
                if (!result.Contains("scaledYzRadiusSerialized"))
                {
                    result["scaledYzRadiusSerialized"] = false;
                }
                if (local.Remaining > 0)
                {
                    result["terminalRawWords"] = ReadRemainingPayloadRawInt32Words(local, $"{fieldName}.terminalRawWords", 128);
                }

                reader.SetPosition(local.Position);
                result["length"] = local.Position - start;
                result["serializedWordCount"] = (local.Position - start) / 4;
                result["decodeStatus"] = "decoded terminal BezierPoint prefix";
                result["partialReasons"] = new[]
                {
                    "The MoveModeData suffix ended before every metadata-listed BezierPoint field was serialized; decoded prefix fields are emitted and the bounded terminal tail is preserved when present.",
                };
                result["layoutNote"] = "Decoded as a terminal prefix of the IL2CPP BezierPoint field order. This handles observed MoveModeData suffixes that stop after xRatioRange, yzAngleRange, or a partial yzRadiusRange instead of serializing the full 21-word empty-key shape.";
                data = result;
                return true;
            }
            catch (InvalidDataException)
            {
                data = null;
                reader.SetPosition(start);
                return false;
            }
        }

        private static OrderedDictionary BuildProjectileBezierPointHeader(int relativeOffset)
        {
            return new OrderedDictionary
            {
                { "$decoded", true },
                { "$inferred", true },
                { "layout", "Beyond.Gameplay.Core.ProjectileComponentData/BezierPoint" },
                { "relativeOffset", relativeOffset },
                { "metadataFieldOrder", new[]
                    {
                        "usePresetPoint",
                        "presetPointKey",
                        "xRatioRange",
                        "yzAngleRange",
                        "yzRadiusRange",
                        "scaledYzRadius",
                    }
                },
            };
        }

        private static bool TryReadProjectileBlackboardDoubleRangeTerminalPrefix(
            ManagedReferencePayloadReader reader,
            string fieldName,
            out OrderedDictionary data
        )
        {
            data = null;
            var start = reader.Position;
            var local = new ManagedReferencePayloadReader(reader.RawData, reader.Position, reader.Remaining);
            if (!TryReadAbilitySystemBlackboardDoubleTerminalPrefix(local, $"{fieldName}.min", out var min, out var minPartial))
            {
                reader.SetPosition(start);
                return false;
            }

            OrderedDictionary max = null;
            var maxPartial = false;
            var maxSerialized = false;
            if (local.Remaining > 0 && TryReadAbilitySystemBlackboardDoubleTerminalPrefix(local, $"{fieldName}.max", out max, out maxPartial))
            {
                maxSerialized = true;
            }

            reader.SetPosition(local.Position);
            var result = new OrderedDictionary
            {
                { "$decoded", true },
                { "$inferred", true },
                { "layout", "Beyond.Blackboard.BlackboardDoubleRange" },
                { "min", min },
                { "maxSerialized", maxSerialized },
            };
            if (maxSerialized)
            {
                result["max"] = max;
                result["valueCandidate"] = new OrderedDictionary
                {
                    { "min", min["valueFloatCandidate"] },
                    { "max", max["valueFloatCandidate"] },
                };
            }
            else
            {
                result["$partial"] = true;
                result["partialReason"] = "Range max was not serialized before the terminal BezierPoint boundary.";
                result["valueCandidate"] = new OrderedDictionary
                {
                    { "min", min["valueFloatCandidate"] },
                };
            }
            if (minPartial || maxPartial)
            {
                result["$partial"] = true;
                result["terminalBlackboardKeyNote"] = "At least one terminal BlackboardDouble ended after useBlackboardKey/value before an aligned blackboardKey string length was serialized.";
            }
            result["length"] = local.Position - start;
            result["serializedWordCount"] = (local.Position - start) / 4;
            data = result;
            return true;
        }

        private static bool TryReadAbilitySystemBlackboardDoubleTerminalPrefix(
            ManagedReferencePayloadReader reader,
            string fieldName,
            out OrderedDictionary data,
            out bool partial
        )
        {
            data = null;
            partial = false;
            var start = reader.Position;
            try
            {
                if (reader.Remaining < 8)
                {
                    reader.SetPosition(start);
                    return false;
                }

                var useBlackboardKey = reader.ReadBool32($"{fieldName}.useBlackboardKey");
                var value = reader.ReadFloat($"{fieldName}.value");
                var result = new OrderedDictionary
                {
                    { "layout", "Beyond.Blackboard.BlackboardDouble" },
                    { "serializationShape", "bool-float-key-terminal" },
                    { "useBlackboardKey", useBlackboardKey },
                    { "value", value },
                    { "valueFloatCandidate", value },
                };

                if (reader.Remaining >= 4)
                {
                    var keyStart = reader.Position;
                    try
                    {
                        result["blackboardKey"] = reader.ReadAlignedAsciiString($"{fieldName}.blackboardKey");
                        result["blackboardKeySerialized"] = true;
                    }
                    catch (InvalidDataException)
                    {
                        reader.SetPosition(keyStart);
                        result["blackboardKeySerialized"] = false;
                        result["terminalRawWords"] = ReadRemainingPayloadRawInt32Words(reader, $"{fieldName}.terminalRawWords", 128);
                        partial = true;
                    }
                }
                else
                {
                    result["blackboardKeySerialized"] = false;
                    partial = true;
                }

                if (partial)
                {
                    result["$partial"] = true;
                    result["layoutNote"] = "Terminal BlackboardDouble prefix decoded from useBlackboardKey and value; the aligned blackboardKey string length was not serialized before the BezierPoint boundary.";
                }
                else
                {
                    result["layoutNote"] = "IL2CPP metadata-backed shape: bool32 useBlackboardKey, float32 value, aligned blackboardKey string.";
                }
                result["length"] = reader.Position - start;
                result["serializedWordCount"] = (reader.Position - start) / 4;
                data = result;
                return true;
            }
            catch (InvalidDataException)
            {
                data = null;
                partial = false;
                reader.SetPosition(start);
                return false;
            }
        }
        private static OrderedDictionary ReadProjectileBezierPointRawRecord(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int wordCount,
            bool requireFull
        )
        {
            if (wordCount < 0 || wordCount > 21)
            {
                throw new InvalidDataException($"invalid BezierPoint word count {wordCount} for {fieldName}");
            }

            if (requireFull && reader.Remaining < wordCount * 4)
            {
                throw new InvalidDataException($"not enough bytes for {fieldName}; need {wordCount * 4}, have {reader.Remaining}");
            }

            var start = reader.Position;
            var actualWordCount = Math.Min(wordCount, reader.Remaining / 4);
            if (actualWordCount == 21)
            {
                var local = new ManagedReferencePayloadReader(reader.RawData, start, wordCount * 4);
                try
                {
                    var decoded = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.ProjectileComponentData/BezierPoint" },
                        { "relativeOffset", start },
                        { "wordCount", actualWordCount },
                        { "usePresetPoint", local.ReadBool32($"{fieldName}.usePresetPoint") },
                        { "presetPointKey", local.ReadAlignedAsciiString($"{fieldName}.presetPointKey") },
                        { "xRatioRange", ReadProjectileBlackboardDoubleRange(local, $"{fieldName}.xRatioRange") },
                        { "yzAngleRange", ReadProjectileBlackboardDoubleRange(local, $"{fieldName}.yzAngleRange") },
                        { "yzRadiusRange", ReadProjectileBlackboardDoubleRange(local, $"{fieldName}.yzRadiusRange") },
                        { "scaledYzRadius", local.ReadBool32($"{fieldName}.scaledYzRadius") },
                        { "layoutNote", "Decoded from IL2CPP BezierPoint field order and the observed 21-word payload shape: bool, aligned string, three two-endpoint BlackboardDouble ranges, bool." },
                    };
                    local.EnsureComplete();
                    reader.SetPosition(start + wordCount * 4);
                    decoded["length"] = wordCount * 4;
                    return decoded;
                }
                catch (InvalidDataException ex)
                {
                    reader.SetPosition(start);
                    var fallback = new OrderedDictionary
                    {
                        { "$partial", true },
                        { "layout", "Beyond.Gameplay.Core.ProjectileComponentData/BezierPoint" },
                        { "relativeOffset", start },
                        { "wordCount", actualWordCount },
                        { "decodeError", ex.Message },
                        { "metadataFieldOrder", new[]
                            {
                                "usePresetPoint",
                                "presetPointKey",
                                "xRatioRange",
                                "yzAngleRange",
                                "yzRadiusRange",
                                "scaledYzRadius",
                            }
                        },
                        { "rawWords", ReadPayloadRawInt32Words(reader, $"{fieldName}.rawWords", actualWordCount) },
                        { "layoutNote", "BezierPoint field order is known, but this record did not match the focused 21-word decoded shape and is retained raw." },
                    };
                    fallback["length"] = reader.Position - start;
                    return fallback;
                }
            }

            var data = new OrderedDictionary
            {
                { "$partial", true },
                { "layout", "Beyond.Gameplay.Core.ProjectileComponentData/BezierPoint" },
                { "relativeOffset", start },
                { "wordCount", actualWordCount },
                { "metadataFieldOrder", new[]
                    {
                        "usePresetPoint",
                        "presetPointKey",
                        "xRatioRange",
                        "yzAngleRange",
                        "yzRadiusRange",
                        "scaledYzRadius",
                    }
                },
                { "rawWords", ReadPayloadRawInt32Words(reader, $"{fieldName}.rawWords", actualWordCount) },
                { "layoutNote", "IL2CPP metadata supplies BezierPoint field order, but this truncated record is shorter than the observed 21-word decoded shape and is retained raw." },
            };
            data["length"] = reader.Position - start;
            return data;
        }

        private static OrderedDictionary ReadProjectileBlackboardDoubleRange(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            var min = ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.min");
            var max = ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.max");
            return new OrderedDictionary
            {
                { "$decoded", true },
                { "$inferred", true },
                { "layout", "Beyond.Blackboard.BlackboardDoubleRange" },
                { "min", min },
                { "max", max },
                { "valueCandidate", new OrderedDictionary
                    {
                        { "min", min["valueFloatCandidate"] },
                        { "max", max["valueFloatCandidate"] },
                    }
                },
            };
        }
        private static OrderedDictionary ReadPayloadEnum32Candidate(
            ManagedReferencePayloadReader reader,
            string fieldName,
            string enumType
        )
        {
            var data = BuildPayloadHash32(reader.ReadInt32(fieldName));
            data["enumType"] = enumType;
            data["layoutNote"] = "Enum field type is known from IL2CPP metadata; numeric names are withheld until enum constants are independently validated.";
            return data;
        }

        private static bool TryDecodeCoreActionConditionManagedReferenceData(
            ManagedReferenceHeader header,
            ManagedReferencePayloadReader reader,
            int offset,
            int length,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid,
            out OrderedDictionary data
        )
        {
            data = null;
            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                && (string.Equals(header.ClassName, "NotNextCheckAction/Data", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "ReturnFalseAction/Data", StringComparison.Ordinal)))
            {
                if (length != 16)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["layoutNote"] = "Installed IL2CPP metadata and current payload bytes show only the inherited AbilityActionData prefix for this action; no class-local field bytes are present.";
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "CheckDamageDecorateMask/Data", StringComparison.Ordinal))
            {
                if (length != 28)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["checkType"] = ReadPayloadNamedEnum32(reader, "checkDamageDecorateMask.checkType", new[] { "Exact", "HasAny", "HasAll", "ExceptAny", "ExceptAll" });
                data["mask"] = BuildPayloadHash64(reader.ReadInt64("checkDamageDecorateMask.mask"));
                data["layoutNote"] = "Installed IL2CPP metadata exposes checkType and mask after the inherited AbilityActionData prefix; all audited payloads are 28 bytes.";
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "CheckBuffIdInContext/Data", StringComparison.Ordinal))
            {
                if (length < 40 || (length % 4) != 0)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["checkType"] = ReadPayloadNamedEnum32(reader, "checkBuffIdInContext.checkType", new[] { "Id", "Tag" });
                data["buffIdList"] = ReadPayloadStringListWithZeroPadding(reader, "checkBuffIdInContext.buffIdList", 16, 128);
                data["query"] = ReadPayloadGameplayTagQueryWithZeroPadding(reader, "checkBuffIdInContext.query", 16, 256);
                data["blackboardKey"] = ReadPayloadAlignedAsciiStringWithZeroPadding(reader, "checkBuffIdInContext.blackboardKey", 128);
                data["layoutNote"] = "Installed IL2CPP metadata exposes checkType, buffIdList, tag query, and blackboardKey after the inherited AbilityActionData prefix; current payloads use bounded string/tag lists and an empty blackboardKey.";
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "CheckMainCharacterCondition/Data", StringComparison.Ordinal))
            {
                if (length < 112 || length > 160 || (length % 4) != 0)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["checkTarget"] = ReadDiagnosticTargetSettings(reader, "checkMainCharacterCondition.checkTarget", offset, recoveredByRid);
                data["observedPayloadStatus"] = "all serialized CheckMainCharacterCondition/Data bytes consumed by this reader; nested TargetSettings carries its own partial marker";
                data["layoutNote"] = "Installed IL2CPP/MemoryPack metadata exposes checkTarget after the inherited AbilityActionData prefix. The payload is consumed completely, but checkTarget is emitted with partial TargetSettings diagnostics because selector/suffix semantics are still unresolved.";
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "CheckObjectTypeMatch/Data", StringComparison.Ordinal))
            {
                if (length < 116 || length > 160 || (length % 4) != 0)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["target"] = ReadDiagnosticTargetSettings(reader, "checkObjectTypeMatch.target", offset, recoveredByRid);
                data["objectTypeMask"] = BuildPayloadHash32(reader.ReadInt32("checkObjectTypeMatch.objectTypeMask"));
                data["observedPayloadStatus"] = "all serialized CheckObjectTypeMatch/Data bytes consumed by this reader; nested TargetSettings carries its own partial marker";
                data["layoutNote"] = "Installed IL2CPP/MemoryPack metadata exposes target and objectTypeMask after the inherited AbilityActionData prefix. The payload is consumed completely, but target is emitted with partial TargetSettings diagnostics because selector/suffix semantics are still unresolved.";
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "CheckTargetsEqual/Data", StringComparison.Ordinal))
            {
                if (length < 200 || length > 280 || (length % 4) != 0)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["firstTargetSettings"] = ReadDiagnosticTargetSettings(reader, "checkTargetsEqual.firstTargetSettings", offset, recoveredByRid);
                data["secondTargetSettings"] = ReadDiagnosticTargetSettings(reader, "checkTargetsEqual.secondTargetSettings", offset, recoveredByRid);
                data["observedPayloadStatus"] = "all serialized CheckTargetsEqual/Data bytes consumed by this reader; nested TargetSettings entries carry their own partial markers";
                data["layoutNote"] = "Installed IL2CPP/MemoryPack metadata exposes firstTargetSettings and secondTargetSettings after the inherited AbilityActionData prefix. The payload is consumed completely, but both TargetSettings objects are emitted with partial diagnostics because selector/suffix semantics are still unresolved.";
                reader.EnsureComplete();
                return true;
            }
            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "CheckBuffStackNum/Data", StringComparison.Ordinal))
            {
                if (length < 144 || length > 240 || (length % 4) != 0)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["checkTarget"] = ReadDiagnosticTargetSettings(reader, "checkBuffStackNum.checkTarget", offset, recoveredByRid);
                data["buffId"] = ReadPayloadAlignedAsciiStringWithZeroPadding(reader, "checkBuffStackNum.buffId", 128);
                data["compareType"] = ReadPayloadNamedEnum32(reader, "checkBuffStackNum.compareType", new[] { "LT", "LE", "GT", "GE", "Equals" });
                data["value"] = ReadPayloadBlackboardDoubleWithZeroPadding(reader, "checkBuffStackNum.value", 128);
                data["observedPayloadStatus"] = "all serialized CheckBuffStackNum/Data bytes consumed by this reader; nested TargetSettings carries its own partial marker";
                data["layoutNote"] = "Installed IL2CPP/MemoryPack metadata exposes checkTarget, buffId, compareType, and BlackboardDouble value after the inherited AbilityActionData prefix. The payload is consumed completely, but checkTarget is emitted with partial TargetSettings diagnostics because selector/suffix semantics are still unresolved.";
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "CheckBuffStackNumByTag/Data", StringComparison.Ordinal))
            {
                if (length < 160 || length > 512 || (length % 4) != 0)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["checkTarget"] = ReadDiagnosticTargetSettings(reader, "checkBuffStackNumByTag.checkTarget", offset, recoveredByRid);
                data["tagQuery"] = ReadPayloadGameplayTagQueryWithZeroPadding(reader, "checkBuffStackNumByTag.tagQuery", 16, 256);
                data["buffStackNumType"] = ReadPayloadEnum32(reader, "checkBuffStackNumByTag.buffStackNumType", 0, 16);
                data["compareType"] = ReadPayloadNamedEnum32(reader, "checkBuffStackNumByTag.compareType", new[] { "LT", "LE", "GT", "GE", "Equals" });
                data["value"] = ReadPayloadBlackboardDoubleWithZeroPadding(reader, "checkBuffStackNumByTag.value", 128);
                data["observedPayloadStatus"] = "all serialized CheckBuffStackNumByTag/Data bytes consumed by this reader; nested TargetSettings carries its own partial marker";
                data["layoutNote"] = "Installed IL2CPP/MemoryPack metadata exposes checkTarget, tagQuery, buffStackNumType, compareType, and BlackboardDouble value after the inherited AbilityActionData prefix. The payload is consumed completely, but checkTarget is emitted with partial TargetSettings diagnostics because selector/suffix semantics are still unresolved.";
                reader.EnsureComplete();
                return true;
            }
            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "CheckBuffStackNumAdvanced/Data", StringComparison.Ordinal))
            {
                if (length < 180 || length > 512 || (length % 4) != 0)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                data["$partial"] = true;
                data["observedPayloadStatus"] = "all serialized CheckBuffStackNumAdvanced/Data bytes consumed by this reader; parent remains partial because buffSettings variants are not fully proven";
                data["partialReasons"] = new List<string>
                {
                    "Nested TargetSettings carries its own partial marker for selector/post-selector semantics.",
                    "BuffFindSettings generic list type names remain unresolved locally.",
                    "Only Id and Tag checkType variants are observed; Environment and Context variants are not byte-proven.",
                };
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["checkTarget"] = ReadDiagnosticTargetSettings(reader, "checkBuffStackNumAdvanced.checkTarget", offset, recoveredByRid);
                data["buffSettings"] = ReadDiagnosticBuffFindSettingsCandidate(reader, "checkBuffStackNumAdvanced.buffSettings");
                data["buffStackNumType"] = ReadPayloadEnum32(reader, "checkBuffStackNumAdvanced.buffStackNumType", 0, 16);
                data["compareType"] = ReadPayloadNamedEnum32(reader, "checkBuffStackNumAdvanced.compareType", new[] { "LT", "LE", "GT", "GE", "Equals" });
                data["value"] = ReadPayloadBlackboardDoubleWithZeroPadding(reader, "checkBuffStackNumAdvanced.value", 128);
                data["limitSkillCastId"] = reader.ReadBool32("checkBuffStackNumAdvanced.limitSkillCastId");
                data["layoutNote"] = "Installed IL2CPP metadata exposes checkTarget, buffSettings, buffStackNumType, compareType, BlackboardDouble value, and limitSkillCastId after the inherited AbilityActionData prefix. The payload is consumed completely, but checkTarget and buffSettings remain partial diagnostics because TargetSettings selector suffixes, BuffFindSettings generic type names, and unobserved Environment/Context variants are not fully proven.";
                reader.EnsureComplete();
                return true;
            }
            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "CheckHp/Data", StringComparison.Ordinal))
            {
                if (length < 136 || length > 192 || (length % 4) != 0)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["hpOwner"] = ReadDiagnosticTargetSettings(reader, "checkHp.hpOwner", offset, recoveredByRid);
                data["compare"] = ReadPayloadNamedEnum32(reader, "checkHp.compare", new[] { "LT", "LE", "GT", "GE", "Equals" });
                data["isRatio"] = reader.ReadBool32("checkHp.isRatio");
                data["value"] = ReadPayloadBlackboardDoubleWithZeroPadding(reader, "checkHp.value", 128);
                data["observedPayloadStatus"] = "all serialized CheckHp/Data bytes consumed by this reader; nested TargetSettings carries its own partial marker";
                data["layoutNote"] = "Installed IL2CPP/MemoryPack metadata exposes hpOwner, compare, isRatio, and BlackboardDouble value after the inherited AbilityActionData prefix. The payload is consumed completely, but hpOwner is emitted with partial TargetSettings diagnostics because selector/suffix semantics are still unresolved.";
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "CheckTagMatch/Data", StringComparison.Ordinal))
            {
                if (length < 120 || length > 512 || (length % 4) != 0)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["checkTarget"] = ReadDiagnosticTargetSettings(reader, "checkTagMatch.checkTarget", offset, recoveredByRid);
                data["query"] = ReadPayloadGameplayTagQueryWithZeroPadding(reader, "checkTagMatch.query", 16, 256);
                data["observedPayloadStatus"] = "all serialized CheckTagMatch/Data bytes consumed by this reader; nested TargetSettings carries its own partial marker";
                data["layoutNote"] = "Installed IL2CPP/MemoryPack metadata exposes checkTarget and GameplayTagQuery after the inherited AbilityActionData prefix. The payload is consumed completely, but checkTarget is emitted with partial TargetSettings diagnostics because selector/suffix semantics are still unresolved.";
                reader.EnsureComplete();
                return true;
            }
            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core.Conditions", StringComparison.Ordinal)
                && (string.Equals(header.ClassName, "CheckSpellInflictionType/Data", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "CheckPhysicalInflictionType/Data", StringComparison.Ordinal)))
            {
                if (length < 24 || length > 64 || (length % 4) != 0)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["mask"] = ReadPayloadMask32(reader, "inflictionType.mask", 0x0f);
                data["savedKey"] = ReadPayloadAlignedAsciiStringWithZeroPadding(reader, "inflictionType.savedKey", 128);
                data["layoutNote"] = "Installed IL2CPP metadata exposes mask and savedKey after the inherited AbilityActionData prefix; audited payloads use a bounded int32 infliction mask and an aligned saved-key string.";
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "CreateBuffAction/Data", StringComparison.Ordinal))
            {
                if (length < 200 || length > 512 || (length % 4) != 0)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                data["$partial"] = true;
                data["observedPayloadStatus"] = "all serialized CreateBuffAction/Data bytes consumed by this reader; parent remains partial because buffs and post-context default tail variants are not fully proven";
                data["partialReasons"] = new List<string>
                {
                    "Nested TargetSettings carries its own partial marker for selector/post-selector semantics.",
                    "The buffs list is observed as a count-prefixed buff-id list with reserved zeros, but the exact generic field type remains unresolved locally.",
                    "The post-context field order is metadata-known, but inheritSkillIdList and BuffIconDurationSourceSetting byte boundaries are not proven by non-default samples.",
                };
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["buffs"] = ReadDiagnosticCreateBuffActionBuffs(reader, "createBuffAction.buffs");
                data["count"] = ReadPayloadBlackboardDoubleWithZeroPadding(reader, "createBuffAction.count", 128);
                data["targetSettings"] = ReadDiagnosticTargetSettings(reader, "createBuffAction.targetSettings", offset, recoveredByRid);
                data["buffSource"] = BuildPayloadHash32(reader.ReadInt32("createBuffAction.buffSource"));
                data["contextKey"] = ReadPayloadAlignedAsciiStringWithZeroPadding(reader, "createBuffAction.contextKey", 128);
                data["postContextTail"] = ReadDiagnosticCreateBuffActionPostContextTail(reader, "createBuffAction.postContextTail");
                data["layoutNote"] = "Installed IL2CPP metadata exposes buffs, count, targetSettings, buffSource, contextKey, and the post-context field order after the inherited AbilityActionData prefix. The payload is consumed completely, but targetSettings and the post-context tail remain partial diagnostics because selector suffixes, inheritSkillIdList, and BuffIconDurationSourceSetting byte boundaries are not fully proven.";
                reader.EnsureComplete();
                return true;
            }
            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "ModifyDynamicBlackboard/Data", StringComparison.Ordinal))
            {
                if (length < 120 || length > 256 || (length % 4) != 0)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["key"] = ReadPayloadAlignedAsciiStringWithZeroPadding(reader, "modifyDynamicBlackboard.key", 128);
                data["operation"] = ReadPayloadNamedEnum32(reader, "modifyDynamicBlackboard.operation", new[] { "Assign", "Add", "Multiply", "Divide" });
                data["directValue"] = reader.ReadBool32("modifyDynamicBlackboard.directValue");
                data["value"] = ReadPayloadBlackboardDoubleWithZeroPadding(reader, "modifyDynamicBlackboard.value", 128);
                data["calculationTarget"] = ReadDiagnosticTargetSettings(reader, "modifyDynamicBlackboard.calculationTarget", offset, recoveredByRid);
                data["calculateType"] = ReadPayloadNamedEnum32(reader, "modifyDynamicBlackboard.calculateType", new[] { "HpRatio" });
                data["observedPayloadStatus"] = "all serialized ModifyDynamicBlackboard/Data bytes consumed by this reader; nested TargetSettings carries its own partial marker";
                data["layoutNote"] = "Installed IL2CPP/MemoryPack metadata exposes key, operation, directValue, BlackboardDouble value, calculationTarget, and calculateType after the inherited AbilityActionData prefix. The payload is consumed completely, but calculationTarget is emitted with partial TargetSettings diagnostics because selector/suffix semantics are still unresolved.";
                reader.EnsureComplete();
                return true;
            }
            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "StoreBuffCount/Data", StringComparison.Ordinal))
            {
                if (length < 144 || length > 320 || (length % 4) != 0)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["useCurrentBuff"] = reader.ReadBool32("storeBuffCount.useCurrentBuff");
                data["buffOwners"] = ReadDiagnosticTargetSettings(reader, "storeBuffCount.buffOwners", offset, recoveredByRid);
                data["buffId"] = ReadPayloadAlignedAsciiStringWithZeroPadding(reader, "storeBuffCount.buffId", 128);
                data["blackboardKey"] = ReadPayloadAlignedAsciiStringWithZeroPadding(reader, "storeBuffCount.blackboardKey", 128);
                data["observedPayloadStatus"] = "all serialized StoreBuffCount/Data bytes consumed by this reader; nested TargetSettings carries its own partial marker";
                data["layoutNote"] = "Installed IL2CPP/MemoryPack metadata exposes useCurrentBuff, buffOwners, buffId, and blackboardKey after the inherited AbilityActionData prefix. The payload is consumed completely, but buffOwners is emitted with partial TargetSettings diagnostics because selector/suffix semantics are still unresolved.";
                reader.EnsureComplete();
                return true;
            }
            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "CompareFloat/Data", StringComparison.Ordinal))
            {
                if (length != 68)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["valueA"] = ReadPayloadBlackboardDoubleWithZeroPadding(reader, "compareFloat.valueA", 128);
                data["compare"] = ReadPayloadEnum32(reader, "compareFloat.compare", 0, 4);
                data["valueB"] = ReadPayloadBlackboardDoubleWithZeroPadding(reader, "compareFloat.valueB", 128);
                data["layoutNote"] = "Installed IL2CPP metadata exposes valueA, compare, and valueB; BlackboardDouble serializes bool32, float32, and an aligned key string; observed non-key values carry an empty key string.";
                reader.EnsureComplete();
                return true;
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "IfElseAction/IfElseActionData", StringComparison.Ordinal))
            {
                if (length != 80)
                {
                    return false;
                }

                data = CreateCoreManagedReferenceData(header, offset, length);
                ReadPayloadAbilityActionDataPrefix(data, reader, "abilityActionData");
                data["conditionAction"] = ReadPayloadSequenceActionData(reader, "ifElseAction.conditionAction", recoveredByRid);
                data["succeedActions"] = ReadPayloadSequenceActionData(reader, "ifElseAction.succeedActions", recoveredByRid);
                data["failActions"] = ReadPayloadSequenceActionData(reader, "ifElseAction.failActions", recoveredByRid);
                data["alwaysNext"] = reader.ReadBool32("ifElseAction.alwaysNext");
                data["layoutNote"] = "Installed IL2CPP metadata exposes conditionAction, succeedActions, failActions, and alwaysNext after the inherited AbilityActionData prefix; each SequenceActionData contains an action-data marker, RID link, and two source-filter bools.";
                reader.EnsureComplete();
                return true;
            }

            return false;
        }

        private static OrderedDictionary CreateCoreManagedReferenceData(
            ManagedReferenceHeader header,
            int offset,
            int length
        )
        {
            return new OrderedDictionary
            {
                { "$decoded", true },
                { "$inferred", true },
                { "layout", $"{header.Namespace}.{header.ClassName}" },
                { "offset", offset },
                { "length", length },
            };
        }

        private static void ReadPayloadAbilityActionDataPrefix(
            OrderedDictionary data,
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            data["isEnable"] = reader.ReadBool32($"{fieldName}.isEnable");
            data["priorityLevel"] = reader.ReadInt32($"{fieldName}.priorityLevel");
            data["priorityOffset"] = reader.ReadInt32($"{fieldName}.priorityOffset");
            data["serverActionIndex"] = reader.ReadInt32($"{fieldName}.serverActionIndex");
        }

        private static OrderedDictionary ReadPayloadBlackboardDoubleWithZeroPadding(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxKeyLength
        )
        {
            return new OrderedDictionary
            {
                { "useBlackboardKey", reader.ReadBool32($"{fieldName}.useBlackboardKey") },
                { "value", reader.ReadFloat($"{fieldName}.value") },
                { "blackboardKey", ReadPayloadAlignedAsciiStringWithZeroPadding(reader, $"{fieldName}.blackboardKey", maxKeyLength) },
            };
        }

        private static OrderedDictionary ReadPayloadSequenceActionData(
            ManagedReferencePayloadReader reader,
            string fieldName,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid
        )
        {
            var actionDataPresent = reader.ReadBool32($"{fieldName}.actionData.present");
            return new OrderedDictionary
            {
                { "actionDataPresent", actionDataPresent },
                { "actionData", ReadPayloadRidLink(reader, $"{fieldName}.actionData", recoveredByRid) },
                { "onlyExecuteWhenSourceIsMainChar", reader.ReadBool32($"{fieldName}.onlyExecuteWhenSourceIsMainChar") },
                { "onlyExecuteWhenSourceIsGuard", reader.ReadBool32($"{fieldName}.onlyExecuteWhenSourceIsGuard") },
            };
        }

        private static OrderedDictionary ReadPayloadGameplayTagQueryWithZeroPadding(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxTagCount,
            int maxPathLength
        )
        {
            return new OrderedDictionary
            {
                { "queryType", ReadPayloadNamedEnum32(reader, $"{fieldName}.queryType", new[] { "HasAny", "HasAll", "ExceptAny", "ExceptAll" }) },
                { "tags", ReadPayloadGameplayTagListWithZeroPadding(reader, $"{fieldName}.tags", maxTagCount, maxPathLength) },
            };
        }

        private static List<OrderedDictionary> ReadPayloadGameplayTagListWithZeroPadding(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxCount,
            int maxPathLength
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
                items.Add(ReadPayloadGameplayTagWithZeroPadding(reader, $"{fieldName}[{i}]", maxPathLength));
            }
            return items;
        }

        private static List<string> ReadPayloadStringListWithZeroPadding(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxCount,
            int maxLength
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
                items.Add(ReadPayloadAlignedAsciiStringWithZeroPadding(reader, $"{fieldName}[{i}]", maxLength));
            }
            return items;
        }

        private static OrderedDictionary ReadPayloadMask32(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxMask
        )
        {
            var value = reader.ReadInt32(fieldName);
            if (value < 0 || value > maxMask)
            {
                throw new InvalidDataException($"invalid mask32 {value} in {fieldName}");
            }
            return BuildPayloadHash32(value);
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

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterFollowGraph/CharacterFollowGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterFollowGraph/CharacterFollowGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseCheckInterval", reader.ReadFloat("baseCheckInterval") },
                        { "randomCheckInterval", reader.ReadFloat("randomCheckInterval") },
                        { "characterSR", ReadCharacterStimulusResponse(reader, "characterSR", 64, recoveredByRid) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterBattleGraph/CharacterBattleGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterBattleGraph/CharacterBattleGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "characterSR", ReadCharacterStimulusResponse(reader, "characterSR", 64, recoveredByRid) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyPatrolGraph/EnemyPatrolGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyPatrolGraph/EnemyPatrolGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "singlePatrol", ReadPayloadGameplayTag(reader, "singlePatrol") },
                        { "groupPatrol", ReadPayloadGameplayTag(reader, "groupPatrol") },
                        { "enemySR", ReadNpcStimulusResponseList(reader, "enemySR", 16, recoveredByRid) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyBornBehavior/EnemyBornBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyBornBehavior/EnemyBornBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "bornBehaviorData", ReadEnemyBornBehaviorData(reader, "bornBehaviorData") },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyBattleGraph/EnemyBattleGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyBattleGraph/EnemyBattleGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "canvasGraph", ReadPayloadPPtr(reader, "canvasGraph") },
                        { "entityMode", BuildPayloadHash32(reader.ReadInt32("entityMode")) },
                        { "soundName", reader.ReadAlignedAsciiString("soundName") },
                        { "alertRange", reader.ReadFloat("alertRange") },
                        { "setWaitTime", reader.ReadBool32("setWaitTime") },
                        { "waitTime", reader.ReadFloat("waitTime") },
                        { "useCommonBehavior", reader.ReadBool32("useCommonBehavior") },
                        { "enterConfrontDis", reader.ReadFloat("enterConfrontDis") },
                        { "enemySR", ReadNpcStimulusResponseList(reader, "enemySR", 64, recoveredByRid) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyDefendBattleGraph/EnemyDefendBattleGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyDefendBattleGraph/EnemyDefendBattleGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "canvasGraph", ReadPayloadPPtr(reader, "canvasGraph") },
                        { "useCommonBehavior", reader.ReadBool32("useCommonBehavior") },
                        { "enterConfrontDis", reader.ReadFloat("enterConfrontDis") },
                        { "searchRadius", reader.ReadFloat("searchRadius") },
                        { "searchHeight", reader.ReadFloat("searchHeight") },
                        { "searchMode", BuildPayloadHash32(reader.ReadInt32("searchMode")) },
                        { "onHitTimeout", reader.ReadFloat("onHitTimeout") },
                        { "enemySR", ReadNpcStimulusResponseList(reader, "enemySR", 64, recoveredByRid) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemySettlementBattleGraph/EnemySettlementBattleGraphData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemySettlementBattleGraph/EnemySettlementBattleGraphData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "canvasGraph", ReadPayloadPPtr(reader, "canvasGraph") },
                        { "battleTag", ReadPayloadGameplayTag(reader, "battleTag") },
                        { "patrolTag", ReadPayloadGameplayTag(reader, "patrolTag") },
                        { "searchCoreRadius", reader.ReadFloat("searchCoreRadius") },
                        { "searchCoreHeight", reader.ReadFloat("searchCoreHeight") },
                        { "searchMode", BuildPayloadHash32(reader.ReadInt32("searchMode")) },
                        { "onHitTimeout", reader.ReadFloat("onHitTimeout") },
                        { "sightRadius", reader.ReadFloat("sightRadius") },
                        { "sightAngle", reader.ReadFloat("sightAngle") },
                        { "leaveDis", reader.ReadFloat("leaveDis") },
                        { "exAction", BuildPayloadHash32(reader.ReadInt32("exAction")) },
                        { "enemySR", ReadNpcStimulusResponseList(reader, "enemySR", 64, recoveredByRid) },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "NPCCommonAnimalRandomPlayMontageBehavior/NPCCommonAnimalRandomPlayMontageBehaviorData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.NPCCommonAnimalRandomPlayMontageBehavior/NPCCommonAnimalRandomPlayMontageBehaviorData" },
                        { "offset", offset },
                        { "length", length },
                        { "baseInterval", reader.ReadFloat("baseInterval") },
                        { "montageInfos", ReadPlayTimedMontageInfoList(reader, "montageInfos", 16) },
                        { "playInterval", ReadPayloadVector2(reader, "playInterval") },
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
                || length < 0
                || offset > rawData.Length
                || offset + length > rawData.Length)
            {
                return false;
            }

            try
            {
                if (length == 0 && IsKnownEmptyViewManagedReferenceData(header))
                {
                    data = BuildEmptyManagedReferenceData(header, offset, length);
                    return true;
                }

                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                if (string.Equals(header.Namespace, "Beyond.Gameplay.View", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "WeaponComponentData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.View.WeaponComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "weaponCfg", ReadPayloadRidLinkList(reader, "weaponCfg", 16, recoveredByRid) },
                        { "layoutNote", "Installed IL2CPP metadata exposes WeaponComponentData.weaponCfg; observed payloads serialize it as a managed-reference RID list." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.View", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterAnimationComponentData", StringComparison.Ordinal))
                {
                    if (length < 40 || (length % 4) != 0)
                    {
                        return false;
                    }
                    var animationConfigPath = ReadPayloadAlignedUtf8StringWithZeroPadding(
                        reader,
                        "characterAnimationComponentData.animationConfigPath",
                        256);
                    if (!animationConfigPath.StartsWith("Data/Json/AnimationConfig/", StringComparison.Ordinal)
                        || !animationConfigPath.EndsWith(".json", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"unexpected animation config path '{animationConfigPath}'");
                    }
                    var minPivotAngle = ReadPayloadFloatRange(reader, "characterAnimationComponentData._minPivotAngle", -360f, 360f);
                    var relaxTriggerTime = ReadPayloadFloatRange(reader, "characterAnimationComponentData._relaxTriggerTime", 0f, 3600f);
                    var idleTriggerTime = ReadPayloadFloatRange(reader, "characterAnimationComponentData._idleTriggerTime", 0f, 3600f);
                    var idleAnimCount = reader.ReadInt32("characterAnimationComponentData._idleAnimCount");
                    if (idleAnimCount < 0 || idleAnimCount > 32)
                    {
                        throw new InvalidDataException($"invalid idle animation count {idleAnimCount}");
                    }
                    var fightIdleTimeout = ReadPayloadFloatRange(reader, "characterAnimationComponentData._fightIdleTimeout", 0f, 3600f);
                    var memberFightIdleTimeout = ReadPayloadFloatRange(reader, "characterAnimationComponentData._memberFightIdleTimeout", 0f, 3600f);
                    var footStepCfgId = ReadPayloadAlignedUtf8StringWithZeroPadding(reader, "characterAnimationComponentData._footStepCfgId", 128);

                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.View.CharacterAnimationComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "animationConfigPath", animationConfigPath },
                        { "_minPivotAngle", minPivotAngle },
                        { "_relaxTriggerTime", relaxTriggerTime },
                        { "_idleTriggerTime", idleTriggerTime },
                        { "_idleAnimCount", idleAnimCount },
                        { "_fightIdleTimeout", fightIdleTimeout },
                        { "_memberFightIdleTimeout", memberFightIdleTimeout },
                        { "_footStepCfgId", footStepCfgId },
                        { "layoutNote", "Installed IL2CPP metadata exposes the timing/count/footstep fields; the leading animation config path is byte-proven in current character payloads and guarded by prefix/suffix." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

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

        private static bool TryDecodeGeneralGameplayManagedReferenceData(
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

            if (TryDecodeSmallGameplayComponentManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                out data))
            {
                return true;
            }

            if (TryDecodeWeaponDataManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                out data))
            {
                return true;
            }

            if (TryDecodeStaticWeaponDataManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                out data))
            {
                return true;
            }

            if (TryDecodeWeaponDataWrapperManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                recoveredByRid,
                out data))
            {
                return true;
            }

            if (TryDecodeSoundGameplayManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                out data))
            {
                return true;
            }

            if (TryDecodeCharacterTemplateManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                recoveredByRid,
                out data))
            {
                return true;
            }

            if (TryDecodeProjectileTemplateManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                recoveredByRid,
                out data))
            {
                return true;
            }

            if (TryDecodeWikiModelSpawnManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                out data))
            {
                return true;
            }

            if (TryDecodeWikiWeaponManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                out data))
            {
                return true;
            }

            if (TryDecodeWeaponDecoEffectManagedReferenceData(
                header,
                rawData,
                offset,
                length,
                out data))
            {
                return true;
            }

            if (length == 0 && IsKnownEmptyGeneralGameplayManagedReferenceData(header))
            {
                data = BuildEmptyManagedReferenceData(header, offset, length);
                return true;
            }

            return false;
        }

        private static bool TryDecodeSmallGameplayComponentManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            try
            {
                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                if (string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CGData", StringComparison.Ordinal))
                {
                    var nameLengthOffset = reader.Position;
                    var nameLength = reader.ReadInt32("cgData.name.length");
                    if (nameLength < 0 || nameLength > 1024)
                    {
                        throw new InvalidDataException($"invalid CGData name length {nameLength}");
                    }
                    reader.SetPosition(nameLengthOffset);
                    var name = reader.ReadAlignedUtf8String("cgData.name");
                    var namePayloadEnd = nameLengthOffset + 4 + nameLength;
                    var alignedNamePayloadEnd = (namePayloadEnd + 3) & ~3;
                    for (var padOffset = namePayloadEnd; padOffset < alignedNamePayloadEnd; padOffset++)
                    {
                        if (reader.RawData[padOffset] != 0)
                        {
                            throw new InvalidDataException($"non-zero CGData name padding byte at {padOffset}");
                        }
                    }

                    if (reader.Remaining != 8)
                    {
                        throw new InvalidDataException("CGData payload must end with skipType and noSafeZone");
                    }
                    var skipType = reader.ReadInt32("cgData.skipType");
                    if (skipType < 0 || skipType > 2)
                    {
                        throw new InvalidDataException($"invalid CGData skipType {skipType}");
                    }

                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.CGData" },
                        { "offset", offset },
                        { "length", length },
                        { "name", name },
                        { "skipType", BuildPayloadHash32(skipType) },
                        { "noSafeZone", reader.ReadBool32("cgData.noSafeZone") },
                        { "layoutNote", "Installed IL2CPP metadata exposes CGData.name, skipType, and noSafeZone; decoder verifies UTF-8 string padding and observed enum bounds." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "ForceSet", StringComparison.Ordinal))
                {
                    if (length != 4)
                    {
                        return false;
                    }
                    var count = reader.ReadInt32("forceSet.count");
                    if (count < 0 || count > 64)
                    {
                        throw new InvalidDataException($"invalid ForceSet count {count}");
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.ForceSet" },
                        { "offset", offset },
                        { "length", length },
                        { "count", count },
                        { "layoutNote", "Installed IL2CPP metadata exposes ForceSet.count; observed payloads serialize exactly one int32." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "RandomAdd", StringComparison.Ordinal))
                {
                    if (length != 8)
                    {
                        return false;
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.RandomAdd" },
                        { "offset", offset },
                        { "length", length },
                        { "range", ReadPayloadVector2(reader, "randomAdd.range") },
                        { "layoutNote", "Installed IL2CPP metadata exposes RandomAdd.range as a Vector2." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "TargetHasTags", StringComparison.Ordinal))
                {
                    if (length != 16)
                    {
                        return false;
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.TargetHasTags" },
                        { "offset", offset },
                        { "length", length },
                        { "revertResult", reader.ReadBool32("targetHasTags.revertResult") },
                        { "tagEntityType", ReadPayloadNamedEnum32(reader, "targetHasTags.tagEntityType", new[] { "Self", "Target" }) },
                        { "tag", BuildPayloadHash32(reader.ReadInt32("targetHasTags.tag")) },
                        { "priority", reader.ReadFloat("targetHasTags.priority") },
                        { "layoutNote", "Installed IL2CPP metadata exposes TargetHasTags tag fields plus priority; IdentityFilter contributes revertResult." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "HasAttackRangeType", StringComparison.Ordinal))
                {
                    if (length != 12)
                    {
                        return false;
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.HasAttackRangeType" },
                        { "offset", offset },
                        { "length", length },
                        { "revertResult", reader.ReadBool32("hasAttackRangeType.revertResult") },
                        { "attackRangeType", ReadPayloadNamedEnum32(reader, "hasAttackRangeType.attackRangeType", new[] { "Melee", "Ranged" }) },
                        { "priority", reader.ReadFloat("hasAttackRangeType.priority") },
                        { "layoutNote", "Installed IL2CPP metadata exposes attackRangeType plus priority; IdentityFilter contributes revertResult." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "HasFinishToken", StringComparison.Ordinal))
                {
                    if (length != 8)
                    {
                        return false;
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.HasFinishToken" },
                        { "offset", offset },
                        { "length", length },
                        { "revertResult", reader.ReadBool32("hasFinishToken.revertResult") },
                        { "priority", reader.ReadFloat("hasFinishToken.priority") },
                        { "layoutNote", "Installed IL2CPP metadata exposes priority; IdentityFilter contributes revertResult." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "PhaseForbidParams", StringComparison.Ordinal))
                {
                    if (length < 8)
                    {
                        return false;
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.PhaseForbidParams" },
                        { "offset", offset },
                        { "length", length },
                        { "phaseForbidStyle", ReadPayloadNamedEnum32(reader, "phaseForbidParams.phaseForbidStyle", new[] { "None", "HideEntrance", "ShowToast" }) },
                        { "toastTextId", reader.ReadAlignedAsciiString("phaseForbidParams.toastTextId") },
                        { "layoutNote", "Installed IL2CPP metadata exposes PhaseForbidParams.phaseForbidStyle and toastTextId; observed current payloads use empty toast strings." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                    && (string.Equals(header.ClassName, "GeneralAbilityForbidParams", StringComparison.Ordinal)
                        || string.Equals(header.ClassName, "GeneralAbilityForbidUseParams", StringComparison.Ordinal)))
                {
                    if (length < 8)
                    {
                        return false;
                    }
                    data = ReadGeneralAbilityForbidParamsPayload(
                        reader,
                        string.Equals(header.ClassName, "GeneralAbilityForbidUseParams", StringComparison.Ordinal)
                            ? "Beyond.Gameplay.GeneralAbilityForbidUseParams"
                            : "Beyond.Gameplay.GeneralAbilityForbidParams",
                        offset,
                        length,
                        "generalAbilityForbidParams");
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "ForbidParamsWithRadioReason", StringComparison.Ordinal))
                {
                    if (length < 4)
                    {
                        return false;
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.ForbidParamsWithRadioReason" },
                        { "offset", offset },
                        { "length", length },
                        { "radioId", reader.ReadAlignedUtf8String("forbidParamsWithRadioReason.radioId") },
                        { "layoutNote", "Installed IL2CPP metadata exposes ForbidParamsWithRadioReason.radioId as an aligned string." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "LongTimeNoIdentity", StringComparison.Ordinal))
                {
                    if (length != 8)
                    {
                        return false;
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.LongTimeNoIdentity" },
                        { "offset", offset },
                        { "length", length },
                        { "outTime", ReadPayloadFloatRange(reader, "longTimeNoIdentity.outTime", 0f, 3600f) },
                        { "multiplier", ReadPayloadFloatRange(reader, "longTimeNoIdentity.multiplier", -1000f, 1000f) },
                        { "layoutNote", "Installed IL2CPP metadata exposes LongTimeNoIdentity.outTime and multiplier as floats." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "ResilienceEmpty", StringComparison.Ordinal))
                {
                    if (length != 8)
                    {
                        return false;
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.ResilienceEmpty" },
                        { "offset", offset },
                        { "length", length },
                        { "revertResult", reader.ReadBool32("resilienceEmpty.revertResult") },
                        { "priority", ReadPayloadFloatRange(reader, "resilienceEmpty.priority", -1000f, 1000f) },
                        { "layoutNote", "Installed IL2CPP metadata exposes ResilienceEmpty.priority; IdentityFilter contributes revertResult." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "TargetDistance", StringComparison.Ordinal))
                {
                    if (length != 8)
                    {
                        return false;
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.TargetDistance" },
                        { "offset", offset },
                        { "length", length },
                        { "disType", ReadPayloadNamedEnum32(reader, "targetDistance.disType", new[] { "All", "MainChar" }) },
                        { "factor", ReadPayloadFloatRange(reader, "targetDistance.factor", -1000f, 1000f) },
                        { "layoutNote", "Installed IL2CPP metadata exposes TargetDistance.disType and factor." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemyRankType", StringComparison.Ordinal))
                {
                    if (length != 12)
                    {
                        return false;
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemyRankType" },
                        { "offset", offset },
                        { "length", length },
                        { "revertResult", reader.ReadBool32("enemyRankType.revertResult") },
                        { "enemyRank", BuildPayloadHash32(reader.ReadInt32("enemyRankType.enemyRank")) },
                        { "priority", ReadPayloadFloatRange(reader, "enemyRankType.priority", -1000f, 1000f) },
                        { "layoutNote", "Installed IL2CPP metadata exposes enemyRank and priority; IdentityFilter contributes revertResult." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "EnemySubRankType", StringComparison.Ordinal))
                {
                    if (length != 12)
                    {
                        return false;
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.EnemySubRankType" },
                        { "offset", offset },
                        { "length", length },
                        { "revertResult", reader.ReadBool32("enemySubRankType.revertResult") },
                        { "enemySubRank", BuildPayloadHash32(reader.ReadInt32("enemySubRankType.enemySubRank")) },
                        { "priority", ReadPayloadFloatRange(reader, "enemySubRankType.priority", -1000f, 1000f) },
                        { "layoutNote", "Installed IL2CPP metadata exposes enemySubRank and priority; IdentityFilter contributes revertResult." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "TargetInsideMaxSlotRange", StringComparison.Ordinal))
                {
                    if (length != 12)
                    {
                        return false;
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.TargetInsideMaxSlotRange" },
                        { "offset", offset },
                        { "length", length },
                        { "revertResult", reader.ReadBool32("targetInsideMaxSlotRange.revertResult") },
                        { "offsetValue", ReadPayloadFloatRange(reader, "targetInsideMaxSlotRange.offset", -1000f, 1000f) },
                        { "priority", ReadPayloadFloatRange(reader, "targetInsideMaxSlotRange.priority", -1000f, 1000f) },
                        { "layoutNote", "Installed IL2CPP metadata exposes offset and priority; IdentityFilter contributes revertResult." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.AI", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterAIComponentData", StringComparison.Ordinal))
                {
                    if (length != 12)
                    {
                        return false;
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.AI.CharacterAIComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "aiCfg", ReadPayloadPPtr(reader, "characterAIComponentData.aiCfg") },
                        { "layoutNote", "Installed IL2CPP metadata exposes CharacterAIComponentData.aiCfg; observed payloads serialize it as a PPtr." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharHurtAnimComponentData", StringComparison.Ordinal))
                {
                    if (length != 8)
                    {
                        return false;
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.CharHurtAnimComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "fullbodyHeavyHurtTime", reader.ReadFloat("charHurtAnimComponentData.fullbodyHeavyHurtTime") },
                        { "fullbodyWhackHurtTime", reader.ReadFloat("charHurtAnimComponentData.fullbodyWhackHurtTime") },
                        { "layoutNote", "Installed IL2CPP metadata exposes two serialized hurt-animation timing floats." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "ObservedComponentData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.ObservedComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "checkTagList", ReadPayloadGameplayTagList(reader, "observedComponentData.checkTagList", 16) },
                        { "shapeType", BuildPayloadHash32(reader.ReadInt32("observedComponentData.shapeType")) },
                        { "center", ReadPayloadVector3(reader, "observedComponentData.center") },
                        { "size", ReadPayloadVector3(reader, "observedComponentData.size") },
                        { "radius", reader.ReadFloat("observedComponentData.radius") },
                        { "layoutNote", "Installed IL2CPP metadata exposes checkTagList, shapeType, center, size, and radius." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.Namespace, "Beyond.Gameplay.View", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "SkeletalMorphComponentData", StringComparison.Ordinal))
                {
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.View.SkeletalMorphComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "avatarTag", ReadPayloadGameplayTag(reader, "skeletalMorphComponentData.avatarTag") },
                        { "layoutNote", "Installed IL2CPP metadata exposes SkeletalMorphComponentData._avatarTag; observed payloads serialize it as a gameplay-tag path plus hash." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.View.Animation", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "CharacterPivotComponentData", StringComparison.Ordinal))
                {
                    if (length < 120 || ((length - 120) % 28) != 0)
                    {
                        return false;
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.View.Animation.CharacterPivotComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "isOverride", reader.ReadBool32("characterPivotComponentData.isOverride") },
                        { "minPivotAngle", ReadPayloadFloatRange(reader, "characterPivotComponentData.minPivotAngle", -360f, 360f) },
                        { "pivotRotationCurve", ReadPayloadAnimationCurveFloat(reader, "characterPivotComponentData.pivotRotationCurve") },
                        { "pivotPositionXCurve", ReadPayloadAnimationCurveFloat(reader, "characterPivotComponentData.pivotPositionXCurve") },
                        { "pivotPositionZCurve", ReadPayloadAnimationCurveFloat(reader, "characterPivotComponentData.pivotPositionZCurve") },
                        { "minTurnStartAngleWalk", ReadPayloadFloatRange(reader, "characterPivotComponentData.minTurnStartAngleWalk", -360f, 360f) },
                        { "minTurnStartAngleWalkStrict", ReadPayloadFloatRange(reader, "characterPivotComponentData.minTurnStartAngleWalkStrict", -360f, 360f) },
                        { "minTurnStartAngleRun", ReadPayloadFloatRange(reader, "characterPivotComponentData.minTurnStartAngleRun", -360f, 360f) },
                        { "minTurnStartAngleSprint", ReadPayloadFloatRange(reader, "characterPivotComponentData.minTurnStartAngleSprint", -360f, 360f) },
                        { "turnStartRotationCurveWalk", ReadPayloadAnimationCurveFloat(reader, "characterPivotComponentData.turnStartRotationCurveWalk") },
                        { "turnStartRotationCurveRun", ReadPayloadAnimationCurveFloat(reader, "characterPivotComponentData.turnStartRotationCurveRun") },
                        { "turnStartRotationCurveSprint", ReadPayloadAnimationCurveFloat(reader, "characterPivotComponentData.turnStartRotationCurveSprint") },
                        { "layoutNote", "Installed IL2CPP metadata exposes CharacterPivotComponentData fields; curves use Unity AnimationCurve<float> records with guarded keyframe counts." },
                    };
                    reader.EnsureComplete();
                    return true;
                }
                if (string.Equals(header.Namespace, "Beyond.Gameplay.Water", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "WaterSensorComponentData", StringComparison.Ordinal))
                {
                    if (length != 36)
                    {
                        return false;
                    }
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Water.WaterSensorComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "enableWater", reader.ReadBool32("waterSensorComponentData.enableWater") },
                        { "enableRain", reader.ReadBool32("waterSensorComponentData.enableRain") },
                        { "enableGameplayWetness", reader.ReadBool32("waterSensorComponentData.enableGameplayWetness") },
                        { "enableWaterRaycast", reader.ReadBool32("waterSensorComponentData.enableWaterRaycast") },
                        { "enableRainRaycast", reader.ReadBool32("waterSensorComponentData.enableRainRaycast") },
                        { "overrideDryDrenchedAfterSeconds", reader.ReadBool32("waterSensorComponentData.overrideDryDrenchedAfterSeconds") },
                        { "dryDrenchedAfterSeconds", reader.ReadFloat("waterSensorComponentData.dryDrenchedAfterSeconds") },
                        { "overrideDryDrenchedSmoothTime", reader.ReadBool32("waterSensorComponentData.overrideDryDrenchedSmoothTime") },
                        { "dryDrenchedSmoothTime", reader.ReadFloat("waterSensorComponentData.dryDrenchedSmoothTime") },
                        { "layoutNote", "Installed IL2CPP metadata exposes five enable flags, two override flags, and two timing floats." },
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

            data = null;
            return false;
        }

        private static OrderedDictionary ReadGeneralAbilityForbidParamsPayload(
            ManagedReferencePayloadReader reader,
            string layout,
            int offset,
            int length,
            string fieldPrefix
        )
        {
            var forbidStyle = reader.ReadInt32($"{fieldPrefix}.forbidStyle");
            if (forbidStyle < 0 || forbidStyle > 256)
            {
                throw new InvalidDataException($"invalid {fieldPrefix}.forbidStyle {forbidStyle}");
            }

            return new OrderedDictionary
            {
                { "$decoded", true },
                { "$inferred", true },
                { "layout", layout },
                { "offset", offset },
                { "length", length },
                { "forbidStyle", BuildPayloadHash32(forbidStyle) },
                { "toastTextId", reader.ReadAlignedUtf8String($"{fieldPrefix}.toastTextId") },
                { "layoutNote", "Installed IL2CPP metadata exposes GeneralAbilityForbidParams.forbidStyle and toastTextId; derived use-param records serialize the same base payload." },
            };
        }
        private static bool TryDecodeWeaponDataManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (!string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "WeaponData", StringComparison.Ordinal)
                || length < 44)
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
                    { "layout", "Beyond.Gameplay.WeaponData" },
                    { "offset", offset },
                    { "length", length },
                    { "weaponIndex", reader.ReadInt32("weaponData.weaponIndex") },
                    { "vfxKey", reader.ReadAlignedAsciiString("weaponData.vfxKey") },
                };
                if (reader.Remaining != 36)
                {
                    throw new InvalidDataException("WeaponData payload must end with scale, visibility fields, and overrideController");
                }

                data["weaponScale"] = reader.ReadFloat("weaponData.weaponScale");
                data["showWhenIdle"] = reader.ReadBool32("weaponData.showWhenIdle");
                data["idleMountPoint"] = reader.ReadInt32("weaponData.idleMountPoint");
                data["showWhenFight"] = reader.ReadBool32("weaponData.showWhenFight");
                data["fightMountPoint"] = reader.ReadInt32("weaponData.fightMountPoint");
                data["overrideAnimation"] = reader.ReadBool32("weaponData.overrideAnimation");
                data["overrideController"] = ReadPayloadPPtr(reader, "weaponData.overrideController");
                data["layoutNote"] = "Installed IL2CPP metadata exposes WeaponDataBase weaponIndex/vfxKey/weaponScale/weaponPath plus WeaponData fields; observed standalone managed-reference payloads serialize the first three base fields, omit weaponPath, then serialize all WeaponData fields.";
                reader.EnsureComplete();
                return true;
            }
            catch (InvalidDataException)
            {
                data = null;
                return false;
            }
        }

        private static bool TryDecodeStaticWeaponDataManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (!string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "StaticWeaponData", StringComparison.Ordinal)
                || length < 48)
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
                    { "layout", "Beyond.Gameplay.StaticWeaponData" },
                    { "offset", offset },
                    { "length", length },
                    { "weaponIndex", reader.ReadInt32("staticWeaponData.weaponIndex") },
                    { "vfxKey", reader.ReadAlignedAsciiString("staticWeaponData.vfxKey") },
                    { "weaponScale", reader.ReadFloat("staticWeaponData.weaponScale") },
                    { "_weaponPath", reader.ReadAlignedAsciiString("staticWeaponData._weaponPath") },
                };
                if (reader.Remaining != 32)
                {
                    throw new InvalidDataException("StaticWeaponData payload must end with visibility fields and overrideController");
                }

                data["showWhenIdle"] = reader.ReadBool32("staticWeaponData.showWhenIdle");
                data["idleMountPoint"] = reader.ReadInt32("staticWeaponData.idleMountPoint");
                data["showWhenFight"] = reader.ReadBool32("staticWeaponData.showWhenFight");
                data["fightMountPoint"] = reader.ReadInt32("staticWeaponData.fightMountPoint");
                data["overrideAnimation"] = reader.ReadBool32("staticWeaponData.overrideAnimation");
                data["overrideController"] = ReadPayloadPPtr(reader, "staticWeaponData.overrideController");
                data["layoutNote"] = "Installed IL2CPP metadata exposes StaticWeaponDataBase._weaponPath plus StaticWeaponData visibility and override fields; observed standalone payloads also include WeaponDataBase weaponIndex/vfxKey/weaponScale before _weaponPath.";
                reader.EnsureComplete();
                return true;
            }
            catch (InvalidDataException)
            {
                data = null;
                return false;
            }
        }

        private static bool TryDecodeWeaponDataWrapperManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid,
            out OrderedDictionary data
        )
        {
            data = null;
            if (!string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "WeaponDataWrapper", StringComparison.Ordinal)
                || length < 4)
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
                    { "layout", "Beyond.Gameplay.WeaponDataWrapper" },
                    { "offset", offset },
                    { "length", length },
                    { "dataList", ReadPayloadRidLinkList(reader, "dataList", 16, recoveredByRid) },
                    { "layoutNote", "Installed IL2CPP metadata exposes WeaponDataWrapper.dataList; observed payloads serialize it as a managed-reference RID list." },
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
        private static bool TryDecodeSoundGameplayManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (!string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                if (string.Equals(header.ClassName, "PlaySound", StringComparison.Ordinal))
                {
                    if (length < 8)
                    {
                        return false;
                    }

                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    var soundName = reader.ReadAlignedAsciiString("soundName");
                    if (reader.Remaining != 4)
                    {
                        throw new InvalidDataException("PlaySound payload must end with largeType int32");
                    }

                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.PlaySound" },
                        { "offset", offset },
                        { "length", length },
                        { "soundName", soundName },
                        { "largeType", BuildPayloadHash32(reader.ReadInt32("largeType")) },
                        { "layoutNote", "Installed IL2CPP metadata exposes static marker fields plus serialized soundName and largeType; observed managed-reference payloads contain soundName followed by largeType." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.ClassName, "PlaySingleSound", StringComparison.Ordinal))
                {
                    if (length == 0)
                    {
                        data = new OrderedDictionary
                        {
                            { "$decoded", true },
                            { "$inferred", true },
                            { "layout", "Beyond.Gameplay.PlaySingleSound" },
                            { "offset", offset },
                            { "length", length },
                            { "serializedFieldsPresent", false },
                            { "layoutNote", "Observed zero-byte PlaySingleSound payload variant; IL2CPP metadata names soundSpawn/soundFinish/shouldTick and override tracking fields, but this serialized entry carries none of those fields." },
                        };
                        return true;
                    }
                    if (length != 28)
                    {
                        return false;
                    }

                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.PlaySingleSound" },
                        { "offset", offset },
                        { "length", length },
                        { "soundBase", new OrderedDictionary
                            {
                                { "soundSpawn", BuildPayloadHash32(reader.ReadInt32("soundBase.soundSpawn")) },
                                { "soundFinish", BuildPayloadHash32(reader.ReadInt32("soundBase.soundFinish")) },
                                { "shouldTick", reader.ReadBool32("soundBase.shouldTick") },
                            }
                        },
                        { "isOverrideTrackingObj", reader.ReadBool32("isOverrideTrackingObj") },
                        { "overridedTrackingObj", ReadPayloadPPtr(reader, "overridedTrackingObj") },
                        { "layoutNote", "IL2CPP metadata exposes PlaySingleSoundBase soundSpawn/soundFinish/shouldTick plus PlaySingleSound override tracking fields; m_audioObj is a runtime cache and is not serialized in the observed 28-byte payload." },
                    };
                    reader.EnsureComplete();
                    return true;
                }

                if (string.Equals(header.ClassName, "PlaySoundByParticleCount", StringComparison.Ordinal))
                {
                    if (length < 20)
                    {
                        return false;
                    }

                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    var soundName = reader.ReadAlignedAsciiString("soundName");
                    if (reader.Remaining != 16)
                    {
                        throw new InvalidDataException("PlaySoundByParticleCount payload must end with particle PPtr plus threshold");
                    }

                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.PlaySoundByParticleCount" },
                        { "offset", offset },
                        { "length", length },
                        { "soundName", soundName },
                        { "particle", ReadPayloadPPtr(reader, "particle") },
                        { "threshold", reader.ReadInt32("threshold") },
                        { "layoutNote", "Installed IL2CPP metadata exposes soundName, particle, threshold, and runtime-only m_lastCount; observed serialized payloads contain the first three fields." },
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

        private static bool TryDecodeCharacterTemplateManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid,
            out OrderedDictionary data
        )
        {
            data = null;
            if (!string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "CharacterTemplateData", StringComparison.Ordinal)
                || length < 300
                || (length % 4) != 0)
            {
                return false;
            }

            try
            {
                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                var id = ReadPayloadAlignedUtf8StringWithZeroPadding(reader, "characterTemplateData.id", 128);
                if (!id.StartsWith("chr_", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"unexpected CharacterTemplateData id {id}");
                }

                var baseName = ReadPayloadAlignedUtf8StringWithZeroPadding(reader, "characterTemplateData.baseTemplate.name", 128);
                var factionIndex = reader.ReadInt32("characterTemplateData.baseTemplate.factionIndex");
                var bornTagPresent = reader.ReadBool32("characterTemplateData.entityTemplate.bornTag.present");
                OrderedDictionary bornTag = null;
                if (bornTagPresent)
                {
                    bornTag = ReadPayloadGameplayTagWithZeroPadding(reader, "characterTemplateData.entityTemplate.bornTag", 256);
                }

                var delayToRecycleTime = ReadPayloadFloatRange(reader, "characterTemplateData.entityTemplate.delayToRecycleTime", 0f, 3600f);
                var delayRecyclePerformTime = ReadPayloadFloatRange(reader, "characterTemplateData.entityTemplate.delayRecyclePerformTime", 0f, 3600f);
                var sendDieEvent = reader.ReadBool32("characterTemplateData.entityTemplate.sendDieEvent");
                var enableBornFadeIn = reader.ReadBool32("characterTemplateData.entityTemplate.enableBornFadeIn");
                var fadeInTime = ReadPayloadFloatRange(reader, "characterTemplateData.entityTemplate.fadeInTime", 0f, 3600f);
                var componentList = ReadPayloadRidLinkList(reader, "characterTemplateData.entityTemplate.componentList", 32, recoveredByRid);
                if (componentList.Count != 26)
                {
                    throw new InvalidDataException($"unexpected CharacterTemplateData component count {componentList.Count}");
                }

                var animConfigPath = ReadPayloadAlignedUtf8StringWithZeroPadding(reader, "characterTemplateData.animConfigPath", 256);
                if (!animConfigPath.StartsWith("Assets/", StringComparison.Ordinal)
                    || !animConfigPath.EndsWith(".asset", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"unexpected CharacterTemplateData anim config path {animConfigPath}");
                }

                var bodyType = reader.ReadInt32("characterTemplateData.bodyType.bodyType");
                var customName = ReadPayloadAlignedUtf8StringWithZeroPadding(reader, "characterTemplateData.bodyType.CustomName", 128);
                var customId = reader.ReadInt32("characterTemplateData.bodyType.CustomId");
                var entityTemplate = new OrderedDictionary
                {
                    { "bornTagPresent", bornTagPresent },
                    { "delayToRecycleTime", delayToRecycleTime },
                    { "delayRecyclePerformTime", delayRecyclePerformTime },
                    { "sendDieEvent", sendDieEvent },
                    { "enableBornFadeIn", enableBornFadeIn },
                    { "fadeInTime", fadeInTime },
                    { "componentList", componentList },
                };
                if (bornTag != null)
                {
                    entityTemplate["bornTag"] = bornTag;
                }

                data = new OrderedDictionary
                {
                    { "$decoded", true },
                    { "$inferred", true },
                    { "layout", "Beyond.Gameplay.CharacterTemplateData" },
                    { "offset", offset },
                    { "length", length },
                    { "id", id },
                    { "baseTemplate", new OrderedDictionary
                        {
                            { "name", baseName },
                            { "factionIndex", BuildPayloadHash32(factionIndex) },
                        }
                    },
                    { "entityTemplate", entityTemplate },
                    { "animConfigPath", animConfigPath },
                    { "bodyType", new OrderedDictionary
                        {
                            { "bodyType", BuildPayloadHash32(bodyType) },
                            { "CustomName", customName },
                            { "CustomId", BuildPayloadHash32(customId) },
                        }
                    },
                    { "layoutNote", "Installed IL2CPP metadata supplies GameDataWithId/BaseTemplateData/EntityTemplateData/CharacterTemplateData/BodyTypeDef field order. Current payloads contain one optional born GameplayTag, exactly 26 component RID links, and an animation config asset path; bodyType and CustomId are retained as raw hash-style int32 values until their enum/domain is identified." },
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

        private static bool TryDecodeProjectileTemplateManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid,
            out OrderedDictionary data
        )
        {
            data = null;
            if (!string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "ProjectileTemplateData", StringComparison.Ordinal)
                || length < 160)
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
                    { "layout", "Beyond.Gameplay.ProjectileTemplateData" },
                    { "offset", offset },
                    { "length", length },
                    { "id", reader.ReadAlignedAsciiString("id") },
                    { "baseTemplate", new OrderedDictionary
                        {
                            { "name", reader.ReadAlignedAsciiString("baseTemplate.name") },
                            { "factionIndex", BuildPayloadHash32(reader.ReadInt32("baseTemplate.factionIndex")) },
                        }
                    },
                    { "entityTemplate", new OrderedDictionary
                        {
                            { "bornTag", BuildPayloadHash32(reader.ReadInt32("entityTemplate.bornTag")) },
                            { "delayToRecycleTime", reader.ReadFloat("entityTemplate.delayToRecycleTime") },
                            { "delayRecyclePerformTime", reader.ReadFloat("entityTemplate.delayRecyclePerformTime") },
                            { "sendDieEvent", reader.ReadBool32("entityTemplate.sendDieEvent") },
                            { "enableBornFadeIn", reader.ReadBool32("entityTemplate.enableBornFadeIn") },
                            { "fadeInTime", reader.ReadFloat("entityTemplate.fadeInTime") },
                            { "componentList", ReadPayloadRidLinkList(reader, "entityTemplate.componentList", 16, recoveredByRid) },
                        }
                    },
                    { "useWeaponEmitMountPoint", reader.ReadBool32("useWeaponEmitMountPoint") },
                };
                data["emitMountPoint"] = BuildPayloadHash32(reader.ReadInt32("emitMountPoint"));
                data["weaponIndex"] = reader.ReadInt32("weaponIndex");
                data["weaponMountPoint"] = BuildPayloadHash32(reader.ReadInt32("weaponMountPoint"));
                data["hitMountPoint"] = BuildPayloadHash32(reader.ReadInt32("hitMountPoint"));
                data["skillDataBundle"] = ReadProjectileSkillDataBundle(reader);
                data["layoutNote"] = "Installed IL2CPP metadata supplies GameDataWithId/BaseTemplateData/EntityTemplateData/ProjectileTemplateData field order; current payloads use a single int32 bornTag, bool32 fields, and an empty comboSkillConditions list and an empty defaultCmdMapping with zero key/value counts.";
                reader.EnsureComplete();
                return true;
            }
            catch (InvalidDataException)
            {
                data = null;
                return false;
            }
        }

        private static bool TryDecodeWikiModelSpawnManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (!string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "WikiModelSpawnData", StringComparison.Ordinal)
                || length < 44)
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
                    { "layout", "Beyond.Gameplay.WikiModelSpawnData" },
                    { "offset", offset },
                    { "length", length },
                };
                foreach (DictionaryEntry entry in ReadWikiModelSpawnData(reader, string.Empty))
                {
                    data[entry.Key] = entry.Value;
                }
                data["layoutNote"] = "Installed IL2CPP metadata and serialized TypeTree expose position, rotation, scale, cameraDistance, and effects; each observed effect contains name, mountPoint, follow flags, offset, rotation, and scale.";
                reader.EnsureComplete();
                return true;
            }
            catch (InvalidDataException)
            {
                data = null;
                return false;
            }
        }

        private static bool TryDecodeWikiWeaponManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (!string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "WikiWeaponData", StringComparison.Ordinal)
                || length < 4)
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
                    { "layout", "Beyond.Gameplay.WikiWeaponData" },
                    { "offset", offset },
                    { "length", length },
                    { "spawnDataList", ReadWikiModelSpawnDataList(reader) },
                    { "layoutNote", "Installed IL2CPP metadata exposes WikiWeaponData.spawnDataList; observed entries serialize nested WikiModelSpawnData records." },
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

        private static bool TryDecodeWeaponDecoEffectManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            if (!string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal)
                || !string.Equals(header.ClassName, "WeaponDecoEffectData", StringComparison.Ordinal)
                || length <= 0
                || length % 4 != 0)
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
                    { "layout", "Beyond.Gameplay.WeaponDecoEffectData" },
                    { "offset", offset },
                    { "length", length },
                    { "gemDeco", ReadWeaponDecoData(reader, "gemDeco") },
                    { "gemMaxDeco", ReadWeaponDecoData(reader, "gemMaxDeco") },
                    { "layoutNote", "Installed IL2CPP metadata exposes gemDeco and gemMaxDeco; each DecoData contains effects and vfxMaterials, and each EffectData contains name, mountPoint, and offset." },
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
                || !header.Namespace.StartsWith("Beyond.Gameplay.Core", StringComparison.Ordinal)
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
                || !header.Namespace.StartsWith("Beyond.Gameplay.Core", StringComparison.Ordinal)
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
                || !header.Namespace.StartsWith("Beyond.Gameplay.Core", StringComparison.Ordinal)
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
                    && string.Equals(header.ClassName, "CharacterRootComponentData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    var locatorIds = ReadPayloadInt32List(reader, "locatorIds", 128);
                    var locatorNameCount = reader.ReadInt32("locatorNames.count");
                    if (locatorNameCount != locatorIds.Count)
                    {
                        throw new InvalidDataException("CharacterRootComponentData id/name count mismatch");
                    }

                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.Core.CharacterRootComponentData" },
                        { "layoutNote", "Byte-proven current corpus shape mirrors EnemyRootComponentData: locator id/name lists, an unknown int32, transform records, and a word-aligned tail preserved verbatim because its semantic fields are not fully named." },
                        { "offset", offset },
                        { "length", length },
                        { "locatorIds", locatorIds },
                        { "locatorNames", ReadPayloadStringListFixed(reader, "locatorNames", locatorNameCount) },
                        { "unknown0", reader.ReadInt32("unknown0") },
                        { "transformRecords", ReadPayloadObjectList(reader, "transformRecords", 16, ReadEnemyRootTransformRecord) },
                        { "trailingWords", ReadRemainingPayloadRawInt32Words(reader, "trailingWords", 8192) },
                    };
                    reader.EnsureComplete();
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
                    && string.Equals(header.Namespace, "Beyond.Gameplay.View", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "FootRippleComponentData", StringComparison.Ordinal))
                {
                    var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                    data = new OrderedDictionary
                    {
                        { "$decoded", true },
                        { "$inferred", true },
                        { "layout", "Beyond.Gameplay.View.FootRippleComponentData" },
                        { "offset", offset },
                        { "length", length },
                        { "entries", ReadFootRippleEntryList(reader, "entries", 16) },
                        { "footWeightThreshold", reader.ReadFloat("footWeightThreshold") },
                        { "speedToRippleIntervalCurve", ReadPayloadAnimationCurveFloat(reader, "speedToRippleIntervalCurve") },
                    };
                    reader.EnsureComplete();
                    return true;
                }
                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
                    && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                    && string.Equals(header.ClassName, "AbilitySystemData", StringComparison.Ordinal))
                {
                    try
                    {
                        var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                        data = new OrderedDictionary
                        {
                            { "$decoded", true },
                            { "$inferred", true },
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
                        if (TryReadAbilitySystemSkillDataBundle(reader, recoveredByRid, out var skillDataBundle))
                        {
                            data["skillDataBundle"] = skillDataBundle;
                            if (TryReadAbilitySystemUIData(reader, out var uiData))
                            {
                                data["uiData"] = uiData;
                                if (TryReadAbilitySystemBuffInputLists(reader, out var buffInputLists))
                                {
                                    foreach (DictionaryEntry entry in buffInputLists)
                                    {
                                        data[entry.Key] = entry.Value;
                                    }

                                    if (TryReadAbilitySystemPostBuffFields(reader, out var postBuffFields))
                                    {
                                        foreach (DictionaryEntry entry in postBuffFields)
                                        {
                                            data[entry.Key] = entry.Value;
                                        }

                                        if (TryReadAbilitySystemEntityBlackboardSection(reader, out var entityBlackboardSection))
                                        {
                                            foreach (DictionaryEntry entry in entityBlackboardSection)
                                            {
                                                data[entry.Key] = entry.Value;
                                            }

                                            if (TryReadAbilitySystemSkillCameraConfigSection(reader, out var skillCameraConfigSection))
                                            {
                                                foreach (DictionaryEntry entry in skillCameraConfigSection)
                                                {
                                                    data[entry.Key] = entry.Value;
                                                }

                                                if (TryReadAbilitySystemPostCameraFields(reader, out var postCameraFields))
                                                {
                                                    foreach (DictionaryEntry entry in postCameraFields)
                                                    {
                                                        data[entry.Key] = entry.Value;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        var remainingStringHints = CollectAbilitySystemRemainingStringHints(rawData, reader.Position, reader.Remaining, 128);
                        data["remainingStringHints"] = remainingStringHints;
                        var remainingRidLinkBudget = MaxHeuristicRidLinksPerReference;
                        var remainingRidLinks = CollectHeuristicRidLinks(rawData, reader.Position, reader.Remaining, recoveredByRid, ref remainingRidLinkBudget);
                        if (remainingRidLinks.Count > 0)
                        {
                            data["remainingRidLinks"] = remainingRidLinks;
                        }
                        var remainingRawWords = ReadRemainingPayloadRawInt32Words(reader, "remainingRawWords", 8192);
                        data["remainingRawWords"] = remainingRawWords;
                        if (remainingStringHints.Count > 0 || remainingRidLinks.Count > 0 || remainingRawWords.Count > 0)
                        {
                            data["$partial"] = true;
                            data["partialReasons"] = new List<string>
                            {
                                "AbilitySystemData reader left own remaining string hints, RID links, or raw words after the staged metadata-backed sections.",
                                "Nested partial payloads carry their own partial markers and are not counted as parent unread bytes.",
                            };
                        }
                        else
                        {
                            data["observedPayloadStatus"] = "all serialized AbilitySystemData bytes consumed by staged reader; nested partial objects carry their own partial markers";
                            data["layoutNote"] = "AbilitySystemData serialized bytes are fully consumed by the staged metadata-backed reader in this payload. Nested objects keep their own decode status, so child partial markers do not imply unread parent bytes.";
                        }
                        reader.EnsureComplete();
                        return true;
                    }
                    catch (InvalidDataException ex)
                    {
                        var fallback = BuildPartialAbilitySystemDataDiagnostic(
                            rawData,
                            offset,
                            length,
                            recoveredByRid,
                            ex
                        );
                        if (fallback != null)
                        {
                            data = fallback;
                            return true;
                        }
                        throw;
                    }
                }

                if (string.Equals(header.AssemblyName, "Gameplay.Beyond", StringComparison.Ordinal)
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
                    if (TryReadEnemyPartsRootComponentData(rawData, offset, length, 8, out data)
                        || TryReadEnemyPartsRootComponentData(rawData, offset, length, 10, out data)
                        || TryReadEnemyPartsRootComponentDataWithPartIdList(rawData, offset, length, out data))
                    {
                        return true;
                    }
                    throw new InvalidDataException("unsupported EnemyPartsRootComponentData prefix variant");
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
                    else if (wordCount >= EnemyPartAbilityPostAttributeScalarWordCount
                        && CanDecodeEnemyPartAbilityPostAttributeScalarTail(rawData, offset + length - (EnemyPartAbilityPostAttributeScalarWordCount * 4), EnemyPartAbilityPostAttributeScalarWordCount * 4))
                    {
                        data["$partial"] = true;
                        data["layoutVariant"] = "postAttributeScalarTail18";
                        data["layoutNote"] = "final 18 scalar fields are decoded; defaultEnabled, asIndividualInExcludeTargetProcessor, and partAttributes remain in the unresolved prolog.";
                        data["partialReasons"] = new List<string>
                        {
                            "AbilitySystemForEnemyPartData front prolog/partAttributes boundary is not fully decoded yet.",
                            "Final scalar fields from useMainBodyHp through damageTransferType validate as a contiguous suffix.",
                        };
                        data["partAttributesAndScalarPrologRawWords"] = ReadPayloadRawInt32Words(
                            reader,
                            "partAttributesAndScalarPrologRawWords",
                            wordCount - EnemyPartAbilityPostAttributeScalarWordCount
                        );
                        data["fields"] = ReadEnemyPartAbilityPostAttributeScalarFields(reader);
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

        private static OrderedDictionary BuildEmptyManagedReferenceData(
            ManagedReferenceHeader header,
            int offset,
            int length
        )
        {
            return new OrderedDictionary
            {
                { "$decoded", true },
                { "layout", string.IsNullOrEmpty(header.Namespace) ? header.ClassName : $"{header.Namespace}.{header.ClassName}" },
                { "layoutNote", "Serialized managed-reference payload length is zero; the type identity is the complete exported data for this entry." },
                { "offset", offset },
                { "length", length },
            };
        }

        private static OrderedDictionary BuildReservedZeroWordsManagedReferenceData(
            ManagedReferenceHeader header,
            byte[] rawData,
            int offset,
            int length,
            int wordCount,
            string layoutNote
        )
        {
            var reader = new ManagedReferencePayloadReader(rawData, offset, length);
            var words = new List<OrderedDictionary>(wordCount);
            for (var i = 0; i < wordCount; i++)
            {
                var value = reader.ReadInt32($"reservedZeroWords[{i}]");
                if (value != 0)
                {
                    throw new InvalidDataException($"nonzero reserved word {value} at index {i}");
                }
                words.Add(BuildPayloadHash32(value));
            }
            reader.EnsureComplete();
            return new OrderedDictionary
            {
                { "$decoded", true },
                { "layout", string.IsNullOrEmpty(header.Namespace) ? header.ClassName : $"{header.Namespace}.{header.ClassName}" },
                { "layoutNote", layoutNote },
                { "offset", offset },
                { "length", length },
                { "reservedZeroWords", words },
            };
        }

        private static bool IsKnownEmptyCoreGameplayManagedReferenceData(ManagedReferenceHeader header)
        {
            return header != null
                && string.Equals(header.Namespace, "Beyond.Gameplay.Core", StringComparison.Ordinal)
                && (string.Equals(header.ClassName, "CharacterControllerData", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "CharacterAudioComponentData", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "CharacterBlowOffComponentData", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "StateTransitionComponentData", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "RemoteFactoryMineComponentData", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "Selector/CharacterTeamFinder/Data", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "Selector/MainCharacterValidator/Data", StringComparison.Ordinal));
        }

        private static bool IsKnownEmptyViewManagedReferenceData(ManagedReferenceHeader header)
        {
            return header != null
                && string.Equals(header.Namespace, "Beyond.Gameplay.View", StringComparison.Ordinal)
                && string.Equals(header.ClassName, "LookAtComponentData", StringComparison.Ordinal);
        }

        private static bool IsKnownEmptyGeneralGameplayManagedReferenceData(ManagedReferenceHeader header)
        {
            if (header == null)
            {
                return false;
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay", StringComparison.Ordinal))
            {
                return string.Equals(header.ClassName, "DynamicBattleShapeComponentData", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "CustomAbilityComponentData", StringComparison.Ordinal);
            }

            if (string.Equals(header.Namespace, "Beyond.Gameplay.InteractiveEvent", StringComparison.Ordinal))
            {
                return string.Equals(header.ClassName, "InteractiveInstigatorControlComponentData", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "DetachFromInstigator", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "ClearInstigator", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "SetInstigator", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "AddThrowCameraControl", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "ThrowByForceAndDir", StringComparison.Ordinal)
                    || string.Equals(header.ClassName, "TriggerPickUpAction", StringComparison.Ordinal);
            }

            return false;
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
        private const int EnemyPartAbilityPostAttributeScalarWordCount = 18;

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

        private static OrderedDictionary ReadPayloadGameplayTagWithZeroPadding(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxPathLength
        )
        {
            return new OrderedDictionary
            {
                { "path", ReadPayloadAlignedUtf8StringWithZeroPadding(reader, $"{fieldName}.path", maxPathLength) },
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

        private static OrderedDictionary ReadGuideConditionBase(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "id", reader.ReadAlignedAsciiString($"{fieldName}.id") },
                { "unknown0", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown0")) },
                { "unknown1", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown1")) },
                { "unknown2", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown2")) },
            };
        }

        private static OrderedDictionary ReadGuideActionBase(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "actionId", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.actionId")) },
                { "key", reader.ReadAlignedAsciiString($"{fieldName}.key") },
                { "unknown0", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown0")) },
                { "unknown1", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown1")) },
                { "unknown2", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown2")) },
                { "triggerActiveDuringRaw", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.triggerActiveDuringRaw")) },
                { "validate", reader.ReadBool32($"{fieldName}.validate") },
                { "nextId", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.nextId")) },
            };
        }

        private static OrderedDictionary ReadGuideActionParamBool(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "paramSource", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.paramSource")) },
                { "path", reader.ReadAlignedAsciiString($"{fieldName}.path") },
                { "value", reader.ReadBool32($"{fieldName}.value") },
                { "idRef", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.idRef")) },
            };
        }

        private static OrderedDictionary ReadGuideActionParamFloat(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "paramSource", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.paramSource")) },
                { "path", reader.ReadAlignedAsciiString($"{fieldName}.path") },
                { "value", reader.ReadFloat($"{fieldName}.value") },
                { "idRef", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.idRef")) },
            };
        }

        private static OrderedDictionary ReadGuideActionParamInt(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "paramSource", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.paramSource")) },
                { "path", reader.ReadAlignedAsciiString($"{fieldName}.path") },
                { "value", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.value")) },
                { "idRef", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.idRef")) },
            };
        }

        private static OrderedDictionary ReadGuideActionParamInt64(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "paramSource", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.paramSource")) },
                { "path", reader.ReadAlignedAsciiString($"{fieldName}.path") },
                { "value", BuildPayloadHash64(reader.ReadInt64($"{fieldName}.value")) },
                { "idRef", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.idRef")) },
            };
        }

        private static OrderedDictionary ReadGuideActionParamString(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "paramSource", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.paramSource")) },
                { "path", reader.ReadAlignedAsciiString($"{fieldName}.path") },
                { "value", ReadGuideParamStringValue(reader, $"{fieldName}.value") },
                { "idRef", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.idRef")) },
            };
        }

        private static OrderedDictionary ReadGuideActionParamVector3(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "paramSource", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.paramSource")) },
                { "path", reader.ReadAlignedAsciiString($"{fieldName}.path") },
                { "value", ReadPayloadVector3(reader, $"{fieldName}.value") },
                { "idRef", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.idRef")) },
            };
        }

        private static OrderedDictionary ReadGuideActionParamOutputInt(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "paramTarget", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.paramTarget")) },
                { "path", reader.ReadAlignedAsciiString($"{fieldName}.path") },
            };
        }

        private static OrderedDictionary ReadGuideActionParamPathRawWords(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int rawWordCount
        )
        {
            return new OrderedDictionary
            {
                { "paramSource", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.paramSource")) },
                { "path", reader.ReadAlignedAsciiString($"{fieldName}.path") },
                { "rawWords", ReadPayloadRawInt32Words(reader, $"{fieldName}.rawWords", rawWordCount) },
            };
        }

        private static OrderedDictionary ReadGuideStringParam(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "unknown0", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown0")) },
                { "unknown1", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown1")) },
                { "value", ReadGuideParamStringValue(reader, $"{fieldName}.value") },
                { "unknown2", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown2")) },
            };
        }

        private static OrderedDictionary ReadGuideIntParam(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "unknown0", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown0")) },
                { "unknown1", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown1")) },
                { "value", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.value")) },
                { "unknown2", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown2")) },
            };
        }

        private static OrderedDictionary ReadGuideBoolParam(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "unknown0", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown0")) },
                { "unknown1", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown1")) },
                { "value", reader.ReadBool32($"{fieldName}.value") },
                { "unknown2", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown2")) },
            };
        }

        private static OrderedDictionary ReadGuideFloatParam(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "unknown0", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown0")) },
                { "unknown1", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown1")) },
                { "value", reader.ReadFloat($"{fieldName}.value") },
                { "unknown2", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown2")) },
            };
        }

        private static OrderedDictionary ReadGuideVector3Param(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "unknown0", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown0")) },
                { "unknown1", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown1")) },
                { "value", ReadPayloadVector3(reader, $"{fieldName}.value") },
                { "unknown2", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown2")) },
            };
        }

        private static OrderedDictionary ReadGuideIntPairParam(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "unknown0", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown0")) },
                { "unknown1", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown1")) },
                { "value0", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.value0")) },
                { "value1", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.value1")) },
                { "unknown2", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown2")) },
            };
        }

        private static List<OrderedDictionary> ReadGuideRawInt32List(
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
        private static OrderedDictionary ReadGuideStringParamWithExtraRawWord(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "unknown0", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown0")) },
                { "unknown1", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown1")) },
                { "unknown2", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown2")) },
                { "value", ReadGuideParamStringValue(reader, $"{fieldName}.value") },
                { "unknown3", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.unknown3")) },
            };
        }
        private static string ReadGuideParamStringValue(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            if (!NextLooksLikeAlignedAsciiString(reader, 256))
            {
                throw new InvalidDataException($"expected aligned string in {fieldName}");
            }
            return reader.ReadAlignedAsciiString(fieldName);
        }

        private static bool NextLooksLikeAlignedAsciiString(
            ManagedReferencePayloadReader reader,
            int maxLength
        )
        {
            if (reader == null || reader.Remaining < 4)
            {
                return false;
            }

            var pos = reader.Position;
            var length = BinaryPrimitives.ReadInt32LittleEndian(reader.RawData.AsSpan(pos, 4));
            if (length < 0 || length > maxLength)
            {
                return false;
            }

            var dataStart = pos + 4;
            var dataEnd = dataStart + length;
            var alignedEnd = (dataEnd + 3) & ~3;
            if (alignedEnd > reader.End)
            {
                return false;
            }

            for (var i = dataStart; i < dataEnd; i++)
            {
                if (reader.RawData[i] < 0x20 || reader.RawData[i] > 0x7E)
                {
                    return false;
                }
            }
            return true;
        }

        private static OrderedDictionary ReadPayloadBlackboardDouble(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "useBlackboardKey", reader.ReadBool32($"{fieldName}.useBlackboardKey") },
                { "value", reader.ReadFloat($"{fieldName}.value") },
                { "blackboardKey", reader.ReadAlignedAsciiString($"{fieldName}.blackboardKey") },
            };
        }

        private static List<OrderedDictionary> ReadPlayTimedMontageInfoList(
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
                    { "playMontageTag", ReadPayloadGameplayTag(reader, $"{fieldName}[{i}].playMontageTag") },
                    { "overrideMontageStartState", reader.ReadBool32($"{fieldName}[{i}].overrideMontageStartState") },
                    { "montageStartState", BuildPayloadHash32(reader.ReadInt32($"{fieldName}[{i}].montageStartState")) },
                    { "limitMaxDuration", reader.ReadBool32($"{fieldName}[{i}].limitMaxDuration") },
                    { "duration", ReadPayloadVector2(reader, $"{fieldName}[{i}].duration") },
                });
            }
            return items;
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

        private static OrderedDictionary ReadEnemyBornBehaviorData(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "desc", reader.ReadAlignedAsciiString($"{fieldName}.desc") },
                { "enterMode", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.enterMode")) },
                { "enterBuffId", reader.ReadAlignedAsciiString($"{fieldName}.enterBuffId") },
                { "enterRepeat", reader.ReadBool32($"{fieldName}.enterRepeat") },
                { "enterAnimSpeed", reader.ReadFloat($"{fieldName}.enterAnimSpeed") },
                { "enterAnimId", reader.ReadAlignedAsciiString($"{fieldName}.enterAnimId") },
                { "enterRootMotion", reader.ReadBool32($"{fieldName}.enterRootMotion") },
                { "enterSkillId", reader.ReadAlignedAsciiString($"{fieldName}.enterSkillId") },
                { "exitMode", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.exitMode")) },
                { "exitBuffId", reader.ReadAlignedAsciiString($"{fieldName}.exitBuffId") },
                { "exitAnimSpeed", reader.ReadFloat($"{fieldName}.exitAnimSpeed") },
                { "exitAnimId", reader.ReadAlignedAsciiString($"{fieldName}.exitAnimId") },
                { "exitRootMotion", reader.ReadBool32($"{fieldName}.exitRootMotion") },
                { "bornCanInterrupt", reader.ReadBool32($"{fieldName}.bornCanInterrupt") },
                { "exitSkillId", reader.ReadAlignedAsciiString($"{fieldName}.exitSkillId") },
                { "canInterruptTime", reader.ReadFloat($"{fieldName}.canInterruptTime") },
            };
        }

        private static OrderedDictionary ReadCharacterStimulusResponse(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxCount,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid
        )
        {
            return new OrderedDictionary
            {
                { "unknownFloat0", reader.ReadFloat($"{fieldName}.unknownFloat0") },
                { "srData", ReadNpcStimulusResponseList(reader, $"{fieldName}.srData", maxCount, recoveredByRid) },
            };
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
            var fields = new OrderedDictionary
            {
                { "defaultEnabled", reader.ReadBool32("defaultEnabled") },
                { "asIndividualInExcludeTargetProcessor", reader.ReadBool32("asIndividualInExcludeTargetProcessor") },
            };
            foreach (DictionaryEntry entry in ReadEnemyPartAbilityPostAttributeScalarFields(reader))
            {
                fields[entry.Key] = entry.Value;
            }
            return fields;
        }

        private static bool CanDecodeEnemyPartAbilityPostAttributeScalarTail(byte[] rawData, int offset, int length)
        {
            try
            {
                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                ReadEnemyPartAbilityPostAttributeScalarFields(reader);
                reader.EnsureComplete();
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        private static OrderedDictionary ReadEnemyPartAbilityPostAttributeScalarFields(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
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

        private static OrderedDictionary BuildPartialAbilitySystemDataDiagnostic(
            byte[] rawData,
            int offset,
            int length,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid,
            InvalidDataException parseError
        )
        {
            if (rawData == null || offset < 0 || length < 12 || offset + length > rawData.Length)
            {
                return null;
            }

            try
            {
                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                var shapeData = new OrderedDictionary
                {
                    { "detectedRadius", reader.ReadFloat("shapeData.detectedRadius") },
                    { "detectedHeight", reader.ReadFloat("shapeData.detectedHeight") },
                };
                var modeCountOffset = reader.Position;
                var modeCount = reader.ReadInt32("modeConfig.modes.count");
                if (modeCount < 0 || modeCount > 128)
                {
                    return null;
                }

                var data = new OrderedDictionary
                {
                    { "$decoded", true },
                    { "$inferred", true },
                    { "$partial", true },
                    { "$diagnostic", true },
                    { "layout", "Beyond.Gameplay.Core.AbilitySystemData" },
                    { "layoutNote", "decoded AbilitySystemData prefix; modeConfig parsing failed on an unhandled mode-tail variant, so the remaining bytes are preserved for recovery" },
                    { "offset", offset },
                    { "length", length },
                    { "parseFailure", parseError?.Message ?? "AbilitySystemData parser failed" },
                    { "shapeData", shapeData },
                    { "modeConfig", new OrderedDictionary
                        {
                            { "$partial", true },
                            { "modeCount", modeCount },
                            { "modeCountOffset", modeCountOffset },
                            { "modePayloadOffset", reader.Position },
                        }
                    },
                };

                var remainingStringHintBudget = MaxHeuristicStringHintsPerReference;
                var alignedHints = CollectAlignedStringHints(rawData, reader.Position, reader.Remaining, ref remainingStringHintBudget);
                if (alignedHints.Count > 0)
                {
                    data["modeAndTailAlignedStringHints"] = alignedHints;
                }

                var hints = CollectAbilitySystemRemainingStringHints(rawData, reader.Position, reader.Remaining, 128);
                if (hints.Count > 0)
                {
                    data["modeAndTailStringHints"] = hints;
                }

                var remainingRidLinkBudget = MaxHeuristicRidLinksPerReference;
                var ridLinks = CollectHeuristicRidLinks(rawData, reader.Position, reader.Remaining, recoveredByRid, ref remainingRidLinkBudget);
                if (ridLinks.Count > 0)
                {
                    data["modeAndTailRidLinks"] = ridLinks;
                }

                data["modeAndTailRawWords"] = ReadRemainingPayloadRawInt32Words(reader, "modeAndTailRawWords", 8192);
                reader.EnsureComplete();
                return data;
            }
            catch (InvalidDataException)
            {
                return null;
            }
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
                    tail["overrideClipMapping"] = ReadAbilitySystemModeOverrideClipMapping(
                        local,
                        "modeConfig.modes.overrideClipMapping"
                    );
                }

                tail["overrideAnimCfg"] = local.ReadBool32("modeConfig.modes.overrideAnimCfg");
                tail["animCfgPath"] = local.ReadAlignedAsciiString("modeConfig.modes.animCfgPath");
                tail["overrideModelKey"] = local.ReadBool32("modeConfig.modes.overrideModelKey");
                tail["modelKey"] = local.ReadAlignedAsciiString("modeConfig.modes.modelKey");
                tail["mountPointDefIndex"] = local.ReadInt32("modeConfig.modes.mountPointDefIndex");
                var overrideCmdMapping = local.ReadBool32("modeConfig.modes.overrideCmdMapping");
                tail["overrideCmdMapping"] = overrideCmdMapping;
                tail["cmdMapping"] = ReadAbilitySystemModeCmdMapping(
                    local,
                    "modeConfig.modes.cmdMapping",
                    8,
                    overrideCmdMapping
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

        private static OrderedDictionary ReadAbilitySystemModeOverrideClipMapping(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            var headerWords = ReadPayloadRawInt32Words(reader, $"{fieldName}.headerRawWords", 2);
            if (!PayloadRawWordsEqual(headerWords, 0, 0))
            {
                throw new InvalidDataException($"unsupported non-empty {fieldName} header");
            }

            return new OrderedDictionary
            {
                { "headerRawWords", headerWords },
            };
        }

        private static OrderedDictionary ReadAbilitySystemModeCmdMapping(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxMappedValues,
            bool overrideCmdMapping
        )
        {
            if (overrideCmdMapping)
            {
                return ReadAbilitySystemBattleCommandStringDictionary(reader, fieldName, maxMappedValues);
            }

            var headerWords = ReadPayloadRawInt32Words(reader, $"{fieldName}.headerRawWords", 4);
            var data = new OrderedDictionary
            {
                { "headerRawWords", headerWords },
            };

            if (PayloadRawWordsEqual(headerWords, 0, 0, 0, 0))
            {
                return data;
            }

            if (!PayloadRawWordsEqual(headerWords, 0, 1, 1, 0))
            {
                data["layoutNote"] = "unrecognized cmdMapping header; no value list consumed";
                return data;
            }

            data["values"] = ReadPayloadStringList(reader, $"{fieldName}.values", maxMappedValues);
            return data;
        }

        private static OrderedDictionary ReadAbilitySystemBattleCommandStringDictionary(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxCount
        )
        {
            var keyCount = reader.ReadInt32($"{fieldName}.keys.count");
            if (keyCount < 0 || keyCount > maxCount)
            {
                throw new InvalidDataException($"invalid key count {keyCount} for {fieldName}");
            }

            var keys = new List<OrderedDictionary>(keyCount);
            for (var i = 0; i < keyCount; i++)
            {
                keys.Add(BuildAbilitySystemBattleCommandType(reader.ReadInt32($"{fieldName}.keys[{i}]")));
            }

            var valueCount = reader.ReadInt32($"{fieldName}.values.count");
            if (valueCount != keyCount)
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
                    { "command", keys[i] },
                    { "skillId", value },
                });
            }

            return new OrderedDictionary
            {
                { "keys", keys },
                { "values", values },
                { "entries", entries },
            };
        }

        private static OrderedDictionary BuildAbilitySystemBattleCommandType(int value)
        {
            var item = BuildPayloadHash32(value);
            switch (value)
            {
                case 0:
                    item["name"] = "Attack";
                    break;
                case 1:
                    item["name"] = "Dash";
                    break;
                case 2:
                    item["name"] = "Jump";
                    break;
                case 3:
                    item["name"] = "NormalSkill";
                    break;
                case 4:
                    item["name"] = "ComboSkill";
                    break;
                case 5:
                    item["name"] = "Count";
                    break;
            }
            return item;
        }

        private static bool PayloadRawWordsEqual(List<OrderedDictionary> words, params int[] expected)
        {
            if (words == null || words.Count != expected.Length)
            {
                return false;
            }

            for (var i = 0; i < expected.Length; i++)
            {
                if (!words[i].Contains("value") || words[i]["value"] is not int value || value != expected[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static OrderedDictionary ReadWeaponDecoData(ManagedReferencePayloadReader reader, string fieldName)
        {
            return new OrderedDictionary
            {
                { "effects", ReadWeaponDecoEffectList(reader, $"{fieldName}.effects") },
                { "vfxMaterials", ReadPayloadStringList(reader, $"{fieldName}.vfxMaterials", 32) },
            };
        }

        private static List<OrderedDictionary> ReadWeaponDecoEffectList(ManagedReferencePayloadReader reader, string fieldName)
        {
            var count = reader.ReadInt32($"{fieldName}.count");
            if (count < 0 || count > 32 || count > reader.Remaining / 20)
            {
                throw new InvalidDataException($"invalid count {count} for {fieldName}");
            }

            var effects = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                effects.Add(ReadWeaponDecoEffectData(reader, $"{fieldName}[{i}]"));
            }
            return effects;
        }

        private static OrderedDictionary ReadWeaponDecoEffectData(ManagedReferencePayloadReader reader, string fieldName)
        {
            return new OrderedDictionary
            {
                { "name", reader.ReadAlignedAsciiString($"{fieldName}.name") },
                { "mountPoint", reader.ReadAlignedAsciiString($"{fieldName}.mountPoint") },
                { "offset", ReadPayloadVector3(reader, $"{fieldName}.offset") },
            };
        }
        private static OrderedDictionary ReadWikiModelSpawnData(ManagedReferencePayloadReader reader, string fieldName)
        {
            var prefix = string.IsNullOrEmpty(fieldName) ? string.Empty : fieldName + ".";
            return new OrderedDictionary
            {
                { "position", ReadPayloadVector3(reader, prefix + "position") },
                { "rotation", ReadPayloadVector3(reader, prefix + "rotation") },
                { "scale", ReadPayloadVector3(reader, prefix + "scale") },
                { "cameraDistance", reader.ReadFloat(prefix + "cameraDistance") },
                { "effects", ReadWikiModelEffectList(reader, prefix + "effects") },
            };
        }

        private static List<OrderedDictionary> ReadWikiModelSpawnDataList(ManagedReferencePayloadReader reader)
        {
            var count = reader.ReadInt32("spawnDataList.count");
            if (count < 0 || count > 16 || count > reader.Remaining / 44)
            {
                throw new InvalidDataException($"invalid count {count} for spawnDataList");
            }

            var items = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                items.Add(ReadWikiModelSpawnData(reader, $"spawnDataList[{i}]"));
            }
            return items;
        }

        private static List<OrderedDictionary> ReadWikiModelEffectList(ManagedReferencePayloadReader reader, string fieldName)
        {
            var count = reader.ReadInt32($"{fieldName}.count");
            if (count < 0 || count > 16 || count > reader.Remaining / 52)
            {
                throw new InvalidDataException($"invalid count {count} for {fieldName}");
            }

            var effects = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                effects.Add(ReadWikiModelEffectData(reader, $"{fieldName}[{i}]"));
            }
            return effects;
        }

        private static OrderedDictionary ReadWikiModelEffectData(ManagedReferencePayloadReader reader, string fieldName)
        {
            return new OrderedDictionary
            {
                { "name", reader.ReadAlignedAsciiString($"{fieldName}.name") },
                { "mountPoint", reader.ReadAlignedAsciiString($"{fieldName}.mountPoint") },
                { "followScale", reader.ReadBool32($"{fieldName}.followScale") },
                { "followRotation", reader.ReadBool32($"{fieldName}.followRotation") },
                { "offset", ReadPayloadVector3(reader, $"{fieldName}.offset") },
                { "rotation", ReadPayloadVector3(reader, $"{fieldName}.rotation") },
                { "scale", ReadPayloadVector3(reader, $"{fieldName}.scale") },
            };
        }

        private static OrderedDictionary ReadProjectileSkillDataBundle(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "$decoded", true },
                { "layout", "Beyond.Gameplay.Core.SkillDataBundle" },
                { "allNormalAttackId", ReadPayloadStringList(reader, "skillDataBundle.allNormalAttackId", 256) },
                { "allActiveSkillId", ReadPayloadStringList(reader, "skillDataBundle.allActiveSkillId", 256) },
                { "allPassiveSkillId", ReadPayloadStringList(reader, "skillDataBundle.allPassiveSkillId", 256) },
                { "normalAttackList", ReadPayloadStringList(reader, "skillDataBundle.normalAttackList", 256) },
                { "enabledBreakingNormalAttacks", ReadPayloadStringList(reader, "skillDataBundle.enabledBreakingNormalAttacks", 256) },
                { "enabledPassiveSkills", ReadPayloadStringList(reader, "skillDataBundle.enabledPassiveSkills", 256) },
                { "normalSkillId", reader.ReadAlignedAsciiString("skillDataBundle.normalSkillId") },
                { "ultimateSkillId", reader.ReadAlignedAsciiString("skillDataBundle.ultimateSkillId") },
                { "plungingAttackStartId", reader.ReadAlignedAsciiString("skillDataBundle.plungingAttackStartId") },
                { "plungingAttackEndId", reader.ReadAlignedAsciiString("skillDataBundle.plungingAttackEndId") },
                { "dodgeSkillId", reader.ReadAlignedAsciiString("skillDataBundle.dodgeSkillId") },
                { "comboSkillConditions", ReadPayloadEmptyCountList(reader, "skillDataBundle.comboSkillConditions") },
                { "comboSkillId", reader.ReadAlignedAsciiString("skillDataBundle.comboSkillId") },
                { "comboSkillSpecialNodeName", reader.ReadAlignedAsciiString("skillDataBundle.comboSkillSpecialNodeName") },
                { "defaultCmdMapping", ReadPayloadEmptyCountObject(reader, "skillDataBundle.defaultCmdMapping") },
            };
        }

        private static OrderedDictionary ReadPayloadEmptyCountList(ManagedReferencePayloadReader reader, string fieldName)
        {
            var count = reader.ReadInt32($"{fieldName}.count");
            if (count != 0)
            {
                throw new InvalidDataException($"unsupported non-empty {fieldName} count {count}");
            }

            return new OrderedDictionary
            {
                { "count", count },
                { "entries", new List<OrderedDictionary>() },
            };
        }
        private static OrderedDictionary ReadPayloadEmptyCountObject(ManagedReferencePayloadReader reader, string fieldName)
        {
            var keyCount = reader.ReadInt32($"{fieldName}.keys.count");
            if (keyCount != 0)
            {
                throw new InvalidDataException($"unsupported non-empty {fieldName} keys count {keyCount}");
            }

            var valueCount = reader.ReadInt32($"{fieldName}.values.count");
            if (valueCount != 0)
            {
                throw new InvalidDataException($"unsupported non-empty {fieldName} values count {valueCount}");
            }

            return new OrderedDictionary
            {
                { "keys", new OrderedDictionary
                    {
                        { "count", keyCount },
                        { "entries", new List<OrderedDictionary>() },
                    }
                },
                { "values", new OrderedDictionary
                    {
                        { "count", valueCount },
                        { "entries", new List<OrderedDictionary>() },
                    }
                },
            };
        }

        private static bool TryReadAbilitySystemUIData(
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
                    { "layout", "Beyond.Gameplay.Core.AbilitySystemData.UIData" },
                    { "showBigHeadBar", local.ReadBool32("uiData.showBigHeadBar") },
                    { "useSpecificDamageTextParam", local.ReadBool32("uiData.useSpecificDamageTextParam") },
                    { "damageTextRelated", ReadAbilitySystemDamageTextData(local, "uiData.damageTextRelated") },
                    { "overrideHeadBarDeltaTowardCamera", local.ReadBool32("uiData.overrideHeadBarDeltaTowardCamera") },
                    { "headBarDeltaTowardCamera", local.ReadFloat("uiData.headBarDeltaTowardCamera") },
                    { "headBar2DOffset", ReadPayloadVector2(local, "uiData.headBar2DOffset") },
                    { "useHeadBarGuideLine", local.ReadBool32("uiData.useHeadBarGuideLine") },
                    { "heightInRangeNoFollow", local.ReadBool32("uiData.heightInRangeNoFollow") },
                    { "heightRange", ReadPayloadVector2(local, "uiData.heightRange") },
                    { "heightFollowMountPoint", BuildPayloadHash32(local.ReadInt32("uiData.heightFollowMountPoint")) },
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

        private static OrderedDictionary ReadAbilitySystemDamageTextData(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "mainChrDmgTxtSpawnOffset", ReadPayloadVector2(reader, $"{fieldName}.mainChrDmgTxtSpawnOffset") },
                { "mainChrDmgTxtMoveSpawnOffset", ReadPayloadVector2(reader, $"{fieldName}.mainChrDmgTxtMoveSpawnOffset") },
                { "mainChrDmgTxtMaxMoveNum", reader.ReadInt32($"{fieldName}.mainChrDmgTxtMaxMoveNum") },
                { "mainChrDmgTxtMoveSpawnWaitTime", reader.ReadFloat($"{fieldName}.mainChrDmgTxtMoveSpawnWaitTime") },
                { "guardDmgTxtSpawnOffset", ReadPayloadVector2(reader, $"{fieldName}.guardDmgTxtSpawnOffset") },
                { "guardDmgTxtSpawnAreaSize", ReadPayloadVector2(reader, $"{fieldName}.guardDmgTxtSpawnAreaSize") },
                { "immuneTxtSpawnOffset", ReadPayloadVector2(reader, $"{fieldName}.immuneTxtSpawnOffset") },
                { "immuneTxtSpawnAreaSize", ReadPayloadVector2(reader, $"{fieldName}.immuneTxtSpawnAreaSize") },
                { "immuneTxtCooldown", reader.ReadFloat($"{fieldName}.immuneTxtCooldown") },
            };
        }

        private static bool TryReadAbilitySystemBuffInputLists(
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
                    { "dashBuff", ReadAbilitySystemBuffInputList(local, "dashBuff", 8) },
                    { "buffDuringPoiseExist", ReadAbilitySystemBuffInputList(local, "buffDuringPoiseExist", 8) },
                    { "buffDuringZeroPoise", ReadAbilitySystemBuffInputList(local, "buffDuringZeroPoise", 8) },
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

        private static OrderedDictionary ReadAbilitySystemBuffInputList(
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

            var entries = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                entries.Add(ReadAbilitySystemBuffInput(reader, $"{fieldName}[{i}]"));
            }

            return new OrderedDictionary
            {
                { "count", count },
                { "entries", entries },
            };
        }

        private static OrderedDictionary ReadAbilitySystemBuffInput(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "layout", "Beyond.Gameplay.Core.BuffInput" },
                { "buffId", reader.ReadAlignedAsciiString($"{fieldName}.buffId") },
                { "assignBlackboard", reader.ReadBool32($"{fieldName}.assignBlackboard") },
                { "assignItems", ReadAbilitySystemBuffAssignItemList(reader, $"{fieldName}.assignItems", 16) },
            };
        }

        private static OrderedDictionary ReadAbilitySystemBuffAssignItemList(
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

            var entries = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                entries.Add(ReadAbilitySystemBuffAssignItem(reader, $"{fieldName}[{i}]"));
            }

            return new OrderedDictionary
            {
                { "count", count },
                { "entries", entries },
            };
        }

        private static OrderedDictionary ReadAbilitySystemBuffAssignItem(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "targetKey", reader.ReadAlignedAsciiString($"{fieldName}.targetKey") },
                { "inputValueKey", reader.ReadAlignedAsciiString($"{fieldName}.inputValueKey") },
                { "useDirectValue", reader.ReadBool32($"{fieldName}.useDirectValue") },
                { "directValueType", BuildPayloadHash32(reader.ReadInt32($"{fieldName}.directValueType")) },
                { "numericValue", reader.ReadFloat($"{fieldName}.numericValue") },
                { "stringValue", reader.ReadAlignedAsciiString($"{fieldName}.stringValue") },
            };
        }

        private static bool TryReadAbilitySystemPostBuffFields(
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
                    { "plungingAttackData", ReadAbilitySystemPlungingAttackData(local) },
                    { "battleRootData", ReadAbilitySystemBattleRootData(local) },
                    { "poiseBrokenEndTime", local.ReadFloat("poiseBrokenEndTime") },
                    { "poiseKnotBreakImmobilizeTime", local.ReadFloat("poiseKnotBreakImmobilizeTime") },
                    { "playPoiseBrokenEffect", local.ReadBool32("playPoiseBrokenEffect") },
                    { "unlockAfterOutScreen", local.ReadBool32("unlockAfterOutScreen") },
                    { "overrideMarkTargetDistance", local.ReadBool32("overrideMarkTargetDistance") },
                    { "customMarkTargetDistance", local.ReadFloat("customMarkTargetDistance") },
                    { "overrideMarkTargetHeight", local.ReadBool32("overrideMarkTargetHeight") },
                    { "customMarkTargetHeight", local.ReadFloat("customMarkTargetHeight") },
                    { "accurateMarkTargetDistance", local.ReadBool32("accurateMarkTargetDistance") },
                    { "defaultHitEffect", local.ReadAlignedAsciiString("defaultHitEffect") },
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

        private static OrderedDictionary ReadAbilitySystemPlungingAttackData(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "layout", "Beyond.Gameplay.Core.AbilitySystemData.PlungingAttackData" },
                { "startDuration", reader.ReadFloat("plungingAttackData.startDuration") },
                { "endDuration", reader.ReadFloat("plungingAttackData.endDuration") },
                { "enableOverridePlungingAttackDownSpeed", reader.ReadBool32("plungingAttackData.enableOverridePlungingAttackDownSpeed") },
                { "overridePlungingAttackDownSpeed", reader.ReadFloat("plungingAttackData.overridePlungingAttackDownSpeed") },
            };
        }

        private static OrderedDictionary ReadAbilitySystemBattleRootData(ManagedReferencePayloadReader reader)
        {
            return new OrderedDictionary
            {
                { "layout", "Beyond.Gameplay.Core.AbilitySystemData.BattleRootData" },
                { "overrideBattleRoot", reader.ReadBool32("battleRootData.overrideBattleRoot") },
                { "rootMountPoint", ReadAbilitySystemMountPoint(reader, "battleRootData.rootMountPoint") },
            };
        }

        private static OrderedDictionary ReadAbilitySystemMountPoint(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return ReadPayloadSparseNamedEnum32(
                reader,
                fieldName,
                true,
                (0, "None"),
                (1, "HeadBar"),
                (2, "FootBar"),
                (3, "LockPoint"),
                (4, "HeadStatus"),
                (5, "DmgTxtSpawnPoint")
            );
        }

        private static bool TryReadAbilitySystemEntityBlackboardSection(
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
                    { "entityBlackboard", ReadAbilitySystemEntityBlackboard(local, "entityBlackboard", 64) },
                    { "bakedMeshPoints", ReadAbilitySystemBakedMeshPointsDictionary(local, "bakedMeshPoints", 64, 4096) },
                    { "bakedMeshPointBonePathList", ReadPayloadStringList(local, "bakedMeshPointBonePathList", 4096) },
                    { "extraShapesData", ReadPayloadEmptyCountObject(local, "extraShapesData") },
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

        private static OrderedDictionary ReadAbilitySystemEntityBlackboard(
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

            var entries = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                entries.Add(ReadAbilitySystemBlackboardDataPair(reader, $"{fieldName}[{i}]"));
            }

            return new OrderedDictionary
            {
                { "count", count },
                { "entries", entries },
            };
        }

        private static OrderedDictionary ReadFootRippleEntryList(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxCount
        )
        {
            const int recordByteCount = 12;
            var count = reader.ReadInt32($"{fieldName}.count");
            if (count < 0 || count > maxCount || count > reader.Remaining / recordByteCount)
            {
                throw new InvalidDataException($"invalid FootRippleEntry count {count} for {fieldName}");
            }

            var entries = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                entries.Add(new OrderedDictionary
                {
                    { "layout", "Beyond.Gameplay.View.FootRippleEntry" },
                    { "mountPoint", BuildPayloadHash32(reader.ReadInt32($"{fieldName}[{i}].mountPoint")) },
                    { "footWeightCurveHash", BuildPayloadHash32(reader.ReadInt32($"{fieldName}[{i}].footWeightCurveHash")) },
                    { "rippleSize", reader.ReadFloat($"{fieldName}[{i}].rippleSize") },
                });
            }

            return new OrderedDictionary
            {
                { "layout", "List<Beyond.Gameplay.View.FootRippleEntry>" },
                { "count", count },
                { "entries", entries },
            };
        }
        private static OrderedDictionary ReadAbilitySystemBakedMeshPointsDictionary(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxCount,
            int maxPointsPerList
        )
        {
            var keyCount = reader.ReadInt32($"{fieldName}.keys.count");
            if (keyCount < 0 || keyCount > maxCount)
            {
                throw new InvalidDataException($"invalid key count {keyCount} for {fieldName}");
            }

            var keys = new List<string>(keyCount);
            for (var i = 0; i < keyCount; i++)
            {
                keys.Add(reader.ReadAlignedAsciiString($"{fieldName}.keys[{i}]"));
            }

            var valueCount = reader.ReadInt32($"{fieldName}.values.count");
            if (valueCount != keyCount)
            {
                throw new InvalidDataException($"mismatched key/value counts {keyCount}/{valueCount} for {fieldName}");
            }

            var values = new List<OrderedDictionary>(valueCount);
            for (var i = 0; i < valueCount; i++)
            {
                values.Add(ReadAbilitySystemBakedMeshPointList(reader, $"{fieldName}.values[{i}]", maxPointsPerList));
            }

            var entries = new List<OrderedDictionary>(keyCount);
            for (var i = 0; i < keyCount; i++)
            {
                entries.Add(new OrderedDictionary
                {
                    { "key", keys[i] },
                    { "value", values[i] },
                });
            }

            return new OrderedDictionary
            {
                { "layout", "SerializeFieldDictionary<string, Beyond.Gameplay.Core.AbilitySystemData.BakedMeshPointList>" },
                { "keys", new OrderedDictionary
                    {
                        { "count", keyCount },
                        { "entries", keys },
                    }
                },
                { "values", new OrderedDictionary
                    {
                        { "count", valueCount },
                        { "entries", values },
                    }
                },
                { "entries", entries },
            };
        }

        private static OrderedDictionary ReadAbilitySystemBakedMeshPointList(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxCount
        )
        {
            const int recordByteCount = 28;
            var count = reader.ReadInt32($"{fieldName}.pointList.count");
            if (count < 0 || count > maxCount || count > reader.Remaining / recordByteCount)
            {
                throw new InvalidDataException($"invalid BakedMeshPoint count {count} for {fieldName}");
            }

            var entries = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                entries.Add(ReadAbilitySystemBakedMeshPoint(reader, $"{fieldName}.pointList[{i}]"));
            }

            return new OrderedDictionary
            {
                { "layout", "Beyond.Gameplay.Core.AbilitySystemData.BakedMeshPointList" },
                { "count", count },
                { "entries", entries },
            };
        }

        private static OrderedDictionary ReadAbilitySystemBakedMeshPoint(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "layout", "Beyond.Gameplay.Core.AbilitySystemData.BakedMeshPoint" },
                { "battleShapePointOffset", ReadPayloadVector3(reader, $"{fieldName}.battleShapePointOffset") },
                { "bonePathIndex", reader.ReadInt32($"{fieldName}.bonePathIndex") },
                { "meshPointOffset", ReadPayloadVector3(reader, $"{fieldName}.meshPointOffset") },
            };
        }
        private static OrderedDictionary ReadAbilitySystemBlackboardDataPair(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "layout", "Beyond.Blackboard.DataPair" },
                { "key", reader.ReadAlignedAsciiString($"{fieldName}.key") },
                { "valueDouble", reader.ReadDouble($"{fieldName}.valueDouble") },
                { "valueStr", reader.ReadAlignedAsciiString($"{fieldName}.valueStr") },
                { "isDynamic", reader.ReadBool32($"{fieldName}.isDynamic") },
            };
        }

        private static bool TryReadAbilitySystemSkillCameraConfigSection(
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
                    { "skillCameraConfig", ReadAbilitySystemSkillCameraConfigDictionary(local, "skillCameraConfig", 16) },
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

        private static OrderedDictionary ReadAbilitySystemSkillCameraConfigDictionary(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxCount
        )
        {
            var keyCount = reader.ReadInt32($"{fieldName}.keys.count");
            if (keyCount < 0 || keyCount > maxCount)
            {
                throw new InvalidDataException($"invalid key count {keyCount} for {fieldName}");
            }

            var keys = new List<string>(keyCount);
            for (var i = 0; i < keyCount; i++)
            {
                keys.Add(reader.ReadAlignedAsciiString($"{fieldName}.keys[{i}]"));
            }

            var valueCount = reader.ReadInt32($"{fieldName}.values.count");
            if (valueCount != keyCount)
            {
                throw new InvalidDataException($"mismatched key/value counts {keyCount}/{valueCount} for {fieldName}");
            }

            var values = new List<OrderedDictionary>(valueCount);
            for (var i = 0; i < valueCount; i++)
            {
                values.Add(ReadAbilitySystemSkillCameraConfig(reader, $"{fieldName}.values[{i}]"));
            }

            var entries = new List<OrderedDictionary>(keyCount);
            for (var i = 0; i < keyCount; i++)
            {
                entries.Add(new OrderedDictionary
                {
                    { "key", keys[i] },
                    { "value", values[i] },
                });
            }

            return new OrderedDictionary
            {
                { "layout", "SerializeFieldDictionary<string, Beyond.Gameplay.Core.SkillCameraConfig>" },
                { "keys", new OrderedDictionary
                    {
                        { "count", keyCount },
                        { "entries", keys },
                    }
                },
                { "values", new OrderedDictionary
                    {
                        { "count", valueCount },
                        { "entries", values },
                    }
                },
                { "entries", entries },
            };
        }

        private static OrderedDictionary ReadAbilitySystemSkillCameraConfig(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "layout", "Beyond.Gameplay.Core.SkillCameraConfig" },
                { "clip", ReadPayloadPPtr(reader, $"{fieldName}.clip") },
                { "clipPathHash", BuildPayloadHash64(reader.ReadInt64($"{fieldName}.clipPathHash")) },
                { "collideShapeList", ReadAbilitySystemSkillCameraCollideShapeList(reader, $"{fieldName}.collideShapeList", 8) },
            };
        }

        private static OrderedDictionary ReadAbilitySystemSkillCameraCollideShapeList(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxCount
        )
        {
            const int recordByteCount = 50 * 4;
            var count = reader.ReadInt32($"{fieldName}.count");
            if (count < 0 || count > maxCount || count > reader.Remaining / recordByteCount)
            {
                throw new InvalidDataException($"invalid count {count} for {fieldName}");
            }

            var entries = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                var recordStart = reader.Position;
                var entry = ReadAbilitySystemSkillCameraShapeData(reader, $"{fieldName}[{i}]");
                var consumed = reader.Position - recordStart;
                if (consumed != recordByteCount)
                {
                    throw new InvalidDataException($"unexpected ShapeData byte count {consumed} for {fieldName}[{i}]");
                }
                entries.Add(entry);
            }

            return new OrderedDictionary
            {
                { "layout", "List<Beyond.Gameplay.Core.Selector.HitBoxFinder.ShapeData>" },
                { "count", count },
                { "entries", entries },
            };
        }

        private static OrderedDictionary ReadAbilitySystemSkillCameraShapeData(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return new OrderedDictionary
            {
                { "layout", "Beyond.Gameplay.Core.Selector.HitBoxFinder.ShapeData" },
                { "shapeType", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.shapeType", false, (0, "Box"), (2, "Capsule"), (4, "Sphere")) },
                { "positionRef", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.positionRef", false, (0, "OwnerMountPoint"), (2, "InputCenter")) },
                { "posRefMP", ReadAbilitySystemShapeMountPoint(reader, $"{fieldName}.posRefMP") },
                { "directionRef", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.directionRef", false, (0, "OwnerForward"), (2, "OwnerMountPoint"), (4, "InputDirection")) },
                { "dirRefMountPoint", ReadAbilitySystemShapeMountPoint(reader, $"{fieldName}.dirRefMountPoint") },
                { "centerOffset", ReadAbilitySystemBlackboardVector3(reader, $"{fieldName}.centerOffset") },
                { "eulerAngle", ReadAbilitySystemBlackboardVector3(reader, $"{fieldName}.eulerAngle") },
                { "size", ReadAbilitySystemBlackboardVector3(reader, $"{fieldName}.size") },
                { "radius", ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.radius") },
                { "height", ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.height") },
                { "limitAngle", reader.ReadBool32($"{fieldName}.limitAngle") },
                { "angle", ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.angle") },
                { "limitHeight", reader.ReadBool32($"{fieldName}.limitHeight") },
                { "maxHeight", ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.maxHeight") },
                { "useDirection", reader.ReadBool32($"{fieldName}.useDirection") },
                { "castDirection", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.castDirection", false, (0, "ZForward"), (2, "ZBackward"), (4, "XForward"), (6, "XBackward"), (8, "YForward"), (10, "YBackward")) },
                { "enablePreview", reader.ReadBool32($"{fieldName}.enablePreview") },
                { "hitEffectTowardsType", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.hitEffectTowardsType", false, (0, "TowardsAttacker"), (2, "TowardsHitBoxCenter")) },
            };
        }

        private static OrderedDictionary ReadAbilitySystemBlackboardVector3(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            var x = ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.x");
            var y = ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.y");
            var z = ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.z");
            return new OrderedDictionary
            {
                { "layout", "Beyond.Blackboard.BlackboardVector3" },
                { "x", x },
                { "y", y },
                { "z", z },
                { "valueCandidate", new OrderedDictionary
                    {
                        { "x", x["valueFloatCandidate"] },
                        { "y", y["valueFloatCandidate"] },
                        { "z", z["valueFloatCandidate"] },
                    }
                },
            };
        }

        private static OrderedDictionary ReadAbilitySystemBlackboardDouble(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            var start = reader.Position;
            var local = new ManagedReferencePayloadReader(reader.RawData, reader.Position, reader.Remaining);
            try
            {
                var useBlackboardKey = local.ReadBool32($"{fieldName}.useBlackboardKey");
                var value = local.ReadFloat($"{fieldName}.value");
                var blackboardKey = local.ReadAlignedAsciiString($"{fieldName}.blackboardKey");
                reader.SetPosition(local.Position);
                return new OrderedDictionary
                {
                    { "layout", "Beyond.Blackboard.BlackboardDouble" },
                    { "serializationShape", "bool-float-key" },
                    { "layoutNote", "IL2CPP metadata-backed shape: bool32 useBlackboardKey, float32 value, aligned blackboardKey string. Empty keys serialize as a three-word wrapper." },
                    { "useBlackboardKey", useBlackboardKey },
                    { "value", value },
                    { "blackboardKey", blackboardKey },
                    { "valueFloatCandidate", value },
                };
            }
            catch (InvalidDataException)
            {
                reader.SetPosition(start);
                var rawWords = ReadPayloadRawInt32Words(reader, $"{fieldName}.rawWords", 3);
                var rawValue = rawWords[1]["value"] is int word ? word : 0;
                var value = BitConverter.Int32BitsToSingle(rawValue);
                return new OrderedDictionary
                {
                    { "layout", "Beyond.Blackboard.BlackboardDouble" },
                    { "serializationShape", "raw-three-word" },
                    { "layoutNote", "Fallback wrapper observed inside ProjectileComponentData MoveModeData records: three int32 words, with the middle word exposed as a float candidate. The metadata-backed bool-float-key shape did not validate at this offset." },
                    { "rawWords", rawWords },
                    { "valueFloatCandidate", value },
                };
            }
        }
        private static OrderedDictionary ReadAbilitySystemBlackboardInt(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            var rawWords = ReadPayloadRawInt32Words(reader, $"{fieldName}.rawWords", 3);
            var value = rawWords[1]["value"] is int word ? word : 0;
            return new OrderedDictionary
            {
                { "layout", "Beyond.Blackboard.BlackboardInt" },
                { "layoutNote", "serialized as three int32 words in observed ProjectileComponentData rows; middle word is exposed as an int candidate" },
                { "rawWords", rawWords },
                { "valueIntCandidate", value },
            };
        }

        private static OrderedDictionary ReadAbilitySystemShapeMountPoint(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return ReadPayloadSparseNamedEnum32(
                reader,
                fieldName,
                true,
                (0, "None"),
                (2, "HeadBar"),
                (4, "FootBar"),
                (6, "LockPoint"),
                (8, "HeadStatus"),
                (10, "DmgTxtSpawnPoint"),
                (2000, "HeadLabel")
            );
        }
        private static OrderedDictionary ReadPayloadSparseNamedEnum32(
            ManagedReferencePayloadReader reader,
            string fieldName,
            bool allowUnknown,
            params (int Value, string Name)[] names
        )
        {
            var value = reader.ReadInt32(fieldName);
            foreach (var entry in names)
            {
                if (entry.Value == value)
                {
                    return new OrderedDictionary
                    {
                        { "value", value },
                        { "name", entry.Name },
                    };
                }
            }

            if (allowUnknown)
            {
                return BuildPayloadHash32(value);
            }

            throw new InvalidDataException($"invalid enum32 {value} in {fieldName}");
        }

        private static bool TryReadAbilitySystemPostCameraFields(
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
                    { "layoutNote", "IL2CPP metadata lists overrideDeadEffect before deadEffect, but focused Unity payloads start directly with deadEffect; overrideDeadEffect is not emitted here until a validated payload variant proves it." },
                    { "deadEffect", ReadAbilitySystemEffectActionCfg(local, "deadEffect") },
                    { "effectScale", local.ReadFloat("effectScale") },
                    { "isPlayHitFlash", local.ReadBool32("isPlayHitFlash") },
                    { "hitFlashAsset", local.ReadAlignedAsciiString("hitFlashAsset") },
                    { "healthType", ReadPayloadSparseNamedEnum32(local, "healthType", false, (0, "Normal"), (2, "Independent")) },
                    { "preloadAbilityEntities", ReadPayloadStringIntDictionary(local, "preloadAbilityEntities", 8) },
                    { "maxPotentialEffectBuffId", local.ReadAlignedAsciiString("maxPotentialEffectBuffId") },
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

        private static OrderedDictionary ReadAbilitySystemEffectActionCfg(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            var start = reader.Position;
            var data = new OrderedDictionary
            {
                { "$partial", true },
                { "$inferred", true },
                { "layout", "Beyond.Gameplay.EffectActionCfg" },
                { "layoutNote", "Unity MonoBehaviour payload follows IL2CPP metadata field order except centerOffset is omitted in observed AbilitySystemData rows. BlackboardDouble internals remain exposed as raw 3-word wrappers." },
                { "observedPayloadStatus", "fixed 107-word AbilitySystemData EffectActionCfg variant consumed by this reader" },
                { "partialReasons", new List<string>
                    {
                        "BlackboardDouble internals remain emitted as raw 3-word wrappers",
                        "IL2CPP metadata lists centerOffset, but observed AbilitySystemData rows omit it",
                        "AbilitySystemData overrideDeadEffect variant is not proven in focused samples",
                    }
                },
                { "omittedSerializedFields", new List<string> { "centerOffset" } },
                { "fxType", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.fxType", true, (0, "Normal"), (2, "Alert"), (4, "BottomScreen"), (6, "WeaponVfx")) },
                { "effectName", reader.ReadAlignedAsciiString($"{fieldName}.effectName") },
                { "guardEffect", reader.ReadBool32($"{fieldName}.guardEffect") },
                { "forceGuardEffect", reader.ReadBool32($"{fieldName}.forceGuardEffect") },
                { "isCenterChangeLod", reader.ReadBool32($"{fieldName}.isCenterChangeLod") },
                { "useScaleBB", reader.ReadBool32($"{fieldName}.useScaleBB") },
                { "scale", ReadPayloadVector3(reader, $"{fieldName}.scale") },
                { "scaleBB", ReadAbilitySystemBlackboardVector3(reader, $"{fieldName}.scaleBB") },
                { "useLengthBB", reader.ReadBool32($"{fieldName}.useLengthBB") },
                { "lengthBB", ReadAbilitySystemBlackboardDouble(reader, $"{fieldName}.lengthBB") },
                { "releaseByAction", reader.ReadBool32($"{fieldName}.releaseByAction") },
                { "ignoreOwnerTimeScale", reader.ReadBool32($"{fieldName}.ignoreOwnerTimeScale") },
                { "interruptTime", reader.ReadFloat($"{fieldName}.interruptTime") },
                { "terrainPrefab", reader.ReadBool32($"{fieldName}.terrainPrefab") },
                { "effectPosData", ReadAbilitySystemTerrainEffectDataArray(reader, $"{fieldName}.effectPosData") },
                { "isShowInDialog", reader.ReadBool32($"{fieldName}.isShowInDialog") },
                { "isLimitEffectCount", reader.ReadBool32($"{fieldName}.isLimitEffectCount") },
                { "limitCount", reader.ReadInt32($"{fieldName}.limitCount") },
                { "protectTime", reader.ReadFloat($"{fieldName}.protectTime") },
                { "limitTime", reader.ReadFloat($"{fieldName}.limitTime") },
                { "limitKey", reader.ReadAlignedAsciiString($"{fieldName}.limitKey") },
                { "assetOnlyAffectModelRoot", reader.ReadBool32($"{fieldName}.assetOnlyAffectModelRoot") },
                { "isUltimateShow", reader.ReadBool32($"{fieldName}.isUltimateShow") },
                { "visibleWithEntity", reader.ReadBool32($"{fieldName}.visibleWithEntity") },
                { "visibleWithEntityType", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.visibleWithEntityType", true, (0, "Source"), (2, "Target")) },
                { "moveType", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.moveType", true, (0, "Stationary"), (2, "FollowTarget"), (4, "FollowCamera"), (6, "FollowSlot")) },
                { "positionRef", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.positionRef", true, (0, "Target"), (2, "Source")) },
                { "grounded", reader.ReadBool32($"{fieldName}.grounded") },
                { "followGrounded", reader.ReadBool32($"{fieldName}.followGrounded") },
                { "followGroundedMaxDistance", reader.ReadFloat($"{fieldName}.followGroundedMaxDistance") },
                { "followHideTarget", reader.ReadBool32($"{fieldName}.followHideTarget") },
                { "visibleWhenHideTarget", reader.ReadBool32($"{fieldName}.visibleWhenHideTarget") },
                { "slotIndex", reader.ReadInt32($"{fieldName}.slotIndex") },
                { "useWeaponMountPoint", reader.ReadBool32($"{fieldName}.useWeaponMountPoint") },
                { "mountPoint", ReadAbilitySystemEffectMountPoint(reader, $"{fieldName}.mountPoint") },
                { "useAccurateMp", reader.ReadBool32($"{fieldName}.useAccurateMp") },
                { "isClothMountPoint", reader.ReadBool32($"{fieldName}.isClothMountPoint") },
                { "weaponIndex", reader.ReadInt32($"{fieldName}.weaponIndex") },
                { "weaponMountPoint", ReadAbilitySystemWeaponMountPoint(reader, $"{fieldName}.weaponMountPoint") },
                { "showHideWithWeapon", reader.ReadBool32($"{fieldName}.showHideWithWeapon") },
                { "offsetDir", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.offsetDir", true, (0, "Self"), (2, "Source"), (4, "Target"), (6, "SelfToSource"), (8, "SelfToTarget"), (10, "SourceToTarget"), (12, "Camera")) },
                { "offsetDirRevert", reader.ReadBool32($"{fieldName}.offsetDirRevert") },
                { "usePositionOffsetBB", reader.ReadBool32($"{fieldName}.usePositionOffsetBB") },
                { "positionOffset", ReadPayloadVector3(reader, $"{fieldName}.positionOffset") },
                { "positionOffsetBB", ReadAbilitySystemBlackboardVector3(reader, $"{fieldName}.positionOffsetBB") },
                { "useTargetRotation", reader.ReadBool32($"{fieldName}.useTargetRotation") },
                { "scaleWithTargetSize", reader.ReadBool32($"{fieldName}.scaleWithTargetSize") },
                { "fxSize", reader.ReadFloat($"{fieldName}.fxSize") },
                { "unpackPosDelayFrame", reader.ReadInt32($"{fieldName}.unpackPosDelayFrame") },
                { "unpackFollowTargetOnRelease", reader.ReadBool32($"{fieldName}.unpackFollowTargetOnRelease") },
                { "rotType", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.rotType", true, (0, "Stationary"), (2, "FollowTarget")) },
                { "rotRef", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.rotRef", true, (0, "Target"), (2, "Source")) },
                { "directionRef", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.directionRef", true, (1, "None"), (0, "Target"), (2, "Source"), (4, "SourceToTarget"), (6, "TargetToSource"), (8, "CurrentPosToTarget"), (10, "CurrentPosToInputTarget"), (12, "CurPosToCamera"), (14, "CameraForward")) },
                { "rotUseWeaponMountPoint", reader.ReadBool32($"{fieldName}.rotUseWeaponMountPoint") },
                { "rotMountPoint", ReadAbilitySystemEffectMountPoint(reader, $"{fieldName}.rotMountPoint") },
                { "rotWeaponIndex", reader.ReadInt32($"{fieldName}.rotWeaponIndex") },
                { "rotWeaponMountPoint", ReadAbilitySystemWeaponMountPoint(reader, $"{fieldName}.rotWeaponMountPoint") },
                { "revertDir", reader.ReadBool32($"{fieldName}.revertDir") },
                { "useSelfRotationBB", reader.ReadBool32($"{fieldName}.useSelfRotationBB") },
                { "selfRotation", ReadPayloadVector3(reader, $"{fieldName}.selfRotation") },
                { "selfRotationBB", ReadAbilitySystemBlackboardVector3(reader, $"{fieldName}.selfRotationBB") },
                { "lockYRotation", reader.ReadBool32($"{fieldName}.lockYRotation") },
                { "unpackRotDelayFrame", reader.ReadInt32($"{fieldName}.unpackRotDelayFrame") },
                { "unpackFollowTargetRotOnRelease", reader.ReadBool32($"{fieldName}.unpackFollowTargetRotOnRelease") },
                { "weaponVfxKey", reader.ReadAlignedAsciiString($"{fieldName}.weaponVfxKey") },
                { "weaponVfxIndex", reader.ReadInt32($"{fieldName}.weaponVfxIndex") },
                { "weaponVfxPersistent", reader.ReadBool32($"{fieldName}.weaponVfxPersistent") },
                { "alertType", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.alertType", true, (0, "Decal"), (2, "Particle")) },
                { "animateAlert", reader.ReadBool32($"{fieldName}.animateAlert") },
                { "alertAnimateDuration", reader.ReadFloat($"{fieldName}.alertAnimateDuration") },
                { "isAlertAnimateReverse", reader.ReadBool32($"{fieldName}.isAlertAnimateReverse") },
                { "angle", reader.ReadFloat($"{fieldName}.angle") },
                { "hollow", reader.ReadFloat($"{fieldName}.hollow") },
                { "modifyType", ReadPayloadSparseNamedEnum32(reader, $"{fieldName}.modifyType", true, (0, "StartLifeTime")) },
                { "value", reader.ReadFloat($"{fieldName}.value") },
            };
            data["serializedWordCount"] = (reader.Position - start) / 4;
            return data;
        }

        private static OrderedDictionary ReadAbilitySystemTerrainEffectDataArray(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            var count = reader.ReadInt32($"{fieldName}.count");
            if (count < 0 || count > 64)
            {
                throw new InvalidDataException($"invalid TerrainEffectData count {count} in {fieldName}");
            }

            var entries = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                entries.Add(new OrderedDictionary
                {
                    { "tag", reader.ReadAlignedAsciiString($"{fieldName}[{i}].tag") },
                    { "effectName", reader.ReadAlignedAsciiString($"{fieldName}[{i}].effectName") },
                });
            }

            return new OrderedDictionary
            {
                { "layout", "Beyond.Gameplay.TerrainEffectData[]" },
                { "count", count },
                { "entries", entries },
            };
        }

        private static OrderedDictionary ReadAbilitySystemEffectMountPoint(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return ReadPayloadSparseNamedEnum32(
                reader,
                fieldName,
                true,
                (0, "None"),
                (2, "HeadBar"),
                (4, "FootBar"),
                (6, "LockPoint"),
                (8, "HeadStatus"),
                (10, "DmgTxtSpawnPoint"),
                (2000, "HeadLabel")
            );
        }

        private static OrderedDictionary ReadAbilitySystemWeaponMountPoint(
            ManagedReferencePayloadReader reader,
            string fieldName
        )
        {
            return ReadPayloadSparseNamedEnum32(
                reader,
                fieldName,
                true,
                (0, "Root"),
                (2, "Muzzle"),
                (200, "Custom0"),
                (202, "Custom1"),
                (204, "Custom2"),
                (206, "Custom3")
            );
        }

        private static OrderedDictionary ReadPayloadStringIntDictionary(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxCount
        )
        {
            var keyCount = reader.ReadInt32($"{fieldName}.keys.count");
            if (keyCount < 0 || keyCount > maxCount)
            {
                throw new InvalidDataException($"invalid key count {keyCount} for {fieldName}");
            }

            var keys = new List<string>(keyCount);
            for (var i = 0; i < keyCount; i++)
            {
                keys.Add(reader.ReadAlignedAsciiString($"{fieldName}.keys[{i}]"));
            }

            var valueCount = reader.ReadInt32($"{fieldName}.values.count");
            if (valueCount != keyCount)
            {
                throw new InvalidDataException($"mismatched key/value counts {keyCount}/{valueCount} for {fieldName}");
            }

            var values = new List<int>(valueCount);
            for (var i = 0; i < valueCount; i++)
            {
                values.Add(reader.ReadInt32($"{fieldName}.values[{i}]"));
            }

            var entries = new List<OrderedDictionary>(keyCount);
            for (var i = 0; i < keyCount; i++)
            {
                entries.Add(new OrderedDictionary
                {
                    { "key", keys[i] },
                    { "value", values[i] },
                });
            }

            return new OrderedDictionary
            {
                { "layout", "SerializeFieldDictionary<string, int>" },
                { "keys", new OrderedDictionary
                    {
                        { "count", keyCount },
                        { "entries", keys },
                    }
                },
                { "values", new OrderedDictionary
                    {
                        { "count", valueCount },
                        { "entries", values },
                    }
                },
                { "entries", entries },
            };
        }

        private static bool TryReadAbilitySystemSkillDataBundle(
            ManagedReferencePayloadReader reader,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid,
            out OrderedDictionary data
        )
        {
            data = null;
            var local = new ManagedReferencePayloadReader(reader.RawData, reader.Position, reader.Remaining);
            try
            {
                data = new OrderedDictionary
                {
                    { "$decoded", true },
                    { "layout", "Beyond.Gameplay.Core.SkillDataBundle" },
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
                };

                var comboSkillConditionCount = local.ReadInt32("skillDataBundle.comboSkillConditions.count");
                data["comboSkillConditions"] = ReadAbilitySystemComboSkillConditionList(
                    local,
                    "skillDataBundle.comboSkillConditions",
                    comboSkillConditionCount,
                    recoveredByRid
                );
                data["comboSkillId"] = local.ReadAlignedAsciiString("skillDataBundle.comboSkillId");
                data["comboSkillSpecialNodeName"] = local.ReadAlignedAsciiString("skillDataBundle.comboSkillSpecialNodeName");
                data["defaultCmdMapping"] = ReadAbilitySystemBattleCommandStringDictionary(local, "skillDataBundle.defaultCmdMapping", 8);
                data["layoutNote"] = "SkillDataBundle fields are consumed through defaultCmdMapping. Nested comboSkillConditions may still contain partial action/condition payloads, and later AbilitySystemData fields are decoded by the parent AbilitySystemData reader.";

                reader.SetPosition(local.Position);
                return true;
            }
            catch (InvalidDataException)
            {
                data = null;
                return false;
            }
        }

        private static OrderedDictionary ReadAbilitySystemComboSkillConditionList(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int count,
            IReadOnlyDictionary<long, ManagedReferenceHeader> recoveredByRid
        )
        {
            if (count < 0 || count > 64)
            {
                throw new InvalidDataException($"invalid count {count} for {fieldName}");
            }

            var entries = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                var comboSkillEvent = reader.ReadInt32($"{fieldName}[{i}].comboSkillEvent");
                var actionCount = reader.ReadInt32($"{fieldName}[{i}].comboSkillCheckAction.count");
                if (actionCount < 0 || actionCount > 32 || actionCount > reader.Remaining / 8)
                {
                    throw new InvalidDataException($"invalid action count {actionCount} for {fieldName}[{i}]");
                }

                var actions = new List<OrderedDictionary>(actionCount);
                for (var j = 0; j < actionCount; j++)
                {
                    var ridOffset = reader.Position;
                    var rid = reader.ReadInt64($"{fieldName}[{i}].comboSkillCheckAction[{j}]");
                    if (recoveredByRid == null || !recoveredByRid.TryGetValue(rid, out var target))
                    {
                        throw new InvalidDataException($"unresolved managed-reference RID {rid} for {fieldName}[{i}].comboSkillCheckAction[{j}]");
                    }
                    actions.Add(BuildManagedReferenceRidLink(rid, target, ridOffset));
                }

                var onlyExecuteWhenSourceIsMainChar = reader.ReadBool32($"{fieldName}[{i}].comboSkillCheckAction.onlyExecuteWhenSourceIsMainChar");
                var onlyExecuteWhenSourceIsGuard = reader.ReadBool32($"{fieldName}[{i}].comboSkillCheckAction.onlyExecuteWhenSourceIsGuard");
                var comboSkillConditionImmediately = reader.ReadBool32($"{fieldName}[{i}].comboSkillConditionImmediately");

                entries.Add(new OrderedDictionary
                {
                    { "comboSkillEvent", BuildAbilitySystemEvent(comboSkillEvent) },
                    { "comboSkillCheckAction", new OrderedDictionary
                        {
                            { "actionData", new OrderedDictionary
                                {
                                    { "count", actionCount },
                                    { "entries", actions },
                                }
                            },
                            { "onlyExecuteWhenSourceIsMainChar", onlyExecuteWhenSourceIsMainChar },
                            { "onlyExecuteWhenSourceIsGuard", onlyExecuteWhenSourceIsGuard },
                        }
                    },
                    { "comboSkillConditionImmediately", comboSkillConditionImmediately },
                });
            }

            return new OrderedDictionary
            {
                { "count", count },
                { "entries", entries },
            };
        }

        private static OrderedDictionary BuildAbilitySystemEvent(int value)
        {
            var item = BuildPayloadHash32(value);
            switch (value)
            {
                case 9:
                    item["name"] = "OnAddedBuff";
                    break;
                case 12:
                    item["name"] = "OnTakeDamage";
                    break;
                case 13:
                    item["name"] = "OnOutputDamage";
                    break;
                case 21:
                    item["name"] = "OnPoiseZero";
                    break;
                case 60:
                    item["name"] = "OnAfterTakePhysicalInfliction";
                    break;
                case 101:
                    item["name"] = "OnBeforeTakeDamage";
                    break;
                case 102:
                    item["name"] = "OnOutputBuff";
                    break;
                case 121:
                    item["name"] = "OnEnemyBeforeTakeSpellInfliction";
                    break;
                case 151:
                    item["name"] = "OnSetWeakness";
                    break;
                case 204:
                    item["name"] = "OnBuffEndsEarly";
                    break;
                case 205:
                    item["name"] = "OnBeforeAddedBuff";
                    break;
                case 241:
                    item["name"] = "OnPoiseKnotBreak";
                    break;
                case 302:
                    item["name"] = "OnBeforeOutputDamage";
                    break;
            }
            return item;
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

        private static bool TryReadEnemyPartsRootComponentData(
            byte[] rawData,
            int offset,
            int length,
            int prefixWordCount,
            out OrderedDictionary data
        )
        {
            data = null;
            try
            {
                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                data = new OrderedDictionary
                {
                    { "$decoded", true },
                    { "$inferred", true },
                    { "layout", "Beyond.Gameplay.Core.EnemyPartsRootComponentData" },
                    { "layoutVariant", $"prefixWords{prefixWordCount}" },
                    { "offset", offset },
                    { "length", length },
                    { "prefixWords", ReadPayloadRawInt32Words(reader, "prefixWords", prefixWordCount) },
                    { "partName", reader.ReadAlignedAsciiString("partName") },
                    { "partTags", ReadEnemyPartTagList(reader) },
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
        private static bool TryReadEnemyPartsRootComponentDataWithPartIdList(
            byte[] rawData,
            int offset,
            int length,
            out OrderedDictionary data
        )
        {
            data = null;
            try
            {
                var reader = new ManagedReferencePayloadReader(rawData, offset, length);
                data = new OrderedDictionary
                {
                    { "$decoded", true },
                    { "$inferred", true },
                    { "layout", "Beyond.Gameplay.Core.EnemyPartsRootComponentData" },
                    { "layoutVariant", "prefixWords6PartIdList" },
                    { "offset", offset },
                    { "length", length },
                    { "prefixWords", ReadPayloadRawInt32Words(reader, "prefixWords", 6) },
                    { "partIdListEnabled", reader.ReadBool32("partIdListEnabled") },
                    { "partIds", ReadEnemyPartIdList(reader) },
                    { "partName", reader.ReadAlignedAsciiString("partName") },
                    { "partTags", ReadEnemyPartTagList(reader) },
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

        private static List<OrderedDictionary> ReadEnemyPartIdList(ManagedReferencePayloadReader reader)
        {
            var count = reader.ReadInt32("partIds.count");
            if (count < 0 || count > 32 || count > reader.Remaining / 4)
            {
                throw new InvalidDataException($"invalid count {count} for partIds");
            }

            var values = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                values.Add(BuildPayloadHash32(reader.ReadInt32($"partIds[{i}]")));
            }
            return values;
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

        private static string ReadPayloadAlignedAsciiStringWithZeroPadding(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxLength
        )
        {
            var lengthOffset = reader.Position;
            var byteLength = reader.ReadInt32($"{fieldName}.length");
            if (byteLength < 0 || byteLength > maxLength)
            {
                throw new InvalidDataException($"invalid string length {byteLength} in {fieldName}");
            }

            reader.SetPosition(lengthOffset);
            var value = reader.ReadAlignedAsciiString(fieldName);
            var payloadEnd = lengthOffset + 4 + byteLength;
            var alignedEnd = (payloadEnd + 3) & ~3;
            if (alignedEnd > reader.End)
            {
                throw new InvalidDataException($"aligned string {fieldName} passes payload end");
            }
            for (var padOffset = payloadEnd; padOffset < alignedEnd; padOffset++)
            {
                if (reader.RawData[padOffset] != 0)
                {
                    throw new InvalidDataException($"non-zero padding byte at {padOffset} in {fieldName}");
                }
            }

            return value;
        }

        private static string ReadPayloadAlignedUtf8StringWithZeroPadding(
            ManagedReferencePayloadReader reader,
            string fieldName,
            int maxLength
        )
        {
            var lengthOffset = reader.Position;
            var byteLength = reader.ReadInt32($"{fieldName}.length");
            if (byteLength < 0 || byteLength > maxLength)
            {
                throw new InvalidDataException($"invalid string length {byteLength} in {fieldName}");
            }

            reader.SetPosition(lengthOffset);
            var value = reader.ReadAlignedUtf8String(fieldName);
            var payloadEnd = lengthOffset + 4 + byteLength;
            var alignedEnd = (payloadEnd + 3) & ~3;
            if (alignedEnd > reader.End)
            {
                throw new InvalidDataException($"aligned string {fieldName} passes payload end");
            }
            for (var padOffset = payloadEnd; padOffset < alignedEnd; padOffset++)
            {
                if (reader.RawData[padOffset] != 0)
                {
                    throw new InvalidDataException($"non-zero padding byte at {padOffset} in {fieldName}");
                }
            }

            return value;
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

        private static OrderedDictionary ReadPayloadAnimationCurveFloat(ManagedReferencePayloadReader reader, string fieldName)
        {
            var count = reader.ReadInt32($"{fieldName}.keyframes.count");
            if (count < 0 || count > 512 || reader.Remaining < 12 || count > (reader.Remaining - 12) / 28)
            {
                throw new InvalidDataException($"invalid AnimationCurve keyframe count {count} in {fieldName}");
            }

            var keyframes = new List<OrderedDictionary>(count);
            for (var i = 0; i < count; i++)
            {
                keyframes.Add(new OrderedDictionary
                {
                    { "time", reader.ReadFloat($"{fieldName}.keyframes[{i}].time") },
                    { "value", reader.ReadFloat($"{fieldName}.keyframes[{i}].value") },
                    { "inSlope", reader.ReadFloat($"{fieldName}.keyframes[{i}].inSlope") },
                    { "outSlope", reader.ReadFloat($"{fieldName}.keyframes[{i}].outSlope") },
                    { "weightedMode", ReadPayloadNamedEnum32(reader, $"{fieldName}.keyframes[{i}].weightedMode", new[] { "None", "In", "Out", "Both" }) },
                    { "inWeight", reader.ReadFloat($"{fieldName}.keyframes[{i}].inWeight") },
                    { "outWeight", reader.ReadFloat($"{fieldName}.keyframes[{i}].outWeight") },
                });
            }

            return new OrderedDictionary
            {
                { "keyframes", keyframes },
                { "preInfinity", ReadPayloadAnimationCurveWrapMode(reader, $"{fieldName}.preInfinity") },
                { "postInfinity", ReadPayloadAnimationCurveWrapMode(reader, $"{fieldName}.postInfinity") },
                { "rotationOrder", ReadPayloadRotationOrder(reader, $"{fieldName}.rotationOrder") },
            };
        }

        private static OrderedDictionary ReadPayloadAnimationCurveWrapMode(ManagedReferencePayloadReader reader, string fieldName)
        {
            var value = reader.ReadInt32(fieldName);
            if (value != 0 && value != 1 && value != 2 && value != 4 && value != 8)
            {
                throw new InvalidDataException($"invalid AnimationCurve wrap mode {value} in {fieldName}");
            }

            return new OrderedDictionary
            {
                { "value", value },
                { "name", value switch
                    {
                        0 => "Default",
                        1 => "Once",
                        2 => "Loop",
                        4 => "PingPong",
                        8 => "ClampForever",
                        _ => "",
                    }
                },
            };
        }

        private static OrderedDictionary ReadPayloadRotationOrder(ManagedReferencePayloadReader reader, string fieldName)
        {
            var value = reader.ReadInt32(fieldName);
            if (value < 0 || value > 5)
            {
                throw new InvalidDataException($"invalid rotation order {value} in {fieldName}");
            }

            return new OrderedDictionary
            {
                { "value", value },
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
            if (!TryExportFile(exportPath, item, ".fbx", out var fbxExportPath))
                return false;
            if (convert.MeshList.Count == 0)
            {
                return ExportEmptyAnimatorMarker(item, m_Animator, convert, exportPath, "no_mesh");
            }
            if (options.exportMaterials)
            {
                var materialExportPath = Path.Combine(Path.GetDirectoryName(fbxExportPath), "Materials");
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
