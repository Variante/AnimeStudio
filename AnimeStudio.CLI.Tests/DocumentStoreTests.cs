using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AnimeStudio.CLI;
using Microsoft.Data.Sqlite;

/// <summary>
/// --document_store: documents become rows of an endfield.unity-object-store.v1
/// SQLite file whose bytes, hashes and header columns match what the file
/// export and scripts/game_data/unity_store.py would produce.
/// </summary>
internal static class DocumentStoreTests
{
    private const string MonoHeaderDocument =
        "{\n  \"$animestudio\": {\n    \"pathId\": -5,\n    \"name\": \"Head\",\n    \"sourceFile\": \"CAB-abc\",\n    \"scriptPathId\": 77,\n    \"nested\": {\"name\": \"inner\"}\n  },\n  \"x\": \"é中\"\n}";

    public static void Run()
    {
        TestStoreRowsMatchTheFileExport();
        TestClaimCreatesNoFilesAndReplacesDuplicates();
        TestFileModeIsUnchanged();
        TestNonJsonTargetStaysOnDisk();
        TestAbandonedStoreLeavesNothing();
        TestDescribeDocumentPort();
    }

    /// <summary>`document-store-fixture OUT`: edge-case documents for the Python describe_document parity test.</summary>
    public static int WriteFixture(string outPath)
    {
        using (var store = UnityDocumentStoreWriter.Open(new FileInfo(outPath)))
        using (DocumentOutput.Route(true))
        {
            var root = Path.Combine(Path.GetTempPath(), "animestudio-document-store-fixture");
            foreach (var (type, name, text) in FixtureDocuments())
            {
                DocumentOutput.WriteAllText(Path.Combine(root, type, name), text);
            }
            store.Complete();
        }
        Console.WriteLine($"wrote {FixtureDocuments().Count()} documents to {outPath}");
        return 0;
    }

    private static IEnumerable<(string Type, string Name, string Text)> FixtureDocuments()
    {
        yield return ("MonoBehaviour", "Head_p0123456789ABCDEF.json", MonoHeaderDocument);
        yield return ("MonoBehaviour", "NoHeader_pFFFFFFFFFFFFFFFE.json", "{\"m_Name\": \"x\"}");
        yield return ("MonoBehaviour", "Bool_p0000000000000001.json", "{\"$animestudio\": {\"pathId\": true, \"scriptPathId\": false, \"name\": null}}");
        yield return ("MonoBehaviour", "Float_p0000000000000002.json", "{\"$animestudio\": {\"pathId\": 1.0, \"name\": 3, \"sourceFile\": \"\"}}");
        yield return ("MonoBehaviour", "Dup_p0000000000000003.json", "{\"$animestudio\": {\"name\": \"first\", \"name\": \"second\", \"pathId\": 9, \"pathId\": \"s\"}}");
        yield return ("MonoBehaviour", "NullHeader_p0000000000000004.json", "{\"$animestudio\": null, \"other\": {\"pathId\": 42, \"name\": \"taken\"}}");
        yield return ("MonoBehaviour", "Broken_p0000000000000005.json", "{\"$animestudio\": {\"pathId\": 5, \"name\": ");
        yield return ("MonoBehaviour", "Far_p0000000000000006.json", "{\"pad\": \"" + new string('a', 70000) + "\", \"$animestudio\": {\"pathId\": 6}}");
        yield return ("MonoBehaviour", "Long_p0000000000000007.json", "{\"$animestudio\": {\"pad\": \"" + new string('b', 70000) + "\", \"pathId\": 7, \"name\": \"long\"}}");
        yield return ("MonoBehaviour", "Date_p0000000000000008.json", "{\"$animestudio\": {\"name\": \"2020-01-01T00:00:00Z\", \"sourceFile\": \"\\u00e9\\ud83d\\ude00\"}}");
        yield return ("TextAsset", "plain.name.with.dots_p00000000000000AA.json", "{\"m_Script\": \"$animestudio\"}");
        yield return ("TextAsset", "unnamed.json", "{}");
        yield return ("AnimationClip", "clip_p00000000000000BB.anim", "%YAML 1.1\n\"$animestudio\": {\"pathId\": 1}\n");
        yield return ("AnimationClip", "Upper_p00000000000000CC.ANIM", "anim");
        yield return ("Material", "m_pabcdefabcdefabcd.json.json", "{\"$animestudio\":{\"pathId\":-9223372036854775808,\"name\":\"min\"}}");
    }

