using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace AnimeStudio.CLI
{
    /// <summary>
    /// Records which Unity object produced each exported path: its serialized
    /// file (CAB), the VFS chunk it was read from, and the offset of its bundle
    /// inside that chunk. Wrappers join these slots against the VFS catalogue
    /// to keep only objects from the bundles the client actually loads, without
    /// re-deriving identity from file names or PathID coincidence.
    ///
    /// A row is written when an output path is claimed. Consumers must check the
    /// file exists, because an export can still fail after its path is claimed.
    /// Several rows may name the same output when one asset is packed into
    /// several bundles; that is a shared output, not a collision.
    /// </summary>
    public sealed class ExportManifestJsonlWriter : IDisposable
    {
        private const int SchemaVersion = 1;
        private readonly object sync = new object();
        private readonly string finalPath;
        private readonly string temporaryPath;
        private readonly string outputRoot;
        private readonly StreamWriter writer;
        private long outputCount;
        private long excludedCount;
        private bool completed;

        private ExportManifestJsonlWriter(FileInfo manifest, DirectoryInfo output)
        {
            finalPath = Path.GetFullPath(manifest.FullName);
            temporaryPath = finalPath + ".tmp";
            var directory = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            outputRoot = Path.GetFullPath(output.FullName);
            writer = new StreamWriter(
                new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false),
                64 * 1024
            );
            Current = this;
        }

        public static ExportManifestJsonlWriter Current { get; private set; }

        public static ExportManifestJsonlWriter Open(FileInfo manifest, DirectoryInfo output)
        {
            if (manifest == null) return null;
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (Current != null) throw new InvalidOperationException("An export manifest writer is already active.");
            return new ExportManifestJsonlWriter(manifest, output);
        }

        public void RecordOutput(AssetItem item, string outputPath)
        {
            var fullPath = Path.GetFullPath(outputPath);
            var relative = Path.GetRelativePath(outputRoot, fullPath).Replace('\\', '/');
            var row = JsonConvert.SerializeObject(new
            {
                kind = "output",
                output = relative,
                type = item.TypeString,
                name = item.Text,
                pathId = item.m_PathID,
                serializedFile = item.SourceFile?.fileName ?? "",
                source = item.SourceFile?.originalPath ?? "",
                sourceOffset = item.SourceFile?.offset ?? 0,
            }, Formatting.None);
            lock (sync)
            {
                if (completed) return;
                writer.WriteLine(row);
                outputCount++;
            }
        }

        /// <summary>An object whose path was claimed but not written: not exactly decodable.</summary>
        public void RecordExcluded(AssetItem item, string reason)
        {
            var row = JsonConvert.SerializeObject(new
            {
                kind = "excluded",
                type = item.TypeString,
                name = item.Text,
                pathId = item.m_PathID,
                serializedFile = item.SourceFile?.fileName ?? "",
                source = item.SourceFile?.originalPath ?? "",
                sourceOffset = item.SourceFile?.offset ?? 0,
                reason,
            }, Formatting.None);
            lock (sync)
            {
                if (completed) return;
                writer.WriteLine(row);
                excludedCount++;
            }
        }

        public void Complete(bool complete)
        {
            lock (sync)
            {
                if (completed) return;
                writer.WriteLine(JsonConvert.SerializeObject(new
                {
                    kind = "summary",
                    schemaVersion = SchemaVersion,
                    complete,
                    outputRoot,
                    outputCount,
                    excludedCount,
                    exactOnly = ExactOnlyGate.Enabled,
                }, Formatting.None));
                writer.Flush();
                writer.Dispose();
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(temporaryPath, finalPath);
                completed = true;
            }
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
