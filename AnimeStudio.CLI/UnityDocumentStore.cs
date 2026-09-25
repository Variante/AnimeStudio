using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace AnimeStudio.CLI
{
    /// <summary>
    /// Writes exported object documents (<c>.json</c>, <c>.anim</c>) into one
    /// SQLite file instead of one loose file each (<c>--document_store</c>).
    ///
    /// The file is the wrapper's Unity object store format exactly
    /// (<c>scripts/game_data/unity_store.py</c>, schema
    /// <c>endfield.unity-object-store.v1</c>): one row per document keyed by its
    /// type folder (the parent directory of the path the file export would have
    /// used) and its file name, holding the exact bytes the file export would
    /// have written, zlib-compressed, with their SHA256, and the indexed header
    /// columns <c>describe_document</c> derives from those bytes. The wrapper
    /// merges these staged stores into the export's <c>game/Unity.sqlite</c>
    /// with SQL, so nothing is re-read or recompressed.
    ///
    /// The store is written to <c>&lt;path&gt;.partial</c> and renamed over the
    /// final path only by <see cref="Complete"/>; a process that dies leaves no
    /// file at the final path. Staging-grade durability (no journal, no fsync)
    /// is intended: a failed process fails the stage.
    /// </summary>
    public sealed class UnityDocumentStoreWriter : IDisposable
    {
        public const string Schema = "endfield.unity-object-store.v1";
        private const int CommitEvery = 4096;
        // Documents waiting to be hashed/compressed/inserted; bounds memory
        // while letting compression overlap the export loop.
        private const int QueueCapacity = 256;

        // Verbatim from scripts/game_data/unity_store.py _SCHEMA_SQL.
        internal const string SchemaSql = @"
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS objects (
    type TEXT NOT NULL,
    name TEXT NOT NULL COLLATE NOCASE,
    object_name TEXT,
    path_id INTEGER,
    source_file TEXT,
    script_path_id INTEGER,
    size INTEGER NOT NULL,
    mtime_ns INTEGER NOT NULL,
    sha256 TEXT NOT NULL,
    data BLOB NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS objects_type_name ON objects (type, name);
CREATE INDEX IF NOT EXISTS objects_type_object_name ON objects (type, object_name);
CREATE INDEX IF NOT EXISTS objects_path_id ON objects (path_id, source_file);
CREATE INDEX IF NOT EXISTS objects_source_file ON objects (source_file, type);
CREATE INDEX IF NOT EXISTS objects_script ON objects (script_path_id) WHERE script_path_id IS NOT NULL;
";

        private enum OperationKind { Put, Remove, Contains }

        /// <summary>One store operation, applied by the writer thread in submission order.</summary>
        private sealed class Operation
        {
            public OperationKind Kind;
            public string Type;
            public string Name;
            public UnityDocumentInfo Info;
            public long Size;
            public long MtimeNs;
            public string Sha256;
            public byte[] Blob;
            public TaskCompletionSource<bool> Answer;
        }

        private readonly object sync = new object();
        private readonly string finalPath;
        private readonly string partialPath;
        // Hashing, header parsing and compression run on the thread pool; one
        // writer thread consumes the results in submission order, so a later
        // write to a name always lands after an earlier one.
        private readonly BlockingCollection<Task<Operation>> queue = new(QueueCapacity);
        private readonly Thread writerThread;
        private SqliteConnection connection;
        private SqliteTransaction transaction;
        private SqliteCommand insert;
        private SqliteCommand delete;
        private SqliteCommand exists;
        private int pending;
        private long documentCount;
        private volatile Exception failure;
        private bool completed;

        private UnityDocumentStoreWriter(FileInfo store)
        {
            finalPath = Path.GetFullPath(store.FullName);
            partialPath = finalPath + ".partial";
            var directory = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            DeleteDatabaseFiles(partialPath);
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = partialPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            connection.Open();
            Execute("PRAGMA journal_mode=OFF");
            Execute("PRAGMA synchronous=OFF");
            Execute(SchemaSql);
            using (var meta = connection.CreateCommand())
            {
                meta.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ('schema', $schema)";
                meta.Parameters.AddWithValue("$schema", Schema);
                meta.ExecuteNonQuery();
            }
            insert = connection.CreateCommand();
            // INSERT OR REPLACE, not an upsert: a file export deletes the old
            // file and creates a new one, so a re-claimed name also takes the
            // new spelling (the name column is NOCASE, like NTFS).
            insert.CommandText =
                "INSERT OR REPLACE INTO objects (type, name, object_name, path_id, source_file, script_path_id, size, mtime_ns, sha256, data) " +
                "VALUES ($type, $name, $object_name, $path_id, $source_file, $script_path_id, $size, $mtime_ns, $sha256, $data)";
            foreach (var parameter in new[] { "$type", "$name", "$object_name", "$path_id", "$source_file", "$script_path_id", "$size", "$mtime_ns", "$sha256", "$data" })
            {
                insert.Parameters.Add(new SqliteParameter { ParameterName = parameter });
            }
            delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM objects WHERE type=$type AND name=$name";
            delete.Parameters.Add(new SqliteParameter { ParameterName = "$type" });
            delete.Parameters.Add(new SqliteParameter { ParameterName = "$name" });
            exists = connection.CreateCommand();
            exists.CommandText = "SELECT 1 FROM objects WHERE type=$type AND name=$name";
            exists.Parameters.Add(new SqliteParameter { ParameterName = "$type" });
            exists.Parameters.Add(new SqliteParameter { ParameterName = "$name" });
            writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "document-store-writer" };
            writerThread.Start();
            Current = this;
        }

        public static UnityDocumentStoreWriter Current { get; private set; }

        public string FinalPath => finalPath;

        /// <summary>Rows inserted so far (a replaced name counts again).</summary>
        public long DocumentCount => Interlocked.Read(ref documentCount);

        public static UnityDocumentStoreWriter Open(FileInfo store)
        {
            if (store == null) return null;
            if (Current != null) throw new InvalidOperationException("A document store writer is already active.");
            return new UnityDocumentStoreWriter(store);
        }

        /// <summary>True for an exported file name the store owns (<c>unity_store.is_store_file</c>).</summary>
        public static bool IsDocumentName(string name)
        {
            var lower = (name ?? "").ToLowerInvariant();
            return lower.EndsWith(".json", StringComparison.Ordinal) || lower.EndsWith(".anim", StringComparison.Ordinal);
        }

        /// <summary>The (type folder, file name) row key of an export path.</summary>
        public static (string Type, string Name) KeyOf(string exportFullPath)
        {
            var name = Path.GetFileName(exportFullPath);
            var type = Path.GetFileName(Path.GetDirectoryName(exportFullPath) ?? "");
            return (type, name);
        }

        /// <summary>Store one document's exact bytes. The caller must not modify <paramref name="data"/> afterwards.</summary>
        public void Put(string exportFullPath, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var (type, name) = KeyOf(exportFullPath);
            var mtimeNs = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100L;
            Submit(Task.Run(() => Prepare(type, name, data, mtimeNs)));
        }

        /// <summary>Drop a claimed name's previous row, as a file export deletes the previous file.</summary>
        public void Remove(string exportFullPath)
        {
            var (type, name) = KeyOf(exportFullPath);
            Submit(Task.FromResult(new Operation { Kind = OperationKind.Remove, Type = type, Name = name }));
        }

        /// <summary>Whether a row exists once every earlier operation has been applied.</summary>
        public bool Contains(string exportFullPath)
        {
            var (type, name) = KeyOf(exportFullPath);
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Submit(Task.FromResult(new Operation { Kind = OperationKind.Contains, Type = type, Name = name, Answer = answer }));
            return answer.Task.GetAwaiter().GetResult();
        }

        /// <summary>Apply every queued document, commit, close, and publish the store at its final path.</summary>
        public void Complete()
        {
            lock (sync)
            {
                if (completed) return;
                StopWriter();
                if (failure != null)
                {
                    throw new InvalidOperationException($"document store {finalPath} was not completed: {failure.Message}", failure);
                }
                CloseConnection(commit: true);
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(partialPath, finalPath);
                DeleteDatabaseFiles(partialPath);
                completed = true;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (!completed)
                {
                    StopWriter();
                    CloseConnection(commit: false);
                    DeleteDatabaseFiles(partialPath);
                    completed = true;
                }
            }
            if (ReferenceEquals(Current, this)) Current = null;
        }

        private void Submit(Task<Operation> operation)
        {
            var failed = failure;
            if (failed != null)
            {
                throw new InvalidOperationException($"document store {finalPath} failed: {failed.Message}", failed);
            }
            try
            {
                queue.Add(operation);
            }
            catch (InvalidOperationException)
            {
                throw new InvalidOperationException($"document store {finalPath} is closed.");
            }
        }

        private static Operation Prepare(string type, string name, byte[] data, long mtimeNs)
        {
            var info = UnityDocumentHeader.Describe(name, data);
            var sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            byte[] blob;
            using (var buffer = new MemoryStream(data.Length / 4 + 64))
            {
                using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                {
                    zlib.Write(data, 0, data.Length);
                }
                blob = buffer.ToArray();
            }
            return new Operation
            {
                Kind = OperationKind.Put,
                Type = type,
                Name = name,
                Info = info,
                Size = data.Length,
                MtimeNs = mtimeNs,
                Sha256 = sha256,
                Blob = blob,
            };
        }

        private void WriterLoop()
        {
            foreach (var task in queue.GetConsumingEnumerable())
            {
                Operation operation = null;
                try
                {
                    operation = task.GetAwaiter().GetResult();
                    if (failure != null)
                    {
                        operation.Answer?.TrySetException(failure);
                        continue;
                    }
                    Apply(operation);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                    operation?.Answer?.TrySetException(exception);
                }
            }
        }

        private void Apply(Operation operation)
        {
            EnsureTransaction();
            switch (operation.Kind)
            {
                case OperationKind.Put:
                    insert.Parameters["$type"].Value = operation.Type;
                    insert.Parameters["$name"].Value = operation.Name;
                    insert.Parameters["$object_name"].Value = (object)operation.Info.ObjectName ?? DBNull.Value;
                    insert.Parameters["$path_id"].Value = operation.Info.PathId.HasValue ? operation.Info.PathId.Value : DBNull.Value;
                    insert.Parameters["$source_file"].Value = (object)operation.Info.SourceFile ?? DBNull.Value;
                    insert.Parameters["$script_path_id"].Value = operation.Info.ScriptPathId.HasValue ? operation.Info.ScriptPathId.Value : DBNull.Value;
                    insert.Parameters["$size"].Value = operation.Size;
                    insert.Parameters["$mtime_ns"].Value = operation.MtimeNs;
                    insert.Parameters["$sha256"].Value = operation.Sha256;
                    insert.Parameters["$data"].Value = operation.Blob;
                    insert.ExecuteNonQuery();
                    Interlocked.Increment(ref documentCount);
                    break;
                case OperationKind.Remove:
                    delete.Parameters["$type"].Value = operation.Type;
                    delete.Parameters["$name"].Value = operation.Name;
                    delete.ExecuteNonQuery();
                    break;
                case OperationKind.Contains:
                    exists.Parameters["$type"].Value = operation.Type;
                    exists.Parameters["$name"].Value = operation.Name;
                    operation.Answer.TrySetResult(exists.ExecuteScalar() != null);
                    return;
            }
            if (++pending >= CommitEvery)
            {
                transaction.Commit();
                transaction.Dispose();
                transaction = null;
                pending = 0;
            }
        }

        private void StopWriter()
        {
            if (!queue.IsAddingCompleted) queue.CompleteAdding();
            if (writerThread.IsAlive) writerThread.Join();
        }

        private void Execute(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private void EnsureTransaction()
        {
            if (transaction != null) return;
            transaction = connection.BeginTransaction();
            insert.Transaction = transaction;
            delete.Transaction = transaction;
            exists.Transaction = transaction;
        }

        private void CloseConnection(bool commit)
        {
            if (connection == null) return;
            if (transaction != null)
            {
                if (commit) transaction.Commit();
                else transaction.Rollback();
                transaction.Dispose();
                transaction = null;
            }
            insert?.Dispose();
            delete?.Dispose();
            exists?.Dispose();
            connection.Close();
            connection.Dispose();
            connection = null;
        }

        private static void DeleteDatabaseFiles(string path)
        {
            foreach (var candidate in new[] { path, path + "-journal", path + "-wal", path + "-shm" })
            {
                if (File.Exists(candidate)) File.Delete(candidate);
            }
        }
    }

    /// <summary>The indexed header columns of one document.</summary>
    public readonly record struct UnityDocumentInfo(string ObjectName, long? PathId, string SourceFile, long? ScriptPathId);

    /// <summary>
    /// A port of <c>unity_store.describe_document</c>: the indexed columns come
    /// from the document's own bytes (its <c>"$animestudio"</c> header when it has
    /// one), so a staged row carries exactly what the wrapper would compute for
    /// the same file. Parsing the written bytes instead of threading the header
    /// metadata through every export site keeps the two in step by construction.
    /// </summary>
    public static class UnityDocumentHeader
    {
        private const int HeaderScanBytes = 65536;
        private static readonly byte[] HeaderKey = Encoding.ASCII.GetBytes("\"$animestudio\"");
        private static readonly Regex PathIdSuffix = new Regex(@"_p([0-9A-Fa-f]{16})(?=\.)", RegexOptions.CultureInvariant);
        private static readonly Regex ObjectNameStem = new Regex(@"^(.*)_p[0-9A-Fa-f]{16}$", RegexOptions.CultureInvariant);

        public static UnityDocumentInfo Describe(string name, byte[] data)
        {
            string objectName = null;
            string sourceFile = null;
            long? scriptPathId = null;
            long? pathId = PathIdFromName(name);
            if (name.ToLowerInvariant().EndsWith(".json", StringComparison.Ordinal))
            {
                var header = ReadHeader(data);
                if (header != null)
                {
                    if (header.TryGetValue("pathId", out var headerPathId) && AsPythonInt(headerPathId) is long pid)
                        pathId = pid;
                    if (header.TryGetValue("name", out var headerName) && headerName.Token == JsonToken.String)
                        objectName = (string)headerName.Value;
                    if (header.TryGetValue("sourceFile", out var headerSource) && headerSource.Token == JsonToken.String)
                        sourceFile = (string)headerSource.Value;
                    if (header.TryGetValue("scriptPathId", out var headerScript) && AsPythonInt(headerScript) is long spid)
                        scriptPathId = spid;
                }
            }
            if (objectName == null)
            {
                var dot = name.IndexOf('.');
                var stem = dot >= 0 ? name.Substring(0, dot) : name;
                var match = ObjectNameStem.Match(stem);
                objectName = match.Success ? match.Groups[1].Value : stem;
            }
            return new UnityDocumentInfo(objectName, pathId, sourceFile, scriptPathId);
        }

        private static long? PathIdFromName(string name)
        {
            var match = PathIdSuffix.Match(name);
            if (!match.Success) return null;
            return unchecked((long)ulong.Parse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
        }

        private readonly record struct Scalar(JsonToken Token, object Value);

        // Python's isinstance(value, int) is also true for bool.
        private static long? AsPythonInt(Scalar value)
        {
            switch (value.Token)
            {
                case JsonToken.Integer:
                    return value.Value is long l ? l : null;
                case JsonToken.Boolean:
                    return (bool)value.Value ? 1L : 0L;
                default:
                    return null;
            }
        }

        /// <summary>The top-level scalar members of the header object, or null when there is none.</summary>
        private static Dictionary<string, Scalar> ReadHeader(byte[] data)
        {
            var limit = Math.Min(data.Length, HeaderScanBytes);
            var key = data.AsSpan(0, limit).IndexOf(HeaderKey);
            if (key < 0) return null;
            var from = key + HeaderKey.Length;
            var brace = data.AsSpan(from, limit - from).IndexOf((byte)'{');
            if (brace < 0) return null;
            var start = from + brace;
            try
            {
                using var stream = new MemoryStream(data, start, data.Length - start, writable: false);
                using var text = new StreamReader(stream, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: false);
                using var reader = new JsonTextReader(text)
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Double,
                    MaxDepth = null,
                };
                if (!reader.Read() || reader.TokenType != JsonToken.StartObject) return null;
                var members = new Dictionary<string, Scalar>(StringComparer.Ordinal);
                while (true)
                {
                    if (!reader.Read()) return null;
                    if (reader.TokenType == JsonToken.EndObject) return members;
                    if (reader.TokenType != JsonToken.PropertyName) return null;
                    var property = (string)reader.Value;
                    if (!reader.Read()) return null;
                    if (reader.TokenType == JsonToken.StartObject || reader.TokenType == JsonToken.StartArray)
                    {
                        reader.Skip();
                        members[property] = new Scalar(JsonToken.None, null);
                    }
                    else
                    {
                        members[property] = new Scalar(reader.TokenType, reader.Value);
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Routes the export of one object document either to disk (the default,
    /// unchanged) or into the active <see cref="UnityDocumentStoreWriter"/> when
    /// the current export target is a JSON export and a store was requested.
    /// </summary>
    internal static class DocumentOutput
    {
        private static readonly UTF8Encoding FileWriteAllTextEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        [ThreadStatic]
        private static bool jsonTargetActive;

        /// <summary>Mark the current thread's export loop as a JSON target for its lifetime.</summary>
        public static IDisposable Route(bool jsonTarget) => new RouteScope(jsonTarget);

        public static bool Routes(string exportFullPath)
            => jsonTargetActive
                && UnityDocumentStoreWriter.Current != null
                && UnityDocumentStoreWriter.IsDocumentName(Path.GetFileName(exportFullPath));

        /// <summary>
        /// TryExportFile for a routed document: the same naming and manifest row,
        /// with the store standing in for the file system.
        /// </summary>
        public static bool Claim(AssetItem item, string dir, string pathIdFileName, string extension, bool allowDuplicates, out string fullPath)
        {
            var store = UnityDocumentStoreWriter.Current;
            fullPath = Path.Combine(dir, $"{pathIdFileName}{extension}");
            if (!allowDuplicates)
            {
                store.Remove(fullPath);
                ExportManifestJsonlWriter.Current?.RecordOutput(item, fullPath);
                return true;
            }
            if (!store.Contains(fullPath))
            {
                ExportManifestJsonlWriter.Current?.RecordOutput(item, fullPath);
                return true;
            }
            for (int i = 0; ; i++)
            {
                fullPath = Path.Combine(dir, $"{pathIdFileName} ({i}){extension}");
                if (!store.Contains(fullPath))
                {
                    ExportManifestJsonlWriter.Current?.RecordOutput(item, fullPath);
                    return true;
                }
            }
        }

        public static void WriteAllText(string exportFullPath, string contents)
        {
            if (!Routes(exportFullPath))
            {
                File.WriteAllText(exportFullPath, contents);
                return;
            }
            // File.WriteAllText(path, string) writes UTF-8 without a BOM and
            // throws on unpaired surrogates; encode identically.
            UnityDocumentStoreWriter.Current.Put(exportFullPath, FileWriteAllTextEncoding.GetBytes(contents ?? ""));
        }

        public static void WriteAllBytes(string exportFullPath, byte[] bytes)
        {
            if (!Routes(exportFullPath))
            {
                File.WriteAllBytes(exportFullPath, bytes);
                return;
            }
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            UnityDocumentStoreWriter.Current.Put(exportFullPath, bytes);
        }

        /// <summary>A loose sidecar next to a routed document: its folder may not exist yet.</summary>
        public static void EnsureDirectoryFor(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }

        private sealed class RouteScope : IDisposable
        {
            private readonly bool previous;
            private bool disposed;

            public RouteScope(bool jsonTarget)
            {
                previous = jsonTargetActive;
                jsonTargetActive = jsonTarget;
            }

            public void Dispose()
            {
                if (disposed) return;
                jsonTargetActive = previous;
                disposed = true;
            }
        }
    }
}