    private static void TestStoreRowsMatchTheFileExport()
    {
        var root = NewTempDirectory();
        var storePath = Path.Combine(root, "store", "MonoBehaviour.sqlite");
        var exportRoot = Path.Combine(root, "json_by_type");
        var docPath = Path.Combine(exportRoot, "MonoBehaviour", "Head_p0123456789ABCDEF.json");
        var fileModePath = Path.Combine(root, "files", "Head_p0123456789ABCDEF.json");
        Directory.CreateDirectory(Path.GetDirectoryName(fileModePath)!);
        File.WriteAllText(fileModePath, MonoHeaderDocument);
        var expected = File.ReadAllBytes(fileModePath);
        var before = DateTime.UtcNow;

        using (var store = UnityDocumentStoreWriter.Open(new FileInfo(storePath))!)
        using (DocumentOutput.Route(true))
        {
            Assert(File.Exists(storePath + ".partial"), "store is staged at .partial while open");
            Assert(!File.Exists(storePath), "final store appears only on Complete");
            DocumentOutput.WriteAllText(docPath, MonoHeaderDocument);
            store.Complete();
        }

        Assert(File.Exists(storePath), "completed store exists");
        foreach (var suffix in new[] { ".partial", "-journal", "-wal", "-shm", ".partial-journal" })
        {
            Assert(!File.Exists(storePath + suffix), $"no {suffix} sidecar is left");
        }
        Assert(!Directory.Exists(exportRoot), "no document folder or file is created in store mode");

        using var connection = OpenReadOnly(storePath);
        AssertEqual("endfield.unity-object-store.v1", Scalar(connection, "SELECT value FROM meta WHERE key='schema'"), "meta schema");
        AssertEqual("delete", Scalar(connection, "PRAGMA journal_mode"), "a plain rollback-journal file for read-only openers");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, object_name, path_id, source_file, script_path_id, size, mtime_ns, sha256, data FROM objects";
        using var reader = command.ExecuteReader();
        Assert(reader.Read(), "one row");
        AssertEqual("MonoBehaviour", reader.GetString(0), "type is the parent folder");
        AssertEqual("Head_p0123456789ABCDEF.json", reader.GetString(1), "name is the file name");
        AssertEqual("Head", reader.GetString(2), "object_name from the header");
        AssertEqual(-5L, reader.GetInt64(3), "path_id from the header wins over the file name");
        AssertEqual("CAB-abc", reader.GetString(4), "source_file");
        AssertEqual(77L, reader.GetInt64(5), "script_path_id");
        AssertEqual((long)expected.Length, reader.GetInt64(6), "size is the file byte count");
        var mtimeNs = reader.GetInt64(7);
        var beforeNs = (before.Ticks - DateTime.UnixEpoch.Ticks) * 100L;
        Assert(mtimeNs >= beforeNs - 1_000_000_000L && mtimeNs <= beforeNs + 600_000_000_000L, "mtime_ns is the write time in ns since the epoch");
        AssertEqual(Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(), reader.GetString(8), "sha256 of the file bytes, lowercase");
        var blob = (byte[])reader.GetValue(9);
        Assert(blob.Length > 2 && blob[0] == 0x78, "data is zlib-format (RFC 1950)");
        AssertBytes(expected, Inflate(blob), "zlib round trip is byte-identical to File.WriteAllText (UTF-8, no BOM)");
        Assert(!reader.Read(), "exactly one row");
    }

