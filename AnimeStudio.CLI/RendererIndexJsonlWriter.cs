using System;
using System.Collections.Generic;
using System.Collections;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace AnimeStudio.CLI
{
    /// <summary>Records exact built-in renderer Mesh and ordered Material PPtrs.</summary>
    public sealed class RendererIndexJsonlWriter : IDisposable
    {
        private const int SchemaVersion = 1;
        private readonly string finalPath;
        private readonly string temporaryPath;
        private readonly string inputPath;
        private readonly StreamWriter writer;
        private readonly HashSet<string> emitted = new HashSet<string>(StringComparer.Ordinal);
        private long rendererCount;
        private bool completed;

        private RendererIndexJsonlWriter(FileInfo output, FileInfo input)
        {
            finalPath = Path.GetFullPath(output.FullName);
            temporaryPath = finalPath + ".tmp";
            var directory = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            inputPath = Path.GetFullPath(input.FullName);
            writer = new StreamWriter(new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false), 64 * 1024);
            Current = this;
        }

        public static RendererIndexJsonlWriter Current { get; private set; }

        public static RendererIndexJsonlWriter Open(FileInfo output, FileInfo input)
        {
            if (output == null) return null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (Current != null) throw new InvalidOperationException("A renderer JSONL index writer is already active.");
            return new RendererIndexJsonlWriter(output, input);
        }

        private static object Identity(AnimeStudio.Object asset) => asset == null ? null : new
        {
            source = asset.assetsFile?.originalPath ?? asset.assetsFile?.fullName ?? asset.assetsFile?.fileName,
            pathId = asset.m_PathID,
            type = asset.type.ToString(),
            name = asset.Name,
        };

        private static object Pointer<T>(PPtr<T> pointer, SerializedFile owner) where T : AnimeStudio.Object
        {
            if (pointer == null) return null;
            if (pointer.TryGet(out var target))
            {
                return new
                {
                    fileId = pointer.m_FileID,
                    pathId = pointer.m_PathID,
                    status = "resolved",
                    target = Identity(target),
                    external = (object)null,
                };
            }
            object external = null;
            if (owner != null && pointer.m_FileID > 0 && pointer.m_FileID <= owner.m_Externals.Count)
            {
                var reference = owner.m_Externals[pointer.m_FileID - 1];
                external = new
                {
                    path = reference.pathName,
                    fileName = reference.fileName,
                    guid = reference.guid,
                    type = reference.type,
                };
            }
            return new
            {
                fileId = pointer.m_FileID,
                pathId = pointer.m_PathID,
                status = external == null ? "unresolved" : "external_target_unloaded",
                target = (object)null,
                external,
            };
        }

        private static List<PPtr<AnimeStudio.Object>> GenericPointers(
            OrderedDictionary payload,
            string field,
            SerializedFile assetsFile)
        {
            var result = new List<PPtr<AnimeStudio.Object>>();
            if (payload?[field] is not IEnumerable values) return result;
            foreach (var value in values)
            {
                if (value is not OrderedDictionary pointer) continue;
                try
                {
                    result.Add(new PPtr<AnimeStudio.Object>(
                        Convert.ToInt32(pointer["m_FileID"]),
                        Convert.ToInt64(pointer["m_PathID"]),
                        assetsFile));
                }
                catch (Exception)
                {
                    // A malformed generic pointer makes only this member
                    // unavailable; the row remains useful and fail-closed.
                }
            }
            return result;
        }

        public void WriteLoadedRenderers(IEnumerable<SerializedFile> serializedFiles)
        {
            var files = (serializedFiles ?? Enumerable.Empty<SerializedFile>())
                .Where(file => file != null).ToArray();
            foreach (var gameObject in files.SelectMany(file => file.Objects).OfType<GameObject>())
            {
                Renderer renderer = gameObject.m_MeshRenderer ?? (Renderer)gameObject.m_SkinnedMeshRenderer;
                PPtr<Mesh> meshPointer = gameObject.m_MeshFilter?.m_Mesh;
                if (renderer is SkinnedMeshRenderer skinned) meshPointer = skinned.m_Mesh;
                if (renderer == null || meshPointer == null || meshPointer.m_PathID == 0) continue;
                var key = $"{gameObject.assetsFile?.fileName}\0{gameObject.m_PathID}\0{renderer.m_PathID}";
                if (!emitted.Add(key)) continue;
                writer.WriteLine(JsonConvert.SerializeObject(new
                {
                    kind = "renderer",
                    schemaVersion = SchemaVersion,
                    gameObject = Identity(gameObject),
                    renderer = Identity(renderer),
                    mesh = Pointer(meshPointer, gameObject.assetsFile),
                    materials = renderer.m_Materials.Select(pointer => Pointer(pointer, gameObject.assetsFile)).ToArray(),
                }, Formatting.None));
                rendererCount++;
            }
            foreach (var data in files.SelectMany(file => file.Objects)
                .Where(asset => asset.type == ClassIDType.HGMeshRendererData))
            {
                OrderedDictionary payload;
                try
                {
                    payload = data.ToType();
                }
                catch (Exception)
                {
                    continue;
                }
                var meshes = GenericPointers(payload, "m_Meshes", data.assetsFile);
                var materials = GenericPointers(payload, "m_Materials", data.assetsFile);
                if (meshes.Count == 0 || materials.Count == 0) continue;
                var key = $"hg-data\0{data.assetsFile?.fileName}\0{data.m_PathID}";
                if (!emitted.Add(key)) continue;
                writer.WriteLine(JsonConvert.SerializeObject(new
                {
                    kind = "hgRendererData",
                    schemaVersion = SchemaVersion,
                    data = Identity(data),
                    name = payload?["m_Name"]?.ToString(),
                    meshes = meshes.Select(pointer => Pointer(pointer, data.assetsFile)).ToArray(),
                    materials = materials.Select(pointer => Pointer(pointer, data.assetsFile)).ToArray(),
                    alignmentContract = "parallel_mesh_material_arrays",
                }, Formatting.None));
                rendererCount++;
            }
        }

        public void Complete(bool complete)
        {
            if (completed) return;
            writer.WriteLine(JsonConvert.SerializeObject(new { kind = "summary", schemaVersion = SchemaVersion, complete, input = inputPath, rendererCount }, Formatting.None));
            writer.Flush();
            writer.Dispose();
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(temporaryPath, finalPath);
            completed = true;
        }

        public void Dispose()
        {
            if (!completed)
            {
                writer.Dispose();
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            if (ReferenceEquals(Current, this)) Current = null;
        }
    }
}