    private static void TestClaimCreatesNoFilesAndReplacesDuplicates()
    {
        var root = NewTempDirectory();
        var storePath = Path.Combine(root, "claims.sqlite");
        var typeDir = Path.Combine(root, "out", "MonoBehaviour") + Path.DirectorySeparatorChar;
        using (var store = UnityDocumentStoreWriter.Open(new FileInfo(storePath))!)
        using (DocumentOutput.Route(true))
        {
            Assert(DocumentOutput.Routes(Path.Combine(typeDir, "a_p0000000000000001.json")), "a .json path routes");
            Assert(DocumentOutput.Routes(Path.Combine(typeDir, "a_p0000000000000001.ANIM")), "suffix match ignores case");
            Assert(!DocumentOutput.Routes(Path.Combine(typeDir, "a_p0000000000000001.png")), "media does not route");

            Assert(DocumentOutput.Claim(null!, typeDir, "a_p0000000000000001", ".json", false, out var first), "claim");
            DocumentOutput.WriteAllText(first, "{\"v\": 1}");
            Assert(DocumentOutput.Claim(null!, typeDir, "A_p0000000000000001", ".json", false, out var second), "re-claim (other case)");
            DocumentOutput.WriteAllText(second, "{\"v\": 2}");
            Assert(DocumentOutput.Claim(null!, typeDir, "gone_p0000000000000002", ".json", false, out var gone), "claim");
            DocumentOutput.WriteAllText(gone, "{}");
            // A re-claimed name whose export then fails leaves nothing, as a deleted file would.
            Assert(DocumentOutput.Claim(null!, typeDir, "gone_p0000000000000002", ".json", false, out _), "re-claim");

            Assert(DocumentOutput.Claim(null!, typeDir, "dup_p0000000000000003", ".json", true, out var dup0), "duplicates allowed");
            DocumentOutput.WriteAllText(dup0, "{}");
            Assert(DocumentOutput.Claim(null!, typeDir, "dup_p0000000000000003", ".json", true, out var dup1), "duplicates allowed");
            AssertEqual("dup_p0000000000000003 (0).json", Path.GetFileName(dup1), "allowDuplicates numbers a taken name like the file export");
            store.Complete();
        }
        Assert(!Directory.Exists(Path.Combine(root, "out")), "claims create no folders");
        using var connection = OpenReadOnly(storePath);
        AssertEqual(2L, (long)ScalarObject(connection, "SELECT COUNT(*) FROM objects")!, "replaced and dropped rows");
        AssertEqual("A_p0000000000000001.json", Scalar(connection, "SELECT name FROM objects WHERE name LIKE 'a%'"), "a replacement takes the new spelling");
        AssertBytes(Encoding.UTF8.GetBytes("{\"v\": 2}"), Inflate((byte[])ScalarObject(connection, "SELECT data FROM objects WHERE name LIKE 'a%'")!), "later write wins");
    }

    private static void TestFileModeIsUnchanged()
    {
        var root = NewTempDirectory();
        var path = Path.Combine(root, "t_p0000000000000001.json");
        var reference = Path.Combine(root, "reference.json");
        DocumentOutput.WriteAllText(path, MonoHeaderDocument);
        File.WriteAllText(reference, MonoHeaderDocument);
        AssertBytes(File.ReadAllBytes(reference), File.ReadAllBytes(path), "no store: WriteAllText is File.WriteAllText");
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 1, 2, 3 };
        DocumentOutput.WriteAllBytes(path, bytes);
        AssertBytes(bytes, File.ReadAllBytes(path), "no store: WriteAllBytes is File.WriteAllBytes");
        using (DocumentOutput.Route(true))
        {
            Assert(UnityDocumentStoreWriter.Current == null, "no store is active");
            Assert(!DocumentOutput.Routes(path), "without a store nothing routes");
        }
    }

    private static void TestNonJsonTargetStaysOnDisk()
    {
        var root = NewTempDirectory();
        var storePath = Path.Combine(root, "store.sqlite");
        var path = Path.Combine(root, "AnimationClip", "c_p0000000000000001.anim");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var store = UnityDocumentStoreWriter.Open(new FileInfo(storePath))!)
        using (DocumentOutput.Route(false))
        {
            DocumentOutput.WriteAllText(path, "%YAML");
            store.Complete();
        }
        Assert(File.Exists(path), "a Convert target's document stays a file");
        using var connection = OpenReadOnly(storePath);
        AssertEqual(0L, (long)ScalarObject(connection, "SELECT COUNT(*) FROM objects")!, "nothing routed");
    }

    private static void TestAbandonedStoreLeavesNothing()
    {
        var root = NewTempDirectory();
        var storePath = Path.Combine(root, "abandoned.sqlite");
        using (UnityDocumentStoreWriter.Open(new FileInfo(storePath))!)
        using (DocumentOutput.Route(true))
        {
            DocumentOutput.WriteAllText(Path.Combine(root, "MonoBehaviour", "x_p0000000000000001.json"), "{}");
        }
        Assert(!File.Exists(storePath) && !File.Exists(storePath + ".partial"), "a store that never completed leaves no file");
        Assert(UnityDocumentStoreWriter.Current == null, "writer released");
    }

    private static void TestDescribeDocumentPort()
    {
        UnityDocumentInfo Describe(string name, string text) => UnityDocumentHeader.Describe(name, Encoding.UTF8.GetBytes(text));

        AssertEqual(new UnityDocumentInfo("NoHeader", -2, null!, null), Describe("NoHeader_pFFFFFFFFFFFFFFFE.json", "{}"), "fallback from the file name, signed PathID");
        AssertEqual(new UnityDocumentInfo("Head", -5, "CAB-abc", 77), Describe("Head_p0123456789ABCDEF.json", MonoHeaderDocument), "header columns");
        AssertEqual(new UnityDocumentInfo("Bool", 1, null!, 0), Describe("Bool_p0000000000000009.json", "{\"$animestudio\": {\"pathId\": true, \"scriptPathId\": false, \"name\": null}}"), "bool counts as a Python int");
        AssertEqual(new UnityDocumentInfo("second", 3, null!, null), Describe("Dup_p0000000000000003.json", "{\"$animestudio\": {\"name\": \"first\", \"name\": \"second\", \"pathId\": 9, \"pathId\": \"s\"}}"), "the last duplicate key wins");
        AssertEqual(new UnityDocumentInfo("taken", 42, null!, null), Describe("NullHeader_p0000000000000004.json", "{\"$animestudio\": null, \"other\": {\"pathId\": 42, \"name\": \"taken\"}}"), "the first object after the key is read, as describe_document does");
        AssertEqual(new UnityDocumentInfo("Broken", 5, null!, null), Describe("Broken_p0000000000000005.json", "{\"$animestudio\": {\"pathId\": 5, \"name\": "), "an unparsable header is ignored");
        AssertEqual(new UnityDocumentInfo("Far", 6, null!, null), Describe("Far_p0000000000000006.json", "{\"pad\": \"" + new string('a', 70000) + "\", \"$animestudio\": {\"pathId\": 60}}"), "a key beyond the first 64 KiB is not read");
        AssertEqual(new UnityDocumentInfo("long", 7, null!, null), Describe("Long_p0000000000000007.json", "{\"$animestudio\": {\"pad\": \"" + new string('b', 70000) + "\", \"pathId\": 7, \"name\": \"long\"}}"), "a header that crosses 64 KiB is read whole");
        AssertEqual(new UnityDocumentInfo("clip", 0xBB, null!, null), Describe("clip_p00000000000000BB.anim", "\"$animestudio\": {\"pathId\": 1}"), ".anim headers are not parsed");
        AssertEqual(new UnityDocumentInfo("plain", 0xAA, null!, null), Describe("plain.name_p00000000000000AA.json", "{}"), "the stem stops at the first dot");
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string? Scalar(SqliteConnection connection, string sql)
        => ScalarObject(connection, sql) is { } value ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null;

    private static object? ScalarObject(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static byte[] Inflate(byte[] blob)
    {
        using var input = new MemoryStream(blob);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "animestudio-document-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"DocumentStoreTests: {label}");
    }

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"DocumentStoreTests: {label}: expected {expected}, got {actual}");
    }

    private static void AssertBytes(byte[] expected, byte[] actual, string label)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new InvalidOperationException($"DocumentStoreTests: {label}: {expected.Length} vs {actual.Length} bytes differ");
    }
}
